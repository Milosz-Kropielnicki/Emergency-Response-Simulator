using System.Text.Json;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;

namespace Emergency_Response_Simulator.Simulation.Services;

/// <summary>
/// The IAP workflow (Design Document §8): operational periods, versioned drafts, compliance-gated
/// submission and approval, and the briefing that pushes an approved plan's assignments out through C2.
/// Reads the COP; writes only events.
/// </summary>
public sealed class IapService(
    ICopService cop,
    IEventPublisher publisher,
    IC2Service c2,
    ISimulationControl clock,
    IRoutingService? routing = null,
    IGisService? gis = null) : IIapService
{
    public static readonly TimeSpan MinimumPeriod = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaximumPeriod = TimeSpan.FromHours(24);

    // ---- Operational periods ----

    public async Task<CommandResult> StartOperationalPeriodAsync(
        Guid incidentId, DateTimeOffset start, DateTimeOffset end, string? focus = null, CancellationToken cancellationToken = default)
    {
        if (cop.FindIncident(incidentId) is not { } incident)
            return CommandResult.Fail("Unknown incident.");
        if (incident.Status == IncidentStatus.Closed)
            return CommandResult.Fail($"{incident.Number} is closed.");
        if (end - start < MinimumPeriod)
            return CommandResult.Fail($"An operational period must last at least {MinimumPeriod.TotalMinutes:F0} minutes.");
        if (end - start > MaximumPeriod)
            return CommandResult.Fail($"An operational period cannot be longer than {MaximumPeriod.TotalHours:F0} hours.");

        var periods = cop.PeriodsOf(incidentId);
        if (periods.LastOrDefault() is { } last && start <= last.Start)
            return CommandResult.Fail($"Period {last.Number + 1} must start after period {last.Number} ({last.Start.ToLocalTime():HH:mm}).");

        var periodId = Guid.NewGuid();
        var result = await PublishAsync(new OperationalPeriodStarted(periodId, incidentId, periods.Count + 1, start, end,
            string.IsNullOrWhiteSpace(focus) ? null : focus.Trim()), cancellationToken);
        return result with { EntityId = periodId };
    }

    // ---- Drafting ----

    public async Task<CommandResult> CreateDraftAsync(Guid periodId, string? preparedBy = null, CancellationToken cancellationToken = default)
    {
        if (cop.OperationalPeriods.FirstOrDefault(p => p.Id == periodId) is not { } period)
            return CommandResult.Fail("Unknown operational period.");
        var versions = cop.VersionsOf(periodId);
        if (versions.FirstOrDefault(v => v.Status is IapStatus.Draft or IapStatus.PendingApproval) is { } open)
            return CommandResult.Fail($"Version {open.Version} is still {(open.Status == IapStatus.Draft ? "a draft" : "awaiting approval")}; finish it first.");

        IapContent content;
        Guid? basedOn;
        if (versions.LastOrDefault() is { } latest)
        {
            // A revision within the period: start from the latest version exactly as it was.
            content = latest.Content.Clone();
            basedOn = latest.Id;
        }
        else if (PreviousPlan(period) is { } previous)
        {
            // A new period: reassess. Achieved objectives drop out and the COP refreshes everything else.
            content = await PullFromCopAsync(period.IncidentId,
                IapReassessment.CarryForward(previous.Content, cop.ObjectiveProgress), IapPullParts.All, cancellationToken);
            basedOn = previous.Id;
        }
        else
        {
            content = await PullFromCopAsync(period.IncidentId, new IapContent(), IapPullParts.All, cancellationToken);
            basedOn = null;
        }

        var planId = Guid.NewGuid();
        var version = versions.Count == 0 ? 1 : versions.Max(v => v.Version) + 1;
        var result = await PublishAsync(new IapDraftCreated(planId, period.IncidentId, periodId, version, basedOn,
            string.IsNullOrWhiteSpace(preparedBy) ? null : preparedBy.Trim(), content), cancellationToken);
        return result with { EntityId = planId };
    }

    /// <summary>The plan in force for the latest earlier period that had one (or its latest version).</summary>
    private IncidentActionPlan? PreviousPlan(OperationalPeriod period)
    {
        foreach (var earlier in cop.PeriodsOf(period.IncidentId).Where(p => p.Number < period.Number).Reverse())
        {
            if (cop.ApprovedPlan(earlier.Id) is { } approved) return approved;
            if (cop.VersionsOf(earlier.Id).LastOrDefault() is { } latest) return latest;
        }
        return null;
    }

    public async Task<CommandResult> SaveDraftAsync(Guid planId, IapContent content, CancellationToken cancellationToken = default)
    {
        if (Find(planId) is not { } plan)
            return CommandResult.Fail("Unknown plan.");
        if (!plan.IsEditable)
            return CommandResult.Fail($"Version {plan.Version} is {DescribeStatus(plan.Status)}; start a new version to change it.");

        var tidy = Tidy(content.Clone());
        if (JsonSerializer.Serialize(tidy) == JsonSerializer.Serialize(plan.Content))
            return CommandResult.Fail("Nothing changed.");

        return await PublishAsync(new IapDraftSaved(planId, tidy), cancellationToken);
    }

    /// <summary>Trims text and drops blank rows the builder leaves behind.</summary>
    private IapContent Tidy(IapContent content)
    {
        static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        content.CommandersIntent = Clean(content.CommandersIntent);
        content.SituationSummary = Clean(content.SituationSummary);
        foreach (var strategic in content.Objectives)
        {
            strategic.Statement = strategic.Statement.Trim();
            foreach (var operational in strategic.Operational)
            {
                operational.Statement = operational.Statement.Trim();
                operational.Responsible = Clean(operational.Responsible);
                operational.ResourcesSummary = Clean(operational.ResourcesSummary);
                operational.PerformanceTarget = Clean(operational.PerformanceTarget);
                operational.Tactics.RemoveAll(t => string.IsNullOrWhiteSpace(t.Description));
                foreach (var tactic in operational.Tactics)
                    tactic.Description = tactic.Description.Trim();
            }
        }
        content.Organization.RemoveAll(p => string.IsNullOrWhiteSpace(p.Name));
        foreach (var position in content.Organization)
            position.Name = position.Name.Trim();
        content.Organization.Sort((a, b) => a.Role.CompareTo(b.Role));
        content.Groups.RemoveAll(g => string.IsNullOrWhiteSpace(g.Name));
        foreach (var group in content.Groups)
        {
            group.Name = group.Name.Trim();
            group.Supervisor = Clean(group.Supervisor);
        }
        foreach (var assignment in content.Assignments)
        {
            assignment.Assignment = assignment.Assignment.Trim();
            assignment.Group = Clean(assignment.Group);
            if (cop.FindUnit(assignment.UnitId) is { } unit)
                assignment.Callsign = unit.Callsign;
        }
        content.Communications.Channels.RemoveAll(c => string.IsNullOrWhiteSpace(c.Channel) && string.IsNullOrWhiteSpace(c.Function));
        content.Communications.Notes = Clean(content.Communications.Notes);
        content.Medical.Hospitals.RemoveAll(h => string.IsNullOrWhiteSpace(h.Name));
        content.Medical.CasualtyClearingStation = Clean(content.Medical.CasualtyClearingStation);
        content.Medical.AmbulanceLoadingPoint = Clean(content.Medical.AmbulanceLoadingPoint);
        content.Medical.MedicalLead = Clean(content.Medical.MedicalLead);
        content.Medical.EmergencyProcedures = Clean(content.Medical.EmergencyProcedures);
        content.Safety.Hazards.RemoveAll(h => string.IsNullOrWhiteSpace(h.Hazard));
        foreach (var hazard in content.Safety.Hazards)
        {
            hazard.Hazard = hazard.Hazard.Trim();
            hazard.Mitigation = Clean(hazard.Mitigation);
        }
        content.Safety.Ppe = content.Safety.Ppe.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
        content.Safety.Message = Clean(content.Safety.Message);
        content.Safety.Accountability = Clean(content.Safety.Accountability);
        return content;
    }

    // ---- Approval workflow ----

    public async Task<CommandResult> SubmitAsync(Guid planId, string submittedBy, CancellationToken cancellationToken = default)
    {
        if (Find(planId) is not { } plan)
            return CommandResult.Fail("Unknown plan.");
        if (plan.Status != IapStatus.Draft)
            return CommandResult.Fail($"Version {plan.Version} is {DescribeStatus(plan.Status)}.");
        if (string.IsNullOrWhiteSpace(submittedBy))
            return CommandResult.Fail("Say who is submitting the plan.");
        if (Blocking(plan) is { } blocked)
            return blocked;

        return await PublishAsync(new IapSubmitted(planId, submittedBy.Trim()), cancellationToken);
    }

    public async Task<CommandResult> ApproveAsync(Guid planId, string approvedBy, CancellationToken cancellationToken = default)
    {
        if (Find(planId) is not { } plan)
            return CommandResult.Fail("Unknown plan.");
        if (plan.Status != IapStatus.PendingApproval)
            return CommandResult.Fail("Only a submitted plan can be approved.");
        if (string.IsNullOrWhiteSpace(approvedBy))
            return CommandResult.Fail("Say who is approving the plan.");
        // The situation may have changed since submission; check again.
        if (Blocking(plan) is { } blocked)
            return blocked;

        return await PublishAsync(new IapApproved(planId, approvedBy.Trim()), cancellationToken);
    }

    public async Task<CommandResult> ReturnAsync(Guid planId, string returnedBy, string comments, CancellationToken cancellationToken = default)
    {
        if (Find(planId) is not { } plan)
            return CommandResult.Fail("Unknown plan.");
        if (plan.Status != IapStatus.PendingApproval)
            return CommandResult.Fail("Only a submitted plan can be returned.");
        if (string.IsNullOrWhiteSpace(comments))
            return CommandResult.Fail("Tell the planner what to change.");

        return await PublishAsync(new IapReturned(planId, string.IsNullOrWhiteSpace(returnedBy) ? "Incident Commander" : returnedBy.Trim(),
            comments.Trim()), cancellationToken);
    }

    private CommandResult? Blocking(IncidentActionPlan plan)
    {
        var errors = IapCompliance.Check(plan.Content, plan.OperationalPeriod, cop.FindIncident(plan.IncidentId), cop.FindUnit)
            .Where(i => i.Severity == IssueSeverity.Error).ToList();
        return errors.Count == 0
            ? null
            : CommandResult.Fail($"{errors.Count} compliance error(s) to fix first. {errors[0].Message}");
    }

    // ---- Briefing: push the plan to the COP ----

    public async Task<CommandResult> BriefAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        if (Find(planId) is not { } plan)
            return CommandResult.Fail("Unknown plan.");
        if (plan.Status != IapStatus.Approved)
            return CommandResult.Fail("Only the approved plan can be briefed.");
        if (cop.FindIncident(plan.IncidentId) is not { } incident)
            return CommandResult.Fail("Unknown incident.");

        var content = plan.Content;
        int staffed = 0, formed = 0, dispatched = 0, placed = 0, ordered = 0, disbanded = 0;
        var skipped = new List<string>();

        foreach (var position in content.Organization.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
        {
            if (incident.Command.Positions.GetValueOrDefault(position.Role) != position.Name.Trim()
                && (await c2.AssignIcsPositionAsync(incident.Id, position.Role, position.Name, cancellationToken)).Succeeded)
                staffed++;
        }

        foreach (var group in content.Groups.Where(g => FindGroup(incident, g.Name) is null))
        {
            if ((await c2.FormGroupAsync(incident.Id, group.Name, group.Kind, group.Supervisor, cancellationToken)).Succeeded)
                formed++;
        }

        // Only units whose assignment changed since the last briefing get a new order.
        var lastBriefed = cop.ActionPlans
            .Where(p => p.IncidentId == incident.Id && p.Id != plan.Id && p.BriefedAt is not null)
            .MaxBy(p => p.BriefedAt);
        var periodLabel = plan.OperationalPeriod is { } period ? $"IAP period {period.Number}" : "IAP";

        foreach (var assignment in content.Assignments)
        {
            if (cop.FindUnit(assignment.UnitId) is not { } unit)
            {
                skipped.Add($"{assignment.Callsign} (not on the roster)");
                continue;
            }

            var newlyDispatched = false;
            if (unit.AssignedIncidentId != incident.Id)
            {
                if (unit.Status != UnitStatus.Available)
                {
                    skipped.Add(unit.Status == UnitStatus.OutOfService
                        ? $"{unit.Callsign} (out of service)"
                        : $"{unit.Callsign} (committed to {unit.AssignedIncident?.Number ?? "another task"})");
                    continue;
                }
                if (!(await c2.DispatchAsync(unit.Id, incident.Id, cancellationToken: cancellationToken)).Succeeded)
                {
                    skipped.Add($"{unit.Callsign} (could not be dispatched)");
                    continue;
                }
                dispatched++;
                newlyDispatched = true;
            }

            var group = assignment.Group is { Length: > 0 } name ? FindGroup(incident, name) : null;
            if (incident.Command.GroupOf(unit.Id)?.Id != group?.Id
                && (await c2.AssignUnitToGroupAsync(unit.Id, group?.Id, cancellationToken)).Succeeded)
                placed++;

            var before = lastBriefed?.Content.Assignments.FirstOrDefault(a => a.UnitId == unit.Id);
            var changed = newlyDispatched || before is null
                          || !string.Equals(before.Assignment.Trim(), assignment.Assignment.Trim(), StringComparison.OrdinalIgnoreCase)
                          || !string.Equals(before.Group ?? "", assignment.Group ?? "", StringComparison.OrdinalIgnoreCase);
            if (!changed || string.IsNullOrWhiteSpace(assignment.Assignment)) continue;

            var text = $"{periodLabel}: {assignment.Assignment}" + (group is not null ? $", under {group.Name}" : "");
            if ((await c2.IssueOrderAsync(incident.Id, OrderTargetKind.Unit, unit.Id, text, cancellationToken: cancellationToken)).Succeeded)
                ordered++;
        }

        // Groups the plan no longer has are stood down once nobody is left in them.
        var committed = incident.AssignedUnits.Select(u => u.Id).ToHashSet();
        foreach (var group in incident.Command.Groups.ToList())
        {
            if (content.Groups.Any(g => SameName(g.Name, group.Name)) || group.UnitIds.Any(committed.Contains)) continue;
            if ((await c2.DisbandGroupAsync(incident.Id, group.Id, cancellationToken)).Succeeded)
                disbanded++;
        }

        var result = await PublishAsync(new IapBriefed(plan.Id, ordered), cancellationToken);

        var done = new List<string> { $"{ordered} order(s) issued" };
        if (staffed > 0) done.Add($"{staffed} position(s) staffed");
        if (formed > 0) done.Add($"{formed} group(s) formed");
        if (disbanded > 0) done.Add($"{disbanded} group(s) stood down");
        if (dispatched > 0) done.Add($"{dispatched} unit(s) dispatched");
        if (placed > 0) done.Add($"{placed} unit(s) placed in groups");
        var detail = "Briefed: " + string.Join(", ", done) + (skipped.Count > 0 ? $". Skipped {string.Join(", ", skipped)}." : ".");
        return result with { Detail = detail };
    }

    private static IcsGroup? FindGroup(Incident incident, string name) =>
        incident.Command.Groups.FirstOrDefault(g => SameName(g.Name, name));

    private static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // ---- Objective tracking ----

    public async Task<CommandResult> SetObjectiveStatusAsync(
        Guid incidentId, Guid objectiveId, ObjectiveStatus status, CancellationToken cancellationToken = default)
    {
        if (cop.FindIncident(incidentId) is null)
            return CommandResult.Fail("Unknown incident.");
        if (!cop.ActionPlans.Any(p => p.IncidentId == incidentId && p.Content.OperationalObjectives.Any(o => o.Id == objectiveId)))
            return CommandResult.Fail("Unknown objective.");
        if (cop.ObjectiveProgress.GetValueOrDefault(objectiveId) == status)
            return CommandResult.Fail("Nothing changed.");

        return await PublishAsync(new ObjectiveStatusChanged(incidentId, objectiveId, status), cancellationToken);
    }

    // ---- Pull the current state from the COP ----

    public async Task<IapContent> PullFromCopAsync(
        Guid incidentId, IapContent content, IapPullParts parts = IapPullParts.All, CancellationToken cancellationToken = default)
    {
        var incident = cop.FindIncident(incidentId) ?? throw new ArgumentException("Unknown incident.", nameof(incidentId));
        var plan = content.Clone();

        if (parts.HasFlag(IapPullParts.Situation))
            plan.SituationSummary = Situation(incident);
        if (parts.HasFlag(IapPullParts.Organisation))
            PullOrganisation(incident, plan);
        if (parts.HasFlag(IapPullParts.Assignments))
            PullAssignments(incident, plan);
        if (parts.HasFlag(IapPullParts.Communications))
            PullCommunications(incident, plan);
        if (parts.HasFlag(IapPullParts.Medical))
            await PullMedicalAsync(incident, plan, cancellationToken);
        if (parts.HasFlag(IapPullParts.Safety))
            PullSafety(incident, plan);
        return plan;
    }

    private string Situation(Incident incident)
    {
        var lines = new List<string>
        {
            $"As of {clock.SimTime.ToLocalTime():HH:mm}: {incident.Name ?? EventDescriber.Humanize(incident.Type)} at " +
            $"{incident.Address ?? $"{incident.Location.Y:F4}, {incident.Location.X:F4}"}. " +
            $"Priority {incident.Priority}, {EventDescriber.Humanize(incident.Status).ToLowerInvariant()}.",
        };
        if (incident.Threats.Count > 0)
            lines.Add($"Threats: {string.Join(", ", incident.Threats)}.");
        if (incident.CasualtiesReported > 0 || incident.CasualtiesConfirmed > 0)
            lines.Add($"Casualties: {incident.CasualtiesReported} reported, {incident.CasualtiesConfirmed} confirmed.");
        if (incident.EvacuationRequired)
            lines.Add("Evacuation required.");

        var units = incident.AssignedUnits;
        if (units.Count > 0)
        {
            var byStatus = units.GroupBy(u => u.Status).Select(g => $"{g.Count()} {EventDescriber.Humanize(g.Key).ToLowerInvariant()}");
            lines.Add($"Resources committed: {units.Count} ({string.Join(", ", byStatus)}).");
        }
        var zones = cop.Zones.Where(z => z.IncidentId == incident.Id).Select(z => z.Name).ToList();
        if (zones.Count > 0)
            lines.Add($"Zones in force: {string.Join("; ", zones)}.");
        if (cop.Weather is { } weather)
            lines.Add($"Wind from the {GeoMath.CompassPoint(weather.WindFromDegrees)} at {weather.WindSpeedMps:F0} m/s ({weather.Source}).");
        return string.Join(" ", lines);
    }

    private static void PullOrganisation(Incident incident, IapContent plan)
    {
        foreach (var (role, name) in incident.Command.Positions)
        {
            var position = plan.Organization.FirstOrDefault(p => p.Role == role);
            if (position is null)
                plan.Organization.Add(new IapPosition { Role = role, Name = name });
            else if (string.IsNullOrWhiteSpace(position.Name))
                position.Name = name;
        }
        plan.Organization.Sort((a, b) => a.Role.CompareTo(b.Role));

        foreach (var group in incident.Command.Groups)
            AddGroup(plan, group.Name, group.Kind, group.Supervisor);
    }

    private static void AddGroup(IapContent plan, string name, IcsGroupKind kind, string? supervisor)
    {
        var existing = plan.Groups.FirstOrDefault(g => SameName(g.Name, name));
        if (existing is null)
            plan.Groups.Add(new IapGroup { Name = name, Kind = kind, Supervisor = supervisor });
        else if (string.IsNullOrWhiteSpace(existing.Supervisor))
            existing.Supervisor = supervisor;
    }

    private void PullAssignments(Incident incident, IapContent plan)
    {
        foreach (var assignment in plan.Assignments)
        {
            if (cop.FindUnit(assignment.UnitId) is { } known)
                assignment.Callsign = known.Callsign;
        }

        foreach (var unit in incident.AssignedUnits.OrderBy(u => u.Callsign, StringComparer.Ordinal))
        {
            if (plan.Assignments.Any(a => a.UnitId == unit.Id)) continue;
            var group = incident.Command.GroupOf(unit.Id);
            if (group is not null)
                AddGroup(plan, group.Name, group.Kind, group.Supervisor);
            plan.Assignments.Add(new IapAssignment
            {
                UnitId = unit.Id,
                Callsign = unit.Callsign,
                Group = group?.Name,
                Assignment = CurrentTask(incident, unit) ?? "",
            });
        }
    }

    /// <summary>What the unit was last told to do at this incident, as a starting assignment.</summary>
    private string? CurrentTask(Incident incident, Unit unit)
    {
        var order = cop.Orders
            .Where(o => o.TargetKind == OrderTargetKind.Unit && o.TargetId == unit.Id && o.IncidentId == incident.Id
                        && o.Status != OrderStatus.Cancelled)
            .MaxBy(o => o.IssuedAt);
        if (order is null) return null;
        var text = order.Text;
        if (text.StartsWith("IAP", StringComparison.Ordinal) && text.IndexOf(": ", StringComparison.Ordinal) is var colon and > 0)
            text = text[(colon + 2)..];
        var under = text.LastIndexOf(", under ", StringComparison.Ordinal);
        return under > 0 ? text[..under] : text;
    }

    private static void PullCommunications(Incident incident, IapContent plan)
    {
        var channels = plan.Communications.Channels;
        if (channels.Count == 0)
        {
            var agencies = incident.AssignedUnits.Select(u => u.Agency?.Type ?? AgencyFor(u.Type)).Distinct();
            plan.Communications.Channels = IapTemplates.DefaultChannels(agencies, plan.Groups.Select(g => g.Name));
            return;
        }

        var next = channels.Select(c => c.Channel.StartsWith("FIRE TAC ", StringComparison.Ordinal)
                && int.TryParse(c.Channel["FIRE TAC ".Length..], out var n) ? n : 2).DefaultIfEmpty(2).Max() + 1;
        foreach (var group in plan.Groups.Where(g => !channels.Any(c => c.AssignedTo.Contains(g.Name, StringComparison.OrdinalIgnoreCase))))
            channels.Add(new RadioChannel { Function = "Tactical", Channel = $"FIRE TAC {next++}", AssignedTo = group.Name });
    }

    private static AgencyType AgencyFor(UnitType type) => ResourceGroups.AgencyFor(type);

    private async Task PullMedicalAsync(Incident incident, IapContent plan, CancellationToken cancellationToken)
    {
        var medical = plan.Medical;
        var origin = GeoPoint.FromPoint(incident.Location);

        if (gis is not null)
        {
            IReadOnlyList<NearestFeature> nearest;
            try
            {
                nearest = await gis.FindNearestAsync(origin, [GisLayerKeys.Hospitals], 25, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                nearest = []; // GIS database unavailable: the planner adds hospitals by hand
            }

            // Hospitals known to have an emergency department first, then other acute hospitals.
            var candidates = nearest
                .Where(f => !string.IsNullOrWhiteSpace(f.Feature.Name) && IapTemplates.IsReceivingHospital(f.Feature.Name))
                .DistinctBy(f => f.Feature.Name)
                .OrderBy(f => IapTemplates.HospitalCapabilities(f.Feature.Name!) is null ? 1 : 0)
                .ThenBy(f => f.DistanceMeters)
                .Take(3);
            foreach (var found in candidates)
            {
                var name = found.Feature.Name!;
                var target = GeoPoint.FromPoint(found.Feature.Geometry.InteriorPoint);
                var route = routing?.IsReady == true
                    ? routing.Route(origin, target, new RouteOptions(UnitType.AmbulanceAls, clock.SimTime, Emergency: true))
                    : null;
                var minutes = route?.Duration.TotalMinutes ?? found.DistanceMeters * 1.3 / 1000 / 40 * 60;
                var kilometres = (route?.DistanceMeters ?? found.DistanceMeters * 1.3) / 1000;

                var hospital = medical.Hospitals.FirstOrDefault(h => SameName(h.Name, name));
                if (hospital is null)
                    medical.Hospitals.Add(hospital = new ReceivingHospital { Name = name });
                hospital.TravelMinutes = Math.Round(minutes, 1);
                hospital.DistanceKm = Math.Round(kilometres, 1);
                hospital.Capabilities ??= IapTemplates.HospitalCapabilities(name);
            }
            medical.Hospitals.Sort((a, b) => (a.TravelMinutes ?? double.MaxValue).CompareTo(b.TravelMinutes ?? double.MaxValue));
        }

        if (string.IsNullOrWhiteSpace(medical.MedicalLead)
            && plan.Groups.FirstOrDefault(g => g.Name.Contains("medical", StringComparison.OrdinalIgnoreCase)) is { } medicalGroup)
            medical.MedicalLead = $"{medicalGroup.Name}" + (medicalGroup.Supervisor is { } s ? $" ({s})" : "");

        if (string.IsNullOrWhiteSpace(medical.CasualtyClearingStation)
            && cop.Zones.FirstOrDefault(z => z.IncidentId == incident.Id && z.Type == ZoneType.StagingArea) is { } staging)
            medical.CasualtyClearingStation = $"Beside {staging.Name}";

        if (string.IsNullOrWhiteSpace(medical.EmergencyProcedures))
        {
            var command = plan.Communications.Channels.FirstOrDefault(c => c.Function.Contains("command", StringComparison.OrdinalIgnoreCase))?.Channel
                          ?? "the command channel";
            var nearestHospital = medical.Hospitals.FirstOrDefault()?.Name ?? "the nearest emergency department";
            medical.EmergencyProcedures = $"MAYDAY on {command}; the rapid intervention crew deploys; injured responders " +
                                          $"to the casualty clearing station, then {nearestHospital}.";
        }
    }

    private void PullSafety(Incident incident, IapContent plan)
    {
        var safety = plan.Safety;
        foreach (var threat in incident.Threats.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            if (safety.Hazards.Any(h => h.Hazard.Contains(threat, StringComparison.OrdinalIgnoreCase))) continue;
            safety.Hazards.Add(new SafetyHazard { Hazard = threat, Mitigation = IapTemplates.MitigationFor(threat) });
        }
        foreach (var zone in cop.Zones.Where(z => z.IncidentId == incident.Id && z.Type is ZoneType.HotZone or ZoneType.FireExclusion
                     or ZoneType.PlumeHigh or ZoneType.PlumeModerate))
        {
            var hazard = $"{EventDescriber.Humanize(zone.Type)}: {zone.Name}";
            if (safety.Hazards.Any(h => SameName(h.Hazard, hazard))) continue;
            safety.Hazards.Add(new SafetyHazard { Hazard = hazard, Mitigation = "No entry without BA, entry control and a briefed crew" });
        }

        if (safety.Ppe.Count == 0)
        {
            if (incident.Type is IncidentType.StructureFire or IncidentType.WildlandFire or IncidentType.Explosion
                || incident.Threats.Any(t => t.Contains("fire", StringComparison.OrdinalIgnoreCase) || t.Contains("smoke", StringComparison.OrdinalIgnoreCase)))
                safety.Ppe.Add("Full structural fire kit and BA in the hot zone");
            if (incident.Type == IncidentType.HazmatRelease
                || incident.Threats.Any(t => t.Contains("chemical", StringComparison.OrdinalIgnoreCase) || t.Contains("gas", StringComparison.OrdinalIgnoreCase)))
            {
                safety.Ppe.Add("Chemical protective suits for hazmat entry");
                safety.Ppe.Add("Gas monitors with entry teams");
            }
            safety.Ppe.Add("Hi-vis on all roads");
        }

        if (string.IsNullOrWhiteSpace(safety.Message))
        {
            var wind = cop.Weather is { } weather
                ? $"Wind from the {GeoMath.CompassPoint(weather.WindFromDegrees)}: approach and stage upwind. "
                : "";
            safety.Message = wind + "Report to entry control before crossing the inner cordon. Stay with your crew. " +
                             "Anyone in difficulty: MAYDAY on the command channel.";
        }
        if (string.IsNullOrWhiteSpace(safety.Accountability))
            safety.Accountability = "Entry control at the inner cordon; tally system for every crew";
    }

    // ---- Helpers ----

    private IncidentActionPlan? Find(Guid planId) => cop.ActionPlans.FirstOrDefault(p => p.Id == planId);

    private static string DescribeStatus(IapStatus status) => status switch
    {
        IapStatus.PendingApproval => "awaiting approval",
        IapStatus.Approved => "approved",
        IapStatus.Superseded => "superseded",
        _ => "a draft",
    };

    private async Task<CommandResult> PublishAsync(DomainEvent payload, CancellationToken cancellationToken)
    {
        var appended = await publisher.PublishAsync(payload, EventVisibility.Perceived, EventSources.Iap, cancellationToken);
        return CommandResult.Ok(appended.Sequence);
    }
}
