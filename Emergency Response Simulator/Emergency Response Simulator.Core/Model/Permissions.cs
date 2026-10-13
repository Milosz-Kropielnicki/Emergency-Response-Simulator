using Emergency_Response_Simulator.Core.Contracts;

namespace Emergency_Response_Simulator.Core.Model;

/// <summary>Who may give one command: the roles whose function it is, or everyone taking part.</summary>
/// <param name="Function">What the command is part of, e.g. "Public information", for messages and the session tab.</param>
public sealed record CommandPermission(Type Contract, string Method, string Function, IReadOnlyList<UserRole> Owners, bool Anyone = false);

/// <summary>Whether a role may give a command, and if not, why.</summary>
/// <param name="ByFallback">Allowed only because the function's own role is unfilled and falls to the Incident Commander.</param>
public sealed record PermissionDecision(bool Allowed, string? Reason = null, bool ByFallback = false)
{
    public static readonly PermissionDecision Yes = new(true);
}

/// <summary>
/// The permission matrix (Phase 11 M3): which role may give each C2 and IAP command. The server enforces it in its
/// command authoriser, whatever a client sends; clients consult the same table so the buttons a role can't use are
/// disabled. Standalone sessions have one person holding every role and don't consult it.
/// <list type="bullet">
/// <item>A command belongs to the roles whose function it is: dispatching to the dispatchers and Operations, the IAP to
/// Planning (with approval for the IC), public statements to the PIO, and so on. Some, like the radio, are anyone's.</item>
/// <item>The Incident Commander holds every function nobody present has taken (decision D5): with no PIO in the session,
/// the IC speaks to the public; once a PIO joins, only the PIO does.</item>
/// <item>The instructor may give any command (stepping into a position to keep the exercise moving), and is recorded as
/// doing so; observers give none.</item>
/// </list>
/// </summary>
public static class PermissionMatrix
{
    private const UserRole IC = UserRole.IncidentCommander, Dispatcher = UserRole.Dispatcher, Ops = UserRole.Operations,
        Planning = UserRole.Planning, Logistics = UserRole.Logistics, Finance = UserRole.FinanceAdmin, Safety = UserRole.Safety,
        Liaison = UserRole.Liaison, Pio = UserRole.PublicInformation, Chief = UserRole.AgencyChief;

    private static readonly Dictionary<(Type, string), CommandPermission> Table = Build();

    /// <summary>Every command in the matrix.</summary>
    public static IEnumerable<CommandPermission> Commands => Table.Values;

    public static CommandPermission? Find(Type contract, string method) => Table.GetValueOrDefault((contract, method));

    /// <summary>
    /// Whether <paramref name="role"/> may give the command. <paramref name="filled"/> says whether someone present holds
    /// a role (for the IC's fallback). A command not in the matrix is refused, so a new one has to be placed in it.
    /// </summary>
    public static PermissionDecision Decide(UserRole role, Type contract, string method, Func<UserRole, bool> filled)
    {
        if (Find(contract, method) is not { } command)
            return new(false, $"{method} isn't in the permission matrix.");
        if (role == UserRole.Instructor) return PermissionDecision.Yes;
        if (role == UserRole.Observer) return new(false, "Observers watch: they don't give commands.");
        if (command.Anyone || command.Owners.Contains(role)) return PermissionDecision.Yes;
        // Another agency's chief answers for that agency: their functions don't fall to our IC.
        if (role == IC && command.Owners.Any(owner => owner != Chief && !filled(owner)))
            return new(true, ByFallback: true);
        return new(false, $"{command.Function} is for the {Holders(command, filled)}, not the {Roles.Name(role)}.");
    }

    public static PermissionDecision Decide<TContract>(UserRole role, string method, Func<UserRole, bool> filled) =>
        Decide(role, typeof(TContract), method, filled);

    /// <summary>The functions a role holds now, its own and any that fall to it, for the session tab.</summary>
    public static IReadOnlyList<string> FunctionsOf(UserRole role, Func<UserRole, bool> filled) =>
        Table.Values.Where(c => Decide(role, c.Contract, c.Method, filled).Allowed && !c.Anyone)
            .Select(c => c.Function).Distinct().OrderBy(f => f).ToList();

    private static string Holders(CommandPermission command, Func<UserRole, bool> filled)
    {
        var names = command.Owners.Select(Roles.Name).ToList();
        if (command.Owners.Contains(IC) || command.Owners.Where(o => o != Chief).All(filled)) return string.Join(" or ", names);
        return string.Join(" or ", names.Append("Incident Commander while that's unfilled"));
    }

    private static Dictionary<(Type, string), CommandPermission> Build()
    {
        var table = new Dictionary<(Type, string), CommandPermission>();
        void C2(string method, string function, params UserRole[] owners) =>
            table[(typeof(IC2Service), method)] = new(typeof(IC2Service), method, function, owners);
        void Iap(string method, string function, params UserRole[] owners) =>
            table[(typeof(IIapService), method)] = new(typeof(IIapService), method, function, owners);
        void Anyone(Type contract, string method, string function) =>
            table[(contract, method)] = new(contract, method, function, [], Anyone: true);

        // Resources: tasking units is the dispatchers' and Operations'; ordering more is Logistics'.
        // An agency chief tasks only the units of the agency they run (checked by C2, Phase 11 M5).
        C2(nameof(IC2Service.DispatchAsync), "Tasking units", Dispatcher, Ops, Chief);
        C2(nameof(IC2Service.CancelDispatchAsync), "Tasking units", Dispatcher, Ops, Chief);
        C2(nameof(IC2Service.ReassignAsync), "Tasking units", Dispatcher, Ops, Chief);
        C2(nameof(IC2Service.UpdateUnitStatusAsync), "Tasking units", Dispatcher, Ops, Chief);
        C2(nameof(IC2Service.RequestResourcesAsync), "Ordering resources", Logistics, Finance);

        // Incidents and information.
        C2(nameof(IC2Service.CreateIncidentAsync), "Opening incidents", Dispatcher, IC);
        C2(nameof(IC2Service.UpdateIncidentAsync), "Updating incidents", Dispatcher, IC, Ops);
        C2(nameof(IC2Service.AssignIncidentCommanderAsync), "Command structure", IC, Dispatcher);
        C2(nameof(IC2Service.AssessReportAsync), "Situation assessment", Planning, Dispatcher);
        C2(nameof(IC2Service.LinkReportAsync), "Situation assessment", Planning, Dispatcher);
        C2(nameof(IC2Service.CallBackAsync), "The 999 line", Dispatcher);
        Anyone(typeof(IC2Service), nameof(IC2Service.AcknowledgeAlertAsync), "Alerts");

        // Hazard prediction (Phase 13): Planning works out where it is going, Safety says what that means for people.
        C2(nameof(IC2Service.PredictPlumeAsync), "Hazard prediction", Planning, Safety, Ops);

        // Zones: hazards are Safety's and Operations'.
        C2(nameof(IC2Service.DeclareZoneAsync), "Zones", Ops, Safety);
        C2(nameof(IC2Service.LiftZoneAsync), "Zones", Ops, Safety);
        C2(nameof(IC2Service.EstablishHazardZonesAsync), "Zones", Safety, Ops);

        // ICS structure: the IC staffs the command; Operations organises its groups.
        C2(nameof(IC2Service.AssignIcsPositionAsync), "Command structure", IC);
        C2(nameof(IC2Service.FormGroupAsync), "Organising operations", Ops);
        C2(nameof(IC2Service.DisbandGroupAsync), "Organising operations", Ops);
        C2(nameof(IC2Service.AssignUnitToGroupAsync), "Organising operations", Ops);

        // Orders and approvals.
        C2(nameof(IC2Service.IssueOrderAsync), "Orders", IC, Ops, Chief);
        C2(nameof(IC2Service.CloseOrderAsync), "Orders", IC, Ops, Chief);
        C2(nameof(IC2Service.ConfirmReadBackAsync), "Orders", IC, Ops, Chief);
        C2(nameof(IC2Service.DecideApprovalAsync), "Approvals", IC);
        C2(nameof(IC2Service.NotifyAsync), "Notifications", Liaison, Dispatcher);

        // Communications: everyone has a radio; the comms plan is Logistics' (the communications unit).
        Anyone(typeof(IC2Service), nameof(IC2Service.TransmitAsync), "Radio");
        Anyone(typeof(IC2Service), nameof(IC2Service.RequestRepeatAsync), "Radio");
        C2(nameof(IC2Service.AssignChannelAsync), "Communications plan", Logistics, Ops);
        C2(nameof(IC2Service.PatchChannelsAsync), "Communications plan", Logistics);
        C2(nameof(IC2Service.RemovePatchAsync), "Communications plan", Logistics);

        // Crews and safety.
        C2(nameof(IC2Service.RequestParAsync), "Responder safety", Safety, Ops);
        C2(nameof(IC2Service.SignalEvacuationAsync), "Evacuation signal", IC, Safety);
        C2(nameof(IC2Service.DeclareEmergencyTrafficAsync), "Responder safety", Safety, Ops, Dispatcher);
        C2(nameof(IC2Service.DeclareMaydayAsync), "Responder safety", Safety, Ops, Dispatcher);
        C2(nameof(IC2Service.DeployRescueTeamAsync), "Responder safety", Ops, Safety);
        C2(nameof(IC2Service.SendToRehabAsync), "Crew welfare", Safety, Ops);
        C2(nameof(IC2Service.RequestReliefAsync), "Crew welfare", Logistics, Ops);
        C2(nameof(IC2Service.ArrangePeerSupportAsync), "Crew welfare", Safety, Logistics);

        // Multi-agency: the Liaison Officer deals with other agencies; the EOC and disputes go to the IC.
        C2(nameof(IC2Service.InviteToUnifiedCommandAsync), "Unified command", IC, Liaison);
        C2(nameof(IC2Service.RequestAgencyAssistanceAsync), "Other agencies", Liaison, Logistics);
        C2(nameof(IC2Service.RequestAgencyBriefingAsync), "Other agencies", Liaison, Planning);
        C2(nameof(IC2Service.ResolveDisputeAsync), "Authority disputes", IC, Liaison, Chief);
        C2(nameof(IC2Service.SetEocLevelAsync), "The EOC", IC);
        C2(nameof(IC2Service.SubmitSitrepAsync), "Situation reports", Planning, IC);
        C2(nameof(IC2Service.IssuePublicStatementAsync), "Public information", Pio);

        // AI role players (Phase 11 M5): politicians ring the IC or the PIO; an agency chief answers for the agency they run.
        C2(nameof(IC2Service.AnswerPoliticalCallAsync), "Political calls", IC, Pio);
        C2(nameof(IC2Service.AnswerAgencyRequestAsync), "Answering for your agency", Chief);

        // The Incident Action Plan: Planning writes it, the IC approves it, Operations carries it out.
        Iap(nameof(IIapService.StartOperationalPeriodAsync), "Planning", Planning);
        Iap(nameof(IIapService.CreateDraftAsync), "Planning", Planning);
        Iap(nameof(IIapService.SaveDraftAsync), "Planning", Planning);
        Iap(nameof(IIapService.SubmitAsync), "Planning", Planning);
        Iap(nameof(IIapService.ApproveAsync), "Plan approval", IC);
        Iap(nameof(IIapService.ReturnAsync), "Plan approval", IC);
        Iap(nameof(IIapService.BriefAsync), "Briefing the plan", IC, Ops);
        Iap(nameof(IIapService.SetObjectiveStatusAsync), "Objectives", Planning, IC, Ops);
        Anyone(typeof(IIapService), nameof(IIapService.PullFromCopAsync), "Reading the COP"); // records nothing

        return table;
    }
}
