using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>An objection from another agency, with what each answer is likely to do.</summary>
public sealed record DisputeRow(
    Guid Id,
    string Title,
    string Agency,
    string Position,
    string Basis,
    string Time,
    string Status,
    bool CanAnswer,
    bool Settled,
    string ConcedeTip,
    string NegotiateTip,
    string AssertTip,
    string EscalateTip);

/// <summary>Another agency: who it answers for, what it wants, how it feels about command, what it has.</summary>
public sealed record AgencyRow(
    Guid Id,
    string Name,
    string Detail,
    string Priority,
    string Standing,
    bool StandingWarning,
    string Role,
    bool HasUnits);

public sealed record MemberRow(string Commander, string Agency, string Detail);

public sealed record StatementRow(string Header, string Text, bool Conflicting, bool Joint);

public sealed record EnquiryRow(string Header, string Question, bool Answered);

public sealed record AgreementRow(string Provider, string Covers, string Terms);

public sealed record SeatRow(string Function, bool Staffed);

/// <summary>
/// Multi-agency command in the right panel (Design Document §14): the EOC and its activation level, unified command of the
/// selected incident, authority disputes, the other agencies (their priorities, standing and units), public information
/// and the mutual-aid agreements. Shows what command knows, never what the agencies are keeping to themselves.
/// </summary>
public partial class AgenciesViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private readonly IC2Service _c2;
    private readonly CopView _cop;

    public AgenciesViewModel(MainViewModel owner, IC2Service c2, CopView cop)
    {
        _owner = owner;
        _c2 = c2;
        _cop = cop;
        SelectedEocLevel = EocLevel.Partial;
    }

    public IReadOnlyList<EocLevel> EocLevelChoices { get; } = Enum.GetValues<EocLevel>();

    public ObservableCollection<SeatRow> EocSeats { get; } = [];
    public ObservableCollection<MemberRow> Members { get; } = [];
    public ObservableCollection<AgencyRow> Invitable { get; } = [];
    public ObservableCollection<DisputeRow> Disputes { get; } = [];
    public ObservableCollection<AgencyRow> Agencies { get; } = [];
    public ObservableCollection<EnquiryRow> Enquiries { get; } = [];
    public ObservableCollection<StatementRow> Statements { get; } = [];
    public ObservableCollection<AgreementRow> Agreements { get; } = [];

    [ObservableProperty] private string _tabHeader = "AGENCIES";
    [ObservableProperty] private EocLevel _selectedEocLevel;
    [ObservableProperty] private string _eocStatus = "";
    [ObservableProperty] private string _eocImplications = "";
    [ObservableProperty] private string _sitrepDue = "";
    [ObservableProperty] private bool _sitrepOverdue;
    [ObservableProperty] private string _sitrepText = "";
    [ObservableProperty] private string _incidentLabel = "Select an incident";
    [ObservableProperty] private string _commandSummary = "";
    [ObservableProperty] private string _jurisdictionSummary = "";
    [ObservableProperty] private AgencyRow? _invitee;
    [ObservableProperty] private int _askQuantity = 1;
    [ObservableProperty] private string _askTask = "Traffic control and cordon";
    [ObservableProperty] private string _pioSummary = "";
    [ObservableProperty] private bool _jointInformation;
    [ObservableProperty] private string _statementText = "";
    [ObservableProperty] private int _openDisputes;

    partial void OnSelectedEocLevelChanged(EocLevel value) => EocImplications = EocLevels.Implications(value);

    // ---- EOC ----

    private bool CanSetEoc => _owner.Permissions.C2(nameof(IC2Service.SetEocLevelAsync));

    [RelayCommand(CanExecute = nameof(CanSetEoc))]
    private Task SetEocLevelAsync() =>
        _owner.RunCommandAsync(() => _c2.SetEocLevelAsync(SelectedEocLevel, _owner.SelectedIncidentId is { } id ? _cop.FindIncident(id)?.Number : null),
            $"EOC to {EocLevels.Label(SelectedEocLevel).ToLowerInvariant()}");

    [RelayCommand]
    private void DraftSitrep()
    {
        var incidents = _cop.Incidents.Where(i => i.Status != IncidentStatus.Closed).ToList();
        if (incidents.Count == 0)
        {
            SitrepText = "No incident open.";
            return;
        }
        SitrepText = string.Join(" ", incidents.Select(i =>
            $"{i.Number} {MainViewModel.Humanize(i.Type).ToLowerInvariant()}{(i.Address is null ? "" : $", {i.Address}")}: {MainViewModel.Humanize(i.Status).ToLowerInvariant()}, " +
            $"priority {i.Priority}, {i.AssignedUnits.Count} units committed, {Math.Max(i.CasualtiesReported, i.CasualtiesConfirmed)} casualties reported" +
            (i.Zones.Count > 0 ? $", {string.Join(", ", i.Zones.Select(z => z.Name))} in force" : "") +
            (i.Command.IsUnified ? $"; unified command with {string.Join(", ", i.Command.Unified.Select(m => m.Agency))}" : "") + "."));
    }

    private bool CanSitrep => _owner.Permissions.C2(nameof(IC2Service.SubmitSitrepAsync));

    [RelayCommand(CanExecute = nameof(CanSitrep))]
    private async Task SendSitrepAsync()
    {
        if (string.IsNullOrWhiteSpace(SitrepText)) DraftSitrep();
        var result = await _owner.RunCommandAsync(() => _c2.SubmitSitrepAsync(SitrepText), "Situation report sent to the EOC");
        if (result?.Succeeded == true) SitrepText = "";
    }

    // ---- Unified command ----

    private bool CanInvite => _owner.Permissions.C2(nameof(IC2Service.InviteToUnifiedCommandAsync));

    [RelayCommand(CanExecute = nameof(CanInvite))]
    private Task InviteAsync()
    {
        if (_owner.SelectedIncidentId is not { } incident) return Fail("Select an incident first.");
        if (Invitee is not { } agency) return Fail("Choose an agency to invite.");
        return _owner.RunCommandAsync(() => _c2.InviteToUnifiedCommandAsync(incident, agency.Id), $"{agency.Name} invited into unified command");
    }

    // ---- Disputes ----

    private bool CanResolve => _owner.Permissions.C2(nameof(IC2Service.ResolveDisputeAsync));

    [RelayCommand(CanExecute = nameof(CanResolve))]
    private Task ConcedeAsync(Guid disputeId) => Resolve(disputeId, DisputeResolution.Concede, "Conceded");

    [RelayCommand(CanExecute = nameof(CanResolve))]
    private Task NegotiateAsync(Guid disputeId) => Resolve(disputeId, DisputeResolution.Negotiate, "Talks started");

    [RelayCommand(CanExecute = nameof(CanResolve))]
    private Task AssertAsync(Guid disputeId) => Resolve(disputeId, DisputeResolution.Assert, "Authority asserted");

    [RelayCommand(CanExecute = nameof(CanResolve))]
    private Task EscalateAsync(Guid disputeId) => Resolve(disputeId, DisputeResolution.Escalate, "Referred to the EOC");

    private Task Resolve(Guid disputeId, DisputeResolution resolution, string done) =>
        _owner.RunCommandAsync(() => _c2.ResolveDisputeAsync(disputeId, resolution), done);

    // ---- Other agencies ----

    private bool CanAskAgency => _owner.Permissions.C2(nameof(IC2Service.RequestAgencyAssistanceAsync));

    [RelayCommand(CanExecute = nameof(CanAskAgency))]
    private Task AskForUnitsAsync(Guid agencyId)
    {
        if (_owner.SelectedIncidentId is not { } incident) return Fail("Select the incident the units are for.");
        var name = _cop.Agencies.FirstOrDefault(a => a.Id == agencyId)?.Name ?? "Agency";
        return _owner.RunCommandAsync(() => _c2.RequestAgencyAssistanceAsync(agencyId, incident, AskQuantity, AskTask),
            $"Asked {name} for {AskQuantity} unit(s)");
    }

    private bool CanAskBriefing => _owner.Permissions.C2(nameof(IC2Service.RequestAgencyBriefingAsync));

    [RelayCommand(CanExecute = nameof(CanAskBriefing))]
    private Task BriefingAsync(Guid agencyId)
    {
        var name = _cop.Agencies.FirstOrDefault(a => a.Id == agencyId)?.Name ?? "Agency";
        return _owner.RunCommandAsync(() => _c2.RequestAgencyBriefingAsync(agencyId), $"Asked {name} what it knows");
    }

    // ---- Public information ----

    private bool CanIssueStatement => _owner.Permissions.C2(nameof(IC2Service.IssuePublicStatementAsync));

    [RelayCommand(CanExecute = nameof(CanIssueStatement))]
    private async Task IssueStatementAsync()
    {
        var result = await _owner.RunCommandAsync(() => _c2.IssuePublicStatementAsync(StatementText, _owner.SelectedIncidentId),
            JointInformation ? "Joint statement issued" : "Statement issued");
        if (result?.Succeeded == true) StatementText = "";
    }

    private Task Fail(string message)
    {
        _owner.ShowCommandResult(CommandResult.Fail(message));
        return Task.CompletedTask;
    }

    // ---- Refresh ----

    public void Refresh()
    {
        var now = _owner.Now;
        var incident = _owner.SelectedIncidentId is { } id ? _cop.FindIncident(id) : null;

        RefreshEoc(now);
        RefreshCommand(incident);

        var disputes = _cop.Disputes;
        OpenDisputes = disputes.Count(d => d.IsOpen);
        TabHeader = OpenDisputes > 0 ? $"AGENCIES · {OpenDisputes} DISPUTE{(OpenDisputes > 1 ? "S" : "")}" : "AGENCIES";
        Replace(Disputes, disputes.Where(d => d.IsOpen || now - (d.ResolvedAt ?? now) < TimeSpan.FromMinutes(15))
            .OrderByDescending(d => d.IsOpen).ThenByDescending(d => d.RaisedAt).Select(d => new DisputeRow(
                d.Id, $"{MainViewModel.Humanize(d.Kind).ToUpperInvariant()}: {d.Subject}", d.Agency, d.Position, d.Basis,
                $"{MainViewModel.Time(d.RaisedAt)} ({(now - d.RaisedAt).TotalMinutes:F0} min ago)",
                d.Status switch
                {
                    DisputeStatus.Negotiating => "Talks under way…",
                    DisputeStatus.Escalated => "With the EOC to decide…",
                    DisputeStatus.Resolved => $"Settled ({MainViewModel.Humanize(d.Resolution!.Value).ToLowerInvariant()}): {d.Outcome}",
                    _ => "Waiting for your answer",
                },
                d.Status == DisputeStatus.Open, !d.IsOpen,
                DisputeAdvice.Consequence(d.Kind, DisputeResolution.Concede), DisputeAdvice.Consequence(d.Kind, DisputeResolution.Negotiate),
                DisputeAdvice.Consequence(d.Kind, DisputeResolution.Assert), DisputeAdvice.Consequence(d.Kind, DisputeResolution.Escalate))));

        var others = _cop.Agencies.Where(a => a.AiControlled).OrderBy(a => a.Type).ThenBy(a => a.Name, StringComparer.Ordinal).ToList();
        Replace(Agencies, others.Select(a => AgencyRow(a, incident)));

        RefreshPublicInformation(incident, now);

        Replace(Agreements, _cop.MutualAidAgreements.OrderBy(a => a.ProviderType).Select(a => new AgreementRow(
            a.Provider,
            $"Up to {a.MaxUnits} × {string.Join(" / ", a.UnitTypes.Select(t => ResourceGroups.Label(t).ToLowerInvariant()))} · approved by {a.ApprovedBy} · " +
            $"about {a.DecisionTime.TotalMinutes:0.#} min to decide, {a.LeadTime.TotalMinutes:0} min to arrive" +
            (a.NeedsMajorEmergency ? " · only once a major emergency is declared" : ""),
            a.Terms)));
    }

    private void RefreshEoc(DateTimeOffset now)
    {
        var eoc = _cop.Eoc;
        var seats = EocLevels.SeatsAt(eoc.Requested is { } up && up > eoc.Level ? up : eoc.Level);
        EocStatus = $"{EocLevels.Label(eoc.Level)}" + (eoc.LevelSince is { } since ? $" since {MainViewModel.Time(since)}" : "") +
                    (eoc.Requested is { } target ? $" · staffing up to {EocLevels.Label(target).ToLowerInvariant()}" : "") +
                    (seats.Count > 0 ? $" · {eoc.Seats.Count} of {seats.Count} seats filled" : "");
        Replace(EocSeats, seats.Select(s => new SeatRow(s.Function + (s.Agency is null ? "" : $" ({s.Agency})"), eoc.Seats.Any(x => x.Function == s.Function))));
        SitrepOverdue = eoc.SitrepDue is { } due && now > due && (eoc.LastSitrepAt is null || eoc.LastSitrepAt < due - (EocLevels.SitrepInterval(eoc.Level) ?? TimeSpan.Zero));
        SitrepDue = eoc.SitrepDue is { } by
            ? (SitrepOverdue ? $"Situation report OVERDUE (wanted by {MainViewModel.Time(by)})" : $"Next situation report due {MainViewModel.Time(by)}") +
              (eoc.SitrepsSubmitted > 0 ? $" · {eoc.SitrepsSubmitted} sent" : "")
            : eoc.Level == EocLevel.Normal ? "The EOC isn't activated." : "";
        EocImplications = EocLevels.Implications(SelectedEocLevel);
    }

    private void RefreshCommand(Incident? incident)
    {
        if (incident is null)
        {
            IncidentLabel = "Select an incident";
            CommandSummary = "";
            JurisdictionSummary = "";
            Replace(Members, []);
            Replace(Invitable, []);
            return;
        }

        var command = incident.Command;
        IncidentLabel = $"{incident.Number} {incident.Name ?? MainViewModel.Humanize(incident.Type)}";
        var lead = command.LeadAgency ?? "your service";
        var ic = command.Positions.GetValueOrDefault(IcsRole.IncidentCommander) ?? incident.IncidentCommanderName;
        CommandSummary = command.IsUnified
            ? $"Unified command, {command.Unified.Count + 1} agencies · lead: {lead}" + (ic is null ? "" : $" · your IC: {ic}")
            : $"Single command · lead: {lead}" + (ic is null ? "" : $" · IC: {ic}") + ". Other agencies coordinate, but don't share command.";
        var areas = _cop.JurisdictionsAt(Core.Geo.GeoPoint.FromPoint(incident.Location));
        JurisdictionSummary = areas.Count == 0 ? "" : "Jurisdictions here: " + string.Join(" · ", areas.Select(j => $"{j.Name} ({MainViewModel.Humanize(j.Level).ToLowerInvariant()})"));

        Replace(Members, command.Unified.Select(m => new MemberRow(m.Commander, m.Agency,
            $"Since {MainViewModel.Time(m.JoinedAt)}" + (m.Priority is { } p ? $" · wants: {p}" : ""))));

        var pending = _cop.UnifiedCommandInvitations.Where(i => i.IncidentId == incident.Id && i.Accepted is null).Select(i => i.AgencyId).ToHashSet();
        var invitable = _cop.Agencies.Where(a => a.AiControlled && !command.InUnifiedCommand(a.Id) && !pending.Contains(a.Id))
            .OrderBy(a => a.Name, StringComparer.Ordinal).Select(a => AgencyRow(a, incident)).ToList();
        if (!invitable.Select(a => a.Id).SequenceEqual(Invitable.Select(a => a.Id)))
        {
            var chosen = Invitee?.Id;
            Replace(Invitable, invitable);
            Invitee = Invitable.FirstOrDefault(a => a.Id == chosen) ?? Invitable.FirstOrDefault();
        }
    }

    private AgencyRow AgencyRow(Agency agency, Incident? incident)
    {
        var units = _cop.Units.Where(u => u.AgencyId == agency.Id).ToList();
        var free = units.Count(u => u.Status == UnitStatus.Available);
        var detail = $"{MainViewModel.Humanize(agency.Type)}" + (agency.Jurisdiction is { } j ? $" · {j}" : "") +
                     (units.Count > 0 ? $" · {free} of {units.Count} units free" : " · no units");
        var invitation = incident is null ? null
            : _cop.UnifiedCommandInvitations.LastOrDefault(i => i.IncidentId == incident.Id && i.AgencyId == agency.Id);
        var role = incident?.Command.InUnifiedCommand(agency.Id) == true ? "In unified command"
            : invitation is { Accepted: null } ? "Invited, waiting for an answer"
            : invitation is { Accepted: false } ? $"Declined: {invitation.Reason}"
            : "";
        var asked = _cop.AssistanceRequests.Where(r => r.AgencyId == agency.Id).OrderBy(r => r.RequestedAt).LastOrDefault();
        if (asked is not null)
            role += (role.Length > 0 ? " · " : "") + (asked.Granted is { } granted ? $"Released {granted} of {asked.Quantity}: {asked.Reason}" : $"Asked for {asked.Quantity}, waiting");
        return new AgencyRow(agency.Id, agency.Name, detail, agency.Priority is { } p ? $"Priority: {p}" : "",
            MainViewModel.Humanize(agency.Standing), agency.Standing != AgencyStanding.Good, role, units.Count > 0);
    }

    private void RefreshPublicInformation(Incident? incident, DateTimeOffset now)
    {
        var pio = incident?.Command.Positions.GetValueOrDefault(IcsRole.PublicInformation)
                  ?? _cop.Incidents.Select(i => i.Command.Positions.GetValueOrDefault(IcsRole.PublicInformation)).FirstOrDefault(n => n is not null);
        JointInformation = _cop.JointInformationCentre(incident?.Id);
        PioSummary = (pio is null ? "No PIO appointed: statements go out from the IC, and nobody answers the media." : $"PIO: {pio}, answering the media.") +
                     (JointInformation ? " Joint information centre working: every agency gives the same message."
                         : " No joint information centre: other agencies may put out their own line.");

        var enquiries = _cop.MediaEnquiries;
        Replace(Enquiries, enquiries.OrderBy(m => m.AnsweredAt is not null).ThenByDescending(m => m.ReceivedAt).Take(6).Select(m => new EnquiryRow(
            $"{MainViewModel.Time(m.ReceivedAt)} {m.Outlet}" + (m.AnsweredAt is { } a ? $" · answered {MainViewModel.Time(a)} by {m.AnsweredBy}" : $" · waiting {(now - m.ReceivedAt).TotalMinutes:F0} min"),
            m.Question, m.AnsweredAt is not null)));
        Replace(Statements, _cop.PublicStatements.OrderByDescending(s => s.IssuedAt).Take(6).Select(s => new StatementRow(
            $"{MainViewModel.Time(s.IssuedAt)} {s.Issuer}" + (s.Conflicting ? " · CONTRADICTS THE OFFICIAL LINE" : s.Joint ? " · joint" : ""),
            s.Text, s.Conflicting, s.Joint)));
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var list = items.ToList();
        if (list.SequenceEqual(target)) return;
        target.Clear();
        foreach (var item in list)
            target.Add(item);
    }
}
