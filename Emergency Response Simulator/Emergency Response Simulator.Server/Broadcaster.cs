using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.State;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Emergency_Response_Simulator.Server;

/// <summary>
/// For every live session, pushes what isn't in the event stream: the clock to everyone, the reported traffic feed to
/// everyone when it refreshes, and, to the session's truth group only, the world snapshot and live traffic about once a
/// second (Phase 11 M1). Also counts as gone participants who dropped and didn't come back in time (M2).
/// </summary>
public sealed class Broadcaster(IHubContext<SimulationHub, ISimulationClient> hub, SessionManager sessions, IOptions<ServerOptions> options,
    ILogger<Broadcaster> logger) : BackgroundService
{
    private sealed class Sent
    {
        public DateTimeOffset LastSnapshot = DateTimeOffset.MinValue;
        public WorldSnapshot? World;
        public DateTimeOffset? Live, Feed;
    }

    public static ClockState ClockOf(ISimulationControl clock) => new(clock.SimTime, clock.IsRunning, clock.TimeScale, DateTimeOffset.UtcNow);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var sent = new Dictionary<Guid, Sent>();
        using var timer = new PeriodicTimer(settings.ClockInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var session in sessions.Live.ToList())
            {
                try
                {
                    if (!sent.TryGetValue(session.Id, out var last)) sent[session.Id] = last = new Sent();
                    await PushAsync(session, last, settings);
                    await session.ExpireDroppedAsync(settings.DropGrace);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Broadcast to session {Session} failed", session.Id);
                }
            }
        }
    }

    private async Task PushAsync(SessionRuntime session, Sent last, ServerOptions settings)
    {
        var everyone = hub.Clients.Group(SimulationHubProtocol.PerceivedGroup(session.Id));
        await everyone.Clock(ClockOf(session.Engine));

        var traffic = session.Services.GetRequiredService<TrafficFeed>();
        if (traffic.Snapshot.At != last.Feed)
        {
            last.Feed = traffic.Snapshot.At;
            await everyone.TrafficFeed(TrafficState.From(traffic.Snapshot));
        }

        if (DateTimeOffset.UtcNow - last.LastSnapshot < settings.SnapshotInterval) return;
        last.LastSnapshot = DateTimeOffset.UtcNow;
        var truth = hub.Clients.Group(SimulationHubProtocol.TruthGroup(session.Id));
        if (!ReferenceEquals(session.Engine.Snapshot, last.World))
        {
            last.World = session.Engine.Snapshot;
            await truth.WorldSnapshot(last.World);
        }
        var live = session.Services.GetRequiredService<LiveTraffic>();
        if (live.Snapshot.At != last.Live)
        {
            last.Live = live.Snapshot.At;
            await truth.LiveTraffic(TrafficState.From(live.Snapshot));
        }
    }
}
