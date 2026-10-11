using System.Security.Cryptography;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Emergency_Response_Simulator.Simulation.State;
using Emergency_Response_Simulator.Simulation.Systems;
using Microsoft.AspNetCore.SignalR;

namespace Emergency_Response_Simulator.Server;

/// <summary>A participant as the server knows them: identity, role, reconnect token and live connections.</summary>
public sealed class ParticipantRecord
{
    public Guid Id { get; init; }
    public required string Name { get; set; }
    public UserRole Role { get; set; }
    public required string Token { get; init; }
    public HashSet<string> Connections { get; } = [];

    /// <summary>When the last connection dropped, while they may still come back.</summary>
    public DateTimeOffset? DroppedAt { get; set; }

    public bool Present { get; set; } = true;

    public bool Truth => Roles.SeesTruth(Role);

    public CommandActor Actor => new(Id, Role, Name);
}

/// <summary>
/// One networked session (Phase 11 M2): its own engine, world, COP and services, built from the scenario the
/// instructor picked; its participants; and its life from lobby to end. Sessions share only the event store (keyed by
/// session) and the GIS database. Session and participant changes are events, stamped with whoever caused them.
/// </summary>
public sealed class SessionRuntime : IAsyncDisposable
{
    private static readonly char[] CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();

    private readonly object _lock = new();
    private readonly Dictionary<Guid, ParticipantRecord> _participants = [];
    private readonly Dictionary<Guid, Handover> _handovers = [];
    private readonly ServiceProvider _services;
    private readonly IReadOnlyList<IHostedService> _hosted;
    private readonly IHubContext<SimulationHub, ISimulationClient> _hub;

    private SessionRuntime(ServiceProvider services, ScenarioInfo scenario, string title, string code, TimeSpan codeLifetime,
        IHubContext<SimulationHub, ISimulationClient> hub, ILoggerFactory loggers)
    {
        _services = services;
        _hub = hub;
        Scenario = scenario;
        Title = title;
        Code = code;
        Engine = services.GetRequiredService<SimulationEngine>();
        Id = Engine.SessionId;
        CreatedAt = DateTimeOffset.UtcNow;
        ExpiresAt = CreatedAt + codeLifetime;
        Feed = new EventFeed(services.GetRequiredService<IEventStore>(), Id, hub, loggers.CreateLogger<EventFeed>());
        _hosted = services.GetServices<IHostedService>().ToList();
    }

    public Guid Id { get; }
    public string Code { get; }
    public string Title { get; }
    public ScenarioInfo Scenario { get; }
    public SessionState State { get; private set; } = SessionState.Lobby;
    public DateTimeOffset CreatedAt { get; }

    /// <summary>After this the code no longer lets anyone new in (participants can still resume).</summary>
    public DateTimeOffset ExpiresAt { get; }

    public IServiceProvider Services => _services;
    public SimulationEngine Engine { get; }
    public EventFeed Feed { get; }
    public CommandGate Gate { get; } = new();

    public bool Joinable(DateTimeOffset now) => State != SessionState.Ended && now < ExpiresAt;

    public IReadOnlyList<ParticipantRecord> Participants
    {
        get { lock (_lock) return _participants.Values.ToList(); }
    }

    /// <summary>Builds the session's own service graph on the shared store and GIS, and starts its engine (paused).</summary>
    public static async Task<SessionRuntime> CreateAsync(ScenarioInfo scenario, string title, IServiceProvider root, IConfiguration configuration,
        string code, TimeSpan codeLifetime)
    {
        var settings = new ConfigurationBuilder()
            .AddConfiguration(configuration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{SimulationOptions.SectionName}:{nameof(SimulationOptions.Scenario)}"] = ScenarioCatalog.SettingFor(scenario),
                [$"{SimulationOptions.SectionName}:{nameof(SimulationOptions.AutoStart)}"] = "false", // the instructor starts it
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(settings);
        services.AddSingleton(root.GetRequiredService<ILoggerFactory>());
        services.AddLogging();
        services.AddSingleton(root.GetRequiredService<IEventStore>());
        if (root.GetService<IGisService>() is { } gis) services.AddSingleton(gis);
        services.AddSimulation(settings);
        services.AddSingleton<IScenario, DemoRosterScenario>();

        var provider = services.BuildServiceProvider();
        var runtime = new SessionRuntime(provider, scenario, title, code, codeLifetime,
            root.GetRequiredService<IHubContext<SimulationHub, ISimulationClient>>(), root.GetRequiredService<ILoggerFactory>());
        foreach (var hosted in runtime._hosted)
            await hosted.StartAsync(CancellationToken.None);
        return runtime;
    }

    public static string NewCode() => string.Concat(Enumerable.Range(0, 6).Select(_ => CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]));

    public SessionInfo InfoFor(ParticipantRecord participant) => new(Id, Title, Code, State, participant.Truth, Broadcaster.ClockOf(Engine),
        _services.GetRequiredService<IAarService>().CanCompareBranches);

    public ParticipantRecord? Find(Guid participantId)
    {
        lock (_lock) return _participants.GetValueOrDefault(participantId);
    }

    public ParticipantRecord? FindByToken(string token)
    {
        lock (_lock) return _participants.Values.FirstOrDefault(p => CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(p.Token), System.Text.Encoding.UTF8.GetBytes(token)));
    }

    // ---- Participants ----

    /// <summary>The instructor who opened the session joins it first and records that it opened.</summary>
    public async Task<JoinTicket> OpenAsync(string instructorName, string connectionId)
    {
        var instructor = Admit(instructorName, UserRole.Instructor, connectionId);
        await PublishAsync(new SessionOpened(Scenario.Key, Title, instructor.Id), instructor);
        await PublishAsync(new ParticipantJoined(instructor.Id, instructor.Name, instructor.Role), instructor);
        return TicketFor(instructor);
    }

    /// <summary>Someone joins by the code. An exclusive role already held by someone present is refused.</summary>
    public async Task<JoinTicket> JoinAsync(string displayName, UserRole role, string connectionId)
    {
        if (State == SessionState.Ended) throw new HubException("That session has ended.");
        var participant = Admit(displayName, role, connectionId);
        await PublishAsync(new ParticipantJoined(participant.Id, participant.Name, participant.Role), participant);
        return TicketFor(participant);
    }

    private ParticipantRecord Admit(string displayName, UserRole role, string connectionId)
    {
        var name = displayName?.Trim() ?? "";
        if (name.Length is 0 or > 40) throw new HubException("Give a display name of up to 40 characters.");
        lock (_lock)
        {
            if (HolderOf(role) is { } holder)
                throw new HubException($"{Roles.Name(role)} is already held by {holder.Name}. Choose another role, or ask them to hand it over.");
            var participant = new ParticipantRecord
            {
                Id = Guid.NewGuid(), Name = name, Role = role,
                Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            };
            participant.Connections.Add(connectionId);
            _participants[participant.Id] = participant;
            return participant;
        }
    }

    /// <summary>A participant comes back on a new connection with their token. Back after being counted as gone: rejoined.</summary>
    public async Task<JoinTicket> ResumeAsync(ParticipantRecord participant, string connectionId)
    {
        bool rejoined;
        lock (_lock)
        {
            participant.Connections.Add(connectionId);
            participant.DroppedAt = null;
            rejoined = !participant.Present;
            if (rejoined && HolderOf(participant.Role, except: participant.Id) is not null)
                participant.Role = UserRole.Observer; // their position was filled while they were away
            participant.Present = true;
        }
        if (rejoined)
            await PublishAsync(new ParticipantJoined(participant.Id, participant.Name, participant.Role), participant);
        return TicketFor(participant);
    }

    public void Disconnected(Guid participantId, string connectionId)
    {
        lock (_lock)
        {
            if (!_participants.TryGetValue(participantId, out var participant)) return;
            participant.Connections.Remove(connectionId);
            if (participant.Connections.Count == 0 && participant.Present)
                participant.DroppedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Counts as gone anyone whose link dropped longer ago than the grace period.</summary>
    public async Task ExpireDroppedAsync(TimeSpan grace)
    {
        List<ParticipantRecord> gone;
        lock (_lock)
        {
            gone = _participants.Values.Where(p => p.Present && p.DroppedAt is { } at && DateTimeOffset.UtcNow - at >= grace).ToList();
            foreach (var participant in gone) participant.Present = false;
        }
        foreach (var participant in gone)
            await PublishAsync(new ParticipantLeft(participant.Id, "connection lost"), participant);
    }

    public async Task LeaveAsync(ParticipantRecord participant)
    {
        lock (_lock)
        {
            if (!participant.Present) return;
            participant.Present = false;
            participant.DroppedAt = null;
        }
        await PublishAsync(new ParticipantLeft(participant.Id, "left"), participant);
    }

    /// <summary>The instructor puts someone in a role. Instructor isn't a role that can be given: it needs the PIN.</summary>
    public async Task AssignRoleAsync(ParticipantRecord instructor, Guid participantId, UserRole role)
    {
        ParticipantRecord target;
        lock (_lock)
        {
            target = _participants.GetValueOrDefault(participantId) ?? throw new HubException("Unknown participant.");
            if (role == UserRole.Instructor || target.Role == UserRole.Instructor)
                throw new HubException("The instructor role is joined with the PIN, not assigned.");
            if (HolderOf(role, except: target.Id) is { } holder)
                throw new HubException($"{Roles.Name(role)} is held by {holder.Name}: have them hand it over.");
            target.Role = role;
        }
        await PublishAsync(new RoleAssigned(target.Id, role), instructor);
    }

    /// <summary>
    /// The holder of a role hands it to another participant, and takes another role themselves. Once the exercise is
    /// running this follows the Phase 8 handover model: the outgoing holder keeps the role while they hand over, for
    /// <see cref="CrewOptions.BriefedHandover"/> with a full briefing (the incoming one is told the role's open business)
    /// or <see cref="CrewOptions.QuickHandover"/> for a quick changeover (which passes none of it on; the review says so).
    /// In the lobby there is nothing to brief, so it happens at once.
    /// </summary>
    public async Task HandOverAsync(ParticipantRecord from, Guid toParticipantId, UserRole fromNewRole, bool fullBriefing = true)
    {
        ParticipantRecord to;
        UserRole role;
        lock (_lock)
        {
            to = _participants.GetValueOrDefault(toParticipantId) ?? throw new HubException("Unknown participant.");
            if (!to.Present) throw new HubException($"{to.Name} isn't in the session.");
            if (to.Id == from.Id) throw new HubException("Hand over to someone else.");
            role = from.Role;
            if (role == UserRole.Instructor || to.Role == UserRole.Instructor || fromNewRole == UserRole.Instructor)
                throw new HubException("The instructor role can't be handed over.");
            if (fromNewRole != to.Role && HolderOf(fromNewRole, except: from.Id) is { } holder)
                throw new HubException($"{Roles.Name(fromNewRole)} is held by {holder.Name}.");
            if (_handovers.Values.Any(h => h.From == from || h.To == from || h.From == to || h.To == to))
                throw new HubException("One of you is already in a handover.");
            if (State == SessionState.Lobby)
            {
                from.Role = fromNewRole;
                to.Role = role;
            }
        }

        if (State == SessionState.Lobby)
        {
            await PublishAsync(new RoleHandedOver(from.Id, to.Id, role, fromNewRole), from);
            return;
        }

        var crews = _services.GetRequiredService<CrewOptions>();
        var handover = new Handover(Guid.NewGuid(), from, to, role, to.Role, fromNewRole, fullBriefing,
            Engine.SimTime + (fullBriefing ? crews.BriefedHandover : crews.QuickHandover));
        lock (_lock) _handovers[handover.Id] = handover;
        await PublishAsync(new RoleHandoverBegun(handover.Id, from.Id, to.Id, role, fromNewRole, fullBriefing, handover.CompletesAt), from);
    }

    private sealed record Handover(Guid Id, ParticipantRecord From, ParticipantRecord To, UserRole Role, UserRole ToRole, UserRole FromNewRole,
        bool FullBriefing, DateTimeOffset CompletesAt);

    /// <summary>
    /// Completes handovers whose time has come (simulation time, so a paused clock pauses them too), and cancels any whose
    /// people left or whose roles changed meanwhile. The briefing is the role's open business as the COP shows it then.
    /// </summary>
    public async Task CompleteHandoversAsync()
    {
        List<(Handover Handover, string? Cancelled)> due;
        lock (_lock)
        {
            due = _handovers.Values
                .Select(h => (h, Cancelled: !h.From.Present || !h.To.Present ? "one of them left"
                    : h.From.Role != h.Role || h.To.Role != h.ToRole ? "their roles changed"
                    : State == SessionState.Ended ? "the session ended" : null))
                .Where(x => x.Cancelled is not null || Engine.SimTime >= x.h.CompletesAt)
                .ToList();
            foreach (var (handover, _) in due) _handovers.Remove(handover.Id);
        }

        foreach (var (handover, cancelled) in due)
        {
            if (cancelled is not null)
            {
                await PublishAsync(new RoleHandoverCancelled(handover.Id, cancelled), handover.From);
                continue;
            }
            var cop = _services.GetRequiredService<ICopService>();
            var filled = Participants.Where(p => p.Present).Select(p => p.Role).ToHashSet();
            await Gate.RunAsync(async () =>
            {
                var items = RoleBriefing.For(handover.Role, cop, filled.Contains);
                lock (_lock)
                {
                    handover.From.Role = handover.FromNewRole;
                    handover.To.Role = handover.Role;
                }
                await PublishAsync(new RoleHandedOver(handover.From.Id, handover.To.Id, handover.Role, handover.FromNewRole,
                    handover.Id, handover.FullBriefing, items), handover.From);
                return true;
            });
        }
    }

    /// <summary>
    /// Someone put in an ICS position takes that role in the session (Phase 11 M3), recorded as an assignment by whoever
    /// staffed it. A role someone else holds has to be handed over instead.
    /// </summary>
    public async Task TakePositionAsync(ParticipantRecord by, Guid participantId, UserRole role)
    {
        ParticipantRecord target;
        lock (_lock)
        {
            target = _participants.GetValueOrDefault(participantId) ?? throw new HubException("Unknown participant.");
            if (target.Role == role) return;
            if (target.Role == UserRole.Instructor) throw new HubException("The instructor can't take an ICS position.");
            if (HolderOf(role, except: target.Id) is { } holder)
                throw new HubException($"{Roles.Name(role)} is held by {holder.Name}: have them hand it over.");
            target.Role = role;
        }
        await PublishAsync(new RoleAssigned(target.Id, role), by);
    }

    private ParticipantRecord? HolderOf(UserRole role, Guid? except = null) =>
        Roles.IsExclusive(role) ? _participants.Values.FirstOrDefault(p => p.Present && p.Role == role && p.Id != except) : null;

    private JoinTicket TicketFor(ParticipantRecord p) => new(Id, Code, p.Id, p.Token, p.Name, p.Role, p.Truth);

    // ---- Lifecycle ----

    /// <summary>The instructor starts the exercise: the clock runs and commands are accepted.</summary>
    public async Task BeginAsync(ParticipantRecord instructor)
    {
        if (State == SessionState.Ended) throw new HubException("That session has ended.");
        using (CommandContext.As(instructor.Actor))
            await Engine.StartAsync();
        if (State == SessionState.Lobby)
        {
            State = SessionState.Running;
            await NotifyAsync();
        }
    }

    /// <summary>The instructor ends the session: the clock stops for good and no more commands are taken.</summary>
    public async Task EndAsync(ParticipantRecord instructor, string reason)
    {
        if (State == SessionState.Ended) return;
        using (CommandContext.As(instructor.Actor))
        {
            await Engine.PauseAsync();
            await Engine.PublishAsync(new SessionEnded(string.IsNullOrWhiteSpace(reason) ? "Ended by the instructor" : reason.Trim(), instructor.Id),
                EventVisibility.Perceived, EventSources.Session);
        }
        State = SessionState.Ended;
        foreach (var hosted in _hosted.Reverse())
            await hosted.StopAsync(CancellationToken.None);
        await NotifyAsync();
    }

    private async Task NotifyAsync()
    {
        foreach (var participant in Participants.Where(p => p.Connections.Count > 0))
            foreach (var connection in participant.Connections.ToList())
                await _hub.Clients.Client(connection).SessionChanged(InfoFor(participant));
    }

    private async Task PublishAsync(DomainEvent payload, ParticipantRecord actor)
    {
        using (CommandContext.As(actor.Actor))
            await Engine.PublishAsync(payload, EventVisibility.Perceived, EventSources.Session);
    }

    public async ValueTask DisposeAsync()
    {
        Feed.Dispose();
        if (State != SessionState.Ended)
            foreach (var hosted in _hosted.Reverse())
                await hosted.StopAsync(CancellationToken.None);
        await _services.DisposeAsync();
    }
}

/// <summary>
/// Serialises a session's commands (Phase 11 M2): each is checked against the COP and its event recorded before the
/// next is looked at, so two players dispatching the same unit at once can't both succeed.
/// </summary>
public sealed class CommandGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> RunAsync<T>(Func<Task<T>> command, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await command();
        }
        finally
        {
            _gate.Release();
        }
    }
}
