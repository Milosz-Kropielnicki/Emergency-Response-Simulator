using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;

namespace Emergency_Response_Simulator.Simulation.Aar;

/// <summary>
/// The recommended response, played by the simulation (Design Document §22: branching AAR), so a session can be
/// compared with what doctrine would have achieved in the same scenario. It sees only the COP, like a trainee, and acts
/// with human-like delays:
/// <list type="bullet">
/// <item>opens an incident 45 s after the first located report and sends a full first attendance (two engines, a
/// ladder, two ambulances, a Garda patrol);</item>
/// <item>on a report of chemicals: the Hazmat unit and hot, warm and cold zones; asks for more resources at 8 minutes;</item>
/// <item>approves requests within a minute (an evacuation with an evacuation zone), alerts hospitals and public health on
/// a casualty report;</item>
/// <item>sounds the evacuation signal on a warning of collapse; on a Mayday clears the channel, sends a rescue team and
/// calls a PAR; PARs every 20 minutes;</item>
/// <item>rotates crews through rehab after 40 minutes on task, relieves crews past their shift, rings back missed callers,
/// asks for repeats of unclear messages and acknowledges alerts.</item>
/// </list>
/// Its commands go straight into the event stream with source C2, so the AAR can judge them like anyone else's.
/// </summary>
public sealed class ExpertPolicy(ICopService cop) : ISimulationSystem
{
    public static readonly TimeSpan ReactionTime = TimeSpan.FromSeconds(30);

    private readonly HashSet<string> _done = [];
    private Guid? _incident;
    private GeoPoint _location;
    private DateTimeOffset _openedAt;
    private DateTimeOffset _lastRehab;

    public int Order => 22; // after the world has moved, like a trainee watching the COP

    public void Update(SimulationContext context)
    {
        var now = context.SimTime;
        Acknowledge(context);
        if (!OpenIncident(context, now) || _incident is not { } incident) return;

        Chemicals(context, now, incident);
        Approvals(context, now, incident);
        Hospitals(context, now, incident);
        Collapse(context, now, incident);
        Maydays(context, now, incident);
        Pars(context, now, incident);
        Crews(context, now);
        Comms(context, now);

        if (now - _openedAt >= TimeSpan.FromMinutes(8) && _done.Add("more"))
        {
            Emit(context, new ResourceRequested(Guid.NewGuid(), incident, "2 more engines and an ambulance", 2, null,
                ResourceRequestKind.AdditionalResources, UnitType.Engine, "Large fire, possible chemical involvement"));
            Emit(context, new ResourceRequested(Guid.NewGuid(), incident, "1 more ambulance", 1, null,
                ResourceRequestKind.AdditionalResources, UnitType.AmbulanceAls, "Multiple casualties"));
        }
    }

    private static void Emit(SimulationContext context, DomainEvent payload) => context.EmitPerceived(payload, EventSources.C2);

    /// <summary>Does something once, the first time its condition holds and the reaction time has passed.</summary>
    private bool Once(string key, DateTimeOffset since, DateTimeOffset now) => now - since >= ReactionTime && _done.Add(key);

    private void Acknowledge(SimulationContext context)
    {
        foreach (var alert in cop.Alerts.Where(a => a.AcknowledgedAt is null && context.SimTime - a.RaisedAt >= TimeSpan.FromSeconds(15)))
            if (_done.Add($"ack:{alert.Id}")) Emit(context, new AlertAcknowledged(alert.Id, null));
    }

    // ---- Incident and first attendance ----

    private bool OpenIncident(SimulationContext context, DateTimeOffset now)
    {
        if (_incident is not null) return cop.FindIncident(_incident.Value) is not null;
        // Open on a well-located report, or two minutes after the first one at the latest: crews drive to where the
        // incident is plotted, so a vague first fix (±150 m) can park them in the wrong street.
        var located = cop.Reports.Where(r => r.Location is not null && r.Source != ReportSource.Media).OrderBy(r => r.ReceivedAt).ToList();
        if (located.Count == 0) return false;
        var precise = located.Any(r => r.LocationAccuracyMeters is <= 60);
        if (now - located[0].ReceivedAt < (precise ? TimeSpan.FromSeconds(45) : TimeSpan.FromMinutes(2))) return false;

        var best = located.MinBy(r => r.LocationAccuracyMeters ?? double.MaxValue)!;
        _location = GeoPoint.FromPoint(best.Location!);
        _incident = Guid.NewGuid();
        _openedAt = now;
        Emit(context, new IncidentCreated(_incident.Value, "INC-00001", IncidentType.StructureFire, IncidentPriority.High, _location,
            null, "Recommended response"));
        foreach (var report in located)
            Emit(context, new ReportLinked(report.Id, _incident.Value));

        // A full first attendance, nearest first.
        var available = cop.Units.Where(u => u.Status == UnitStatus.Available && u.Agency?.AiControlled != true && u.Location is not null)
            .OrderBy(u => GeoMath.DistanceMeters(GeoPoint.FromPoint(u.Location!), _location)).ToList();
        var send = available.Where(u => u.Type == UnitType.Engine).Take(2)
            .Concat(available.Where(u => u.Type == UnitType.Ladder).Take(1))
            .Concat(available.Where(u => u.Type is UnitType.AmbulanceAls or UnitType.AmbulanceBls).OrderBy(u => u.Type).Take(2))
            .Concat(available.Where(u => u.Type is UnitType.Patrol).Take(1));
        foreach (var unit in send)
            Emit(context, new UnitDispatched(unit.Id, _incident.Value, null));
        return false; // the COP has the incident next tick
    }

    private void Dispatch(SimulationContext context, Guid incident, Func<Unit, bool> which, int count)
    {
        foreach (var unit in cop.Units.Where(u => u.Status == UnitStatus.Available && u.Agency?.AiControlled != true && which(u)).Take(count))
            Emit(context, new UnitDispatched(unit.Id, incident, null));
    }

    // ---- Hazards and liaison ----

    private void Chemicals(SimulationContext context, DateTimeOffset now, Guid incident)
    {
        var report = cop.Reports.FirstOrDefault(r => AarAnalyzer.IsChemicalReport(r.Claim));
        if (report is null || !Once("chemicals", report.ReceivedAt, now)) return;
        Dispatch(context, incident, u => u.Type == UnitType.Hazmat, 1);
        foreach (var (type, radius, name) in new[] { (ZoneType.ColdZone, 400.0, "Cold zone"), (ZoneType.WarmZone, 150.0, "Warm zone"), (ZoneType.HotZone, 60.0, "Hot zone") })
            Emit(context, new ZoneDeclared(Guid.NewGuid(), type, $"{name} ({radius:F0} m)", Wgs84.Circle(_location, radius), incident));
        Emit(context, new NotificationSent(Guid.NewGuid(), incident, "HSE Public Health", "Possible chlorine release at Barrow Street; advise residents downwind to stay indoors."));
    }

    private void Approvals(SimulationContext context, DateTimeOffset now, Guid incident)
    {
        foreach (var approval in cop.Approvals.Where(a => a.Status == ApprovalStatus.Pending))
        {
            if (!Once($"approval:{approval.Id}", approval.RequestedAt, now)) continue;
            Emit(context, new ApprovalDecided(approval.Id, true, "Approved"));
            if (approval.Subject.Contains("evacuat", StringComparison.OrdinalIgnoreCase))
            {
                Emit(context, new ZoneDeclared(Guid.NewGuid(), ZoneType.EvacuationZone, "Evacuation zone", Wgs84.Circle(_location, 250), incident));
                Emit(context, new NotificationSent(Guid.NewGuid(), incident, "Dublin City Council", "Evacuation under way: rest centre and buses needed."));
            }
        }
    }

    private void Hospitals(SimulationContext context, DateTimeOffset now, Guid incident)
    {
        var report = cop.Reports.FirstOrDefault(r => r.Claim.Contains("casualties", StringComparison.OrdinalIgnoreCase) || r.Claim.Contains("coughing"));
        if (report is null || !Once("hospitals", report.ReceivedAt, now)) return;
        foreach (var hospital in cop.Hospitals.OrderBy(h => GeoMath.DistanceMeters(h.Location, _location)).Take(2))
            Emit(context, new NotificationSent(Guid.NewGuid(), incident, hospital.Name, "Major incident at Barrow Street: expect multiple casualties, activate your major emergency plan."));
    }

    // ---- Safety ----

    private void Collapse(SimulationContext context, DateTimeOffset now, Guid incident)
    {
        var warning = cop.Reports.FirstOrDefault(r => r.Source == ReportSource.FieldUnit && AarAnalyzer.IsCollapseWarning(r.Claim));
        if (warning is not null && Once("evacuate", warning.ReceivedAt, now))
            Emit(context, new EvacuationSignalled(Guid.NewGuid(), incident));
    }

    private void Maydays(SimulationContext context, DateTimeOffset now, Guid incident)
    {
        foreach (var mayday in cop.Maydays.Where(m => m.Active))
        {
            if (!Once($"mayday:{mayday.Id}", mayday.DeclaredAt, now)) continue;
            if (cop.FindUnit(mayday.UnitId)?.Channel is { } channel && !cop.Channels.Any(c => c.Info.Id == channel && c.EmergencyTraffic))
                Emit(context, new EmergencyTrafficDeclared(channel, true));
            var rescuers = cop.Units.Where(u => u.Id != mayday.UnitId && u.Agency?.AiControlled != true && ResourceGroups.AgencyFor(u.Type) == AgencyType.Fire
                                                && u.Status is UnitStatus.OnScene or UnitStatus.Operating && u.Crew is { RescueFor: null } crew
                                                && crew.Holding(Qualifications.BreathingApparatus) >= 2)
                .OrderByDescending(u => u.Crew!.Holding(Qualifications.Usar)).FirstOrDefault();
            if (rescuers is not null)
                Emit(context, new RescueTeamDeployed(mayday.Id, rescuers.Id));
            else
                _done.Remove($"mayday:{mayday.Id}"); // nobody free yet: try again
            if (_done.Add($"mayday-par:{mayday.Id}"))
                Emit(context, new ParRequested(Guid.NewGuid(), incident, "PAR after Mayday"));
        }
    }

    private void Pars(SimulationContext context, DateTimeOffset now, Guid incident)
    {
        var working = cop.Units.Count(u => u.Status == UnitStatus.Operating && u.AssignedIncidentId == incident && ResourceGroups.AgencyFor(u.Type) == AgencyType.Fire);
        if (working < 2) return;
        var last = cop.ParChecks.Select(p => (DateTimeOffset?)p.RequestedAt).LastOrDefault() ?? _openedAt;
        if (now - last >= TimeSpan.FromMinutes(20) && _done.Add($"par:{now.Ticks}"))
            Emit(context, new ParRequested(Guid.NewGuid(), incident, "Routine PAR"));
    }

    private void Crews(SimulationContext context, DateTimeOffset now)
    {
        foreach (var unit in cop.Units.Where(u => u.Crew is not null && u.Agency?.AiControlled != true))
        {
            var crew = unit.Crew!;
            if (unit.Status == UnitStatus.Operating && crew.OnTask(now) >= TimeSpan.FromMinutes(40) && crew.RescueFor is null
                && now - _lastRehab >= TimeSpan.FromMinutes(2) && _done.Add($"rehab:{unit.Id}:{crew.LastRehabEnded?.Ticks}"))
            {
                _lastRehab = now;
                Emit(context, new CrewRehabOrdered(unit.Id));
            }
            if (UnitStatusRules.IsCommitted(unit.Status) && crew.PastShiftEnd(now) && _done.Add($"relief:{unit.Id}:{crew.ShiftEnd.Ticks}"))
                Emit(context, new CrewReliefRequested(Guid.NewGuid(), unit.Id, FullBriefing: true));
        }
    }

    private void Comms(SimulationContext context, DateTimeOffset now)
    {
        foreach (var missed in cop.MissedCalls.Where(m => m.CalledBackAt is null))
            if (Once($"callback:{missed.Id}", missed.At, now)) Emit(context, new CallbackMade(missed.Id));
        foreach (var message in cop.CommsLog.Where(m => !m.FromControl && m.Quality != CommsQuality.Clear && !m.RepeatRequested
                                                        && m.ChannelId is not (RadioPlan.Calls or RadioPlan.Chat) && now - m.At < TimeSpan.FromMinutes(1)))
            if (_done.Add($"repeat:{message.Id}")) Emit(context, new RepeatRequested(message.Id));
    }
}
