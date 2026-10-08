using System.Text.Json;
using Emergency_Response_Simulator.Core.Geo;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Operation.Polygonize;

namespace Emergency_Response_Simulator.GisImport;

/// <summary>An OSM element converted to a WGS84 geometry with its tags.</summary>
public sealed record OsmFeature(string OsmId, Geometry Geometry, IReadOnlyDictionary<string, string> Tags);

/// <summary>
/// Converts an Overpass <c>out tags geom</c> JSON response into geometries:
/// nodes → points, open ways → lines, closed area ways → polygons,
/// multipolygon / boundary relations → (multi)polygons assembled from their member ways.
/// </summary>
public static class OsmGeometryBuilder
{
    /// <summary>Tags whose closed ways are still lines (a roundabout is not an area).</summary>
    private static readonly HashSet<string> LinearKeys = ["highway", "railway", "barrier", "waterway"];

    /// <summary>Waterway values that describe an area rather than a centre line.</summary>
    private static readonly HashSet<string> AreaWaterways = ["riverbank", "dock"];

    public static (List<OsmFeature> Features, int Skipped) Build(JsonDocument overpassResponse)
    {
        var features = new List<OsmFeature>();
        var skipped = 0;

        foreach (var element in overpassResponse.RootElement.GetProperty("elements").EnumerateArray())
        {
            var type = element.GetProperty("type").GetString();
            var id = element.GetProperty("id").GetInt64();
            var tags = ReadTags(element);

            Geometry? geometry;
            try
            {
                geometry = type switch
                {
                    "node" => Wgs84.Factory.CreatePoint(new Coordinate(
                        element.GetProperty("lon").GetDouble(), element.GetProperty("lat").GetDouble())),
                    "way" => BuildWay(element, tags),
                    "relation" => BuildRelation(element),
                    _ => null,
                };
            }
            catch (Exception ex) when (ex is ArgumentException or TopologyException or KeyNotFoundException or InvalidOperationException)
            {
                geometry = null;
            }

            if (geometry is null || geometry.IsEmpty)
            {
                skipped++;
                continue;
            }

            if (!geometry.IsValid)
                geometry = GeometryFixer.Fix(geometry);

            geometry.SRID = Wgs84.Srid;
            features.Add(new OsmFeature($"{type}/{id}", geometry, tags));
        }

        return (features, skipped);
    }

    public static bool IsArea(IReadOnlyDictionary<string, string> tags)
    {
        if (tags.TryGetValue("area", out var area))
            return area == "yes";
        if (tags.TryGetValue("waterway", out var waterway))
            return AreaWaterways.Contains(waterway);
        return !tags.Keys.Any(LinearKeys.Contains);
    }

    private static Geometry? BuildWay(JsonElement element, IReadOnlyDictionary<string, string> tags)
    {
        var coordinates = ReadCoordinates(element.GetProperty("geometry"));
        if (coordinates.Length < 2) return null;

        var closed = coordinates.Length >= 4 && coordinates[0].Equals2D(coordinates[^1]);
        return closed && IsArea(tags)
            ? Wgs84.Factory.CreatePolygon(coordinates)
            : Wgs84.Factory.CreateLineString(coordinates);
    }

    private static Geometry? BuildRelation(JsonElement element)
    {
        if (!element.TryGetProperty("members", out var members)) return null;

        var outer = new Polygonizer(extractOnlyPolygonal: true);
        var inner = new Polygonizer(extractOnlyPolygonal: true);
        var hasOuter = false;

        foreach (var member in members.EnumerateArray())
        {
            if (member.GetProperty("type").GetString() != "way" || !member.TryGetProperty("geometry", out var geom))
                continue;

            var coordinates = ReadCoordinates(geom);
            if (coordinates.Length < 2) continue;

            var line = Wgs84.Factory.CreateLineString(coordinates);
            if (member.TryGetProperty("role", out var role) && role.GetString() == "inner")
            {
                inner.Add(line);
            }
            else
            {
                outer.Add(line);
                hasOuter = true;
            }
        }

        if (!hasOuter) return null;

        var outerArea = Union(outer.GetPolygons());
        if (outerArea is null) return null;

        var innerArea = Union(inner.GetPolygons());
        return innerArea is null ? outerArea : outerArea.Difference(innerArea);
    }

    private static Geometry? Union(ICollection<Geometry> polygons) => polygons.Count switch
    {
        0 => null,
        1 => polygons.First(),
        _ => Wgs84.Factory.BuildGeometry(polygons).Union(),
    };

    private static Coordinate[] ReadCoordinates(JsonElement geometry) => geometry.EnumerateArray()
        // Overpass emits null for nodes outside the requested area on clipped output.
        .Where(p => p.ValueKind == JsonValueKind.Object)
        .Select(p => new Coordinate(p.GetProperty("lon").GetDouble(), p.GetProperty("lat").GetDouble()))
        .ToArray();

    private static Dictionary<string, string> ReadTags(JsonElement element)
    {
        var tags = new Dictionary<string, string>();
        if (element.TryGetProperty("tags", out var tagElement))
        {
            foreach (var tag in tagElement.EnumerateObject())
                tags[tag.Name] = tag.Value.GetString() ?? "";
        }
        return tags;
    }
}
