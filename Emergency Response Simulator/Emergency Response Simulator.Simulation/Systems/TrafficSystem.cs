using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.State;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// The roads as they really are (Design Document §10.4, §10.5). Every 10 seconds it works out a congestion factor
/// for each affected piece of road and publishes it to <see cref="LiveTraffic"/>, which vehicles drive by:
/// <list type="bullet">
/// <item><b>Closures and blockages</b> (declared closures, hot zones, unreported obstructions, flooded streets): queues
/// build up on the roads leading into them and spill back further the longer they stay closed.</item>
/// <item><b>Dark signals</b>: in a power outage, signalised junctions (three or more main roads meeting) lose their
/// lights and flow through them drops by more than half. Police directing traffic there restore most of it.</item>
/// <item><b>Vehicles</b>: evacuees' cars are agents on the road network; a street holding more cars than it has room
/// for slows to a crawl.</item>
/// </list>
/// The traffic feed command and crews' navigation use is refreshed from this every two minutes. Knock-on effects are
/// recorded as cascades: junctions going dark, gridlock forming, an ambulance delayed by dark signals.
/// </summary>
public sealed class TrafficSystem(RoutingService routing, LiveTraffic live, TrafficFeed? feed = null) : ISimulationSystem
{
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(10);

    /// <summary>Flow through a junction whose signals are dark, as a share of normal.</summary>
    public const double DarkSignalFactor = 0.45;

    /// <summary>A police officer directing traffic through a dark junction gets most of the flow back.</summary>
    public const double DirectedFactor = 0.8;

    public const double TrafficControlRadiusMeters = 150;

    /// <summary>Speed of evacuees when there is no road network to drive on.</summary>
    private const double OffNetworkCarKph = 20;

    private readonly Dictionary<int, DateTimeOffset> _blockedSince = [];
    private readonly HashSet<Guid> _announcedOutages = [];
    private readonly Dictionary<string, DateTimeOffset> _gridlockReported = [];
    private readonly Dictionary<Guid, IReadOnlyList<RouteLeg>> _delayReported = [];
    private HashSet<int> _blocked = [];
    private string _blockedKey = "";
    private DateTimeOffset? _lastUpdate;
    private DateTimeOffset? _lastFeed;

    public int Order => 25; // after units, people and medical crews have moved

    public void Update(SimulationContext context)
    {
        MoveVehicles(context);
        if (_lastUpdate is { } last && context.SimTime - last < UpdateInterval) return;
        _lastUpdate = context.SimTime;

        if (routing.Network is not { } network) return;
        var world = context.World;

        var factors = new Dictionary<int, double>();
        var causes = new Dictionary<int, string>();
        void Apply(int edge, double factor, string cause)
        {
            var current = factors.GetValueOrDefault(edge, 1);
            if (factor < current) causes[edge] = cause;
            factors[edge] = current * factor;
        }

        Spillback(context, network, Apply);
        var darkFactors = DarkSignals(context, network, Apply);
        Density(world, network, Apply);

        foreach (var edge in factors.Keys.ToList())
            factors[edge] = Math.Max(0.05, factors[edge]);

        live.Publish(new TrafficSnapshot(context.SimTime, factors, darkFactors.Keys.ToHashSet()));
        if (feed is not null && (_lastFeed is not { } fed || context.SimTime - fed >= TrafficFeed.RefreshInterval))
        {
            _lastFeed = context.SimTime;
            feed.Publish(live.Snapshot);
        }

        RecordGridlock(context, network, factors, causes);
        RecordAmbulanceDelays(context, network, darkFactors);
    }

    // ---- Closures ----

    private static double ClassWeight(RoadClass roadClass) => roadClass switch
    {
        RoadClass.Motorway or RoadClass.Trunk or RoadClass.Primary or RoadClass.Secondary => 1.0,
        RoadClass.Tertiary => 0.7,
        RoadClass.Local => 0.4,
        _ => 0.1,
    };

    /// <summary>Queues on the roads into a closure: up to three junctions back, growing over 12 minutes.</summary>
    private void Spillback(SimulationContext context, RoadNetwork network, Action<int, double, string> apply)
    {
        var world = context.World;
        var key = string.Join(',', world.Obstructions.Keys.Order()) + "|" + world.NoGoVersion;
        if (key != _blockedKey)
        {
            _blockedKey = key;
            _blocked = routing.BlockedEdges(world.Obstructions.Values.Select(o => o.Line).Concat(world.DeclaredNoGoAreas.Values));
        }

        foreach (var edge in _blockedSince.Keys.Where(e => !_blocked.Contains(e)).ToList())
            _blockedSince.Remove(edge);

        foreach (var edgeId in _blocked)
        {
            if (!_blockedSince.TryGetValue(edgeId, out var since))
                _blockedSince[edgeId] = since = context.SimTime;
            var blocked = network.Edge(edgeId);
            var growth = Math.Clamp((context.SimTime - since).TotalMinutes / 12, 0.1, 1);
            var cause = $"{blocked.Name ?? "Road"} blocked";

            var frontier = new List<int> { edgeId };
            foreach (var strength in new[] { 0.85, 0.5, 0.25 })
            {
                var next = new List<int>();
                foreach (var downstream in frontier)
                {
                    var into = network.Edge(downstream).From;
                    foreach (var upstream in network.IncomingEdges(into))
                    {
                        var edge = network.Edge(upstream);
                        if (_blocked.Contains(upstream) || edge.From == network.Edge(downstream).To) continue;
                        apply(upstream, 1 - strength * growth * ClassWeight(edge.Class), cause);
                        next.Add(upstream);
                    }
                }
                frontier = next;
            }
        }
    }

    // ---- Dark signals ----

    private static bool IsMainRoad(RoadClass roadClass) => roadClass <= RoadClass.Tertiary;

    /// <summary>
    /// Edges into signalised junctions inside an outage, with the factor applied: dark, or directed by police.
    /// </summary>
    private Dictionary<int, double> DarkSignals(SimulationContext context, RoadNetwork network, Action<int, double, string> apply)
    {
        var world = context.World;
        var dark = new Dictionary<int, double>();
        var police = world.Units.Values
            .Where(u => u.Type is UnitType.Patrol or UnitType.Traffic or UnitType.Motorcycle or UnitType.Supervisor
                        && u.Phase is ResponsePhase.OnScene or ResponsePhase.Operating && !u.BrokenDown)
            .Select(u => u.Location)
            .ToList();

        foreach (var outage in world.Outages.Values)
        {
            var junctions = network.EdgesNear(outage.Centre, outage.RadiusMeters)
                .Select(e => e.To)
                .Distinct()
                .Where(node => outage.Covers(network.NodeLocation(node))
                               && network.IncomingEdges(node).Count(e => IsMainRoad(network.Edge(e).Class)) >= 3)
                .ToList();

            foreach (var node in junctions)
            {
                var directed = police.Any(p => GeoMath.DistanceMeters(p, network.NodeLocation(node)) <= TrafficControlRadiusMeters);
                var factor = directed ? DirectedFactor : DarkSignalFactor;
                foreach (var edgeId in network.IncomingEdges(node))
                {
                    dark[edgeId] = factor;
                    apply(edgeId, factor, "Traffic signals dark (power outage)");
                }
            }

            if (_announcedOutages.Add(outage.Id) && junctions.Count > 0)
            {
                var roads = junctions.SelectMany(n => network.IncomingEdges(n)).Select(e => network.Edge(e).Name)
                    .OfType<string>().Distinct().Take(3).ToList();
                context.EmitTruth(new CascadeOccurred($"Power outage: {outage.Cause}",
                    $"Traffic signals dark at {junctions.Count} junction(s)" + (roads.Count > 0 ? $" ({string.Join(", ", roads)})" : "")));
            }
        }
        return dark;
    }

    // ---- Vehicles ----

    private static double Lanes(RoadClass roadClass) => roadClass switch
    {
        RoadClass.Motorway or RoadClass.Trunk or RoadClass.Primary => 2,
        RoadClass.Secondary => 1.5,
        _ => 1,
    };

    /// <summary>Greenshields: speed falls in proportion to how full the road is (a car per 7 m of lane is jammed).</summary>
    private static void Density(WorldState world, RoadNetwork network, Action<int, double, string> apply)
    {
        var counts = new Dictionary<int, int>();
        foreach (var car in world.Vehicles.Values)
        {
            if (car.Arrived || car.LegIndex >= car.Route.Count) continue;
            var edgeId = car.Route[car.LegIndex].EdgeId;
            if (edgeId >= 0) counts[edgeId] = counts.GetValueOrDefault(edgeId) + 1;
        }

        foreach (var (edgeId, cars) in counts)
        {
            var edge = network.Edge(edgeId);
            var capacity = Math.Max(1, edge.LengthMeters * Lanes(edge.Class) / 7);
            apply(edgeId, Math.Max(0.08, 1 - cars / capacity), "Evacuation traffic");
        }
    }

    private void MoveVehicles(SimulationContext context)
    {
        var world = context.World;
        if (world.Vehicles.Count == 0) return;
        var network = routing.Network;
        var avoid = world.DeclaredNoGoAreas.Values.Concat(world.Obstructions.Values.Select(o => o.Line)).ToList();

        foreach (var car in world.Vehicles.Values)
        {
            if (car.Arrived) continue;
            if (!car.Planned)
            {
                car.Planned = true;
                car.Route = routing.Route(car.Location, car.Destination, new RouteOptions(UnitType.Other, context.SimTime, Emergency: false, Avoid: avoid))?.Legs
                            ?? [new RouteLeg(car.Location, car.Destination, Math.Max(1, GeoMath.DistanceMeters(car.Location, car.Destination)), OffNetworkCarKph, null, -1)];
            }

            var remaining = context.Delta.TotalSeconds;
            while (remaining > 0 && car.LegIndex < car.Route.Count)
            {
                var leg = car.Route[car.LegIndex];
                var speed = (network is null ? leg.SpeedKph : live.LegSpeedKph(network, leg, UnitType.Other, context.SimTime, emergency: false)) / 3.6;
                var needed = (leg.LengthMeters - car.LegProgressMeters) / speed;
                if (needed <= remaining)
                {
                    remaining -= needed;
                    car.LegIndex++;
                    car.LegProgressMeters = 0;
                }
                else
                {
                    car.LegProgressMeters += speed * remaining;
                    remaining = 0;
                }
            }

            if (car.LegIndex >= car.Route.Count)
            {
                car.Location = car.Destination;
                car.Arrived = true;
                continue;
            }

            var current = car.Route[car.LegIndex];
            var fraction = current.LengthMeters <= 0 ? 1 : car.LegProgressMeters / current.LengthMeters;
            car.Location = new GeoPoint(
                current.From.Latitude + (current.To.Latitude - current.From.Latitude) * fraction,
                current.From.Longitude + (current.To.Longitude - current.From.Longitude) * fraction);
        }
    }

    // ---- Cascades ----

    /// <summary>A named main road with two or more gridlocked stretches: recorded once per road every 15 minutes.</summary>
    private void RecordGridlock(SimulationContext context, RoadNetwork network, Dictionary<int, double> factors, Dictionary<int, string> causes)
    {
        var gridlocked = factors.Where(f => f.Value < LiveTraffic.GridlockFactor)
            .Select(f => (Edge: network.Edge(f.Key), Cause: causes.GetValueOrDefault(f.Key, "Congestion")))
            .Where(x => x.Edge.Name is not null && IsMainRoad(x.Edge.Class))
            .GroupBy(x => x.Edge.Name!)
            .Where(g => g.Count() >= 2);

        foreach (var road in gridlocked)
        {
            if (_gridlockReported.TryGetValue(road.Key, out var at) && context.SimTime - at < TimeSpan.FromMinutes(15)) continue;
            _gridlockReported[road.Key] = context.SimTime;
            var cause = road.GroupBy(x => x.Cause).MaxBy(g => g.Count())!.Key;
            context.EmitTruth(new CascadeOccurred(cause, $"Gridlock on {road.Key}"));
        }
    }

    /// <summary>
    /// The end of the classic chain (§10.5): an ambulance losing time at dark junctions. Recorded once per journey
    /// when the delay attributable to dark signals on its remaining route passes 45 seconds.
    /// </summary>
    private void RecordAmbulanceDelays(SimulationContext context, RoadNetwork network, Dictionary<int, double> darkFactors)
    {
        if (darkFactors.Count == 0) return;
        foreach (var unit in context.World.Units.Values)
        {
            if (unit.Type is not (UnitType.AmbulanceAls or UnitType.AmbulanceBls)
                || unit.Phase is not (ResponsePhase.Travelling or ResponsePhase.Transporting)) continue;
            if (_delayReported.TryGetValue(unit.Id, out var route) && ReferenceEquals(route, unit.Route)) continue;

            double delay = 0;
            for (var i = unit.LegIndex; i < unit.Route.Count; i++)
            {
                var leg = unit.Route[i];
                if (leg.EdgeId < 0 || !darkFactors.TryGetValue(leg.EdgeId, out var dark)) continue;
                var actual = live.LegSpeedKph(network, leg, unit.Type, context.SimTime, emergency: true) / 3.6;
                var withoutDark = actual / Math.Pow(dark, 0.7);
                delay += leg.LengthMeters / actual - leg.LengthMeters / withoutDark;
            }
            if (delay < 45) continue;

            _delayReported[unit.Id] = unit.Route;
            var why = unit.Phase == ResponsePhase.Transporting ? "patients reach hospital later" : "slower to reach the scene";
            context.EmitTruth(new CascadeOccurred("Traffic signals dark (power outage)",
                $"{unit.Callsign} delayed about {delay / 60:F1} min: {why}", unit.Id));
        }
    }
}
