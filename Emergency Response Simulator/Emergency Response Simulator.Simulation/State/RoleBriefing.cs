using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.State;

/// <summary>
/// What someone taking over a role needs to be told (Phase 11 M3): the open business of every function the outgoing
/// holder has now, their own and any that fell to them, read from the COP. A full briefing passes it on; a quick
/// changeover leaves it out, as a rushed crew handover does in Phase 8, and the review says so.
/// </summary>
public static class RoleBriefing
{
    public static IReadOnlyList<string> For(UserRole role, ICopService cop, Func<UserRole, bool> filled)
    {
        bool Can<T>(string method) => PermissionMatrix.Decide<T>(role, method, filled).Allowed;
        var items = new List<string>();

        var live = cop.Incidents.Where(i => i.Status != IncidentStatus.Closed).ToList();
        if (live.Count > 0)
            items.Add($"{live.Count} live incident(s): " + string.Join(", ", live.Select(i => $"{i.Number} {i.Priority} {i.Status}")));

        if (Can<IC2Service>(nameof(IC2Service.DispatchAsync)))
        {
            var committed = cop.Units.Count(u => u.AssignedIncidentId is not null);
            if (committed > 0) items.Add($"{committed} unit(s) committed");
        }
        if (Can<IC2Service>(nameof(IC2Service.CloseOrderAsync)))
        {
            var open = cop.Orders.Where(o => o.Status is OrderStatus.Issued or OrderStatus.Acknowledged).ToList();
            if (open.Count > 0) items.Add($"{open.Count} open order(s), e.g. {open[0].TargetName}: \"{open[0].Text}\"");
            var unconfirmed = cop.Orders.Count(o => o.ReadBack is not null && o.ReadBackConfirmedAt is null && o.Status != OrderStatus.Cancelled);
            if (unconfirmed > 0) items.Add($"{unconfirmed} read-back(s) not yet confirmed");
        }
        if (Can<IC2Service>(nameof(IC2Service.DecideApprovalAsync)))
            Count(items, cop.Approvals.Count(a => a.Status == ApprovalStatus.Pending), "decision(s) awaiting approval");
        if (Can<IC2Service>(nameof(IC2Service.RequestResourcesAsync)))
            Count(items, cop.ResourceRequests.Count(r => r.Status is ResourceRequestStatus.Requested or ResourceRequestStatus.Approved), "resource request(s) outstanding");
        if (Can<IC2Service>(nameof(IC2Service.RequestAgencyAssistanceAsync)))
            Count(items, cop.AssistanceRequests.Count(r => r.DecidedAt is null), "request(s) to other agencies unanswered");
        if (Can<IC2Service>(nameof(IC2Service.ResolveDisputeAsync)))
            Count(items, cop.Disputes.Count(d => d.Status is DisputeStatus.Open or DisputeStatus.Negotiating), "authority dispute(s) open");
        if (Can<IC2Service>(nameof(IC2Service.DeployRescueTeamAsync)) || Can<IC2Service>(nameof(IC2Service.RequestParAsync)))
            Count(items, cop.Maydays.Count(m => m.Active), "Mayday(s) active");
        if (Can<IC2Service>(nameof(IC2Service.PatchChannelsAsync)))
            Count(items, cop.Patches.Count, "channel patch(es) in place");
        if (Can<IIapService>(nameof(IIapService.SaveDraftAsync)))
            Count(items, cop.ActionPlans.Count(p => p.Status == IapStatus.Draft), "IAP draft(s) in progress");
        if (Can<IIapService>(nameof(IIapService.ApproveAsync)))
            Count(items, cop.ActionPlans.Count(p => p.Status == IapStatus.PendingApproval), "IAP(s) waiting for approval");
        if (Can<IC2Service>(nameof(IC2Service.IssuePublicStatementAsync)))
        {
            Count(items, cop.MediaEnquiries.Count(m => m.AnsweredAt is null), "media enquiry(ies) unanswered");
            if (cop.PublicStatements.Count > 0) items.Add($"{cop.PublicStatements.Count} public statement(s) issued so far");
        }
        if (Can<IC2Service>(nameof(IC2Service.DeclareZoneAsync)))
            Count(items, cop.Zones.Count, "zone(s) declared");
        return items;
    }

    private static void Count(List<string> items, int count, string what)
    {
        if (count > 0) items.Add($"{count} {what}");
    }
}
