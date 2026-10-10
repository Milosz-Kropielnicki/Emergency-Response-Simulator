using Emergency_Response_Simulator.Core.Geo;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;

namespace Emergency_Response_Simulator.Simulation.Hazards;

/// <summary>
/// A square grid of cells on a local flat (east, north) plane around a centre point. Over the couple of kilometres
/// a hazard covers, the equirectangular approximation is accurate to well under a metre, and it is far cheaper
/// than a full projection for the thousands of cell lookups each tick.
/// </summary>
public sealed class LocalGrid
{
    private const double MetersPerDegree = 111_320.0;
    private static readonly GeometryFactory Plane = new();

    private readonly double _metersPerDegreeLon;

    public LocalGrid(GeoPoint centre, double cellMeters, int halfCells)
    {
        Centre = centre;
        CellMeters = cellMeters;
        HalfCells = halfCells;
        _metersPerDegreeLon = MetersPerDegree * Math.Cos(centre.Latitude * Math.PI / 180);
    }

    public GeoPoint Centre { get; }
    public double CellMeters { get; }
    public int HalfCells { get; }
    public int Size => 2 * HalfCells + 1;
    public int CellCount => Size * Size;
    public double CellArea => CellMeters * CellMeters;

    public int Index(int col, int row) => row * Size + col;
    public (int Col, int Row) Cell(int index) => (index % Size, index / Size);
    public bool Contains(int col, int row) => col >= 0 && row >= 0 && col < Size && row < Size;

    public (double East, double North) ToLocal(GeoPoint point) =>
        ((point.Longitude - Centre.Longitude) * _metersPerDegreeLon, (point.Latitude - Centre.Latitude) * MetersPerDegree);

    public GeoPoint FromLocal(double east, double north) =>
        new(Centre.Latitude + north / MetersPerDegree, Centre.Longitude + east / _metersPerDegreeLon);

    public bool TryIndex(GeoPoint point, out int index)
    {
        var (east, north) = ToLocal(point);
        var col = (int)Math.Floor(east / CellMeters + 0.5) + HalfCells;
        var row = (int)Math.Floor(north / CellMeters + 0.5) + HalfCells;
        index = Contains(col, row) ? Index(col, row) : -1;
        return index >= 0;
    }

    public (double East, double North) LocalCentre(int index)
    {
        var (col, row) = Cell(index);
        return ((col - HalfCells) * CellMeters, (row - HalfCells) * CellMeters);
    }

    public GeoPoint CentreOf(int index)
    {
        var (east, north) = LocalCentre(index);
        return FromLocal(east, north);
    }

    public double DistanceMeters(int a, int b)
    {
        var (ax, ay) = Cell(a);
        var (bx, by) = Cell(b);
        return Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by)) * CellMeters;
    }

    /// <summary>A single cell as a WGS84 square.</summary>
    public Polygon CellPolygon(int index)
    {
        var (east, north) = LocalCentre(index);
        var h = CellMeters / 2;
        return ToWgs84(Plane.CreatePolygon(
        [
            new(east - h, north - h), new(east + h, north - h), new(east + h, north + h), new(east - h, north + h),
            new(east - h, north - h),
        ]));
    }

    /// <summary>The union of a set of cells as WGS84 polygons: what a hazard covers.</summary>
    public IReadOnlyList<Polygon> Outline(IEnumerable<int> cells)
    {
        var h = CellMeters / 2;
        var squares = cells.Select(index =>
        {
            var (east, north) = LocalCentre(index);
            // A hair of overlap so adjacent cells merge instead of touching along an edge.
            var g = h * 1.001;
            return (Geometry)Plane.CreatePolygon(
            [
                new(east - g, north - g), new(east + g, north - g), new(east + g, north + g), new(east - g, north + g),
                new(east - g, north - g),
            ]);
        }).ToList();
        if (squares.Count == 0) return [];

        var union = CascadedPolygonUnion.Union(squares);
        var polygons = new List<Polygon>();
        for (var i = 0; i < union.NumGeometries; i++)
        {
            if (union.GetGeometryN(i) is Polygon polygon)
                polygons.Add(ToWgs84(polygon));
        }
        return polygons;
    }

    /// <summary>Converts a polygon on the local plane (metres) to WGS84.</summary>
    public Polygon ToWgs84(Polygon local)
    {
        LinearRing Ring(LineString ring) => Wgs84.Factory.CreateLinearRing(ring.Coordinates.Select(c =>
        {
            var p = FromLocal(c.X, c.Y);
            return new Coordinate(p.Longitude, p.Latitude);
        }).ToArray());

        return Wgs84.Factory.CreatePolygon(Ring(local.ExteriorRing), local.InteriorRings.Select(Ring).ToArray());
    }

    /// <summary>The exterior ring of a WGS84 polygon as points, for events.</summary>
    public static IReadOnlyList<GeoPoint> RingOf(Polygon polygon) =>
        polygon.ExteriorRing.Coordinates.Select(c => new GeoPoint(c.Y, c.X)).ToList();
}
