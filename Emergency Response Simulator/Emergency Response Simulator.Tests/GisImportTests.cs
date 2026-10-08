using System.Text.Json;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.GisImport;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Tests;

public class GisImportTests
{
    // A trimmed Overpass "out geom" response covering each element shape the builder must handle.
    private const string Response = """
        {"elements":[
          {"type":"node","id":1,"lat":53.35,"lon":-6.26,"tags":{"emergency":"fire_hydrant"}},
          {"type":"way","id":2,"tags":{"highway":"primary","name":"Dame Street"},
           "geometry":[{"lat":53.344,"lon":-6.266},{"lat":53.344,"lon":-6.262}]},
          {"type":"way","id":3,"tags":{"building":"yes"},
           "geometry":[{"lat":53.30,"lon":-6.30},{"lat":53.30,"lon":-6.29},{"lat":53.31,"lon":-6.29},{"lat":53.30,"lon":-6.30}]},
          {"type":"way","id":4,"tags":{"highway":"primary","junction":"roundabout"},
           "geometry":[{"lat":53.30,"lon":-6.30},{"lat":53.30,"lon":-6.29},{"lat":53.31,"lon":-6.29},{"lat":53.30,"lon":-6.30}]},
          {"type":"relation","id":5,"tags":{"type":"multipolygon","building":"hospital","name":"Courtyard Hospital"},
           "members":[
             {"type":"way","ref":10,"role":"outer","geometry":[{"lat":0,"lon":0},{"lat":0,"lon":10}]},
             {"type":"way","ref":11,"role":"outer","geometry":[{"lat":0,"lon":10},{"lat":10,"lon":10},{"lat":10,"lon":0},{"lat":0,"lon":0}]},
             {"type":"way","ref":12,"role":"inner","geometry":[{"lat":4,"lon":4},{"lat":4,"lon":6},{"lat":6,"lon":6},{"lat":6,"lon":4},{"lat":4,"lon":4}]},
             {"type":"node","ref":13,"role":"label","lat":5,"lon":5}
           ]},
          {"type":"relation","id":6,"tags":{"boundary":"administrative","name":"No members"}}
        ]}
        """;

    [Fact]
    public void Builds_points_lines_polygons_and_multipolygons_with_holes()
    {
        using var json = JsonDocument.Parse(Response);

        var (features, skipped) = OsmGeometryBuilder.Build(json);
        var byId = features.ToDictionary(f => f.OsmId);

        Assert.Equal(1, skipped); // the relation without members
        Assert.IsType<Point>(byId["node/1"].Geometry);
        Assert.IsType<LineString>(byId["way/2"].Geometry);
        Assert.IsType<Polygon>(byId["way/3"].Geometry);
        Assert.IsType<LineString>(byId["way/4"].Geometry); // closed but a road, so still a line

        var courtyard = Assert.IsType<Polygon>(byId["relation/5"].Geometry);
        Assert.Single(courtyard.Holes);
        Assert.Equal(100 - 4, courtyard.Area, 6);
        Assert.All(features, f => Assert.Equal(Wgs84.Srid, f.Geometry.SRID));
    }

    [Fact]
    public void Queries_request_relation_members_and_stay_inside_the_box()
    {
        var query = OsmLayerQueries.Find(GisLayerKeys.Buildings)!.ToOverpassQl("53.3,-6.3,53.4,-6.2");

        Assert.Contains("[bbox:53.3,-6.3,53.4,-6.2]", query);
        Assert.Contains("(node.all;way.all;);out tags geom;", query);
        Assert.Contains("rel.all;out geom;", query); // "out tags" would drop the members
    }

    [Fact]
    public void Every_static_layer_except_elevation_has_an_osm_query()
    {
        var missing = GisLayerKeys.All
            .Where(d => d.Key != GisLayerKeys.Elevation && OsmLayerQueries.Find(d.Key) is null)
            .Select(d => d.Key);
        Assert.Empty(missing);
    }

    [Fact]
    public void Features_keep_only_listed_tags_and_infrastructure_gets_a_category()
    {
        var osm = new OsmFeature("node/7", new GeoPoint(53.34, -6.25).ToPoint(),
            new Dictionary<string, string> { ["power"] = "substation", ["name"] = "Ringsend", ["fixme"] = "check", ["voltage"] = "110000" });

        var feature = GisImporter.ToFeature(osm, OsmLayerQueries.Find(GisLayerKeys.CriticalInfrastructure)!);

        Assert.Equal("Ringsend", feature.Name);
        Assert.Equal("Power", feature.Properties["category"]);
        Assert.Equal("110000", feature.Properties["voltage"]);
        Assert.Equal("node/7", feature.Properties["osm_id"]);
        Assert.False(feature.Properties.ContainsKey("fixme"));
    }

    [Fact]
    public void Elevation_grid_covers_the_box_at_the_requested_spacing()
    {
        var box = BoundingBox.Parse("53.325,-6.300,53.365,-6.215");

        var grid = DataSources.ElevationGrid(box, 250);

        Assert.All(grid, p => Assert.True(box.Contains(p)));
        Assert.InRange(grid.Count, 380, 450); // ~4.5 km × 5.6 km at 250 m
        Assert.InRange(GeoMath.DistanceMeters(grid[0], grid[1]), 240, 260);
    }

    [Theory]
    [InlineData("53.3,-6.3,53.2,-6.2")] // south > north
    [InlineData("53.3,-6.3,53.4")]
    public void Rejects_malformed_bounding_boxes(string value) =>
        Assert.Throws<FormatException>(() => BoundingBox.Parse(value));
}
