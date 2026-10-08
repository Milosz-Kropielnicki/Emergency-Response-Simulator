using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Core.Contracts;

/// <summary>
/// Geographic data: "What exists where?" (Design Document §7.1).
/// Serves the static base layers from PostGIS. Dynamic objects (zones, closures)
/// travel through the event stream and live on the COP.
/// </summary>
public interface IGisService
{
    Task<IReadOnlyList<GisLayer>> GetLayersAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GisFeature>> GetFeaturesAsync(
        string layerKey, BoundingBox bounds, CancellationToken cancellationToken = default);

    /// <summary>Every feature in a layer, for loading the map.</summary>
    Task<IReadOnlyList<GisFeature>> GetAllFeaturesAsync(
        string layerKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Features from the given layers that intersect an area, e.g. schools inside a projected plume.
    /// </summary>
    Task<IReadOnlyList<GisFeature>> FindFeaturesWithinAsync(
        Geometry area, IReadOnlyCollection<string> layerKeys, CancellationToken cancellationToken = default);

    /// <summary>
    /// The closest features to a point, nearest first, with straight-line distance in metres,
    /// e.g. the three nearest hospitals. Uses the spatial index.
    /// </summary>
    Task<IReadOnlyList<NearestFeature>> FindNearestAsync(
        GeoPoint point, IReadOnlyCollection<string> layerKeys, int maxResults = 5,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ground elevation in metres above sea level, interpolated from the imported elevation grid,
    /// or null when there is no elevation data near the point.
    /// </summary>
    Task<double?> GetElevationAsync(GeoPoint point, CancellationToken cancellationToken = default);
}

public sealed record BoundingBox(double MinLatitude, double MinLongitude, double MaxLatitude, double MaxLongitude)
{
    public bool Contains(GeoPoint point) =>
        point.Latitude >= MinLatitude && point.Latitude <= MaxLatitude
        && point.Longitude >= MinLongitude && point.Longitude <= MaxLongitude;

    public GeoPoint Center => new((MinLatitude + MaxLatitude) / 2, (MinLongitude + MaxLongitude) / 2);

    /// <summary>Parses "south,west,north,east", the order Overpass and most tools use.</summary>
    public static BoundingBox Parse(string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries)
            .Select(p => double.Parse(p, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        if (parts.Length != 4 || parts[0] >= parts[2] || parts[1] >= parts[3])
            throw new FormatException($"Expected \"south,west,north,east\", got \"{value}\".");
        return new BoundingBox(parts[0], parts[1], parts[2], parts[3]);
    }

    public override string ToString() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{MinLatitude},{MinLongitude},{MaxLatitude},{MaxLongitude}");
}

public sealed record NearestFeature(GisFeature Feature, string LayerKey, double DistanceMeters);
