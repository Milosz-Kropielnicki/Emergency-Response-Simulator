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

    /// <summary>
    /// Features from the given layers that intersect an area, e.g. schools inside a projected plume.
    /// </summary>
    Task<IReadOnlyList<GisFeature>> FindFeaturesWithinAsync(
        Geometry area, IReadOnlyCollection<string> layerKeys, CancellationToken cancellationToken = default);
}

public sealed record BoundingBox(double MinLatitude, double MinLongitude, double MaxLatitude, double MaxLongitude);
