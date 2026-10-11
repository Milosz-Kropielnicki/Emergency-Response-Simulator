using System.Text.Json;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.State;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Emergency_Response_Simulator.Client;

/// <summary>Where and as whom a client connects (bound from configuration: --Server, --Role, --Pin).</summary>
public sealed record ClientOptions(Uri Server, string? Role = null, string? Pin = null)
{
    public bool WantsTruth => string.Equals(Role, SimulationHubProtocol.InstructorRole, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One client's link to the simulation server (Phase 11 M1). It keeps the mirror of the event stream, the clock, the
/// traffic pictures and (for an instructor) the world snapshot up to date from the server's pushes, and carries
/// commands to the server. When the link drops it reconnects and asks for the stream from the last sequence it
/// applied, so the COP resumes with no gaps and no duplicates.
/// </summary>
public sealed class SimulationConnection : IAsyncDisposable, IWorldSnapshotSource
{
    private readonly HubConnection _hub;
    private readonly CancellationTokenSource _disposing = new();
    private ClockState _clock = new(default, false, 1, DateTimeOffset.UtcNow);
    private DateTimeOffset _clockReceivedAt = DateTimeOffset.UtcNow;
    private bool _feedWanted;

    /// <param name="configure">Lets tests route the connection through an in-process server.</param>
    public SimulationConnection(ClientOptions options, Action<HttpConnectionOptions>? configure = null)
    {
        Options = options;
        var query = $"?{SimulationHubProtocol.RoleQuery}={Uri.EscapeDataString(options.Role ?? "")}" +
                    (options.Pin is { Length: > 0 } pin ? $"&{SimulationHubProtocol.PinQuery}={Uri.EscapeDataString(pin)}" : "");
        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(options.Server, SimulationHubProtocol.Path + query), http => configure?.Invoke(http))
            .WithAutomaticReconnect(new KeepTrying())
            .AddJsonProtocol(json => WireJson.Configure(json.PayloadSerializerOptions))
            .Build();
        _hub.ServerTimeout = TimeSpan.FromSeconds(30);

        _hub.On<IReadOnlyList<SimEvent>>(nameof(ISimulationClient.Events), events => Mirror.Accept(events));
        _hub.On<ClockState>(nameof(ISimulationClient.Clock), clock =>
        {
            _clock = clock;
            _clockReceivedAt = DateTimeOffset.UtcNow;
        });
        _hub.On<TrafficState>(nameof(ISimulationClient.TrafficFeed), traffic => TrafficFeed.Publish(traffic.ToSnapshot()));
        _hub.On<TrafficState>(nameof(ISimulationClient.LiveTraffic), traffic => LiveTraffic.Publish(traffic.ToSnapshot()));
        _hub.On<WorldSnapshot>(nameof(ISimulationClient.WorldSnapshot), snapshot => Snapshot = snapshot);

        _hub.Reconnecting += _ =>
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        };
        _hub.Reconnected += async _ =>
        {
            Reconnects++;
            await HelloAsync(_disposing.Token);
            if (_feedWanted) await _hub.InvokeAsync("Subscribe", Mirror.LastSequence, _disposing.Token);
            StateChanged?.Invoke(this, EventArgs.Empty);
        };
        _hub.Closed += async _ =>
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            if (!_disposing.IsCancellationRequested) await RestartAsync();
        };
    }

    public ClientOptions Options { get; }

    public MirrorEventStore Mirror { get; } = new();

    /// <summary>The traffic command's tools see, as the server last sent it.</summary>
    public TrafficFeed TrafficFeed { get; } = new(new TimeOfDayTraffic());

    /// <summary>The traffic as it really is: filled only for an instructor connection.</summary>
    public LiveTraffic LiveTraffic { get; } = new(new TimeOfDayTraffic());

    /// <summary>Ground truth: filled only for an instructor connection.</summary>
    public WorldSnapshot Snapshot { get; private set; } = WorldSnapshot.Empty;

    public SessionInfo? Session { get; private set; }

    public bool Connected => _hub.State == HubConnectionState.Connected;

    public int Reconnects { get; private set; }

    /// <summary>Raised when the link drops or comes back (on a background thread).</summary>
    public event EventHandler? StateChanged;

    /// <summary>The engine clock, moved on locally between the server's pushes while it runs.</summary>
    public ClockState Clock
    {
        get
        {
            var clock = _clock;
            return clock.IsRunning
                ? clock with { SimTime = clock.SimTime + (DateTimeOffset.UtcNow - _clockReceivedAt) * clock.TimeScale }
                : clock;
        }
    }

    /// <summary>Connects and learns which session the server runs and what this connection may see.</summary>
    public async Task<SessionInfo> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _hub.StartAsync(cancellationToken);
        return await HelloAsync(cancellationToken);
    }

    /// <summary>Starts the event stream after the last sequence applied. Call once the COP is following the mirror.</summary>
    public async Task StartFeedAsync(CancellationToken cancellationToken = default)
    {
        _feedWanted = true;
        await _hub.InvokeAsync("Subscribe", Mirror.LastSequence, cancellationToken);
    }

    /// <summary>Calls a service contract method on the server; the result comes back in the wire format.</summary>
    public async Task<JsonElement?> CallAsync(string service, string method, JsonElement[] args, CancellationToken cancellationToken = default) =>
        await _hub.InvokeAsync<JsonElement?>("Call", service, method, args, cancellationToken);

    /// <summary>Drops the link without disposing (tests: a client crash or network loss).</summary>
    public Task DropAsync() => _hub.StopAsync();

    private async Task<SessionInfo> HelloAsync(CancellationToken cancellationToken)
    {
        var info = await _hub.InvokeAsync<SessionInfo>("Hello", cancellationToken);
        if (Session is { } known && known.SessionId != info.SessionId)
            throw new InvalidOperationException("The server is now running a different session.");
        Session = info;
        _clock = info.Clock;
        _clockReceivedAt = DateTimeOffset.UtcNow;
        return info;
    }

    /// <summary>After the automatic reconnects give up (or a manual stop), keep trying until disposed.</summary>
    private async Task RestartAsync()
    {
        while (!_disposing.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _disposing.Token);
                if (_hub.State != HubConnectionState.Disconnected) return;
                await _hub.StartAsync(_disposing.Token);
                Reconnects++;
                await HelloAsync(_disposing.Token);
                if (_feedWanted) await _hub.InvokeAsync("Subscribe", Mirror.LastSequence, _disposing.Token);
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            catch (OperationCanceledException) when (_disposing.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Server still away: try again shortly.
            }
        }
    }

    /// <summary>Reconnects without a limit: 0, 2, 5 then every 10 seconds.</summary>
    private sealed class KeepTrying : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) => retryContext.PreviousRetryCount switch
        {
            0 => TimeSpan.Zero,
            1 => TimeSpan.FromSeconds(2),
            2 => TimeSpan.FromSeconds(5),
            _ => TimeSpan.FromSeconds(10),
        };
    }

    public async ValueTask DisposeAsync()
    {
        _disposing.Cancel();
        await _hub.DisposeAsync();
    }
}
