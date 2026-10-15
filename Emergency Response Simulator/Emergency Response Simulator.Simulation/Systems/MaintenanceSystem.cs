using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Equipment maintenance and failure (Phase 15).
/// <list type="bullet">
/// <item><b>Wear</b>: every appliance starts somewhere between just serviced and due a service, and wears with use: a
/// little driving, a lot pumping for hours. Past <see cref="WarningWear"/> the crew notices something (a pump running
/// hot, a hydraulic leak) and says so; it still works.</item>
/// <item><b>Failure</b>: from then on it may break down at any time, more likely the more worn it is. A warning heeded
/// (sent for servicing) almost always prevents it. The crew diagnoses a breakdown and says whether a mechanic can fix it
/// where it stands.</item>
/// <item><b>Repair</b>: a mechanic reaches it in <see cref="MechanicTravel"/> and fixes a minor fault in 15–30 minutes;
/// recovery to the workshop takes <see cref="RecoveryTravel"/> to arrive and 90–150 minutes in the workshop, and the
/// unit comes back to its station. Servicing takes <see cref="ServiceTime"/>, at once at a staging area, base camp or
/// station (a mechanic is there), otherwise once the mechanic arrives. Every repair is costed.</item>
/// </list>
/// Only command's own units (and its mutual aid) are maintained here; outcomes are seeded from the unit's id.
/// </summary>
public sealed class MaintenanceSystem : ISimulationSystem
{
    public const double WarningWear = 0.75;
    public static readonly TimeSpan MechanicTravel = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan RecoveryTravel = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ServiceTime = TimeSpan.FromMinutes(20);

    private const string FleetMaintenance = "Fleet Maintenance";

    private readonly HashSet<Guid> _seen = [];
    private readonly HashSet<Guid> _warned = [];
    private readonly Dictionary<Guid, long> _rolledMinute = [];

    public int Order => 11; // after units move (10), before ambulance crews and logistics read who can work

    public void Update(SimulationContext context)
    {
        Wear(context);
        Repair(context);
    }

    // ---- Wear and failure ----

    private void Wear(SimulationContext context)
    {
        var world = context.World;
        var hours = context.Delta.TotalHours;
        var minute = context.SimTime.ToUnixTimeSeconds() / 60;
        foreach (var unit in world.Units.Values)
        {
            if (!LogisticsSystem.Ours(world, unit)) continue;
            if (_seen.Add(unit.Id))
                unit.Wear = 0.15 + SimRandom.For(unit.Id, 152).NextDouble() * 0.45;
            if (unit.Disabled) continue;

            var rate = unit.Phase switch
            {
                ResponsePhase.Operating => LogisticsSystem.Pumps(unit.Type) ? 0.15 : 0.04,
                ResponsePhase.Travelling or ResponsePhase.Transporting => 0.0015 * unit.SpeedKph,
                _ => 0,
            };
            unit.Wear = Math.Min(1, unit.Wear + rate * hours);
            if (unit.Wear < WarningWear) continue;

            var fault = Maintenance.FaultFor(unit);
            if (_warned.Add(unit.Id))
                CommsNet.Voice(context, unit, $"Control, {unit.Callsign}: {fault.Warning}. Still working, but she needs looking at.",
                    new UnitDefectReported(unit.Id, Maintenance.Capital(fault.Warning), Immobilised: false, FieldRepairable: false));

            // Once a minute, a chance of failing that grows with wear: about one in three per hour at 0.8, near certain at 1.
            if (rate <= 0 || _rolledMinute.GetValueOrDefault(unit.Id) == minute) continue;
            _rolledMinute[unit.Id] = minute;
            var perMinute = (unit.Wear - 0.7) * 4 / 60;
            if (SimRandom.For(unit.Id, (int)(minute % 100_000) + 1000).NextDouble() < perMinute)
                context.EmitTruth(new UnitBrokeDown(unit.Id, fault.Fault));
        }
    }

    // ---- Repairs and servicing ----

    private void Repair(SimulationContext context)
    {
        var world = context.World;
        var logistics = world.Logistics;
        foreach (var repair in logistics.Repairs.Values.ToList())
        {
            if (!world.Units.TryGetValue(repair.UnitId, out var unit))
            {
                logistics.Repairs.Remove(repair.UnitId);
                continue;
            }
            if (!repair.Started) Schedule(context, repair, unit);

            // The mechanic (or the recovery truck) has reached it.
            if (!unit.UnderRepair && repair.StartAt is { } start && context.SimTime >= start)
            {
                unit.UnderRepair = true;
                unit.SpeedKph = 0;
                if (repair.Kind == RepairKind.Workshop)
                {
                    unit.Location = unit.Home;
                    unit.Route = [];
                    unit.Phase = ResponsePhase.Idle;
                    unit.OrderedIncidentId = null;
                }
            }
            if (repair.DoneAt is not { } done || context.SimTime < done) continue;

            logistics.Repairs.Remove(repair.UnitId);
            var atHome = repair.Kind == RepairKind.Workshop;
            unit.BrokenDown = false;
            unit.BreakdownReported = false;
            unit.BreakdownFault = null;
            unit.BrokeDownAt = null;
            unit.UnderRepair = false;
            unit.Wear = repair.Kind == RepairKind.Service ? 0.2 : 0.1;
            unit.Phase = ResponsePhase.Idle;
            unit.OrderedIncidentId = null;
            unit.SpeedKph = 0;
            _warned.Remove(unit.Id);
            CommsNet.Chat(context, FleetMaintenance,
                $"{FleetMaintenance}: {unit.Callsign} is {(repair.Kind == RepairKind.Service ? "serviced" : "repaired")} and back in service{(atHome ? " at its station" : "")}.",
                new UnitRepaired(unit.Id, repair.Kind, atHome), TimeSpan.Zero);
            if (atHome)
                CommsNet.Data(context, unit, new UnitPositionReported(unit.Id, unit.Location, 0, unit.Heading, null), EventSources.Avl);
            CommsNet.Data(context, unit, new UnitStatusChanged(unit.Id, UnitStatus.Available), EventSources.Comms);
        }
    }

    private static void Schedule(SimulationContext context, WorldLogistics.PendingRepair repair, WorldUnit unit)
    {
        var logistics = context.World.Logistics;
        repair.Started = true;
        var random = SimRandom.For(unit.Id, 153 + logistics.Repairs.Count);

        // A mechanic sent to a fault only the workshop can fix says so, and calls the recovery truck.
        var note = "";
        if (repair.Kind == RepairKind.FieldRepair && unit.BrokenDown && !Maintenance.FieldRepairable(unit.BreakdownFault))
        {
            repair.Kind = RepairKind.Workshop;
            note = " The mechanic says it can't be fixed at the roadside: it is going to the workshop.";
        }

        decimal cost;
        string provider;
        switch (repair.Kind)
        {
            case RepairKind.Service:
                var mechanicThere = GeoMath.DistanceMeters(unit.Location, unit.Home) <= 200
                                    || logistics.Facilities.Values.Any(f => GeoMath.DistanceMeters(f.Location, unit.Location) <= 300);
                repair.StartAt = context.SimTime + (mechanicThere ? TimeSpan.Zero : MechanicTravel);
                repair.DoneAt = repair.StartAt + ServiceTime;
                cost = 300m;
                provider = FleetMaintenance;
                // Off the job while it is done: the crew books out of service.
                unit.UnderRepair = mechanicThere;
                unit.Phase = ResponsePhase.Idle;
                unit.OrderedIncidentId = null;
                unit.SpeedKph = 0;
                CommsNet.Data(context, unit, new UnitStatusChanged(unit.Id, UnitStatus.OutOfService), EventSources.Comms);
                break;
            case RepairKind.FieldRepair:
                repair.StartAt = context.SimTime + MechanicTravel;
                repair.DoneAt = repair.StartAt + TimeSpan.FromMinutes(15 + random.NextDouble() * 15);
                cost = 450m + Math.Round((decimal)random.NextDouble() * 300m);
                provider = FleetMaintenance;
                break;
            default:
                repair.StartAt = context.SimTime + RecoveryTravel;
                repair.DoneAt = repair.StartAt + TimeSpan.FromMinutes(90 + random.NextDouble() * 60);
                cost = 2000m + Math.Round((decimal)random.NextDouble() * 4000m);
                provider = "Fleet workshop";
                break;
        }

        logistics.Costs.Add(CostCategory.Repairs, cost, logistics.MajorEmergencySince is not null);
        var what = repair.Kind switch
        {
            RepairKind.Service => "servicing",
            RepairKind.FieldRepair => "a mechanic on the way",
            _ => "recovery to the workshop",
        };
        CommsNet.Chat(context, provider,
            $"{provider}: {unit.Callsign}, {what}.{note} Back in service about {repair.DoneAt!.Value.ToLocalTime():HH:mm}; {CostRates.Money(cost)}.",
            new RepairScheduled(unit.Id, repair.Kind, repair.DoneAt.Value, cost, provider), TimeSpan.Zero);
    }
}

/// <summary>What goes wrong with appliances, and what a mechanic can fix where it stands (Phase 15).</summary>
public static class Maintenance
{
    /// <summary>A warning sign the crew notices, and the failure it leads to.</summary>
    public sealed record FaultPair(string Warning, string Fault);

    private static readonly string[] FieldFixes = ["tyre", "belt", "alternator", "hose", "battery", "generator", "wiper", "light"];

    /// <summary>Whether a mechanic can fix the fault at the roadside; anything else needs the workshop.</summary>
    public static bool FieldRepairable(string? fault) =>
        fault is not null && FieldFixes.Any(f => fault.Contains(f, StringComparison.OrdinalIgnoreCase));

    /// <summary>This unit's likely failure, by type (seeded from its id, so the warning and the failure match).</summary>
    public static FaultPair FaultFor(WorldUnit unit)
    {
        FaultPair[] options = unit.Type switch
        {
            UnitType.Engine or UnitType.Wildland =>
                [new("pump overheating, pressure dropping", "pump seal has failed"), new("fan belt squealing", "fan belt has snapped")],
            UnitType.Ladder => [new("aerial hydraulics leaking", "aerial hydraulics have failed")],
            UnitType.Tanker or UnitType.WaterTender => [new("air brakes dragging", "air brakes have locked on")],
            UnitType.Hazmat or UnitType.Rescue => [new("generator surging", "generator has failed"), new("engine running hot", "radiator has split")],
            UnitType.AmbulanceAls or UnitType.AmbulanceBls =>
                [new("engine warning light on, losing power", "turbo has failed, engine in limp mode"), new("battery warning light on", "alternator has failed")],
            _ => [new("engine running hot", "coolant hose has burst, engine overheated")],
        };
        return options[SimRandom.For(unit.Id, 154).Next(options.Length)];
    }

    public static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
