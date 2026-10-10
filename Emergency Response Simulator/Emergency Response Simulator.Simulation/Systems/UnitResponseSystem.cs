using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.State;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Moves dispatched units through DISPATCHED → EN ROUTE → ON SCENE → OPERATING (Design Document §6.4)
/// and generates their AVL feed (§7.4). Crews drive a route on the road network that avoids every zone
/// command has declared closed; they re-plan when a closure is declared mid-journey; and they only find an
/// unreported obstruction by reaching it, when they stop, report it and re-route (§7.5).
/// Without road data the route is a straight line with a detour factor.
/// With live traffic (Phase 6) crews plan with the traffic feed their navigation receives but drive at the speed the
/// roads really allow, and re-route when a jam costs them far more than their navigation promised.
/// What a unit does always happens in the world; command only hears about it if the unit's radio works.
/// </summary>
public sealed class UnitResponseSystem(RoutingService? routing = null, LiveTraffic? live = null) : ISimulationSystem
{
    public static readonly TimeSpan TurnoutTime = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan SizeUpTime = TimeSpan.FromSeconds(60);

    /// <summary>AVL fix rate while moving.</summary>
    public static readonly TimeSpan FixInterval = TimeSpan.FromSeconds(5);

    /// <summary>AVL heartbeat while committed but stationary.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    /// <summary>How long a crew spends checking a blocked road before turning round.</summary>
    public static readonly TimeSpan ObstructionAssessTime = TimeSpan.FromSeconds(40);

    /// <summary>How long a broken-down crew takes to diagnose the fault and report it.</summary>
    public static readonly TimeSpan BreakdownReportDelay = TimeSpan.FromMinutes(2);

    /// <summary>How often a moving crew compares its progress with its navigation's estimate.</summary>
    public static readonly TimeSpan TrafficCheckInterval = TimeSpan.FromSeconds(60);

    /// <summary>Roads are longer than the straight line between two points (fallback without road data).</summary>
    private const double DetourFactor = 1.3;

    private readonly Dictionary<Guid, HashSet<int>> _obstructedEdges = [];

    public int Order => 10;

    public void Update(SimulationContext context)
    {
        foreach (var unit in context.World.Units.Values)
        {
            if (unit.BrokenDown)
            {
                Broken(context, unit);
                continue;
            }

            // Another system (e.g. an ambulance crew leaving for hospital) has set a destination.
            if (unit.PendingPlanReason is { } reason && unit.Phase is ResponsePhase.Travelling or ResponsePhase.Transporting)
            {
                unit.PendingPlanReason = null;
                Plan(context, unit, reason);
            }

            switch (unit.Phase)
            {
                // A tired crew is slower to get going and to size up (Phase 8).
                case ResponsePhase.TurningOut when context.SimTime - unit.PhaseStartedAt >= TurnoutTime * CrewFactors.SlowFactor(unit):
                    StartTravel(context, unit);
                    break;

                case ResponsePhase.Travelling or ResponsePhase.Transporting:
                    Travel(context, unit);
                    break;

                case ResponsePhase.OnScene when context.SimTime - unit.PhaseStartedAt >= SizeUpTime * CrewFactors.SlowFactor(unit):
                    unit.Phase = ResponsePhase.Operating;
                    unit.PhaseStartedAt = context.SimTime;
                    Report(context, unit, new UnitStatusChanged(unit.Id, UnitStatus.Operating));
                    break;
            }

            // Committed vehicles keep sending AVL heartbeats while stationary, which is how
            // command knows they are still in contact (and notices when they stop).
            if (unit.OrderedIncidentId is not null && unit.Phase is not (ResponsePhase.Travelling or ResponsePhase.Transporting)
                && context.SimTime - unit.LastFixAt >= HeartbeatInterval)
            {
                Fix(context, unit, eta: null);
            }
        }
    }

    /// <summary>Average urban emergency-response speed; heavy apparatus is slower. Used without road data.</summary>
    public static double ResponseSpeedKph(UnitType type) => type switch
    {
        UnitType.Ladder or UnitType.Tanker or UnitType.Hazmat or UnitType.WaterTender
            or UnitType.HeavyMachinery or UnitType.Bus => 35,
        UnitType.Motorcycle => 55,
        _ => 45,
    };

    /// <summary>Straight-line estimate used when there is no road network.</summary>
    public static TimeSpan EstimateTravelTime(GeoPoint from, GeoPoint to, UnitType type) =>
        TimeSpan.FromHours(GeoMath.DistanceMeters(from, to) * DetourFactor / 1000.0 / ResponseSpeedKph(type));

    private void StartTravel(SimulationContext context, WorldUnit unit)
    {
        if (unit.OrderedIncidentId is not { } incidentId
            || !context.World.ReportedIncidentLocations.TryGetValue(incidentId, out var destination))
        {
            unit.Phase = ResponsePhase.Idle;
            return;
        }

        unit.Destination = destination;
        unit.Phase = ResponsePhase.Travelling;
        unit.PhaseStartedAt = context.SimTime;
        Report(context, unit, new UnitStatusChanged(unit.Id, UnitStatus.EnRoute));
        Plan(context, unit, "Dispatched");
    }

    private void Travel(SimulationContext context, WorldUnit unit)
    {
        // Command declared or lifted a closure: re-plan from where we are.
        if (unit.PlannedWithNoGoVersion != context.World.NoGoVersion && unit.HeldUpSince is null)
            Plan(context, unit, "Re-routed: road closure declared");

        if (unit.HeldUpSince is { } since)
        {
            if (context.SimTime - since < ObstructionAssessTime)
            {
                if (context.SimTime - unit.LastFixAt >= FixInterval)
                    Fix(context, unit, Remaining(context, unit));
                return;
            }

            // The crew has seen enough: report it and turn round.
            var obstructionId = unit.HeldUpBy!.Value;
            var (line, description) = context.World.Obstructions.TryGetValue(obstructionId, out var o)
                ? o
                : (Wgs84.Factory.CreateLineString([unit.Location.ToPoint().Coordinate, unit.Location.ToPoint().Coordinate]), "Road blocked");
            unit.KnownObstructions[obstructionId] = line;
            unit.HeldUpSince = null;
            unit.HeldUpBy = null;

            var road = routing?.NearestRoad(unit.Location)?.Name ?? "the road ahead";
            Report(context, unit, new ReportReceived(Guid.NewGuid(), unit.OrderedIncidentId, ReportSource.FieldUnit, unit.Callsign,
                $"{road} blocked: {description}. Re-routing.", Confidence.High, VerificationStatus.Confirmed,
                unit.Location, 15, unit.Id));
            Plan(context, unit, $"Re-routed around obstruction on {road}");
            return;
        }

        CheckTraffic(context, unit);

        var remaining = context.Delta.TotalSeconds;
        while (remaining > 0 && unit.LegIndex < unit.Route.Count)
        {
            var leg = unit.Route[unit.LegIndex];

            // Reaching an obstruction nobody told us about.
            if (unit.LegProgressMeters == 0 && ObstructionOn(context, unit, leg) is { } obstruction)
            {
                unit.HeldUpSince = context.SimTime;
                unit.HeldUpBy = obstruction;
                unit.SpeedKph = 0;
                Fix(context, unit, Remaining(context, unit));
                return;
            }

            var speed = Speed(context, unit, leg) / 3.6;
            var needed = (leg.LengthMeters - unit.LegProgressMeters) / speed;
            if (needed <= remaining)
            {
                remaining -= needed;
                unit.LegIndex++;
                unit.LegProgressMeters = 0;
            }
            else
            {
                unit.LegProgressMeters += speed * remaining;
                remaining = 0;
            }
        }

        if (unit.LegIndex >= unit.Route.Count)
        {
            Arrive(context, unit);
            return;
        }

        var current = unit.Route[unit.LegIndex];
        var fraction = current.LengthMeters <= 0 ? 1 : unit.LegProgressMeters / current.LengthMeters;
        unit.Location = new GeoPoint(
            current.From.Latitude + (current.To.Latitude - current.From.Latitude) * fraction,
            current.From.Longitude + (current.To.Longitude - current.From.Longitude) * fraction);
        unit.Heading = GeoMath.BearingDegrees(current.From, current.To);
        unit.SpeedKph = Speed(context, unit, current);

        if (context.SimTime - unit.LastFixAt >= FixInterval)
            Fix(context, unit, Remaining(context, unit));
    }

    /// <summary>How fast the unit really manages on a leg: live road speed when simulated, else the planned speed.</summary>
    private double Speed(SimulationContext context, WorldUnit unit, RouteLeg leg) =>
        live is not null && routing?.Network is { } network
            ? live.LegSpeedKph(network, leg, unit.Type, context.SimTime, emergency: true)
            : leg.SpeedKph;

    /// <summary>Time left at the speeds the roads really allow now (what the crew's progress is heading for).</summary>
    private TimeSpan Remaining(SimulationContext context, WorldUnit unit)
    {
        if (live is null || routing?.Network is null) return unit.RemainingTime;
        double seconds = 0;
        for (var i = unit.LegIndex; i < unit.Route.Count; i++)
        {
            var leg = unit.Route[i];
            var length = i == unit.LegIndex ? leg.LengthMeters - unit.LegProgressMeters : leg.LengthMeters;
            seconds += Math.Max(0, length) / (Speed(context, unit, leg) / 3.6);
        }
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Stuck in a jam the navigation did not know about: once the drive is taking far longer than planned, ask the
    /// navigation (which plans with the traffic feed) for something better, and take it if it is clearly faster.
    /// </summary>
    private void CheckTraffic(SimulationContext context, WorldUnit unit)
    {
        if (live is null || routing?.Network is null || context.SimTime - unit.LastTrafficCheckAt < TrafficCheckInterval) return;
        unit.LastTrafficCheckAt = context.SimTime;

        var actual = Remaining(context, unit);
        var promised = unit.RemainingTime;
        if (actual < promised * 1.5 || actual - promised < TimeSpan.FromSeconds(90)) return;

        var alternative = Compute(context, unit);
        if (alternative is null) return;

        // Judge the alternative at the speeds the roads really allow, not just what the feed says.
        var alternativeActual = alternative.Legs.Sum(l => l.LengthMeters / (Speed(context, unit, l) / 3.6));
        if (alternativeActual < actual.TotalSeconds * 0.8)
            Adopt(context, unit, alternative, "Re-routed: heavy traffic");
    }

    /// <summary>
    /// A broken-down vehicle stands still but its AVL keeps reporting; after diagnosing the fault the
    /// crew tells command and the unit goes out of service.
    /// </summary>
    private static void Broken(SimulationContext context, WorldUnit unit)
    {
        unit.SpeedKph = 0;
        var committed = unit.OrderedIncidentId is not null;

        if (committed && context.SimTime - unit.LastFixAt >= (unit.Phase == ResponsePhase.Travelling ? FixInterval : HeartbeatInterval))
            Fix(context, unit, null);

        if (!unit.BreakdownReported && unit.BrokeDownAt is { } at && context.SimTime - at >= BreakdownReportDelay)
        {
            unit.BreakdownReported = true;
            Report(context, unit, new ReportReceived(Guid.NewGuid(), unit.OrderedIncidentId, ReportSource.FieldUnit, unit.Callsign,
                $"{unit.Callsign}: {unit.BreakdownFault}. Vehicle immobilised, request replacement.", Confidence.High,
                VerificationStatus.Confirmed, unit.Location, 10, unit.Id));
            Report(context, unit, new UnitStatusChanged(unit.Id, UnitStatus.OutOfService));
        }
    }

    private void Arrive(SimulationContext context, WorldUnit unit)
    {
        unit.Location = unit.Destination;
        unit.SpeedKph = 0;
        unit.Route = [];
        if (unit.Phase == ResponsePhase.Transporting)
        {
            // MedicalSystem takes it from here: handover, then the crew clears.
            unit.Phase = ResponsePhase.AtHospital;
            unit.PhaseStartedAt = context.SimTime;
            Fix(context, unit, TimeSpan.Zero);
            return;
        }
        unit.Phase = ResponsePhase.OnScene;
        unit.PhaseStartedAt = context.SimTime;
        Fix(context, unit, TimeSpan.Zero);
        Report(context, unit, new UnitStatusChanged(unit.Id, UnitStatus.OnScene));
    }

    /// <summary>Plans (or re-plans) the journey from the unit's current position and tells command.</summary>
    private void Plan(SimulationContext context, WorldUnit unit, string reason) =>
        Adopt(context, unit, Compute(context, unit) ?? StraightLine(unit.Location, unit.Destination, unit.Type), reason);

    private RouteResult? Compute(SimulationContext context, WorldUnit unit)
    {
        var avoid = context.World.DeclaredNoGoAreas.Values.Concat(unit.KnownObstructions.Values).ToList();
        return routing?.Route(unit.Location, unit.Destination,
            new RouteOptions(unit.Type, context.SimTime, Emergency: true, Avoid: avoid));
    }

    private void Adopt(SimulationContext context, WorldUnit unit, RouteResult route, string reason)
    {
        unit.LastTrafficCheckAt = context.SimTime;
        unit.Route = route.Legs;
        unit.LegIndex = 0;
        unit.LegProgressMeters = 0;
        unit.PlannedWithNoGoVersion = context.World.NoGoVersion;
        unit.SpeedKph = route.Legs.FirstOrDefault()?.SpeedKph ?? 0;
        unit.Heading = route.Legs.Count > 0 ? GeoMath.BearingDegrees(route.Legs[0].From, route.Legs[0].To) : unit.Heading;

        Report(context, unit, new RouteReported(unit.Id, unit.OrderedIncidentId, route.Path, route.DistanceMeters, route.Duration, reason),
            EventSources.Avl);
        Fix(context, unit, Remaining(context, unit));
    }

    private static RouteResult StraightLine(GeoPoint from, GeoPoint to, UnitType type)
    {
        var length = GeoMath.DistanceMeters(from, to) * DetourFactor;
        var leg = new RouteLeg(from, to, Math.Max(length, 0.1), ResponseSpeedKph(type), null, -1);
        return new RouteResult([leg], leg.LengthMeters, leg.Duration);
    }

    /// <summary>An unreported obstruction on this leg that the crew doesn't already know about.</summary>
    private Guid? ObstructionOn(SimulationContext context, WorldUnit unit, RouteLeg leg)
    {
        if (leg.EdgeId < 0 || routing is null) return null;

        foreach (var (id, (line, _)) in context.World.Obstructions)
        {
            if (unit.KnownObstructions.ContainsKey(id)) continue;
            if (!_obstructedEdges.TryGetValue(id, out var edges))
                _obstructedEdges[id] = edges = routing.BlockedEdges([line]);
            if (edges.Contains(leg.EdgeId)) return id;
        }
        return null;
    }

    private static void Fix(SimulationContext context, WorldUnit unit, TimeSpan? eta)
    {
        unit.LastFixAt = context.SimTime;
        Report(context, unit, new UnitPositionReported(unit.Id, unit.Location, unit.SpeedKph, unit.Heading, eta), EventSources.Avl);
    }

    /// <summary>Spoken reports go by radio; status changes, routes and fixes go by mobile data (Phase 7).</summary>
    private static void Report(SimulationContext context, WorldUnit unit, DomainEvent payload, string source = EventSources.Comms)
    {
        if (payload is ReportReceived report)
            CommsNet.Report(context, unit, report);
        else
            CommsNet.Data(context, unit, payload, source);
    }
}
