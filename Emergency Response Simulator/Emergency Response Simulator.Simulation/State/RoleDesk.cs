using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.State;

public enum DeskUrgency
{
    Routine,

    /// <summary>Waiting longer than it should.</summary>
    Overdue,

    /// <summary>Life safety, or the clock is about to run out.</summary>
    Urgent,
}

/// <summary>Something waiting for a role to act on, as its desk lists it.</summary>
/// <param name="Function">The function it belongs to, as the permission matrix names it.</param>
/// <param name="Since">When it started waiting (or, for something due, when it falls due).</param>
public sealed record DeskItem(string Function, string Text, DateTimeOffset Since, DeskUrgency Urgency, Guid? IncidentId = null, Guid? EntityId = null)
{
    public TimeSpan Age(DateTimeOffset now) => now > Since ? now - Since : TimeSpan.Zero;
}

/// <summary>
/// A role's desk (Phase 12): what is waiting for that role to act on now, read from the COP. Where
/// <see cref="RoleBriefing"/> lists a role's open business for a handover, the desk lists only what needs its holder's
/// decision, oldest and most urgent first: approvals to decide, read-backs to confirm, journalists and politicians to
/// answer, Maydays, disputes, a situation report falling due. Each item belongs to a function the role holds now (its
/// own, or one that fell to the IC), so the same rules decide who sees it as decide who may act on it.
/// The instructor's player monitor counts each player's desk, so an overloaded player shows before they fall behind.
/// </summary>
public static class RoleDesk
{
    /// <summary>How long most things may wait before they show as overdue.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromMinutes(3);

    /// <param name="me">The participant whose desk this is, for an agency chief's own agency.</param>
    public static IReadOnlyList<DeskItem> For(UserRole role, ICopService cop, Func<UserRole, bool> filled, Guid? me = null)
    {
        var now = cop.AsOf;
        bool Can<T>(string method) => PermissionMatrix.Decide<T>(role, method, filled).Allowed;
        string Function<T>(string method) => PermissionMatrix.Find(typeof(T), method)?.Function ?? method;
        DeskUrgency After(DateTimeOffset since, TimeSpan patience) => now - since >= patience ? DeskUrgency.Overdue : DeskUrgency.Routine;
        string Incident(Guid? id) => id is { } i && cop.Incidents.FirstOrDefault(x => x.Id == i) is { } incident ? $" ({incident.Number})" : "";
        var items = new List<DeskItem>();

        // Responder safety first: a Mayday waits for nobody.
        if (Can<IC2Service>(nameof(IC2Service.DeployRescueTeamAsync)))
            foreach (var mayday in cop.Maydays.Where(m => m.Active && m.RescueUnitId is null))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.DeployRescueTeamAsync)),
                    $"MAYDAY {mayday.Callsign}{(mayday.Member is { } who ? $" ({who})" : "")}: send a rescue team", mayday.DeclaredAt, DeskUrgency.Urgent,
                    mayday.IncidentId, mayday.Id));

        if (Can<IC2Service>(nameof(IC2Service.DecideApprovalAsync)))
            foreach (var approval in cop.Approvals.Where(a => a.Status == ApprovalStatus.Pending))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.DecideApprovalAsync)), $"Decide: {approval.Subject} — {approval.RequestedBy}{Incident(approval.IncidentId)}",
                    approval.RequestedAt, After(approval.RequestedAt, Patience), approval.IncidentId, approval.Id));

        if (Can<IIapService>(nameof(IIapService.ApproveAsync)))
            foreach (var plan in cop.ActionPlans.Where(p => p.Status == IapStatus.PendingApproval))
                items.Add(new(Function<IIapService>(nameof(IIapService.ApproveAsync)), $"Approve or return IAP v{plan.Version}{Incident(plan.IncidentId)}",
                    plan.SubmittedAt ?? plan.LastSavedAt, After(plan.SubmittedAt ?? plan.LastSavedAt, TimeSpan.FromMinutes(5)), plan.IncidentId, plan.Id));

        if (Can<IC2Service>(nameof(IC2Service.ConfirmReadBackAsync)))
            foreach (var order in cop.Orders.Where(o => o.ReadBack is not null && o.ReadBackConfirmedAt is null
                                                         && o.Status is not (OrderStatus.Cancelled or OrderStatus.Completed or OrderStatus.Declined)))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.ConfirmReadBackAsync)),
                    $"Confirm {order.TargetName}'s read-back{(order.ReadBackGarbled ? " (garbled: check it)" : "")}: \"{order.ReadBack}\"",
                    order.AcknowledgedAt ?? order.IssuedAt, order.ReadBackGarbled ? DeskUrgency.Overdue : After(order.AcknowledgedAt ?? order.IssuedAt, Patience),
                    order.IncidentId, order.Id));

        if (Can<IC2Service>(nameof(IC2Service.DispatchAsync)))
            foreach (var incident in cop.Incidents.Where(i => i.Status != IncidentStatus.Closed && !cop.Units.Any(u => u.AssignedIncidentId == i.Id)))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.DispatchAsync)), $"Nobody is assigned to {incident.Number} {incident.Type} ({incident.Priority})",
                    incident.ReportedAt, incident.Priority >= IncidentPriority.High ? DeskUrgency.Urgent : After(incident.ReportedAt, TimeSpan.FromMinutes(2)),
                    incident.Id, incident.Id));

        // A release nobody has projected (Phase 13): until someone does, nobody knows who is downwind.
        if (Can<IC2Service>(nameof(IC2Service.PredictPlumeAsync)))
            foreach (var incident in cop.Incidents.Where(i => i.Status != IncidentStatus.Closed && i.Type == IncidentType.HazmatRelease
                                                              && !cop.PlumeProjections.Any(p => p.IncidentId == i.Id)))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.PredictPlumeAsync)), $"No plume projection yet for {incident.Number}: who is downwind?",
                    incident.ReportedAt, now - incident.ReportedAt >= TimeSpan.FromMinutes(10) ? DeskUrgency.Urgent : After(incident.ReportedAt, TimeSpan.FromMinutes(4)),
                    incident.Id, incident.Id));

        if (Can<IC2Service>(nameof(IC2Service.CallBackAsync)))
            foreach (var missed in cop.MissedCalls.Where(c => c.CalledBackAt is null))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.CallBackAsync)), "A 999 caller hung up waiting: call them back", missed.At,
                    After(missed.At, TimeSpan.FromMinutes(2)), EntityId: missed.Id));

        if (Can<IC2Service>(nameof(IC2Service.IssuePublicStatementAsync)))
            foreach (var enquiry in cop.MediaEnquiries.Where(m => m.AnsweredAt is null))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.IssuePublicStatementAsync)), $"{enquiry.Outlet} asks: {enquiry.Question}",
                    enquiry.ReceivedAt, After(enquiry.ReceivedAt, TimeSpan.FromMinutes(8)), enquiry.IncidentId, enquiry.Id));

        // ---- Phase 14: the public ----
        var pub = cop.Public;
        if (Can<IC2Service>(nameof(IC2Service.RebutRumourAsync)))
            foreach (var rumour in pub.Rumours.Where(r => r.Spreading))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.RebutRumourAsync)),
                    $"Rumour (~{rumour.ReachThousands}k): \"{rumour.Claim}\" — check it, then answer it", rumour.FirstSeenAt,
                    rumour.ReachThousands >= 100 ? DeskUrgency.Urgent : rumour.ReachThousands >= 20 ? DeskUrgency.Overdue : DeskUrgency.Routine,
                    rumour.IncidentId, rumour.Id));

        if (Can<IC2Service>(nameof(IC2Service.ManageVolunteersAsync)))
            foreach (var group in pub.Volunteers.Where(v => v.Waiting))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.ManageVolunteersAsync)),
                    $"{group.Count} volunteers at the cordon: register them or turn them away", group.ArrivedAt,
                    group.SelfDeployed > 0 ? DeskUrgency.Urgent : After(group.ArrivedAt, TimeSpan.FromMinutes(5)), group.IncidentId));

        if (Can<IC2Service>(nameof(IC2Service.RequestAssistedEvacuationAsync)))
            foreach (var report in pub.Vulnerable.Where(v => v.AssistanceRequestedAt is null && v.MobilityImpaired + v.Homeless > 0)
                         .Where(v => cop.Zones.Any(z => z.Id == v.ZoneId && z.Type == ZoneType.EvacuationZone))
                         .GroupBy(v => v.ZoneId).Select(g => g.MaxBy(v => v.MobilityImpaired + v.Homeless)!))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.RequestAssistedEvacuationAsync)),
                    $"{report.MobilityImpaired} can't leave unaided" + (report.Homeless > 0 ? $", {report.Homeless} sleeping rough" : "") +
                    $" in {cop.Zones.First(z => z.Id == report.ZoneId).Name}: arrange an assisted evacuation", report.ReportedAt, DeskUrgency.Urgent, EntityId: report.ZoneId));

        if (Can<IC2Service>(nameof(IC2Service.OpenFamilyAssistanceCentreAsync)) && pub.AssistanceCentres.Count == 0 && pub.MissingPersons.Count(m => m.Waiting) is > 0 and var waitingFamilies)
        {
            var since = pub.MissingPersons.Where(m => m.Waiting).Min(m => m.ReceivedAt);
            items.Add(new(Function<IC2Service>(nameof(IC2Service.OpenFamilyAssistanceCentreAsync)),
                $"{waitingFamilies} famil{(waitingFamilies == 1 ? "y" : "ies")} waiting for news: open a family assistance centre", since,
                After(since, TimeSpan.FromMinutes(15))));
        }

        // ---- Phase 15: keeping the operation going ----
        var logistics = cop.Logistics;
        string Callsign(Guid id) => cop.FindUnit(id)?.Callsign ?? "A unit";
        if (Can<IC2Service>(nameof(IC2Service.OrderSuppliesAsync)))
            foreach (var (incidentId, supplies) in logistics.Incidents)
                foreach (var stock in supplies.Stocks.Where(s => s.LowSince is not null || s.OutSince is not null))
                {
                    // Something already on its way answers the warning.
                    if (logistics.Orders.Any(o => o.IncidentId == incidentId && o.Kind == stock.Kind && o.Open && o.RequestedAt >= (stock.LowSince ?? stock.OutSince)))
                        continue;
                    if (logistics.Contracts.Any(c => c.IncidentId == incidentId && Covers(c.Kind, stock.Kind))) continue;
                    var outNow = stock.OutSince is not null;
                    items.Add(new(Function<IC2Service>(nameof(IC2Service.OrderSuppliesAsync)),
                        outNow ? $"{Supplies.Name(stock.Kind)} has run out{Incident(incidentId)}: order more"
                               : $"{Supplies.Name(stock.Kind)} low{Incident(incidentId)}: {Supplies.Quantity(stock.Kind, stock.OnHand)}" +
                                 (stock.HoursLeft is { } left ? $", about {Math.Max(1, (int)(left * 60))} min left" : "") + " — order more",
                        (stock.OutSince ?? stock.LowSince)!.Value, outNow ? DeskUrgency.Urgent : DeskUrgency.Overdue, incidentId));
                }

        if (Can<IC2Service>(nameof(IC2Service.RequestRepairAsync)))
            foreach (var broken in logistics.Fleet.Values.Where(f => f.Immobilised && !f.UnderRepair))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.RequestRepairAsync)),
                    $"{Callsign(broken.UnitId)} broken down ({broken.Defect}): {(broken.FieldRepairable ? "send a mechanic" : "recover it to the workshop")}",
                    broken.DefectReportedAt ?? now, After(broken.DefectReportedAt ?? now, Patience), EntityId: broken.UnitId));

        if (Can<IC2Service>(nameof(IC2Service.ServiceUnitAsync)))
            foreach (var worn in logistics.Fleet.Values.Where(f => f is { Defect: not null, Immobilised: false, UnderRepair: false }))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.ServiceUnitAsync)),
                    $"{Callsign(worn.UnitId)}: {worn.Defect} — service it before it fails", worn.DefectReportedAt ?? now,
                    After(worn.DefectReportedAt ?? now, TimeSpan.FromMinutes(10)), EntityId: worn.UnitId));

        if (Can<IC2Service>(nameof(IC2Service.OrderSuppliesAsync)))
            foreach (var dry in logistics.Fleet.Values.Where(f => f.Fuel is <= 0.1 && !f.UnderRepair))
            {
                var at = cop.FindUnit(dry.UnitId)?.AssignedIncidentId;
                if (at is { } incidentId && logistics.Incidents.GetValueOrDefault(incidentId)?.Stock(SupplyKind.Fuel) is { OnHand: > 0 }) continue;
                items.Add(new(Function<IC2Service>(nameof(IC2Service.OrderSuppliesAsync)),
                    dry.Dry ? $"{Callsign(dry.UnitId)} has run out of fuel: get a bowser to it" : $"{Callsign(dry.UnitId)} fuel critical, and no fuel at the scene",
                    dry.FuelReportedAt ?? now, dry.Dry ? DeskUrgency.Urgent : DeskUrgency.Overdue, at, dry.UnitId));
            }

        if (Can<IC2Service>(nameof(IC2Service.SubmitReimbursementClaimAsync)) && logistics.MajorEmergencySince is { } declared
            && logistics.Reimbursable >= 1_000m && !logistics.Claims.Any(c => c.Waiting)
            && (logistics.Claims.Count == 0 || now - logistics.Claims.Max(c => c.SubmittedAt) >= TimeSpan.FromHours(1)))
            items.Add(new(Function<IC2Service>(nameof(IC2Service.SubmitReimbursementClaimAsync)),
                $"{CostRates.Money(logistics.Reimbursable)} of eligible costs not yet claimed from central government", declared, DeskUrgency.Routine));

        // ---- Phase 16: health ----
        var health = cop.Health;
        string HospitalName(Guid id) => cop.Hospitals.FirstOrDefault(h => h.Id == id)?.Name ?? "A hospital";
        if (Can<IC2Service>(nameof(IC2Service.SetTransportDestinationAsync)))
            foreach (var group in health.Patients.Where(p => p is { Need: not null, IncidentId: not null, State: CasualtyState.TreatedOnScene })
                         .GroupBy(p => (Incident: p.IncidentId!.Value, Need: p.Need!.Value))
                         .Where(g => health.DestinationFor(g.Key.Incident, g.Key.Need) is null))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.SetTransportDestinationAsync)),
                    $"{group.Count()} {Health.Name(group.Key.Need).ToLowerInvariant()} patient(s){Incident(group.Key.Incident)} with no destination set: crews will take them to the nearest",
                    group.Min(p => p.TaggedAt), After(group.Min(p => p.TaggedAt), TimeSpan.FromMinutes(5)), group.Key.Incident));

        if (Can<IC2Service>(nameof(IC2Service.RequestBloodAsync)))
            foreach (var low in health.Hospitals.Values.Where(h => h.BloodLowSince is not null))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.RequestBloodAsync)),
                    $"Blood low at {HospitalName(low.HospitalId)}: {low.BloodUnits} units — ask the IBTS", low.BloodLowSince!.Value, DeskUrgency.Urgent, EntityId: low.HospitalId));

        var fatalities = health.Fatalities;
        var bodiesAtScene = fatalities.AtScene.Values.Sum();
        if (bodiesAtScene > 0 && fatalities.CoronerNotifiedAt is null && Can<IC2Service>(nameof(IC2Service.NotifyAsync)))
            items.Add(new(Function<IC2Service>(nameof(IC2Service.NotifyAsync)),
                $"{bodiesAtScene} dead at the scene: notify the coroner (nobody can be moved without the coroner's authority)", now, DeskUrgency.Overdue));
        if (fatalities.MortuaryOpenedAt is null && Can<IC2Service>(nameof(IC2Service.OpenTemporaryMortuaryAsync))
            && (bodiesAtScene >= 3 || health.Hospitals.Values.Any(h => h.MorgueSpaces > 0 && h.MorgueFull)))
            items.Add(new(Function<IC2Service>(nameof(IC2Service.OpenTemporaryMortuaryAsync)),
                health.Hospitals.Values.Any(h => h.MorgueSpaces > 0 && h.MorgueFull)
                    ? "A hospital mortuary is full: open a temporary mortuary"
                    : $"{bodiesAtScene} dead at the scene: open a temporary mortuary", now, DeskUrgency.Overdue));

        foreach (var threat in health.Threats)
        {
            if (Health.Contagious(threat.Kind) && threat.IsolationOrderedAt is null && Can<IC2Service>(nameof(IC2Service.OrderIsolationAsync)))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.OrderIsolationAsync)), $"{threat.Agent}: {threat.Ill} ill — isolate the cases (with HSE Public Health)",
                    threat.ReportedAt, DeskUrgency.Urgent, threat.IncidentId, threat.Id));
            if (!health.Dispensing.Any(d => d.ThreatId == threat.Id) && Can<IC2Service>(nameof(IC2Service.OpenDispensingPointAsync)))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.OpenDispensingPointAsync)),
                    $"{threat.Agent}: ~{threat.EstimatedExposed} need {Health.Countermeasure(threat.Kind)} — open points of dispensing",
                    threat.ReportedAt, DeskUrgency.Urgent, threat.IncidentId, threat.Id));
        }

        if (Can<IC2Service>(nameof(IC2Service.DeployCrisisCounsellingAsync)))
            foreach (var (incidentId, distressed) in health.Distressed.Where(d => d.Value >= 20 && !health.Counselling.Any(c => c.IncidentId == d.Key || c.IncidentId is null)))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.DeployCrisisCounsellingAsync)),
                    $"About {distressed} people in distress{Incident(incidentId)}: send crisis counselling", now, DeskUrgency.Routine, incidentId));

        // ---- Phase 17: evacuation, shelter, infrastructure, cyber and the environment ----
        var protection = cop.Protection;
        foreach (var zone in protection.Evacuations.Where(z => z.ReEntryAt is null && cop.Zones.Any(c => c.Id == z.ZoneId)))
        {
            if (zone.Route.Count == 0 && Can<IC2Service>(nameof(IC2Service.SetEvacuationRouteAsync)))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.SetEvacuationRouteAsync)),
                    $"{zone.Name}: no evacuation route — people go straight out, whatever lies that way", zone.OrderedAt,
                    After(zone.OrderedAt, TimeSpan.FromMinutes(5)), zone.IncidentId, zone.ZoneId));
            if (zone is { BusesRequested: 0, Remaining: >= 30 } && zone.ClearanceMinutes is null or > 60 && Can<IC2Service>(nameof(IC2Service.RequestEvacuationBusesAsync)))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.RequestEvacuationBusesAsync)),
                    $"{zone.Name}: ~{zone.Remaining} still in" + (zone.ClearanceMinutes is { } c ? $", clear in {Protection.Clearance(c)}" : ", hardly moving") +
                    " — buses would carry those on foot", zone.ReportedAt ?? zone.OrderedAt, DeskUrgency.Routine, zone.IncidentId, zone.ZoneId));
        }

        if (Can<IC2Service>(nameof(IC2Service.OpenShelterAsync)))
            foreach (var (incidentId, people) in protection.Unsheltered.Where(u => u.Value > 0))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.OpenShelterAsync)),
                    $"About {people} evacuees with nowhere to go{Incident(incidentId)}: " + (protection.Shelters.Count == 0 ? "open a rest centre" : "the rest centres are full — open another"),
                    now, protection.Shelters.Count == 0 ? DeskUrgency.Urgent : DeskUrgency.Overdue, incidentId));
        foreach (var shelter in protection.Shelters)
        {
            if (shelter.Short.Count > 0 && shelter.IncidentId is { } supplied && Can<IC2Service>(nameof(IC2Service.OrderSuppliesAsync)))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.OrderSuppliesAsync)),
                    $"{shelter.Name} has run out of {string.Join(" and ", shelter.Short.Select(k => Supplies.Name(k).ToLowerInvariant()))}: order more for the incident",
                    shelter.ReportedAt ?? now, DeskUrgency.Urgent, supplied, shelter.Id));
            if (shelter is { Occupants: >= 40, SecurityArrangedAt: null } && Can<IC2Service>(nameof(IC2Service.ArrangeShelterSecurityAsync)))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.ArrangeShelterSecurityAsync)),
                    $"{shelter.Name}: {shelter.Occupants} people and no security" + (shelter.Incidents > 0 ? $" ({shelter.Incidents} incident(s) already)" : ""),
                    shelter.ReportedAt ?? now, shelter.Incidents > 0 ? DeskUrgency.Overdue : DeskUrgency.Routine, shelter.IncidentId, shelter.Id));
        }

        foreach (var asset in protection.Infrastructure)
        {
            if (asset is { Kind: InfrastructureKind.Water or InfrastructureKind.Telecom, Status: InfrastructureStatus.OnBackup or InfrastructureStatus.Failed, GeneratorRequestedAt: null }
                && !asset.Detail.Contains("damaged", StringComparison.OrdinalIgnoreCase) && !asset.Detail.Contains("out of action", StringComparison.OrdinalIgnoreCase)
                && Can<IC2Service>(nameof(IC2Service.DeployGeneratorAsync)))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.DeployGeneratorAsync)),
                    $"{asset.Name} {Protection.Name(asset.Status)}: {asset.Detail} — send a generator", asset.ReportedAt,
                    asset.Status == InfrastructureStatus.Failed ? DeskUrgency.Urgent : DeskUrgency.Overdue, EntityId: asset.Id));
            if (asset is { Kind: InfrastructureKind.Gas, Status: InfrastructureStatus.Leaking, IsolationRequestedAt: null } && Can<IC2Service>(nameof(IC2Service.IsolateGasAsync)))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.IsolateGasAsync)), $"Gas escaping at {asset.Name}: have it isolated before it finds a spark",
                    asset.ReportedAt, DeskUrgency.Urgent, EntityId: asset.Id));
        }

        if (!protection.ManualPositionReporting && protection.Gnss.FirstOrDefault(g => g.EndedAt is null) is { } gnss
            && Can<IC2Service>(nameof(IC2Service.SetManualPositionReportingAsync)))
            items.Add(new(Function<IC2Service>(nameof(IC2Service.SetManualPositionReportingAsync)),
                $"GPS {(gnss.Kind == GnssInterferenceKind.Jamming ? "jamming" : "spoofing")} reported: AVL can't be trusted there — have crews give positions by voice",
                gnss.ReportedAt, DeskUrgency.Urgent, EntityId: gnss.Id));
        foreach (var attack in protection.Cyber.Where(c => c is { RestoredAt: null, FallbackAt: null }))
            if (Can<IC2Service>(nameof(IC2Service.ActivateCyberFallbackAsync)))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.ActivateCyberFallbackAsync)),
                    $"Ransomware: {attack.System} — order manual working ({Protection.Fallback(attack.Target)})", attack.ReportedAt, DeskUrgency.Urgent, EntityId: attack.Id));
        if (Can<IC2Service>(nameof(IC2Service.IssuePublicStatementAsync)))
            foreach (var attack in protection.Cyber.Where(c => c.Impact.Contains("boil water", StringComparison.OrdinalIgnoreCase)
                                                               && !cop.PublicStatements.Any(s => s.Text.Contains("boil", StringComparison.OrdinalIgnoreCase))))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.IssuePublicStatementAsync)),
                    "Uisce Éireann advise a boil water notice: tell the public to boil their water", attack.ReportedAt, DeskUrgency.Urgent, EntityId: attack.Id));

        foreach (var impact in protection.Impacts.Where(i => i.Medium == EnvironmentMedium.Water))
        {
            if (impact.IncidentId is { } polluted && !protection.RunoffContained.ContainsKey(polluted) && Can<IC2Service>(nameof(IC2Service.ContainRunoffAsync)))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.ContainRunoffAsync)), $"{impact.What}: contain the run-off{Incident(polluted)}",
                    impact.ReportedAt, DeskUrgency.Urgent, polluted, impact.Id));
            if (!cop.Notifications.Any(n => Systems.EnvironmentSystem.IsEpa(n.Recipient)) && Can<IC2Service>(nameof(IC2Service.NotifyAsync)))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.NotifyAsync)), "Pollution has reached water: notify the EPA (a legal duty)",
                    impact.ReportedAt, After(impact.ReportedAt, TimeSpan.FromMinutes(10)), impact.IncidentId, impact.Id));
        }

        if (Can<IC2Service>(nameof(IC2Service.AnswerPoliticalCallAsync)))
            foreach (var call in cop.PoliticalCalls.Where(c => c.Waiting))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.AnswerPoliticalCallAsync)), $"Call back {call.Caller} (for the {call.To})",
                    call.ReceivedAt, call.Pressure >= 3 ? DeskUrgency.Urgent : After(call.ReceivedAt, TimeSpan.FromMinutes(4)), call.IncidentId, call.Id));

        if (Can<IC2Service>(nameof(IC2Service.ResolveDisputeAsync)))
            foreach (var dispute in cop.Disputes.Where(d => d.Status == DisputeStatus.Open && d.Resolution is null))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.ResolveDisputeAsync)), $"{dispute.Agency} disputes: {dispute.Subject}",
                    dispute.RaisedAt, After(dispute.RaisedAt, TimeSpan.FromMinutes(5)), dispute.IncidentId, dispute.Id));

        if (Can<IC2Service>(nameof(IC2Service.SubmitSitrepAsync)) && cop.Eoc.SitrepDue is { } due)
            items.Add(new(Function<IC2Service>(nameof(IC2Service.SubmitSitrepAsync)), $"Situation report due for the EOC at {due:HH:mm}",
                due, now >= due ? DeskUrgency.Urgent : due - now <= TimeSpan.FromMinutes(5) ? DeskUrgency.Overdue : DeskUrgency.Routine));

        // An agency chief answers for the agency they run.
        if (role == UserRole.AgencyChief && me is { } chief && cop.Agencies.FirstOrDefault(a => a.ControlledBy == chief) is { } mine)
        {
            var function = Function<IC2Service>(nameof(IC2Service.AnswerAgencyRequestAsync));
            foreach (var request in cop.AssistanceRequests.Where(r => r.AgencyId == mine.Id && r.DecidedAt is null))
                items.Add(new(function, $"Command asks {mine.Name} for {request.Quantity} unit(s): {request.Task}", request.RequestedAt,
                    After(request.RequestedAt, Patience), request.IncidentId, request.Id));
            foreach (var invitation in cop.UnifiedCommandInvitations.Where(i => i.AgencyId == mine.Id && i.Accepted is null))
                items.Add(new(function, $"Invited to unified command{Incident(invitation.IncidentId)}", invitation.InvitedAt,
                    After(invitation.InvitedAt, Patience), invitation.IncidentId, invitation.Id));
        }

        return [.. items.OrderByDescending(i => i.Urgency).ThenBy(i => i.Since)];
    }

    /// <summary>Whether a standing contract keeps an incident supplied with this (Phase 15).</summary>
    private static bool Covers(ContractKind contract, SupplyKind kind) => (contract, kind) switch
    {
        (ContractKind.Catering, SupplyKind.Food or SupplyKind.Water) => true,
        (ContractKind.Sanitation, SupplyKind.Sanitation) => true,
        (ContractKind.FuelSupply, SupplyKind.Fuel) => true,
        _ => false,
    };
}
