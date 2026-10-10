using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>A Mayday as command knows it, with who is going in.</summary>
public sealed record MaydayRow(Guid Id, string Title, string Details, string Time, string Rescue, bool Active, bool NeedsRescue, bool Unclear);

/// <summary>One crew's answer (or silence) in the latest PAR.</summary>
public sealed record ParLine(Guid UnitId, string Callsign, string Text, bool Missing, bool Outstanding, string? MissingMember);

/// <summary>One crew's welfare at a glance: shift, time on task, what it last said, qualifications and its people.</summary>
public sealed record CrewRow(
    Guid UnitId,
    string Callsign,
    string Status,
    string Shift,
    bool ShiftOver,
    string Work,
    bool RehabDue,
    string Condition,
    bool ConditionWarning,
    string Qualifications,
    string Notes,
    string Roster,
    bool AtScene,
    bool CanRelieve);

/// <summary>
/// Crews and safety in the right panel (Design Document §12): Maydays and the rescue team, PAR and the evacuation signal,
/// emergency traffic, and every crew's shift, time on task, condition, qualifications and people, with rehab, relief and
/// peer support. Shows what command knows (the roster, the clock and what crews have said), never their real fatigue.
/// </summary>
public partial class CrewsViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private readonly IC2Service _c2;
    private readonly CopView _cop;

    public CrewsViewModel(MainViewModel owner, IC2Service c2, CopView cop)
    {
        _owner = owner;
        _c2 = c2;
        _cop = cop;

        // Time on shift and on task move with the clock, not only with events.
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Refresh(), Application.Current.Dispatcher);
        timer.Start();
    }

    public ObservableCollection<MaydayRow> Maydays { get; } = [];
    public ObservableCollection<CrewRow> Crews { get; } = [];
    public ObservableCollection<ParLine> ParLines { get; } = [];

    /// <summary>Crews at the incident that could go in as the rescue team.</summary>
    public ObservableCollection<OrderTarget> RescueCandidates { get; } = [];

    public ObservableCollection<string> RadioChannels { get; } = [];

    [ObservableProperty] private OrderTarget? _rescueTeam;
    [ObservableProperty] private string _parSummary = "No PAR called yet.";
    [ObservableProperty] private bool _parProblem;
    [ObservableProperty] private string? _trafficChannel = RadioPlan.FireCommand;
    [ObservableProperty] private string _emergencyTraffic = "";
    [ObservableProperty] private bool _hasEmergencyTraffic;
    [ObservableProperty] private bool _committedOnly = true;
    [ObservableProperty] private int _activeMaydays;
    [ObservableProperty] private string _tabHeader = "CREWS";

    partial void OnCommittedOnlyChanged(bool value) => Refresh();

    // ---- Safety ----

    [RelayCommand]
    private Task RequestParAsync() => _owner.SelectedIncidentId is { } incident
        ? _owner.RunCommandAsync(() => _c2.RequestParAsync(incident), "PAR called")
        : Fail("Select an incident first.");

    [RelayCommand]
    private Task EvacuateAsync()
    {
        if (_owner.SelectedIncidentId is not { } incident) return Fail("Select an incident first.");
        var confirm = MessageBox.Show("Sound the evacuation signal? Every crew at the incident withdraws from the building and operations go defensive.",
            "Evacuation signal", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        return confirm == MessageBoxResult.OK
            ? _owner.RunCommandAsync(() => _c2.SignalEvacuationAsync(incident), "Evacuation signal sounded")
            : Task.CompletedTask;
    }

    [RelayCommand]
    private Task ToggleEmergencyTrafficAsync()
    {
        if (TrafficChannel is not { } channel) return Task.CompletedTask;
        var active = _cop.Channels.FirstOrDefault(c => c.Info.Id == channel)?.EmergencyTraffic == true;
        return _owner.RunCommandAsync(() => _c2.DeclareEmergencyTrafficAsync(channel, !active),
            active ? $"Emergency traffic lifted on {channel}" : $"Emergency traffic declared on {channel}");
    }

    [RelayCommand]
    private Task DeployRescueAsync(Guid maydayId) => RescueTeam is { Id: { } unit } team
        ? _owner.RunCommandAsync(() => _c2.DeployRescueTeamAsync(maydayId, unit), $"{team.Label} sent in as the rescue team")
        : Fail("Choose a crew at the scene to send in.");

    [RelayCommand]
    private Task DeclareMaydayAsync(ParLine line) =>
        _owner.RunCommandAsync(() => _c2.DeclareMaydayAsync(line.UnitId, line.MissingMember,
            line.Outstanding
                ? $"{line.Callsign} has not answered the PAR and can't be raised"
                : $"{line.MissingMember ?? "A member"} of {line.Callsign} missing at the PAR"), "Mayday declared");

    // ---- Welfare ----

    [RelayCommand]
    private Task RehabAsync(Guid unitId) => _owner.RunCommandAsync(() => _c2.SendToRehabAsync(unitId), $"{Callsign(unitId)} sent to rehab");

    [RelayCommand]
    private Task RelieveAsync(Guid unitId) =>
        _owner.RunCommandAsync(() => _c2.RequestReliefAsync(unitId, fullBriefing: true), $"Relief crew requested for {Callsign(unitId)} (full briefing)");

    [RelayCommand]
    private Task QuickRelieveAsync(Guid unitId) =>
        _owner.RunCommandAsync(() => _c2.RequestReliefAsync(unitId, fullBriefing: false), $"Relief crew requested for {Callsign(unitId)} (quick changeover)");

    [RelayCommand]
    private Task PeerSupportAsync(Guid unitId) =>
        _owner.RunCommandAsync(() => _c2.ArrangePeerSupportAsync(unitId), $"Peer support arranged for {Callsign(unitId)}");

    private string Callsign(Guid unitId) => _cop.FindUnit(unitId)?.Callsign ?? "unit";

    private Task Fail(string message)
    {
        _owner.ShowCommandResult(CommandResult.Fail(message));
        return Task.CompletedTask;
    }

    // ---- Refresh ----

    public void Refresh()
    {
        var now = _owner.Now;
        var incident = _owner.SelectedIncidentId;

        var maydays = _cop.Maydays;
        ActiveMaydays = maydays.Count(m => m.Active);
        TabHeader = ActiveMaydays > 0 ? $"CREWS · {ActiveMaydays} MAYDAY" : "CREWS";
        Replace(Maydays, maydays.Where(m => m.Active || now - (m.ResolvedAt ?? now) < TimeSpan.FromMinutes(10)).OrderByDescending(m => m.Active)
            .ThenByDescending(m => m.DeclaredAt).Select(m => new MaydayRow(
                m.Id, (m.Unclear ? "UNCLEAR MAYDAY: " : "MAYDAY: ") + (m.Member is { } who ? $"{who}, {m.Callsign}" : m.Callsign),
                m.Active ? m.Details : m.Outcome ?? "Resolved",
                $"{MainViewModel.Time(m.DeclaredAt, seconds: true)} ({(now - m.DeclaredAt).TotalMinutes:F0} min ago)",
                m.RescueCallsign is { } rescuer ? $"Rescue team: {rescuer} since {MainViewModel.Time(m.RescueDeployedAt ?? now)}" : "No rescue team",
                m.Active, m.Active && m.RescueUnitId is null, m.Unclear)));

        var candidates = _cop.Units
            .Where(u => u.Agency?.AiControlled != true && ResourceGroups.AgencyFor(u.Type) == AgencyType.Fire
                        && u.Status is UnitStatus.OnScene or UnitStatus.Operating && u.Crew is { RescueFor: null } crew
                        && crew.Holding(Qualifications.BreathingApparatus) >= 2 && !maydays.Any(m => m.Active && m.UnitId == u.Id))
            .OrderBy(u => u.Callsign, StringComparer.Ordinal)
            .Select(u => new OrderTarget(OrderTargetKind.Unit, u.Id, null,
                $"{u.Callsign} ({u.Crew!.Holding(Qualifications.BreathingApparatus)} BA" +
                (u.Crew.Holding(Qualifications.Usar) >= 2 ? ", USAR" : "") + (u.Crew.InRehab ? ", in rehab" : "") + ")"))
            .ToList();
        if (!candidates.Select(c => c.Label).SequenceEqual(RescueCandidates.Select(c => c.Label)))
        {
            var selected = RescueTeam?.Id;
            Replace(RescueCandidates, candidates);
            RescueTeam = RescueCandidates.FirstOrDefault(c => c.Id == selected) ?? RescueCandidates.FirstOrDefault();
        }

        RefreshPar(incident, now);

        var heard = _cop.HeardChannels();
        Replace(RadioChannels, _cop.Channels.Where(c => c.Info.Kind == ChannelKind.Radio && heard.Contains(c.Info.Id)).Select(c => c.Info.Id));
        var cleared = _cop.Channels.Where(c => c.EmergencyTraffic).Select(c => c.Info.Id).ToList();
        HasEmergencyTraffic = cleared.Count > 0;
        EmergencyTraffic = cleared.Count > 0 ? $"Emergency traffic only: {string.Join(", ", cleared)}" : "No channel under emergency traffic";

        var rows = _cop.Units
            .Where(u => u.Crew is not null && u.Agency?.AiControlled != true
                        && (!CommittedOnly || UnitStatusRules.IsCommitted(u.Status) || u.Crew.ReliefRequestedAt is not null))
            .OrderByDescending(u => u.AssignedIncidentId == incident && incident is not null)
            .ThenByDescending(u => UnitStatusRules.IsCommitted(u.Status))
            .ThenBy(u => u.Callsign, StringComparer.Ordinal)
            .Select(u => Row(u, now))
            .ToList();
        Replace(Crews, rows);
    }

    private void RefreshPar(Guid? incident, DateTimeOffset now)
    {
        var par = _cop.ParChecks.LastOrDefault(p => incident is null || p.IncidentId == incident || p.IncidentId is null);
        if (par is null)
        {
            ParSummary = "No PAR called yet.";
            ParProblem = false;
            Replace(ParLines, []);
            return;
        }

        var answered = par.Responses.Count;
        var missing = par.Responses.Values.Sum(r => r.Missing.Count);
        ParSummary = $"{par.Reason} at {MainViewModel.Time(par.RequestedAt)}: {answered} of {par.Expected.Count} answered" +
                     (missing > 0 ? $", {missing} MISSING" : par.AllAccounted ? ", all accounted for" : "");
        ParProblem = missing > 0 || (!par.Complete && now - par.RequestedAt > TimeSpan.FromMinutes(2));

        var lines = par.Responses.Values.OrderBy(r => r.Callsign, StringComparer.Ordinal).Select(r => new ParLine(
                r.UnitId, r.Callsign, $"{r.Accounted} of {r.Expected}" + (r.Missing.Count > 0 ? $": missing {string.Join(", ", r.Missing)}" : ""),
                r.Missing.Count > 0, false, r.Missing.FirstOrDefault()))
            .Concat(par.Expected.Where(e => !par.Responses.ContainsKey(e.Key)).OrderBy(e => e.Value, StringComparer.Ordinal)
                .Select(e => new ParLine(e.Key, e.Value, "no answer", false, true, null)))
            .ToList();
        Replace(ParLines, lines);
    }

    private static CrewRow Row(Unit unit, DateTimeOffset now)
    {
        var crew = unit.Crew!;
        var onShift = crew.OnShift(now);
        var over = now - crew.ShiftEnd;
        var shift = crew.PastShiftEnd(now)
            ? $"OVERTIME {Clock(over)} (shift ended {MainViewModel.Time(crew.ShiftEnd)})"
            : $"On duty {Clock(onShift)} · shift ends {MainViewModel.Time(crew.ShiftEnd)}";

        var onTask = crew.OnTask(now);
        var work = crew.RescueFor is not null ? "Rescue team"
            : crew.InRehab ? $"In rehab {(now - crew.RehabSince!.Value).TotalMinutes:F0} min"
            : crew.WorkingSince is not null ? $"On task {onTask.TotalMinutes:F0} min" + (crew.LastRehabEnded is { } rested ? $" (rehab ended {MainViewModel.Time(rested)})" : "")
            : StatusText(unit.Status);

        var condition = crew.ConditionReportedAt is { } said
            ? $"{crew.Condition} ({MainViewModel.Time(said)}): \"{crew.ConditionNote}\""
            : "Nothing reported";

        var qualifications = string.Join(" · ", Core.Model.Qualifications.All
            .Select(q => (q.Code, Count: crew.Holding(q.Code)))
            .Where(q => q.Count > 0)
            .Select(q => $"{q.Code} {q.Count}"));

        var notes = new List<string>();
        if (crew.ReliefRequestedAt is { } asked && (crew.RelievedAt is null || crew.RelievedAt < asked))
            notes.Add($"Relief crew coming ({(crew.ReliefBriefing ? "full briefing" : "quick changeover")}, asked {MainViewModel.Time(asked)})");
        else if (crew.RelievedAt is { } relieved)
            notes.Add($"Relieved at {MainViewModel.Time(relieved)}");
        if (crew.PeerSupportArrangedAt is { } support && (crew.PeerSupportGivenAt is null || crew.PeerSupportGivenAt < support))
            notes.Add($"Peer support arranged {MainViewModel.Time(support)}");
        else if (crew.PeerSupportGivenAt is { } given)
            notes.Add($"Peer support given {MainViewModel.Time(given)}");
        if (crew.Members.Any(m => m.Lapsed.Count > 0))
            notes.Add("Lapsed: " + string.Join(", ", crew.Members.SelectMany(m => m.Lapsed).GroupBy(q => q).Select(g => $"{g.Key} ×{g.Count()}")));

        // One line per person: "Sub-Officer Aoife Walsh: BA, USAR (lapsed: Swiftwater) — MISSING".
        var roster = string.Join(Environment.NewLine, crew.Members.Select(m =>
            $"{WorldResponder.RoleTitle(m.Role)} {m.Name}" +
            (m.Qualifications.Count > 0 ? $": {string.Join(", ", m.Qualifications)}" : "") +
            (m.Lapsed.Count > 0 ? $" (lapsed: {string.Join(", ", m.Lapsed)})" : "") +
            (m.Status == MemberStatus.OnDuty ? "" : $" — {Core.Events.EventDescriber.Humanize(m.Status).ToUpperInvariant()}")));

        return new CrewRow(
            unit.Id, unit.Callsign, $"{Core.Events.EventDescriber.Humanize(unit.Status)} · {crew.OnDuty}/{crew.Members.Count} on duty",
            shift, crew.PastShiftEnd(now), work, unit.Status == UnitStatus.Operating && onTask >= TimeSpan.FromMinutes(40) && crew.RescueFor is null,
            condition, crew.Condition is CrewCondition.Exhausted or CrewCondition.Shaken || (crew.Condition == CrewCondition.Tired && crew.ConditionReportedAt is not null),
            qualifications.Length > 0 ? qualifications : "No specialist qualifications", string.Join(" · ", notes), roster,
            unit.Status is UnitStatus.OnScene or UnitStatus.Operating, unit.Status != UnitStatus.OutOfService);
    }

    private static string StatusText(UnitStatus status) => Core.Events.EventDescriber.Humanize(status);

    private static string Clock(TimeSpan span) => $"{(int)span.TotalHours}:{span.Minutes:D2}";

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var list = items.ToList();
        if (list.SequenceEqual(target)) return;
        target.Clear();
        foreach (var item in list)
            target.Add(item);
    }
}
