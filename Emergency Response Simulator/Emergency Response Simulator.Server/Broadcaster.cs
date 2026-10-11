using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.State;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Emergency_Response_Simulator.Server;

/// <summary>
/// Pushes what isn't in the event stream: the clock to everyone, the reported traffic feed to everyone when it refreshes,
/// and, to the truth group only, the world snapshot and live traffic about once a second (Phase 11 M1).
/// </summary>
public sealed class Broadcaster(IHubContext<SimulationHub, ISimulationClient> hub, ISimulationControl clock, IWorldSnapshotSource world,
    LiveTraffic live, TrafficFeed traffic, IOptions<ServerOptions> options, ILogger<Broadcaster> logger) : BackgroundService
{
    public static ClockState ClockOf(ISimulationControl clock) => new(clock.SimTime, clock.IsRunning, clock.TimeScale, DateTimeOffset.UtcNow);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var lastSnapshot = DateTimeOffset.MinValue;
        WorldSnapshot? sentWorld = null;
        DateTimeOffset? sentLive = null, sentFeed = null;
        using var timer = new PeriodicTimer(settings.ClockInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await hub.Clients.Group(SimulationHubProtocol.PerceivedGroup).Clock(ClockOf(clock));

                if (traffic.Snapshot.At != sentFeed)
                {
                    sentFeed = traffic.Snapshot.At;
                    await hub.Clients.Group(SimulationHubProtocol.PerceivedGroup).TrafficFeed(TrafficState.From(traffic.Snapshot));
                }

                if (DateTimeOffset.UtcNow - lastSnapshot < settings.SnapshotInterval) continue;
                lastSnapshot = DateTimeOffset.UtcNow;
                var truth = hub.Clients.Group(SimulationHubProtocol.TruthGroup);
                if (!ReferenceEquals(world.Snapshot, sentWorld))
                {
                    sentWorld = world.Snapshot;
                    await truth.WorldSnapshot(sentWorld);
                }
                if (live.Snapshot.At != sentLive)
                {
                    sentLive = live.Snapshot.At;
                    await truth.LiveTraffic(TrafficState.From(live.Snapshot));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Broadcast failed");
            }
        }
    }
}
