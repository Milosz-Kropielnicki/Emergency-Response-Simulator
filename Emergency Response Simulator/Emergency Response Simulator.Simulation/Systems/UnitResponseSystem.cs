using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Moves dispatched units through DISPATCHED → EN ROUTE → ON SCENE → OPERATING (Design Document §6.4):
/// crews turn out, drive to the location command gave them, and start work on arrival.
/// Travel is a straight line with a road-detour factor; Phase 3 replaces it with routing on the road network.
/// What a unit does always happens in the world; command only hears about it if the unit's radio works.
/// </summary>
public sealed class UnitResponseSystem : ISimulationSystem
{
    public static readonly TimeSpan TurnoutTime = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan SizeUpTime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan FixInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    /// <summary>Roads are longer than the straight line between two points.</summary>
    private const double DetourFactor = 1.3;

    public int Order => 10;

    public void Update(SimulationContext context)
    {
        foreach (var unit in context.World.Units.Values)
        {
            if (unit.BrokenDown) continue;

            switch (unit.Phase)
            {
                case ResponsePhase.TurningOut when context.SimTime - unit.PhaseStartedAt >= TurnoutTime:
                    StartTravel(context, unit);
                    break;

                case ResponsePhase.Travelling:
                    Travel(context, unit);
                    break;

                case ResponsePhase.OnScene when context.SimTime - unit.PhaseStartedAt >= SizeUpTime:
                    unit.Phase = ResponsePhase.Operating;
                    unit.PhaseStartedAt = context.SimTime;
                    Report(context, unit, new UnitStatusChanged(unit.Id, UnitStatus.Operating));
                    break;
            }

            // Committed vehicles keep sending AVL heartbeats while stationary, which is how
            // command knows they are still in contact (and notices when they stop).
            if (unit.OrderedIncidentId is not null && unit.Phase != ResponsePhase.Travelling
                && context.SimTime - unit.LastFixAt >= HeartbeatInterval)
            {
                unit.LastFixAt = context.SimTime;
                Report(context, unit, new UnitPositionReported(unit.Id, unit.Location, 0, unit.Heading, null), EventSources.Avl);
            }
        }
    }

    /// <summary>Average urban emergency-response speed; heavy apparatus is slower.</summary>
    public static double ResponseSpeedKph(UnitType type) => type switch
    {
        UnitType.Ladder or UnitType.Tanker or UnitType.Hazmat or UnitType.WaterTender
            or UnitType.HeavyMachinery or UnitType.Bus => 35,
        UnitType.Motorcycle => 55,
        _ => 45,
    };

    public static TimeSpan EstimateTravelTime(GeoPoint from, GeoPoint to, UnitType type) =>
        TimeSpan.FromHours(GeoMath.DistanceMeters(from, to) * DetourFactor / 1000.0 / ResponseSpeedKph(type));

    private static void StartTravel(SimulationContext context, WorldUnit unit)
    {
        if (unit.OrderedIncidentId is not { } incidentId
            || !context.World.ReportedIncidentLocations.TryGetValue(incidentId, out var destination))
        {
            unit.Phase = ResponsePhase.Idle;
            return;
        }

        unit.Phase = ResponsePhase.Travelling;
        unit.PhaseStartedAt = context.SimTime;
        unit.TravelFrom = unit.Location;
        unit.TravelTo = destination;
        unit.TravelTime = EstimateTravelTime(unit.Location, destination, unit.Type);
        unit.Heading = GeoMath.BearingDegrees(unit.Location, destination);
        unit.SpeedKph = ResponseSpeedKph(unit.Type);
        unit.LastFixAt = context.SimTime;

        Report(context, unit, new UnitStatusChanged(unit.Id, UnitStatus.EnRoute));
        Report(context, unit, new UnitPositionReported(unit.Id, unit.Location, unit.SpeedKph, unit.Heading, unit.TravelTime), EventSources.Avl);
    }

    private static void Travel(SimulationContext context, WorldUnit unit)
    {
        var elapsed = context.SimTime - unit.PhaseStartedAt;
        if (elapsed >= unit.TravelTime)
        {
            unit.Location = unit.TravelTo;
            unit.SpeedKph = 0;
            unit.Phase = ResponsePhase.OnScene;
            unit.PhaseStartedAt = context.SimTime;
            Report(context, unit, new UnitPositionReported(unit.Id, unit.Location, 0, unit.Heading, TimeSpan.Zero), EventSources.Avl);
            Report(context, unit, new UnitStatusChanged(unit.Id, UnitStatus.OnScene));
            return;
        }

        var fraction = elapsed / unit.TravelTime;
        unit.Location = new GeoPoint(
            unit.TravelFrom.Latitude + (unit.TravelTo.Latitude - unit.TravelFrom.Latitude) * fraction,
            unit.TravelFrom.Longitude + (unit.TravelTo.Longitude - unit.TravelFrom.Longitude) * fraction);

        if (context.SimTime - unit.LastFixAt >= FixInterval)
        {
            unit.LastFixAt = context.SimTime;
            Report(context, unit, new UnitPositionReported(unit.Id, unit.Location, unit.SpeedKph, unit.Heading,
                unit.TravelTime - elapsed), EventSources.Avl);
        }
    }

    private static void Report(SimulationContext context, WorldUnit unit, DomainEvent payload, string source = EventSources.Comms)
    {
        if (!unit.RadioFailed)
            context.EmitPerceived(payload, source);
    }
}
