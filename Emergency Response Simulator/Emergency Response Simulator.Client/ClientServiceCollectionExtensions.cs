using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.Services;
using Emergency_Response_Simulator.Simulation.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Emergency_Response_Simulator.Client;

public static class ClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers what the UI depends on, backed by the server (Phase 11 M1): the same contracts as a standalone session,
    /// with the COP, AVL trails and comms log projected locally from the mirrored event stream, commands sent to the
    /// server, and the clock, traffic and (for an instructor) ground truth taken from its pushes. The engine never runs
    /// on a client. <paramref name="connection"/> must already be connected, so the session id is known.
    /// </summary>
    public static IServiceCollection AddSimulationClient(this IServiceCollection services, SimulationConnection connection)
    {
        var session = connection.Session ?? throw new InvalidOperationException("Connect before registering the client services.");

        services.AddSingleton(connection);
        services.AddSingleton(new SimulationSession(session.SessionId));
        services.AddSingleton<IEventStore>(connection.Mirror);
        services.AddSingleton<IWorldSnapshotSource>(connection);

        services.AddSingleton<ICopService>(sp =>
        {
            var cop = new PerceivedState();
            cop.Follow(connection.Mirror, session.SessionId);
            return cop;
        });
        services.AddSingleton(sp => new CopView(sp.GetRequiredService<ICopService>()));

        // Local route estimates and the map's roads, on the traffic the server reports.
        services.AddSingleton(connection.TrafficFeed);
        services.AddSingleton(connection.LiveTraffic);
        services.AddSingleton<ITrafficModel>(connection.TrafficFeed);
        services.AddSingleton<RoutingService>();
        services.AddSingleton<IRoutingService>(sp => sp.GetRequiredService<RoutingService>());
        services.AddSingleton(sp => new AvlService(sp.GetRequiredService<ICopService>(), connection.Mirror, session.SessionId));
        services.AddSingleton<IAvlService>(sp => sp.GetRequiredService<AvlService>());

        services.AddSingleton(RemoteProxy.Create<IC2Service>(connection, SimulationHubProtocol.Services.C2));
        services.AddSingleton(RemoteProxy.Create<IIapService>(connection, SimulationHubProtocol.Services.Iap));
        services.AddSingleton<ISimulationControl>(new RemoteSimulationControl(connection));
        services.AddSingleton<IAarService>(new ClientAarService(connection));
        services.AddSingleton<ICommsService>(sp => new CommsService(sp.GetRequiredService<ICopService>(), sp.GetRequiredService<IC2Service>(),
            connection.Mirror, session.SessionId));

        services.AddHostedService<ClientFeedService>();
        return services;
    }
}

/// <summary>
/// Starts the event stream once everything that follows the mirror is listening (the COP, AVL), and loads the road
/// network for local route estimates.
/// </summary>
public sealed class ClientFeedService(SimulationConnection connection, ICopService cop, AvlService avl, RoutingService routing) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = cop;
        _ = avl;
        await connection.StartFeedAsync(stoppingToken);
        await routing.LoadAsync(stoppingToken);
    }
}
