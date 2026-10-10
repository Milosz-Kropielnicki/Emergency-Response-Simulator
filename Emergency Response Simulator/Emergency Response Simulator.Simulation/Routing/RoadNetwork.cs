using System.Globalization;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace Emergency_Response_Simulator.Simulation.Routing;

public enum RoadClass
{
    Motorway,
    Trunk,
    Primary,
    Secondary,
    Tertiary,
    Local,
    Service,
    Pedestrian,
}

/// <summary>A directed piece of road between two network vertices.</summary>
public sealed record RoadEdge(int Id, int From, int To, double LengthMeters, RoadClass Class, double SpeedKph, string? Name);

/// <summary>
/// A routable graph built from the imported OSM road centre lines. Ways that meet share an identical
/// vertex coordinate, which is how junctions are found. One-way streets (and roundabouts, motorways)
/// only get a forward edge. Routing is A* on travel time.
/// </summary>
public sealed class RoadNetwork
{
    /// <summary>Speed off the network (yards, car parks) for the link between a point and the nearest road.</summary>
    public const double OffRoadSpeedKph = 15;

    /// <summary>Emergency vehicles may drive against a one-way street when nothing else works, at a heavy cost.</summary>
    private const double ContraflowPenalty = 3.0;

    private const double MaxSpeedKph = 110;

    private readonly List<GeoPoint> _nodes = [];
    private readonly List<RoadEdge> _edges = [];
    private readonly List<List<int>> _outgoing = [];
    private readonly List<List<int>> _incoming = [];
    private readonly HashSet<int> _contraflow = []; // reverse edges of one-way roads, only used as a fallback
    private readonly Dictionary<(long, long), int> _nodeIndex = [];
    private readonly STRtree<int> _edgeIndex = new();

    public int NodeCount => _nodes.Count;
    public int EdgeCount => _edges.Count;
    public IReadOnlyList<RoadEdge> Edges => _edges;

    public static RoadNetwork Build(IEnumerable<GisFeature> roads)
    {
        var network = new RoadNetwork();
        foreach (var road in roads)
        {
            if (road.Geometry is not LineString line || line.NumPoints < 2) continue;
            if (!road.Properties.TryGetValue("highway", out var highway)) continue;

            var roadClass = Classify(highway);
            var speed = SpeedFor(roadClass, road.Properties.GetValueOrDefault("maxspeed"));
            var (forward, backward) = Directions(road.Properties, roadClass);
            var name = road.Name ?? road.Properties.GetValueOrDefault("ref");

            for (var i = 0; i < line.NumPoints - 1; i++)
            {
                var a = network.Node(line.GetCoordinateN(i));
                var b = network.Node(line.GetCoordinateN(i + 1));
                if (a == b) continue;

                var length = GeoMath.DistanceMeters(network._nodes[a], network._nodes[b]);
                network.AddEdge(a, b, length, roadClass, speed, name, contraflow: !forward);
                network.AddEdge(b, a, length, roadClass, speed, name, contraflow: !backward);
            }
        }

        network._edgeIndex.Build();
        return network;
    }

    public RoadEdge Edge(int id) => _edges[id];
    public GeoPoint NodeLocation(int id) => _nodes[id];

    /// <summary>Edges driven into a vertex (queues spill back along these), excluding contraflow fallbacks.</summary>
    public IReadOnlyList<int> IncomingEdges(int node) => _incoming[node];

    /// <summary>Edges leaving a vertex, excluding contraflow fallbacks.</summary>
    public IEnumerable<int> OutgoingEdges(int node) => _outgoing[node].Where(e => !_contraflow.Contains(e));

    public bool IsContraflow(int edgeId) => _contraflow.Contains(edgeId);

    /// <summary>The edge's geometry as a WGS84 line.</summary>
    public LineString EdgeLine(int edgeId)
    {
        var edge = _edges[edgeId];
        return Wgs84.Factory.CreateLineString([ToCoordinate(_nodes[edge.From]), ToCoordinate(_nodes[edge.To])]);
    }

    /// <summary>Edges whose segment passes within <paramref name="radiusMeters"/> of a point (approximate, by envelope then distance).</summary>
    public IEnumerable<RoadEdge> EdgesNear(GeoPoint point, double radiusMeters)
    {
        var dLat = radiusMeters / 111_320.0;
        var dLon = radiusMeters / (111_320.0 * Math.Cos(point.Latitude * Math.PI / 180));
        var envelope = new Envelope(point.Longitude - dLon, point.Longitude + dLon, point.Latitude - dLat, point.Latitude + dLat);
        return _edgeIndex.Query(envelope)
            .Select(id => _edges[id])
            .Where(e => !_contraflow.Contains(e.Id) && DistanceToSegment(point, _nodes[e.From], _nodes[e.To]) <= radiusMeters);
    }

    /// <summary>Speed on an empty road for this vehicle, before any traffic.</summary>
    public static double FreeSpeedKph(RoadEdge edge, UnitType vehicle) => edge.SpeedKph * VehicleFactor(vehicle);

    /// <summary>The fastest route from <paramref name="from"/> to <paramref name="to"/>, or null if unreachable.</summary>
    public RouteResult? Route(GeoPoint from, GeoPoint to, RouteOptions options, ITrafficModel traffic)
    {
        var blocked = BlockedEdges(options.Avoid ?? []);
        if (NearestEdge(from, blocked) is not { } start || NearestEdge(to, blocked) is not { } end)
            return null;

        var startNode = Closer(from, start);
        var endNode = Closer(to, end);

        var path = Search(startNode, endNode, blocked, options, traffic, allowContraflow: false)
                   ?? Search(startNode, endNode, blocked, options, traffic, allowContraflow: true);
        if (path is null) return null;

        var legs = new List<RouteLeg>();
        AddConnector(legs, from, _nodes[startNode]);
        foreach (var edgeId in path)
        {
            var edge = _edges[edgeId];
            var speed = EffectiveSpeed(edge, options, traffic);
            legs.Add(new RouteLeg(_nodes[edge.From], _nodes[edge.To], edge.LengthMeters, speed, edge.Name, edge.Id));
        }
        AddConnector(legs, _nodes[endNode], to);

        return new RouteResult(legs, legs.Sum(l => l.LengthMeters),
            TimeSpan.FromSeconds(legs.Sum(l => l.Duration.TotalSeconds)));
    }

    /// <summary>Edges that cannot be used because they cross something to avoid (closure line, hot zone).</summary>
    public HashSet<int> BlockedEdges(IEnumerable<Geometry> avoid)
    {
        var blocked = new HashSet<int>();
        foreach (var geometry in avoid)
        {
            // Lines (closures, obstructions) get a road's width of tolerance so they catch the carriageway.
            var area = geometry is LineString or MultiLineString or Point
                ? MetricProjection.For(GeoPoint.FromPoint(geometry.Centroid)).Buffer(geometry, 12)
                : geometry;

            foreach (var edgeId in _edgeIndex.Query(area.EnvelopeInternal))
            {
                var edge = _edges[edgeId];
                var segment = Wgs84.Factory.CreateLineString([ToCoordinate(_nodes[edge.From]), ToCoordinate(_nodes[edge.To])]);
                if (area.Intersects(segment))
                    blocked.Add(edgeId);
            }
        }
        return blocked;
    }

    /// <summary>The nearest drivable edge to a point, ignoring blocked ones.</summary>
    public RoadEdge? NearestEdge(GeoPoint point, IReadOnlySet<int>? blocked = null, Func<RoadEdge, bool>? where = null)
    {
        foreach (var radiusMeters in new[] { 150.0, 600.0, 2500.0 })
        {
            var dLat = radiusMeters / 111_320.0;
            var dLon = radiusMeters / (111_320.0 * Math.Cos(point.Latitude * Math.PI / 180));
            var envelope = new Envelope(point.Longitude - dLon, point.Longitude + dLon, point.Latitude - dLat, point.Latitude + dLat);

            var best = _edgeIndex.Query(envelope)
                .Select(id => _edges[id])
                .Where(e => blocked?.Contains(e.Id) != true && !_contraflow.Contains(e.Id) && (where?.Invoke(e) ?? true))
                .Select(e => (Edge: e, Distance: DistanceToSegment(point, _nodes[e.From], _nodes[e.To])))
                .OrderBy(x => x.Distance)
                .FirstOrDefault();

            if (best.Edge is not null && best.Distance <= radiusMeters)
                return best.Edge;
        }
        return null;
    }

    public static double DistanceToSegment(GeoPoint p, GeoPoint a, GeoPoint b)
    {
        // Local equirectangular projection: accurate to well under a metre over a few kilometres.
        var k = Math.Cos(p.Latitude * Math.PI / 180);
        double X(GeoPoint g) => g.Longitude * 111_320.0 * k;
        double Y(GeoPoint g) => g.Latitude * 111_320.0;

        var (px, py, ax, ay, bx, by) = (X(p), Y(p), X(a), Y(a), X(b), Y(b));
        var (dx, dy) = (bx - ax, by - ay);
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lengthSquared, 0, 1);
        var (cx, cy) = (ax + t * dx - px, ay + t * dy - py);
        return Math.Sqrt(cx * cx + cy * cy);
    }

    private List<int>? Search(int start, int goal, HashSet<int> blocked, RouteOptions options, ITrafficModel traffic, bool allowContraflow)
    {
        if (start == goal) return [];

        var cost = new Dictionary<int, double> { [start] = 0 };
        var via = new Dictionary<int, int>();
        var open = new PriorityQueue<int, double>();
        var settled = new HashSet<int>();
        open.Enqueue(start, 0);
        var goalLocation = _nodes[goal];

        while (open.TryDequeue(out var node, out _))
        {
            // The heuristic is consistent, so a node's first expansion is its best; skip stale queue entries.
            if (!settled.Add(node)) continue;

            if (node == goal)
            {
                var path = new List<int>();
                for (var current = goal; current != start; current = _edges[via[current]].From)
                    path.Add(via[current]);
                path.Reverse();
                return path;
            }

            foreach (var edgeId in _outgoing[node])
            {
                if (blocked.Contains(edgeId)) continue;
                var isContraflow = _contraflow.Contains(edgeId);
                if (isContraflow && !allowContraflow) continue;

                var edge = _edges[edgeId];
                var seconds = edge.LengthMeters / (EffectiveSpeed(edge, options, traffic) / 3.6);
                if (isContraflow) seconds *= ContraflowPenalty;

                var candidate = cost[node] + seconds;
                if (cost.TryGetValue(edge.To, out var known) && known <= candidate) continue;

                cost[edge.To] = candidate;
                via[edge.To] = edgeId;
                // Admissible heuristic: straight line at the fastest possible speed.
                open.Enqueue(edge.To, candidate + GeoMath.DistanceMeters(_nodes[edge.To], goalLocation) / (MaxSpeedKph / 3.6));
            }
        }
        return null;
    }

    private static double EffectiveSpeed(RoadEdge edge, RouteOptions options, ITrafficModel traffic) =>
        Math.Max(1, FreeSpeedKph(edge, options.Vehicle) * traffic.SpeedFactor(edge, options.DepartAt, options.Emergency));

    public static double VehicleFactor(UnitType type) => type switch
    {
        UnitType.Ladder or UnitType.Tanker or UnitType.Hazmat or UnitType.WaterTender
            or UnitType.HeavyMachinery or UnitType.Bus => 0.85,
        UnitType.Motorcycle => 1.1,
        _ => 1.0,
    };

    private static void AddConnector(List<RouteLeg> legs, GeoPoint from, GeoPoint to)
    {
        var length = GeoMath.DistanceMeters(from, to);
        if (length > 1)
            legs.Add(new RouteLeg(from, to, length, OffRoadSpeedKph, null, -1));
    }

    private int Closer(GeoPoint point, RoadEdge edge) =>
        GeoMath.DistanceMeters(point, _nodes[edge.From]) <= GeoMath.DistanceMeters(point, _nodes[edge.To]) ? edge.From : edge.To;

    private int Node(Coordinate c)
    {
        // OSM ways that meet share a node, so identical coordinates identify junctions.
        var key = ((long)Math.Round(c.Y * 1e7), (long)Math.Round(c.X * 1e7));
        if (_nodeIndex.TryGetValue(key, out var id)) return id;

        id = _nodes.Count;
        _nodes.Add(new GeoPoint(c.Y, c.X));
        _outgoing.Add([]);
        _incoming.Add([]);
        _nodeIndex[key] = id;
        return id;
    }

    private void AddEdge(int from, int to, double length, RoadClass roadClass, double speed, string? name, bool contraflow)
    {
        var edge = new RoadEdge(_edges.Count, from, to, length, roadClass, speed, name);
        _edges.Add(edge);
        _outgoing[from].Add(edge.Id);
        if (contraflow) _contraflow.Add(edge.Id);
        else _incoming[to].Add(edge.Id);

        var a = _nodes[from];
        var b = _nodes[to];
        _edgeIndex.Insert(new Envelope(a.Longitude, b.Longitude, a.Latitude, b.Latitude), edge.Id);
    }

    private static Coordinate ToCoordinate(GeoPoint p) => new(p.Longitude, p.Latitude);

    public static RoadClass Classify(string highway) => highway switch
    {
        "motorway" or "motorway_link" => RoadClass.Motorway,
        "trunk" or "trunk_link" => RoadClass.Trunk,
        "primary" or "primary_link" => RoadClass.Primary,
        "secondary" or "secondary_link" => RoadClass.Secondary,
        "tertiary" or "tertiary_link" => RoadClass.Tertiary,
        "service" => RoadClass.Service,
        "pedestrian" => RoadClass.Pedestrian,
        _ => RoadClass.Local,
    };

    /// <summary>Typical emergency-response speed by road class, capped near the posted limit.</summary>
    public static double SpeedFor(RoadClass roadClass, string? maxspeed)
    {
        var classSpeed = roadClass switch
        {
            RoadClass.Motorway => 100,
            RoadClass.Trunk => 70,
            RoadClass.Primary => 55,
            RoadClass.Secondary => 50,
            RoadClass.Tertiary => 45,
            RoadClass.Local => 35,
            RoadClass.Service => 15,
            _ => 10, // pedestrian streets: emergency access at walking-plus pace
        };

        if (maxspeed is null) return classSpeed;
        var digits = new string(maxspeed.TakeWhile(char.IsDigit).ToArray());
        if (!double.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) || limit <= 0)
            return classSpeed;
        if (maxspeed.Contains("mph")) limit *= 1.609;

        // Blue lights allow some margin over the limit.
        return Math.Min(classSpeed, limit * 1.25);
    }

    private static (bool Forward, bool Backward) Directions(IReadOnlyDictionary<string, string> tags, RoadClass roadClass)
    {
        var oneway = tags.GetValueOrDefault("oneway");
        if (oneway == "-1") return (false, true);
        if (oneway is "yes" or "true" or "1") return (true, false);
        if (oneway == "no") return (true, true);
        if (tags.GetValueOrDefault("junction") is "roundabout" or "circular") return (true, false);
        if (roadClass == RoadClass.Motorway) return (true, false);
        return (true, true);
    }
}
