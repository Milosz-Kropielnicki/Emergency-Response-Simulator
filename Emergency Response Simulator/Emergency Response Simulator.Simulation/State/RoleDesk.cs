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

        if (Can<IC2Service>(nameof(IC2Service.CallBackAsync)))
            foreach (var missed in cop.MissedCalls.Where(c => c.CalledBackAt is null))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.CallBackAsync)), "A 999 caller hung up waiting: call them back", missed.At,
                    After(missed.At, TimeSpan.FromMinutes(2)), EntityId: missed.Id));

        if (Can<IC2Service>(nameof(IC2Service.IssuePublicStatementAsync)))
            foreach (var enquiry in cop.MediaEnquiries.Where(m => m.AnsweredAt is null))
                items.Add(new(Function<IC2Service>(nameof(IC2Service.IssuePublicStatementAsync)), $"{enquiry.Outlet} asks: {enquiry.Question}",
                    enquiry.ReceivedAt, After(enquiry.ReceivedAt, TimeSpan.FromMinutes(8)), enquiry.IncidentId, enquiry.Id));

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
}
