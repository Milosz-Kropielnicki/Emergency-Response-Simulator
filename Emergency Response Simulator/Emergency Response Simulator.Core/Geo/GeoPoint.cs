using NetTopologySuite;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Core.Geo;

/// <summary>
/// A WGS84 latitude/longitude pair. Used in events and module contracts so they stay plain,
/// serialisable data; entities use NetTopologySuite geometries so they map straight onto PostGIS.
/// </summary>
public readonly record struct GeoPoint(double Latitude, double Longitude)
{
    public Point ToPoint() => Wgs84.Factory.CreatePoint(new Coordinate(Longitude, Latitude));

    public static GeoPoint FromPoint(Point point) => new(point.Y, point.X);
}

public static class Wgs84
{
    public const int Srid = 4326;

    public static GeometryFactory Factory { get; } =
        NtsGeometryServices.Instance.CreateGeometryFactory(Srid);

    /// <summary>A circle of true radius (metres) around a point, as a ring of points.</summary>
    public static IReadOnlyList<GeoPoint> Circle(GeoPoint center, double radiusMeters, int segments = 48) =>
        Enumerable.Range(0, segments)
            .Select(i => GeoMath.Destination(center, 360.0 * i / segments, radiusMeters))
            .ToList();

    /// <summary>Builds a closed polygon from a ring of points (the first point is repeated at the end if needed).</summary>
    public static Polygon CreatePolygon(IReadOnlyList<GeoPoint> ring)
    {
        if (ring.Count < 3)
            throw new ArgumentException("A polygon needs at least three points.", nameof(ring));

        var coordinates = ring.Select(p => new Coordinate(p.Longitude, p.Latitude)).ToList();
        if (!coordinates[0].Equals2D(coordinates[^1]))
            coordinates.Add(coordinates[0].Copy());

        return Factory.CreatePolygon(coordinates.ToArray());
    }
}
