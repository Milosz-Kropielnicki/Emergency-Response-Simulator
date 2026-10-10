using System.Diagnostics;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Index.Strtree;

namespace Emergency_Response_Simulator.Simulation.Hazards;

/// <summary>What the ground is like at a point, for the hazard models and for placing people.</summary>
public interface IHazardTerrain
{
    /// <summary>How readily a fire spreads into this spot: 0 (water) to 1 (inside a building).</summary>
    double Fuel(GeoPoint point);

    /// <summary>Ground height in metres; 0 everywhere without elevation data.</summary>
    double Elevation(GeoPoint point);

    /// <summary>Whether there is a building at the point (people are more likely to be indoors).</summary>
    bool IsBuilding(GeoPoint point);
}

/// <summary>Dense urban ground everywhere, flat: used without GIS data and in tests.</summary>
public sealed class UniformTerrain(double fuel = 0.8, Func<GeoPoint, double>? elevation = null) : IHazardTerrain
{
    public double Fuel(GeoPoint point) => fuel;
    public double Elevation(GeoPoint point) => elevation?.Invoke(point) ?? 0;
    public bool IsBuilding(GeoPoint point) => fuel >= 0.9;
}

/// <summary>
/// Terrain from the imported GIS layers: building footprints carry fire, water stops it, and the elevation grid
/// tells flood water where to run. Loads once at start-up; until then (or with no GIS) it behaves like
/// <see cref="UniformTerrain"/>.
/// </summary>
public sealed class HazardTerrain(IServiceProvider services, ILogger<HazardTerrain>? logger = null) : IHazardTerrain
{
    /// <summary>Yards, streets and gardens: bins, cars and vegetation burn, but much less readily than buildings.</summary>
    public const double OpenGroundFuel = 0.3;

    private readonly ILogger _logger = logger ?? NullLogger<HazardTerrain>.Instance;
    private readonly UniformTerrain _fallback = new();
    private STRtree<IPreparedGeometry>? _buildings;
    private STRtree<IPreparedGeometry>? _water;
    private STRtree<(GeoPoint Point, double Elevation)>? _elevation;

    public bool IsLoaded => _buildings is not null;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (services.GetService<IGisService>() is not { } gis)
        {
            _logger.LogInformation("No GIS service configured; hazard models use uniform terrain");
            return;
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var buildings = await gis.GetAllFeaturesAsync(GisLayerKeys.Buildings, cancellationToken);
            var water = await gis.GetAllFeaturesAsync(GisLayerKeys.Water, cancellationToken);
            var elevation = await gis.GetAllFeaturesAsync(GisLayerKeys.Elevation, cancellationToken);

            await Task.Run(() =>
            {
                _water = Index(water.Select(f => f.Geometry).Where(g => g is Polygon or MultiPolygon));
                _elevation = ElevationIndex(elevation);
                // Assigned last: IsLoaded switches on once everything is in place.
                _buildings = Index(buildings.Select(f => f.Geometry).Where(g => g is Polygon or MultiPolygon));
            }, cancellationToken);

            _logger.LogInformation("Hazard terrain: {Buildings} buildings, {Water} water areas, {Elevation} elevation points in {Ms} ms",
                buildings.Count, water.Count, elevation.Count, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not load hazard terrain; hazard models use uniform terrain");
        }
    }

    public double Fuel(GeoPoint point)
    {
        if (_buildings is null) return _fallback.Fuel(point);
        if (Hit(_water, point)) return 0;
        return Hit(_buildings, point) ? 1.0 : OpenGroundFuel;
    }

    public bool IsBuilding(GeoPoint point) => _buildings is null ? _fallback.IsBuilding(point) : Hit(_buildings, point);

    public double Elevation(GeoPoint point)
    {
        if (_elevation is null) return 0;

        // Inverse-distance weighting of the nearest grid points; widen the search where the grid is sparse.
        IList<(GeoPoint Point, double Elevation)> nearby = [];
        foreach (var searchMeters in new[] { 250.0, 1000.0 })
        {
            var dLat = searchMeters / 111_320.0;
            var dLon = searchMeters / (111_320.0 * Math.Cos(point.Latitude * Math.PI / 180));
            nearby = _elevation.Query(new Envelope(point.Longitude - dLon, point.Longitude + dLon, point.Latitude - dLat, point.Latitude + dLat));
            if (nearby.Count > 0) break;
        }
        if (nearby.Count == 0) return 0;

        double weights = 0, sum = 0;
        foreach (var (p, height) in nearby)
        {
            var d = Math.Max(1, GeoMath.DistanceMeters(point, p));
            var w = 1 / (d * d);
            weights += w;
            sum += w * height;
        }
        return sum / weights;
    }

    private static bool Hit(STRtree<IPreparedGeometry>? index, GeoPoint point)
    {
        if (index is null) return false;
        var p = point.ToPoint();
        foreach (var candidate in index.Query(p.EnvelopeInternal))
        {
            if (candidate.Contains(p)) return true;
        }
        return false;
    }

    private static STRtree<IPreparedGeometry> Index(IEnumerable<Geometry> geometries)
    {
        var tree = new STRtree<IPreparedGeometry>();
        foreach (var geometry in geometries)
            tree.Insert(geometry.EnvelopeInternal, PreparedGeometryFactory.Prepare(geometry));
        tree.Build();
        return tree;
    }

    private static STRtree<(GeoPoint, double)>? ElevationIndex(IReadOnlyList<GisFeature> features)
    {
        var tree = new STRtree<(GeoPoint, double)>();
        var count = 0;
        foreach (var feature in features)
        {
            if (feature.Geometry is not Point point) continue;
            if (!feature.Properties.TryGetValue("elevation_m", out var raw)
                || !double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var height))
                continue;
            tree.Insert(point.EnvelopeInternal, (GeoPoint.FromPoint(point), height));
            count++;
        }
        if (count == 0) return null;
        tree.Build();
        return tree;
    }
}
