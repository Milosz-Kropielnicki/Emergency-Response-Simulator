using System.Diagnostics;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Geo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Simulation.Routing;

/// <summary>
/// Routing over the imported road network. Loads once at start-up from the GIS service; until then (or
/// with no road data) <see cref="IsReady"/> is false and callers fall back to straight-line estimates.
/// </summary>
public sealed class RoutingService(ITrafficModel traffic, IServiceProvider services, ILogger<RoutingService>? logger = null)
    : IRoutingService
{
    private readonly ILogger _logger = logger ?? NullLogger<RoutingService>.Instance;
    private RoadNetwork? _network;

    public bool IsReady => _network is not null;

    public RoadNetwork? Network => _network;

    public ITrafficModel Traffic => traffic;

    /// <summary>Uses an already-built network (tests, tools).</summary>
    public void Use(RoadNetwork network) => _network = network;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (services.GetService<IGisService>() is not { } gis)
        {
            _logger.LogInformation("No GIS service configured; routing falls back to straight lines");
            return;
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var roads = await gis.GetAllFeaturesAsync(GisLayerKeys.Roads, cancellationToken);
            _network = await Task.Run(() => RoadNetwork.Build(roads), cancellationToken);
            _logger.LogInformation("Road network: {Nodes} nodes, {Edges} edges from {Roads} roads in {Ms} ms",
                _network.NodeCount, _network.EdgeCount, roads.Count, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not load the road network; routing falls back to straight lines");
        }
    }

    public RouteResult? Route(GeoPoint from, GeoPoint to, RouteOptions options) =>
        _network?.Route(from, to, options, traffic);

    public HashSet<int> BlockedEdges(IEnumerable<Geometry> avoid) => _network?.BlockedEdges(avoid) ?? [];

    public (string Name, double DistanceMeters)? NearestRoad(GeoPoint point)
    {
        if (_network?.NearestEdge(point, where: e => e.Name is not null) is not { } edge) return null;
        var distance = RoadNetwork.DistanceToSegment(point, _network.NodeLocation(edge.From), _network.NodeLocation(edge.To));
        return (edge.Name!, distance);
    }
}
