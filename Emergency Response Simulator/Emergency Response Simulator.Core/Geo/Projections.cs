using NetTopologySuite.Geometries;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;

namespace Emergency_Response_Simulator.Core.Geo;

/// <summary>
/// Coordinate systems used by the simulator:
/// <list type="bullet">
/// <item><b>WGS84 (EPSG:4326)</b> — storage and exchange: PostGIS, events, contracts.</item>
/// <item><b>Web Mercator (EPSG:3857)</b> — map rendering only; distorts distance and area.</item>
/// <item><b>UTM (EPSG:326xx/327xx)</b> — metric work (buffers, areas, lengths) via <see cref="MetricProjection"/>.</item>
/// </list>
/// </summary>
public static class WebMercator
{
    private const double EarthRadius = 6378137.0;
    private const double MaxLatitude = 85.05112878;

    public static (double X, double Y) FromLonLat(double longitude, double latitude)
    {
        var lat = Math.Clamp(latitude, -MaxLatitude, MaxLatitude);
        var x = longitude * Math.PI / 180.0 * EarthRadius;
        var y = Math.Log(Math.Tan(Math.PI / 4.0 + lat * Math.PI / 360.0)) * EarthRadius;
        return (x, y);
    }

    public static GeoPoint ToGeoPoint(double x, double y)
    {
        var longitude = x / EarthRadius * 180.0 / Math.PI;
        var latitude = (2.0 * Math.Atan(Math.Exp(y / EarthRadius)) - Math.PI / 2.0) * 180.0 / Math.PI;
        return new GeoPoint(latitude, longitude);
    }

    /// <summary>Copies a WGS84 geometry into Web Mercator metres for rendering.</summary>
    public static Geometry FromWgs84(Geometry wgs84)
    {
        var copy = wgs84.Copy();
        copy.Apply(new CoordinateFilter((x, y) => FromLonLat(x, y)));
        copy.SRID = 3857;
        return copy;
    }

    /// <summary>Copies a Web Mercator geometry back to WGS84, e.g. a shape drawn on the map.</summary>
    public static Geometry ToWgs84(Geometry mercator)
    {
        var copy = mercator.Copy();
        copy.Apply(new CoordinateFilter((x, y) =>
        {
            var p = ToGeoPoint(x, y);
            return (p.Longitude, p.Latitude);
        }));
        copy.SRID = Wgs84.Srid;
        return copy;
    }
}

/// <summary>
/// A UTM projection chosen for an area, for calculations that need true metres:
/// buffering a road closure, measuring a zone's area, projecting a plume footprint.
/// </summary>
public sealed class MetricProjection
{
    private static readonly CoordinateTransformationFactory TransformFactory = new();

    private readonly MathTransform _toMetric;
    private readonly MathTransform _toWgs84;

    private MetricProjection(int zone, bool north)
    {
        Zone = zone;
        IsNorthernHemisphere = north;
        Srid = (north ? 32600 : 32700) + zone;

        var utm = ProjectedCoordinateSystem.WGS84_UTM(zone, north);
        _toMetric = TransformFactory.CreateFromCoordinateSystems(GeographicCoordinateSystem.WGS84, utm).MathTransform;
        _toWgs84 = TransformFactory.CreateFromCoordinateSystems(utm, GeographicCoordinateSystem.WGS84).MathTransform;
    }

    public int Zone { get; }
    public bool IsNorthernHemisphere { get; }
    public int Srid { get; }

    /// <summary>The UTM zone containing a point (Dublin → zone 29N, EPSG:32629).</summary>
    public static MetricProjection For(GeoPoint point)
    {
        var zone = (int)Math.Floor((point.Longitude + 180.0) / 6.0) + 1;
        return new MetricProjection(Math.Clamp(zone, 1, 60), point.Latitude >= 0);
    }

    public Geometry ToMetric(Geometry wgs84)
    {
        var copy = wgs84.Copy();
        copy.Apply(new CoordinateFilter((x, y) => _toMetric.Transform(x, y)));
        copy.SRID = Srid;
        return copy;
    }

    public Geometry ToWgs84(Geometry metric)
    {
        var copy = metric.Copy();
        copy.Apply(new CoordinateFilter((x, y) => _toWgs84.Transform(x, y)));
        copy.SRID = Wgs84.Srid;
        return copy;
    }

    /// <summary>Buffers a WGS84 geometry by a distance in metres and returns WGS84.</summary>
    public Geometry Buffer(Geometry wgs84, double meters) => ToWgs84(ToMetric(wgs84).Buffer(meters));

    public double AreaSquareMeters(Geometry wgs84) => ToMetric(wgs84).Area;

    public double LengthMeters(Geometry wgs84) => ToMetric(wgs84).Length;
}

/// <summary>Great-circle helpers for quick point-to-point calculations.</summary>
public static class GeoMath
{
    public const double EarthRadiusMeters = 6371008.8;

    public static double DistanceMeters(GeoPoint a, GeoPoint b)
    {
        var lat1 = ToRadians(a.Latitude);
        var lat2 = ToRadians(b.Latitude);
        var dLat = lat2 - lat1;
        var dLon = ToRadians(b.Longitude - a.Longitude);

        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    /// <summary>Initial bearing from <paramref name="from"/> to <paramref name="to"/>, degrees clockwise from north.</summary>
    public static double BearingDegrees(GeoPoint from, GeoPoint to)
    {
        var lat1 = ToRadians(from.Latitude);
        var lat2 = ToRadians(to.Latitude);
        var dLon = ToRadians(to.Longitude - from.Longitude);

        var y = Math.Sin(dLon) * Math.Cos(lat2);
        var x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        return (ToDegrees(Math.Atan2(y, x)) + 360.0) % 360.0;
    }

    /// <summary>The point reached by travelling <paramref name="meters"/> along <paramref name="bearingDegrees"/>.</summary>
    public static GeoPoint Destination(GeoPoint start, double bearingDegrees, double meters)
    {
        var angular = meters / EarthRadiusMeters;
        var bearing = ToRadians(bearingDegrees);
        var lat1 = ToRadians(start.Latitude);
        var lon1 = ToRadians(start.Longitude);

        var lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(angular) + Math.Cos(lat1) * Math.Sin(angular) * Math.Cos(bearing));
        var lon2 = lon1 + Math.Atan2(
            Math.Sin(bearing) * Math.Sin(angular) * Math.Cos(lat1),
            Math.Cos(angular) - Math.Sin(lat1) * Math.Sin(lat2));

        return new GeoPoint(ToDegrees(lat2), (ToDegrees(lon2) + 540.0) % 360.0 - 180.0);
    }

    /// <summary>Compass point for a bearing, e.g. 47° → "NE".</summary>
    public static string CompassPoint(double bearingDegrees)
    {
        string[] points = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        return points[(int)Math.Round(((bearingDegrees % 360) + 360) % 360 / 45.0) % 8];
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
    private static double ToDegrees(double radians) => radians * 180.0 / Math.PI;
}

/// <summary>Applies a per-coordinate (x, y) → (x, y) transform to every coordinate of a geometry.</summary>
internal sealed class CoordinateFilter(Func<double, double, (double X, double Y)> transform) : IEntireCoordinateSequenceFilter
{
    public bool Done => false;
    public bool GeometryChanged => true;

    public void Filter(CoordinateSequence seq)
    {
        for (var i = 0; i < seq.Count; i++)
        {
            var (x, y) = transform(seq.GetX(i), seq.GetY(i));
            seq.SetX(i, x);
            seq.SetY(i, y);
        }
    }
}
