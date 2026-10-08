using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Core.Contracts;

/// <summary>
/// Routing on the road network: "Engine 12 can reach the incident in approximately 6 minutes"
/// (Design Document §7.5). Accounts for road class, one-way streets, time-of-day traffic and
/// anything to avoid (declared closures, hot zones, known obstructions).
/// </summary>
public interface IRoutingService
{
    /// <summary>False until the road network has loaded (or when no road data is available).</summary>
    bool IsReady { get; }

    /// <summary>The fastest route, or null when no road connects the two points.</summary>
    RouteResult? Route(GeoPoint from, GeoPoint to, RouteOptions options);

    /// <summary>The nearest named road to a point, e.g. "Pearse Street", with the distance in metres.</summary>
    (string Name, double DistanceMeters)? NearestRoad(GeoPoint point);
}

/// <param name="Vehicle">Heavier apparatus is slower.</param>
/// <param name="DepartAt">Used for time-of-day traffic.</param>
/// <param name="Emergency">Blue lights: less held up by traffic.</param>
/// <param name="Avoid">Areas or lines that cannot be driven through (closures, hot zones, obstructions).</param>
public sealed record RouteOptions(
    UnitType Vehicle,
    DateTimeOffset DepartAt,
    bool Emergency = true,
    IReadOnlyList<Geometry>? Avoid = null);

public sealed record RouteResult(
    IReadOnlyList<RouteLeg> Legs,
    double DistanceMeters,
    TimeSpan Duration)
{
    public IReadOnlyList<GeoPoint> Path =>
        Legs.Count == 0 ? [] : [Legs[0].From, .. Legs.Select(l => l.To)];
}

/// <summary>One straight piece of a route between two vertices of the road network.</summary>
/// <param name="EdgeId">The road-network edge, or -1 for the off-road link to or from the network.</param>
public sealed record RouteLeg(
    GeoPoint From,
    GeoPoint To,
    double LengthMeters,
    double SpeedKph,
    string? RoadName,
    int EdgeId)
{
    public TimeSpan Duration => TimeSpan.FromHours(LengthMeters / 1000.0 / SpeedKph);
}
