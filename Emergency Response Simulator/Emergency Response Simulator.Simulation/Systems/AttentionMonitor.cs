using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// "Something requires your attention" (Design Document §6.9). Watches the COP — what command
/// knows, not ground truth — and raises an alert once when a condition starts, re-arming when it clears:
/// critical incidents, resource shortages, situation changes (wind shifts) and communication failures,
/// plus alerts derived from the AVL feed (§7.4): units stopped en route, off their reported route, delayed
/// by a re-route, or whose AVL has gone quiet.
/// </summary>
public sealed class AttentionMonitor(ICopService cop, AttentionOptions options, IRoutingService? routing = null) : ISimulationSystem
{
    private readonly HashSet<string> _active = [];
    private readonly Dictionary<Guid, AvlWatch> _avl = [];
    private PerceivedWeather? _lastWeather;

    /// <summary>What the monitor remembers about each unit's movement between ticks.</summary>
    private sealed class AvlWatch
    {
        public DateTimeOffset? StoppedSince;
        public LineString? Route;

        /// <summary>Expected arrival: time of the last fix plus the ETA it carried.</summary>
        public DateTimeOffset? Arrival;
        public int OffRouteFixes;
        public DateTimeOffset? LastFixSeen;
    }

    public int Order => 100; // after systems that change the world

    public void Update(SimulationContext context)
    {
        CheckCriticalIncidents(context);
        CheckResourceShortages(context);
        CheckWeather(context);
        CheckCommunications(context);
        CheckAvl(context);
        CheckCommand(context);
        CheckPlanning(context);
    }

    /// <summary>
    /// Planning health (§8.6–8.7): incidents running without a plan, periods about to end, plans waiting for
    /// approval or briefing, and the plan in force diverging from the COP — units it relies on that are out of
    /// service or gone, and objectives past their target time.
    /// </summary>
    private void CheckPlanning(SimulationContext context)
    {
        var now = context.SimTime;
        foreach (var incident in cop.Incidents.Where(i => i.Status != IncidentStatus.Closed))
        {
            var periods = cop.PeriodsOf(incident.Id);
            Track(context, $"no-iap:{incident.Id}",
                periods.Count == 0 && incident.AssignedUnits.Count >= options.PlanningUnitThreshold
                                   && now - incident.ReportedAt >= options.PlanningExpectedAfter,
                AlertCategory.Planning, AlertSeverity.Info,
                $"{incident.Number}: no Incident Action Plan",
                $"{incident.AssignedUnits.Count} units committed and no operational period defined. Start period 1 and draft an IAP.",
                incident.Id);
            if (periods.Count == 0) continue;

            var last = periods[^1];
            Track(context, $"period-ending:{last.Id}", now >= last.End - options.PeriodEndWarning && now < last.End,
                AlertCategory.Planning, AlertSeverity.Warning,
                $"{incident.Number}: operational period {last.Number} ends at {last.End.ToLocalTime():HH:mm}",
                $"Reassess the situation and prepare the IAP for period {last.Number + 1}.",
                incident.Id);
            Track(context, $"period-ended:{last.Id}", now >= last.End,
                AlertCategory.Planning, AlertSeverity.Warning,
                $"{incident.Number}: operational period {last.Number} has ended",
                "No next period has been started; crews are still working to an expired plan.",
                incident.Id);

            foreach (var pending in cop.ActionPlans.Where(p => p.IncidentId == incident.Id && p.Status == IapStatus.PendingApproval))
            {
                Track(context, $"iap-pending:{pending.Id}", now - (pending.SubmittedAt ?? now) >= options.DecisionReminder,
                    AlertCategory.Planning, AlertSeverity.Warning,
                    $"{incident.Number}: IAP version {pending.Version} awaiting approval",
                    $"Submitted by {pending.SubmittedBy} {(now - (pending.SubmittedAt ?? now)).TotalMinutes:F0} min ago.",
                    incident.Id);
            }

            if (cop.CurrentPeriod(incident.Id, now) is not { } current) continue;
            var plan = cop.ApprovedPlan(current.Id);
            Track(context, $"no-plan:{current.Id}", plan is null && now - current.Start >= options.PlanApprovalGrace,
                AlertCategory.Planning, AlertSeverity.Warning,
                $"{incident.Number}: no approved IAP for period {current.Number}",
                $"The period began at {current.Start.ToLocalTime():HH:mm}; operations are running without an approved plan.",
                incident.Id);
            if (plan is null) continue;

            Track(context, $"brief:{plan.Id}", plan.BriefedAt is null && now - (plan.ApprovedAt ?? now) >= options.BriefingReminder,
                AlertCategory.Planning, AlertSeverity.Info,
                $"{incident.Number}: IAP version {plan.Version} not yet briefed",
                "The plan is approved but its assignments have not been pushed to the units.",
                incident.Id);

            // Reality diverges from the plan (§8.6): "IAP: Engine 7 → Exposure protection; COP: Engine 7 → OUT OF SERVICE".
            foreach (var assignment in plan.Content.Assignments)
            {
                var problem = IapCompliance.ViabilityProblem(assignment, incident, cop.FindUnit(assignment.UnitId),
                    expectCommitted: plan.BriefedAt is not null);
                Track(context, $"viability:{plan.Id}:{assignment.UnitId}", problem is not null,
                    AlertCategory.Planning, AlertSeverity.Warning,
                    $"Plan not viable: {assignment.Callsign} {problem}",
                    $"IAP: {assignment.Callsign} → {assignment.Assignment}. Reassign the task or revise the plan.",
                    incident.Id, assignment.UnitId);
            }

            var progress = cop.ObjectiveProgress;
            foreach (var objective in plan.Content.OperationalObjectives)
            {
                var status = progress.GetValueOrDefault(objective.Id);
                Track(context, $"overdue:{plan.Id}:{objective.Id}",
                    objective.TargetTime is { } due && now > due && status is ObjectiveStatus.Open or ObjectiveStatus.InProgress,
                    AlertCategory.Planning, AlertSeverity.Warning,
                    $"Objective overdue: {plan.Content.NumberOf(objective.Id)} {objective.Statement}",
                    $"Target{(objective.PerformanceTarget is { } target ? $" \"{target}\"" : "")} was due at " +
                    $"{objective.TargetTime?.ToLocalTime():HH:mm}. Update its progress or revise the plan.",
                    incident.Id);
            }
        }
    }

    /// <summary>
    /// Command health: supervisors over their span of control (§8.1), orders nobody has read back,
    /// and decisions waiting on command.
    /// </summary>
    private void CheckCommand(SimulationContext context)
    {
        foreach (var incident in cop.Incidents.Where(i => i.Status != IncidentStatus.Closed))
        {
            var units = incident.AssignedUnits.Select(u => u.Id).ToList();
            foreach (var span in SpanOfControl.Assess(incident.Command, units))
            {
                Track(context, $"span:{incident.Id}:{span.Key}", span.IsOverloaded,
                    AlertCategory.Safety, AlertSeverity.Warning,
                    $"{incident.Number}: {span.Supervisor} has {span.DirectReports} direct reports",
                    $"Span of control exceeded (recommended {SpanOfControl.Minimum}–{SpanOfControl.Maximum}). " +
                    "Orders through this supervisor will be slower and may be lost. Form groups or staff Operations.",
                    incident.Id);
            }
        }

        foreach (var order in cop.Orders.Where(o => o.Status == OrderStatus.Issued))
        {
            Track(context, $"order:{order.Id}", context.SimTime - order.IssuedAt >= options.OrderAcknowledgeTimeout,
                AlertCategory.CommunicationFailure, AlertSeverity.Warning,
                $"Order to {order.TargetName} not acknowledged",
                $"No read-back after {options.OrderAcknowledgeTimeout.TotalMinutes:F0} min: \"{order.Text}\". Repeat the order or check comms.",
                order.IncidentId, order.TargetKind == OrderTargetKind.Unit ? order.TargetId : null);
        }

        foreach (var approval in cop.Approvals.Where(a => a.Status == ApprovalStatus.Pending))
        {
            Track(context, $"approval:{approval.Id}", context.SimTime - approval.RequestedAt >= options.DecisionReminder,
                AlertCategory.SituationChange, AlertSeverity.Warning,
                $"Decision awaited: {approval.Subject}",
                $"{approval.RequestedBy} has been waiting {(context.SimTime - approval.RequestedAt).TotalMinutes:F0} min for a decision.",
                approval.IncidentId);
        }
    }

    private void CheckCriticalIncidents(SimulationContext context)
    {
        foreach (var incident in cop.Incidents.Where(i => i.Status != IncidentStatus.Closed))
        {
            Track(context, $"critical:{incident.Id}", incident.Priority == IncidentPriority.Critical,
                AlertCategory.Critical, AlertSeverity.Critical,
                $"{incident.Number} upgraded to CRITICAL",
                $"{incident.Name ?? EventDescriber.Humanize(incident.Type)} at {incident.Address ?? "reported location"}.",
                incident.Id);

            Track(context, $"mci:{incident.Id}", incident.CasualtiesReported >= options.MassCasualtyThreshold,
                AlertCategory.Critical, AlertSeverity.Critical,
                $"Possible mass-casualty incident at {incident.Number}",
                $"{incident.CasualtiesReported} casualties reported ({incident.CasualtiesConfirmed} confirmed). Consider MCI procedures.",
                incident.Id);
        }
    }

    private void CheckResourceShortages(SimulationContext context)
    {
        foreach (var group in cop.Units.GroupBy(u => ResourceGroups.For(u.Type)))
        {
            var total = group.Count();
            if (total < options.ShortageMinimumFleet) continue;

            var available = group.Count(u => u.Status == UnitStatus.Available);
            var key = $"shortage:{group.Key}";

            if (available <= options.ShortageThreshold)
            {
                Track(context, key, true, AlertCategory.ResourceShortage, AlertSeverity.Warning,
                    $"{group.Key}: {available} of {total} available",
                    available == 0
                        ? $"No {LowerFirst(group.Key)} remain available in the operational area. Consider mutual aid."
                        : $"Only {available} of {total} {LowerFirst(group.Key)} remain available in the operational area.");
            }
            else if (available > options.ShortageThreshold + 1)
            {
                // Hysteresis: re-arm only once there is real slack again, so the alert doesn't flap.
                _active.Remove(key);
            }
        }
    }

    /// <summary>"Fire engines" → "fire engines", but "ALS ambulances" keeps its acronym.</summary>
    private static string LowerFirst(string text) =>
        text.Length > 1 && char.IsUpper(text[1]) ? text : char.ToLowerInvariant(text[0]) + text[1..];

    private void CheckWeather(SimulationContext context)
    {
        if (cop.Weather is not { } weather) return;
        if (_lastWeather is null || _lastWeather.ObservedAt == weather.ObservedAt)
        {
            _lastWeather ??= weather;
            return;
        }

        var previous = _lastWeather;
        _lastWeather = weather;

        var shift = Math.Abs(((weather.WindFromDegrees - previous.WindFromDegrees + 540) % 360) - 180);
        var speedChange = Math.Abs(weather.WindSpeedMps - previous.WindSpeedMps);
        if (shift < options.WindShiftDegrees && speedChange < options.WindSpeedChangeMps) return;

        string Direction(PerceivedWeather w) =>
            $"{GeoMath.CompassPoint(w.WindFromDegrees)} → {GeoMath.CompassPoint(w.WindFromDegrees + 180)}";

        var affected = cop.Zones.Count(z => z.Type is ZoneType.EvacuationZone or ZoneType.ShelterInPlace
            or ZoneType.HotZone or ZoneType.WarmZone or ZoneType.ColdZone);
        Raise(context, AlertCategory.SituationChange, AlertSeverity.Warning,
            "Wind direction changed",
            $"Previous: {Direction(previous)} {previous.WindSpeedMps:F0} m/s. Current: {Direction(weather)} {weather.WindSpeedMps:F0} m/s." +
            (affected > 0 ? $" {affected} hazard/evacuation zone(s) potentially affected." : ""));
    }

    private void CheckCommunications(SimulationContext context)
    {
        foreach (var unit in cop.Units)
        {
            if (!UnitStatusRules.IsCommitted(unit.Status) || !unit.CommsConnected || unit.LastContactAt is not { } last)
                continue;

            var silence = context.SimTime - last;
            if (silence < options.CommunicationTimeout) continue;

            var position = unit.Location is { } p
                ? $"{p.Y:F4}, {p.X:F4}" + (unit.AssignedIncident is { } i ? $" ({i.Number})" : "")
                : "unknown";
            // The COP marks the unit as out of contact when this alert is applied, which stops repeats.
            Raise(context, AlertCategory.CommunicationFailure, AlertSeverity.Warning,
                $"{unit.Callsign} not heard from",
                $"{unit.Callsign} has not transmitted for {silence.TotalMinutes:F0} minutes. Last known position: {position}.",
                unit.AssignedIncidentId, unit.Id);
        }
    }

    private void CheckAvl(SimulationContext context)
    {
        foreach (var unit in cop.Units)
        {
            if (!_avl.TryGetValue(unit.Id, out var watch))
                _avl[unit.Id] = watch = new AvlWatch();

            var enRoute = unit.Status == UnitStatus.EnRoute;
            var where = Describe(unit);

            // A new route: compare the expected arrival time with the one before. This counts time lost
            // while held up as well as the longer drive.
            var arrival = unit.LastAvlUpdate is { } fixedAt && unit.Eta is { } eta ? fixedAt + eta : (DateTimeOffset?)null;
            if (!ReferenceEquals(watch.Route, unit.PlannedRoute))
            {
                if (enRoute && watch.Route is not null && watch.Arrival is { } before && arrival is { } after
                    && after - before >= options.EtaIncreaseAlert)
                {
                    Raise(context, AlertCategory.SituationChange, AlertSeverity.Warning,
                        $"{unit.Callsign} delayed: re-routed",
                        $"Expected arrival now {after.ToLocalTime():HH:mm:ss}, {(after - before).TotalMinutes:F1} min later than planned. " +
                        $"Position: {where}.",
                        unit.AssignedIncidentId, unit.Id);
                }
                watch.Route = unit.PlannedRoute;
                watch.OffRouteFixes = 0;
                _active.Remove($"deviation:{unit.Id}");
            }
            if (!enRoute || unit.SpeedKph >= 1)
                watch.Arrival = arrival; // while held up, keep the arrival the unit was heading for

            if (!enRoute)
            {
                watch.StoppedSince = null;
                _active.Remove($"stopped:{unit.Id}");
                _active.Remove($"avl:{unit.Id}");
                continue;
            }

            // Stopped movement.
            if (unit.SpeedKph < 1)
                watch.StoppedSince ??= unit.LastAvlUpdate ?? context.SimTime;
            else
                watch.StoppedSince = null;

            Track(context, $"stopped:{unit.Id}",
                watch.StoppedSince is { } stopped && context.SimTime - stopped >= options.StoppedAlertAfter,
                AlertCategory.SituationChange, AlertSeverity.Warning,
                $"{unit.Callsign} stopped en route",
                $"No movement for {(context.SimTime - (watch.StoppedSince ?? context.SimTime)).TotalMinutes:F0} min at {where}.",
                unit.AssignedIncidentId, unit.Id);

            // Route deviation: two consecutive fixes well off the reported route.
            if (unit.LastAvlUpdate != watch.LastFixSeen && unit.Location is { } location && unit.PlannedRoute is { } route)
            {
                watch.LastFixSeen = unit.LastAvlUpdate;
                var off = OffRouteMeters(GeoPoint.FromPoint(location), route);
                watch.OffRouteFixes = off > options.RouteDeviationMeters ? watch.OffRouteFixes + 1 : 0;
            }
            Track(context, $"deviation:{unit.Id}", watch.OffRouteFixes >= 2,
                AlertCategory.SituationChange, AlertSeverity.Warning,
                $"{unit.Callsign} off planned route",
                $"More than {options.RouteDeviationMeters:F0} m from its reported route at {where}.",
                unit.AssignedIncidentId, unit.Id);

            // AVL loss: a moving unit should report every few seconds.
            Track(context, $"avl:{unit.Id}",
                unit.LastAvlUpdate is { } last && context.SimTime - last >= options.AvlLossAfter,
                AlertCategory.CommunicationFailure, AlertSeverity.Warning,
                $"{unit.Callsign}: AVL signal lost",
                $"No position fix for {(context.SimTime - (unit.LastAvlUpdate ?? context.SimTime)).TotalSeconds:F0} s while en route. Last known position: {where}.",
                unit.AssignedIncidentId, unit.Id);
        }
    }

    private string Describe(Unit unit)
    {
        if (unit.Location is not { } p) return "unknown";
        var point = GeoPoint.FromPoint(p);
        return routing?.NearestRoad(point) is { } road && road.DistanceMeters < 60
            ? road.Name
            : $"{point.Latitude:F4}, {point.Longitude:F4}";
    }

    private static double OffRouteMeters(GeoPoint point, LineString route)
    {
        var best = double.MaxValue;
        for (var i = 0; i < route.NumPoints - 1; i++)
        {
            var a = route.GetCoordinateN(i);
            var b = route.GetCoordinateN(i + 1);
            best = Math.Min(best, Routing.RoadNetwork.DistanceToSegment(point, new GeoPoint(a.Y, a.X), new GeoPoint(b.Y, b.X)));
        }
        return best;
    }

    private void Track(SimulationContext context, string key, bool condition, AlertCategory category,
        AlertSeverity severity, string title, string message, Guid? incidentId = null, Guid? unitId = null)
    {
        if (!condition)
        {
            _active.Remove(key);
            return;
        }

        if (_active.Add(key))
            Raise(context, category, severity, title, message, incidentId, unitId);
    }

    private static void Raise(SimulationContext context, AlertCategory category, AlertSeverity severity,
        string title, string message, Guid? incidentId = null, Guid? unitId = null) =>
        context.EmitPerceived(new AlertRaised(Guid.NewGuid(), category, severity, title, message, incidentId, unitId),
            EventSources.Cop);
}

/// <summary>Bound from the "Attention" configuration section.</summary>
public sealed class AttentionOptions
{
    public const string SectionName = "Attention";

    public TimeSpan CommunicationTimeout { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>Raise a shortage alert when this many or fewer units of a type remain available.</summary>
    public int ShortageThreshold { get; set; } = 1;

    /// <summary>Only watch resource types with at least this many units.</summary>
    public int ShortageMinimumFleet { get; set; } = 2;

    public int MassCasualtyThreshold { get; set; } = 10;
    public double WindShiftDegrees { get; set; } = 45;
    public double WindSpeedChangeMps { get; set; } = 5;

    public TimeSpan StoppedAlertAfter { get; set; } = TimeSpan.FromSeconds(90);
    public TimeSpan OrderAcknowledgeTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan DecisionReminder { get; set; } = TimeSpan.FromMinutes(3);
    public TimeSpan AvlLossAfter { get; set; } = TimeSpan.FromSeconds(60);
    public double RouteDeviationMeters { get; set; } = 150;
    /// <summary>Alert when a re-route pushes expected arrival back by at least this much.</summary>
    public TimeSpan EtaIncreaseAlert { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Suggest an IAP once an incident is this old with at least <see cref="PlanningUnitThreshold"/> units committed.</summary>
    public TimeSpan PlanningExpectedAfter { get; set; } = TimeSpan.FromMinutes(20);
    public int PlanningUnitThreshold { get; set; } = 4;
    public TimeSpan PeriodEndWarning { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a period may run before missing an approved plan is flagged.</summary>
    public TimeSpan PlanApprovalGrace { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan BriefingReminder { get; set; } = TimeSpan.FromMinutes(5);
}
