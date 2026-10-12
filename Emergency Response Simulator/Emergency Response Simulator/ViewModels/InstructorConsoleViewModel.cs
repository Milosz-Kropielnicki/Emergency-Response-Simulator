using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Instructor;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.ViewModels;

public sealed record InjectRow(int Number, string At, string Description, InjectStatus Status, string State, bool CanFire, bool CanHold, bool CanRelease, bool CanSkip);

/// <summary>A template parameter the instructor fills in.</summary>
public sealed partial class ParameterField(InjectParameter parameter) : ObservableObject
{
    public InjectParameter Parameter { get; } = parameter;
    public string Label => Parameter.Label;
    public IReadOnlyList<string> Choices => Parameter.Choices ?? [];
    public bool IsChoice => Parameter.Kind == InjectParameterKind.Choice;
    public bool IsText => !IsChoice;

    [ObservableProperty] private string _value = parameter.Default;
}

public sealed record AiRoleRow(string Key, string Name, string System, string Covers, bool TakenOver, int Waiting, string? PlayedBy, bool CanGive)
{
    public string State => TakenOver ? Waiting > 0 ? $"YOU · {Waiting} waiting" : "YOU" : PlayedBy is { } who ? $"run by {who}" : "Simulation";
    public bool Human => PlayedBy is not null;
    public bool Givable => CanGive && PlayedBy is null;
}

/// <summary>Something waiting for the instructor in a role they play, with their answer.</summary>
public sealed partial class WaitingRow(AiRoleItem item) : ObservableObject
{
    public AiRoleItem Item { get; } = item;
    public string Role => Item.Role;
    public string Summary => Item.Summary;
    public string Since => MainViewModel.Time(Item.Since);
    public bool HasQuantity => Item.Quantity is not null;
    public bool IsReply => Item.Kind == AiRoleItemKind.Notification;
    public string ApproveLabel => Item.Kind switch
    {
        AiRoleItemKind.Notification => "Send reply",
        AiRoleItemKind.Invitation => "Join",
        AiRoleItemKind.Assistance => "Send units",
        _ => "Approve",
    };
    public bool CanDecline => !IsReply;

    [ObservableProperty] private string _quantity = item.Quantity?.ToString() ?? "";
    [ObservableProperty] private string _text = "";
}

/// <param name="Desk">What waits on their role's desk (Phase 12), e.g. "7 · 3 overdue".</param>
public sealed record PlayerRow(string Name, string Role, string Commands, string LastCommand, string Idle, bool IsIdle, string LastAction,
    string Desk = "", bool IsOverloaded = false, string Oldest = "");

public sealed record FeedRow(string Time, string Who, string Text);

/// <summary>
/// The instructor console (Phase 11 M4), its own window: the scenario script live (fire, hold, release, skip, ad hoc),
/// the players (activity, idleness, alerts, radio load, bookmarks), role-play as anyone in the world, and the roles the
/// simulation plays, which the instructor can take over. Actions go through <see cref="IInstructorService"/>: the
/// server's in a networked session (which refuses anyone but the instructor), the local one standalone.
/// </summary>
public partial class InstructorConsoleViewModel : ObservableObject
{
    private readonly IInstructorService _console;
    private readonly ICopService _cop;
    private readonly IEventStore _store;
    private readonly Guid _sessionId;
    private readonly ISimulationControl _clock;
    private readonly List<SimEvent> _events = [];
    private bool _refreshing;

    public InstructorConsoleViewModel(IInstructorService console, CopView cop, IEventStore store, SimulationSession session, ISimulationControl clock,
        SimulationConnection? connection = null)
    {
        _console = console;
        _cop = cop;
        _store = store;
        _sessionId = session.Id;
        _clock = clock;
        IsAvailable = connection is null || connection.Ticket?.Role == UserRole.Instructor;
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, async (_, _) => await RefreshAsync(), Application.Current.Dispatcher);
        timer.Start();
    }

    /// <summary>The instructor in a networked session; anyone standalone.</summary>
    public bool IsAvailable { get; }

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _failed;

    private void Report(CommandResult result, string? done = null)
    {
        Failed = !result.Succeeded;
        Status = result.Succeeded ? result.Detail ?? done ?? "Done" : result.Error ?? "Refused";
    }

    private async Task Run(Func<Task<CommandResult>> action, string? done = null)
    {
        try
        {
            Report(await action(), done);
        }
        catch (Exception ex)
        {
            Report(CommandResult.Fail(RemoteErrors.Message(ex)));
        }
        await RefreshAsync();
    }

    // ---- Injects ----

    public ObservableCollection<InjectRow> Injects { get; } = [];
    public ObservableCollection<InjectTemplate> Templates { get; } = [];
    public ObservableCollection<ParameterField> Fields { get; } = [];
    [ObservableProperty] private InjectTemplate? _template;
    [ObservableProperty] private string _scriptSummary = "";

    partial void OnTemplateChanged(InjectTemplate? value)
    {
        Fields.Clear();
        foreach (var parameter in value?.Parameters ?? [])
            Fields.Add(new ParameterField(parameter));
    }

    [RelayCommand] private Task FireAsync(int number) => Run(() => _console.FireInjectAsync(number));
    [RelayCommand] private Task HoldAsync(int number) => Run(() => _console.HoldInjectAsync(number));
    [RelayCommand] private Task ReleaseAsync(int number) => Run(() => _console.ReleaseInjectAsync(number));
    [RelayCommand] private Task SkipAsync(int number) => Run(() => _console.SkipInjectAsync(number));

    [RelayCommand]
    private Task FireAdHocAsync() => Template is { } template
        ? Run(() => _console.FireAdHocAsync(template.Key, Fields.ToDictionary(f => f.Parameter.Key, f => f.Value)))
        : Task.CompletedTask;

    // ---- Role-play ----

    public IReadOnlyList<RolePlayKind> RolePlayKinds { get; } = Enum.GetValues<RolePlayKind>();
    public ObservableCollection<string> Channels { get; } = [];
    public ObservableCollection<IncidentChoice> Incidents { get; } = [];
    [ObservableProperty] private RolePlayKind _rolePlayKind = RolePlayKind.Journalist;
    [ObservableProperty] private string _rolePlayAs = "RTÉ News";
    [ObservableProperty] private string? _rolePlayChannel;
    [ObservableProperty] private IncidentChoice? _rolePlayIncident;
    [ObservableProperty] private string _rolePlayText = "";

    public bool IsRadio => RolePlayKind == RolePlayKind.Radio;

    partial void OnRolePlayKindChanged(RolePlayKind value)
    {
        OnPropertyChanged(nameof(IsRadio));
        RolePlayAs = value switch
        {
            RolePlayKind.Radio => "Engine 4",
            RolePlayKind.Agency => "Dublin City Council",
            RolePlayKind.Caller => "999 caller (mobile)",
            RolePlayKind.Journalist => "RTÉ News",
            RolePlayKind.Politician => "Deputy Ciara Nolan TD",
            _ => "Garda Inspector, Pearse Street",
        };
    }

    [RelayCommand]
    private async Task SayAsync()
    {
        await Run(() => _console.RolePlayAsync(new RolePlayRequest(RolePlayKind, RolePlayAs, RolePlayText, IsRadio ? RolePlayChannel : null,
            RolePlayIncident?.Id)));
        if (!Failed) RolePlayText = "";
    }

    // ---- Roles the simulation plays ----

    public ObservableCollection<AiRoleRow> AiRoles { get; } = [];
    public ObservableCollection<WaitingRow> Waiting { get; } = [];
    [ObservableProperty] private string _rolesSummary = "AI ROLES";

    [RelayCommand] private Task TakeOverAsync(string key) => Run(() => _console.TakeOverAsync(key));
    [RelayCommand] private Task HandBackAsync(string key) => Run(() => _console.HandBackAsync(key));

    /// <summary>Agency chiefs present, who can be given an agency's control room to run (Phase 11 M5).</summary>
    public ObservableCollection<ParticipantChoice> Chiefs { get; } = [];
    [ObservableProperty] private ParticipantChoice? _chief;

    [RelayCommand]
    private Task GiveAsync(string key) => Chief?.Id is { } who
        ? Run(() => _console.GiveToParticipantAsync(key, who))
        : Run(() => Task.FromResult(CommandResult.Fail("Choose an agency chief to give it to (someone who joined as one, or whom you made one).")));

    [RelayCommand]
    private Task ApproveAsync(WaitingRow row) => Run(() => _console.DecideAsync(new AiRoleDecision(row.Item.Id, true,
        int.TryParse(row.Quantity, out var quantity) ? quantity : null, row.Text)));

    [RelayCommand]
    private Task DeclineAsync(WaitingRow row) => Run(() => _console.DecideAsync(new AiRoleDecision(row.Item.Id, false, null, row.Text)));

    // ---- Players ----

    public ObservableCollection<PlayerRow> Players { get; } = [];
    public ObservableCollection<FeedRow> Feed { get; } = [];
    public ObservableCollection<string> Alerts { get; } = [];
    public ObservableCollection<string> ChannelLoads { get; } = [];
    public ObservableCollection<string> Bookmarks { get; } = [];
    [ObservableProperty] private string _playersSummary = "PLAYERS";
    [ObservableProperty] private string _bookmarkNote = "";

    [RelayCommand]
    private async Task BookmarkAsync()
    {
        await Run(() => _console.BookmarkAsync(BookmarkNote), "Bookmarked");
        if (!Failed) BookmarkNote = "";
    }

    // ---- Exercise control (Phase 12) ----

    /// <summary>Who an exercise control message goes to: everyone (no id) or one participant present.</summary>
    public ObservableCollection<ParticipantChoice> MessageTargets { get; } = [];
    [ObservableProperty] private ParticipantChoice? _messageTarget;
    [ObservableProperty] private string _messageText = "";

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        await Run(() => _console.SendExerciseMessageAsync(MessageText, MessageTarget?.Id));
        if (!Failed) MessageText = "";
    }

    // ---- Refresh ----

    public async Task RefreshAsync()
    {
        if (!IsAvailable || _refreshing) return;
        _refreshing = true;
        try
        {
            await foreach (var simEvent in _store.ReadAsync(_sessionId, _events.Count > 0 ? _events[^1].Sequence : 0))
                _events.Add(simEvent);
            var now = _clock.SimTime;
            var start = _events.FirstOrDefault(e => e.Payload is SimulationStarted)?.SimTime;

            var script = await _console.ListInjectsAsync();
            Sync(Injects, script.Select(i => new InjectRow(i.Number, $"+{(int)i.At.TotalMinutes:00}:{i.At.Seconds:00}", i.Description, i.Status,
                i.Status switch
                {
                    InjectStatus.Pending when start is { } s && now - s < i.At => $"in {Clock(i.At - (now - s))}" + (i.Conditional ? " (conditional)" : ""),
                    InjectStatus.Pending => i.Conditional ? "waiting for its condition" : "due",
                    InjectStatus.Fired => $"fired {MainViewModel.Time(i.FiredAt!.Value)}",
                    _ => i.Status.ToString().ToLowerInvariant(),
                },
                i.Status is InjectStatus.Pending or InjectStatus.Held or InjectStatus.Expired, i.Status == InjectStatus.Pending,
                i.Status == InjectStatus.Held, i.Status is InjectStatus.Pending or InjectStatus.Held or InjectStatus.Expired)).ToList());
            ScriptSummary = $"{script.Count(i => i.Status == InjectStatus.Fired)} of {script.Count} fired · {script.Count(i => i.Status == InjectStatus.Held)} held · " +
                            $"{script.Count(i => i.Status == InjectStatus.Skipped)} skipped";
            if (Templates.Count == 0)
            {
                foreach (var template in await _console.ListTemplatesAsync()) Templates.Add(template);
                Template ??= Templates.FirstOrDefault();
            }

            Sync(AiRoles, (await _console.ListAiRolesAsync()).Select(r => new AiRoleRow(r.Key, r.Name, r.System, r.Covers, r.TakenOver, r.Waiting, r.PlayedBy, r.CanGive)).ToList());
            Sync(Chiefs, _cop.Participants.Where(p => p.Present && p.Role == UserRole.AgencyChief).Select(p => new ParticipantChoice(p.Id, p.DisplayName)).ToList());
            Chief ??= Chiefs.FirstOrDefault();
            var target = MessageTarget?.Id;
            Sync(MessageTargets, [new ParticipantChoice(null, "Everyone"), .. _cop.Participants.Where(p => p.Present && p.Role != UserRole.Instructor)
                .Select(p => new ParticipantChoice(p.Id, $"{p.DisplayName} ({Roles.Name(p.Role)})"))]);
            MessageTarget = MessageTargets.FirstOrDefault(t => t.Id == target) ?? MessageTargets[0];
            var waiting = await _console.ListWaitingAsync();
            // Keep the rows (and what the instructor has typed) for items still waiting.
            foreach (var gone in Waiting.Where(w => waiting.All(i => i.Id != w.Item.Id)).ToList()) Waiting.Remove(gone);
            foreach (var item in waiting.Where(i => Waiting.All(w => w.Item.Id != i.Id))) Waiting.Add(new WaitingRow(item));
            RolesSummary = waiting.Count > 0 ? $"AI ROLES · {waiting.Count} WAITING" : "AI ROLES";

            var monitor = PlayerMonitor.Build(_events, _cop, now);
            Sync(Players, monitor.Players.Select(p => new PlayerRow(p.Name + (p.Present ? "" : " (away)"), Roles.Name(p.Role), $"{p.Commands} ({p.Recent} recent)",
                p.LastCommandAt is { } at ? MainViewModel.Time(at) : "none", p.Idle ? $"IDLE {Clock(p.IdleFor)}" : Clock(p.IdleFor), p.Idle, p.LastAction ?? "",
                p.Waiting == 0 ? "clear" : (p.Overloaded ? "OVERLOADED " : "") + $"{p.Waiting}" + (p.Overdue > 0 ? $" · {p.Overdue} overdue" : ""),
                p.Overloaded, p.Oldest ?? "")).ToList());
            Sync(Feed, monitor.Feed.Take(40).Select(f => new FeedRow(MainViewModel.Time(f.At), f.Who, f.Text)).ToList());
            Sync(Alerts, monitor.Alerts.Select(a => $"{a.Title} · {Clock(a.Age)} unacknowledged").ToList());
            Sync(ChannelLoads, monitor.Channels.Take(8).Select(c => $"{c.Name}: {c.Utilisation:P0}{(c.Busy ? " · on air" : "")}{(c.EmergencyTraffic ? " · EMERGENCY TRAFFIC" : "")}").ToList());
            Sync(Bookmarks, monitor.Bookmarks.Select(b => $"{MainViewModel.Time(b.At)}  {b.Note}").ToList());
            var idle = monitor.Players.Count(p => p.Idle);
            var overloaded = monitor.Players.Count(p => p.Overloaded);
            PlayersSummary = idle + overloaded == 0 ? $"PLAYERS · {monitor.Players.Count(p => p.Present)}"
                : "PLAYERS" + (idle > 0 ? $" · {idle} IDLE" : "") + (overloaded > 0 ? $" · {overloaded} OVERLOADED" : "");

            Sync(Channels, _cop.Channels.Select(c => c.Info.Id).ToList());
            RolePlayChannel ??= Channels.FirstOrDefault();
            Sync(Incidents, _cop.Incidents.Where(i => i.Status != IncidentStatus.Closed).Select(i => new IncidentChoice(i.Id, $"{i.Number} {i.Name ?? i.Address}")).ToList());
            RolePlayIncident ??= Incidents.LastOrDefault();
        }
        catch (Exception ex)
        {
            Report(CommandResult.Fail(RemoteErrors.Message(ex)));
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static string Clock(TimeSpan span) => span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes}:{span.Seconds:00}";

    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> rows)
    {
        if (target.SequenceEqual(rows)) return;
        target.Clear();
        foreach (var row in rows) target.Add(row);
    }
}

public sealed record IncidentChoice(Guid Id, string Label)
{
    public override string ToString() => Label;
}
