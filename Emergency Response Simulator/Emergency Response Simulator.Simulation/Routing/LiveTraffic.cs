using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Routing;

/// <summary>
/// The true state of the roads (Design Document §10.4 "traffic gridlock"): typical time-of-day congestion times a
/// per-edge factor the traffic simulation updates every few seconds: queues backing up from closures, junctions
/// whose signals have failed, police directing traffic, evacuees' cars filling the streets.
/// Vehicles really drive at these speeds. Thread-safe: the engine replaces the snapshot while the UI reads it.
/// </summary>
public sealed class LiveTraffic(ITrafficModel? baseline = null) : ITrafficModel
{
    private readonly ITrafficModel _baseline = baseline ?? new TimeOfDayTraffic();
    private volatile TrafficSnapshot _snapshot = TrafficSnapshot.Empty;

    /// <summary>Below this share of normal speed a road counts as gridlocked.</summary>
    public const double GridlockFactor = 0.2;

    public TrafficSnapshot Snapshot => _snapshot;

    public void Publish(TrafficSnapshot snapshot) => _snapshot = snapshot;

    public double SpeedFactor(RoadEdge edge, DateTimeOffset at, bool emergency) =>
        Combine(_baseline.SpeedFactor(edge, at, false), _snapshot.Factor(edge.Id), emergency);

    /// <summary>
    /// Blue lights get through ordinary congestion (traffic pulls over) but much less through gridlock, where there
    /// is nowhere to pull over to.
    /// </summary>
    public static double Combine(double baseline, double local, bool emergency) =>
        emergency ? Math.Sqrt(baseline) * Math.Pow(local, 0.7) : baseline * local;

    /// <summary>The speed a vehicle actually manages on a leg right now.</summary>
    public double LegSpeedKph(RoadNetwork network, RouteLeg leg, UnitType vehicle, DateTimeOffset at, bool emergency)
    {
        if (leg.EdgeId < 0) return leg.SpeedKph; // off-road link: no traffic
        var edge = network.Edge(leg.EdgeId);
        return Math.Max(1, RoadNetwork.FreeSpeedKph(edge, vehicle) * SpeedFactor(edge, at, emergency));
    }
}

/// <summary>
/// What a city traffic service publishes (Design Document §6.8 traffic view): a copy of the live congestion
/// refreshed every couple of minutes. Command's ETA estimates and crews' navigation plan with it, so they see a
/// jam a little late, and only once it has formed.
/// </summary>
public sealed class TrafficFeed(ITrafficModel? baseline = null) : ITrafficModel
{
    private readonly ITrafficModel _baseline = baseline ?? new TimeOfDayTraffic();
    private volatile TrafficSnapshot _snapshot = TrafficSnapshot.Empty;

    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(2);

    public TrafficSnapshot Snapshot => _snapshot;

    /// <summary>When the feed last refreshed, or null before the first refresh.</summary>
    public DateTimeOffset? AsOf => _snapshot.At == default ? null : _snapshot.At;

    public void Publish(TrafficSnapshot snapshot) => _snapshot = snapshot;

    public double SpeedFactor(RoadEdge edge, DateTimeOffset at, bool emergency) =>
        LiveTraffic.Combine(_baseline.SpeedFactor(edge, at, false), _snapshot.Factor(edge.Id), emergency);
}

/// <summary>Local congestion factors by edge (missing = 1, flowing normally), and why.</summary>
public sealed class TrafficSnapshot(DateTimeOffset at, IReadOnlyDictionary<int, double> factors, IReadOnlySet<int> darkSignals)
{
    public static readonly TrafficSnapshot Empty = new(default, new Dictionary<int, double>(), new HashSet<int>());

    public DateTimeOffset At { get; } = at;
    public IReadOnlyDictionary<int, double> Factors { get; } = factors;

    /// <summary>Edges leading into junctions whose traffic signals have no power.</summary>
    public IReadOnlySet<int> DarkSignals { get; } = darkSignals;

    public double Factor(int edgeId) => Factors.TryGetValue(edgeId, out var f) ? f : 1.0;
}
