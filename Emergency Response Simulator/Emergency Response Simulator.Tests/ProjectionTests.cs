using Emergency_Response_Simulator.Core.Geo;

namespace Emergency_Response_Simulator.Tests;

public class ProjectionTests
{
    private static readonly GeoPoint Dublin = new(53.3498, -6.2603);
    private static readonly GeoPoint Belfast = new(54.5973, -5.9301);

    [Fact]
    public void Web_mercator_matches_the_standard_formula_and_round_trips()
    {
        var (x0, y0) = WebMercator.FromLonLat(0, 0);
        var (x180, _) = WebMercator.FromLonLat(180, 0);
        Assert.Equal(0, x0, 6);
        Assert.Equal(0, y0, 6);
        Assert.Equal(20037508.34, x180, 1);

        var (x, y) = WebMercator.FromLonLat(Dublin.Longitude, Dublin.Latitude);
        var back = WebMercator.ToGeoPoint(x, y);
        Assert.Equal(Dublin.Latitude, back.Latitude, 9);
        Assert.Equal(Dublin.Longitude, back.Longitude, 9);
    }

    [Fact]
    public void Dublin_uses_utm_zone_29_north()
    {
        var projection = MetricProjection.For(Dublin);
        Assert.Equal(29, projection.Zone);
        Assert.Equal(32629, projection.Srid);
    }

    [Fact]
    public void Metric_buffer_produces_a_true_100_metre_circle()
    {
        var projection = MetricProjection.For(Dublin);

        var circle = projection.Buffer(Dublin.ToPoint(), 100);

        Assert.Equal(4326, circle.SRID);
        Assert.Equal(Math.PI * 100 * 100, projection.AreaSquareMeters(circle), Math.PI * 100 * 100 * 0.01);
        // A degree-based buffer would be badly squashed at 53°N; this one is round on the ground.
        var east = GeoMath.DistanceMeters(Dublin, new GeoPoint(Dublin.Latitude, circle.EnvelopeInternal.MaxX));
        var north = GeoMath.DistanceMeters(Dublin, new GeoPoint(circle.EnvelopeInternal.MaxY, Dublin.Longitude));
        Assert.Equal(100, east, 1.0);
        Assert.Equal(100, north, 1.0);
    }

    [Fact]
    public void Great_circle_distance_bearing_and_destination_agree()
    {
        var distance = GeoMath.DistanceMeters(Dublin, Belfast);
        var bearing = GeoMath.BearingDegrees(Dublin, Belfast);

        Assert.InRange(distance, 139_000, 141_000);
        Assert.Equal("N", GeoMath.CompassPoint(bearing));

        var arrived = GeoMath.Destination(Dublin, bearing, distance);
        Assert.True(GeoMath.DistanceMeters(arrived, Belfast) < 1);
    }

    [Theory]
    [InlineData(0, "N")]
    [InlineData(44, "NE")]
    [InlineData(270, "W")]
    [InlineData(350, "N")]
    [InlineData(-90, "W")]
    public void Compass_points(double bearing, string expected) => Assert.Equal(expected, GeoMath.CompassPoint(bearing));
}
