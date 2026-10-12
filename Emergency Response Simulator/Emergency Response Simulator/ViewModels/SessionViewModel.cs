using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Remote;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>A participant in the SESSION tab.</summary>
public sealed record ParticipantRow(Guid Id, string Name, string Role, bool Present, bool IsMe, string Status)
{
    public override string ToString() => $"{Name} ({Role})";
}

/// <summary>
/// The networked session in the right panel (Phase 11 M2): its code and state, who is in it and in which role, the
/// instructor's start and end controls and role assignment, and handing your own role to someone else. Hidden in a
/// standalone session. Participants come from the event stream (the COP); actions go to the server, which checks them.
/// </summary>
public partial class SessionViewModel : ObservableObject
{
    private readonly ICopService _cop;
    private readonly SimulationConnection? _connection;
    private Action<CommandResult, string?>? _report;

    private readonly Permissions? _permissions;

    public SessionViewModel(ICopService cop, SimulationConnection? connection, Permissions? permissions = null)
    {
        _cop = cop;
        _connection = connection;
        _permissions = permissions;
        if (connection is not null)
            connection.StateChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(Refresh);
    }

    public bool IsNetworked => _connection is not null;

    /// <summary>This participant sees ground truth now (the instructor, or an observer granted it, Phase 11 M6).</summary>
    public bool SeesTruth => _connection?.Session?.Truth == true;

    public IReadOnlyList<UserRole> RoleChoices { get; } = Enum.GetValues<UserRole>().Where(r => r != UserRole.Instructor).ToList();

    public ObservableCollection<ParticipantRow> Participants { get; } = [];

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _you = "";
    [ObservableProperty] private bool _isInstructor;
    [ObservableProperty] private bool _inLobby;
    [ObservableProperty] private bool _ended;
    [ObservableProperty] private string _tabHeader = "SESSION";
    [ObservableProperty] private ParticipantRow? _selected;
    [ObservableProperty] private UserRole _assignRole = UserRole.Operations;
    [ObservableProperty] private UserRole _myNewRole = UserRole.Observer;
    [ObservableProperty] private string _endReason = "Exercise complete";
    [ObservableProperty] private bool _fullBriefing = true;
    [ObservableProperty] private string _functions = "";
    [ObservableProperty] private string _inherited = "";
    [ObservableProperty] private bool _hasInherited;
    [ObservableProperty] private string _handover = "";
    [ObservableProperty] private bool _hasHandover;

    /// <summary>Where the main window reports what happened to a session action.</summary>
    public void ReportTo(Action<CommandResult, string?> report) => _report = report;

    [RelayCommand]
    private Task StartAsync() => Run(() => _connection!.StartSessionAsync(), "Exercise started");

    [RelayCommand]
    private Task EndAsync()
    {
        var confirm = MessageBox.Show("End the session for everyone? The clock stops and no more commands are taken; the record stays for the review.",
            "End session", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        return confirm == MessageBoxResult.OK ? Run(() => _connection!.EndSessionAsync(EndReason), "Session ended") : Task.CompletedTask;
    }

    [RelayCommand]
    private Task AssignAsync() => Selected is { } who
        ? Run(() => _connection!.AssignRoleAsync(who.Id, AssignRole), $"{who.Name} is now {Roles.Name(AssignRole)}")
        : Fail("Choose a participant.");

    /// <summary>Lets the selected observer see ground truth (Phase 11 M6).</summary>
    [RelayCommand]
    private Task GrantTruthAsync() => Selected is { } who
        ? Run(() => _connection!.SetObserverTruthAsync(who.Id, true), $"{who.Name} sees ground truth")
        : Fail("Choose an observer.");

    [RelayCommand]
    private Task RevokeTruthAsync() => Selected is { } who
        ? Run(() => _connection!.SetObserverTruthAsync(who.Id, false), $"{who.Name} sees what the players see")
        : Fail("Choose an observer.");

    [RelayCommand]
    private Task HandOverAsync() => Selected is { IsMe: false } who
        ? Run(() => _connection!.HandOverRoleAsync(who.Id, MyNewRole, FullBriefing),
            InLobby ? $"Your role handed to {who.Name}" : $"Handing your role to {who.Name}" + (FullBriefing ? " with a full briefing" : " (quick changeover)"))
        : Fail("Choose someone else to hand your role to.");

    private async Task Run(Func<Task> action, string done)
    {
        try
        {
            await action();
            _report?.Invoke(new CommandResult(true), done);
        }
        catch (Exception ex)
        {
            _report?.Invoke(CommandResult.Fail(RemoteErrors.Message(ex)), null);
        }
        Refresh();
    }

    private Task Fail(string message)
    {
        _report?.Invoke(CommandResult.Fail(message), null);
        return Task.CompletedTask;
    }

    public void Refresh()
    {
        if (_connection?.Session is not { } session || _connection.Ticket is not { } ticket) return;
        var people = _cop.Participants;
        var me = people.FirstOrDefault(p => p.Id == ticket.ParticipantId);
        IsInstructor = (me?.Role ?? ticket.Role) == UserRole.Instructor;
        InLobby = session.State == SessionState.Lobby;
        Ended = session.State == SessionState.Ended;
        var present = people.Count(p => p.Present);
        Summary = $"{session.Title} · code {session.Code} · " + session.State switch
        {
            SessionState.Lobby => "in the lobby: waiting for the instructor to start",
            SessionState.Running => _connection.Connected ? "running" : "running (reconnecting…)",
            _ => "ended",
        };
        You = $"You: {me?.DisplayName ?? ticket.DisplayName}, {Roles.Name(me?.Role ?? ticket.Role)}";
        TabHeader = $"SESSION · {present}" + (InLobby ? " · LOBBY" : Ended ? " · ENDED" : "");

        // What the role may do (Phase 11 M3), and any handover under way.
        _permissions?.Refresh();
        var functions = _permissions?.Functions ?? [];
        Functions = IsInstructor ? "You may give any command (recorded as the instructor)."
            : functions.Count == 0 ? "Your role gives no commands." : $"Your functions: {string.Join(", ", functions)}.";
        var inherited = _permissions?.Inherited ?? [];
        HasInherited = inherited.Count > 0;
        Inherited = HasInherited ? $"Unfilled, so yours as IC: {string.Join(", ", inherited)}." : "";
        string Name(Guid id) => people.FirstOrDefault(p => p.Id == id)?.DisplayName ?? "someone";
        var handing = people.FirstOrDefault(p => p.HandingOver is not null && (p.Id == ticket.ParticipantId || p.HandingOver.ToParticipantId == ticket.ParticipantId));
        HasHandover = handing is not null;
        Handover = handing?.HandingOver is { } h
            ? (handing.Id == ticket.ParticipantId ? $"Handing {Roles.Name(handing.Role)} to {Name(h.ToParticipantId)}" : $"Taking over {Roles.Name(handing.Role)} from {handing.DisplayName}")
              + (h.FullBriefing ? " with a full briefing" : " in a quick changeover") + $", done at {MainViewModel.Time(h.CompletesAt)}."
            : "";

        var rows = people.Select(p => new ParticipantRow(p.Id, p.DisplayName, Roles.Name(p.Role), p.Present, p.Id == ticket.ParticipantId,
            p.Present ? $"since {MainViewModel.Time(p.JoinedAt)}" : $"away since {MainViewModel.Time(p.LeftAt!.Value)}")).ToList();
        if (!rows.SequenceEqual(Participants))
        {
            var chosen = Selected?.Id;
            Participants.Clear();
            foreach (var row in rows) Participants.Add(row);
            Selected = Participants.FirstOrDefault(p => p.Id == chosen);
        }
    }
}
