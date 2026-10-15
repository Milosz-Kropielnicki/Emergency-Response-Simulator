using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Sustaining the operation (Phase 15).
/// <list type="bullet">
/// <item><b>Supplies</b>: appliances arrive with what they carry (foam, suits, kits, batteries, a little water and food)
/// and that is the incident's first stock. The people working drink, eat and need toilets; engines on a fuel or chemical
/// fire get through foam; a hazardous-materials job gets through suits; ambulance crews use a kit per casualty. Run out,
/// and the work suffers (see <see cref="SupplyFactors"/>). The supply unit reports levels every few minutes and warns
/// when something has less than <see cref="LowWarning"/> left, and again when it runs out.</item>
/// <item><b>Fuel</b>: appliances burn it driving, and far faster pumping. Crews call a quarter tank and fuel critical;
/// at empty the pump stops. A fuel bowser goes round the units at an incident (and its staging area) whenever the
/// incident has fuel in stock.</item>
/// <item><b>Ordering</b>: local orders come quickly from the council's stores until they run out; regional ones need
/// approving and take longer; national reserves are released only in a declared major emergency, and take longest.
/// Deliveries need a staging area or base camp to unload at; without one the driver waits at the cordon.</item>
/// <item><b>Contracts</b>: catering, sanitation and fuel contracts deliver to the incident every couple of hours; plant
/// hire brings an excavator.</item>
/// <item><b>Finance</b>: every hour of every person and appliance working for command is costed (overtime past the end of
/// a shift; mutual aid as its provider will bill it), with supplies, contracts and repairs. The finance section reports
/// every <see cref="CostReportInterval"/>; a claim to central government is decided after <see cref="ClaimDecisionTime"/>.</item>
/// </list>
/// Only command's own units and the mutual aid it brought in are sustained and costed here: other agencies look after theirs.
/// </summary>
public sealed class LogisticsSystem : ISimulationSystem
{
    public static readonly TimeSpan LevelReportInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan CostReportInterval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ClaimDecisionTime = TimeSpan.FromMinutes(10);

    /// <summary>The supply unit warns when something will run out within this time at the current rate.</summary>
    public static readonly TimeSpan LowWarning = TimeSpan.FromMinutes(20);

    /// <summary>The bowser takes this long over each appliance.</summary>
    public static readonly TimeSpan RefuelTime = TimeSpan.FromMinutes(3);

    /// <summary>Appliances below this are topped up when the bowser comes round.</summary>
    public const double RefuelBelow = 0.5;

    /// <summary>A delivery with nowhere to unload waits this long at the cordon.</summary>
    public static readonly TimeSpan NoDropPointDelay = TimeSpan.FromMinutes(15);

    /// <summary>Crews make do with the toilets nearby for this long before sanitation has to be provided.</summary>
    public static readonly TimeSpan SanitationAfter = TimeSpan.FromMinutes(90);

    /// <summary>How much an order from the regional stores can release at once, in typical orders.</summary>
    public const int RegionalCap = 5;

    private const string FinanceSection = "Finance Section";
    private const string SupplyUnit = "Supply Unit";
    private const string NationalDirectorate = "National Directorate for Fire & Emergency Management";

    private readonly HashSet<(Guid Unit, Guid Incident)> _unloaded = [];
    private readonly Dictionary<Guid, double> _fuelWarned = [];
    private readonly HashSet<Guid> _fuelSeen = [];
    private readonly Dictionary<Guid, DateTimeOffset> _bowserFree = [];
    private readonly Dictionary<Guid, DateTimeOffset> _nextLevels = [];
    private readonly HashSet<(Guid, SupplyKind)> _lowWarned = [];
    private readonly HashSet<(Guid, SupplyKind)> _outWarned = [];
    private readonly HashSet<Guid> _plantDelivered = [];
    private DateTimeOffset? _nextCosts;
    private decimal _lastReportedTotal;
    private int _plantUnits;

    public int Order => 13; // after units and ambulance crews (12) have moved and worked; before crews tire (16)

    public void Attach(WorldState world) => world.Logistics.Active = true;

    public void Update(SimulationContext context)
    {
        Fuel(context);
        Consume(context);
        Refuel(context);
        ProgressOrders(context);
        Contracts(context);
        Warn(context);
        ReportLevels(context);
        Accrue(context);
        DecideClaims(context);
    }

    /// <summary>Command's own units and the mutual aid it brought in; other agencies sustain their own.</summary>
    public static bool Ours(WorldState world, WorldUnit unit) =>
        !(unit.AgencyId is { } agency && world.Agencies.GetValueOrDefault(agency)?.AiControlled == true);

    private static bool AtScene(WorldUnit unit) => unit.Phase is ResponsePhase.OnScene or ResponsePhase.Operating or ResponsePhase.Rehab;

    /// <summary>The incident a unit is working at, if it is at one of command's incidents.</summary>
    private static Guid? WorkingAt(WorldState world, WorldUnit unit) =>
        AtScene(unit) && unit.OrderedIncidentId is { } id && world.Logistics.IncidentTypes.ContainsKey(id) ? id : null;

    private static int People(WorldUnit unit) => unit.Crew.Rostered ? unit.Crew.OnDuty : Math.Max(1, unit.CrewSize);

    /// <summary>Appliances that pump: they burn fuel (and wear) far faster working than driving.</summary>
    public static bool Pumps(UnitType type) =>
        type is UnitType.Engine or UnitType.Ladder or UnitType.Tanker or UnitType.WaterTender or UnitType.Hazmat or UnitType.Rescue
            or UnitType.Wildland or UnitType.HeavyMachinery;

    // ---- Fuel ----

    private void Fuel(SimulationContext context)
    {
        var world = context.World;
        var hours = context.Delta.TotalHours;
        foreach (var unit in world.Units.Values)
        {
            if (!Ours(world, unit)) continue;
            // Not every tank is full when the call comes.
            if (_fuelSeen.Add(unit.Id))
                unit.Fuel = 0.45 + SimRandom.For(unit.Id, 151).NextDouble() * 0.55;
            if (unit.OutOfFuel || unit.UnderRepair) continue;

            var burn = unit.Phase switch
            {
                ResponsePhase.Travelling or ResponsePhase.Transporting or ResponsePhase.TurningOut => 0.003 * unit.SpeedKph,
                ResponsePhase.Operating => Pumps(unit.Type) ? 0.25 : 0.06,
                ResponsePhase.OnScene or ResponsePhase.Rehab or ResponsePhase.AtHospital => 0.03,
                _ => 0,
            };
            if (burn <= 0) continue;
            unit.Fuel = Math.Max(0, unit.Fuel - burn * hours);

            var warned = _fuelWarned.GetValueOrDefault(unit.Id, 1);
            if (unit.Fuel <= 0)
            {
                unit.OutOfFuel = true;
                unit.SpeedKph = 0;
                _fuelWarned[unit.Id] = 0;
                CommsNet.Voice(context, unit, $"Control, {unit.Callsign}: we've run out of fuel. {(Pumps(unit.Type) ? "Pump has stopped." : "Vehicle immobilised.")} Need the bowser.",
                    new UnitFuelReported(unit.Id, 0), priority: 3);
            }
            else if (unit.Fuel <= 0.1 && warned > 0.1)
            {
                _fuelWarned[unit.Id] = 0.1;
                CommsNet.Voice(context, unit, $"Control, {unit.Callsign}: fuel critical, ten per cent. We'll be dry within the hour.", new UnitFuelReported(unit.Id, unit.Fuel), priority: 2);
            }
            else if (unit.Fuel <= 0.25 && warned > 0.25)
            {
                _fuelWarned[unit.Id] = 0.25;
                CommsNet.Voice(context, unit, $"Control, {unit.Callsign}: quarter of a tank left.", new UnitFuelReported(unit.Id, unit.Fuel));
            }
        }
    }

    /// <summary>The bowser goes round the units at each incident with fuel in stock, the emptiest first.</summary>
    private void Refuel(SimulationContext context)
    {
        var world = context.World;
        var logistics = world.Logistics;
        foreach (var incident in logistics.Stocks.Keys.Where(i => logistics.Stock(i, SupplyKind.Fuel) > 1).ToList())
        {
            if (_bowserFree.TryGetValue(incident, out var free) && context.SimTime < free) continue;
            var places = logistics.FacilitiesOf(incident).Select(f => f.Location)
                .Concat(logistics.IncidentLocations.TryGetValue(incident, out var at) ? [at] : []).ToList();
            var next = world.Units.Values
                .Where(u => Ours(world, u) && !u.UnderRepair && !u.BrokenDown && u.Fuel < RefuelBelow
                            && (WorkingAt(world, u) == incident
                                || logistics.Staged.TryGetValue(u.Id, out var zone) && logistics.Facilities.GetValueOrDefault(zone)?.IncidentId == incident
                                   && u.Phase == ResponsePhase.Idle
                                || u.OutOfFuel && places.Any(p => GeoMath.DistanceMeters(p, u.Location) <= 600)))
                .OrderBy(u => u.Fuel)
                .FirstOrDefault();
            if (next is null) continue;

            var tank = Supplies.TankLitres(next.Type);
            var litres = logistics.Draw(incident, SupplyKind.Fuel, (1 - next.Fuel) * tank, context.SimTime);
            next.Fuel = Math.Min(1, next.Fuel + litres / tank);
            next.OutOfFuel = false;
            _fuelWarned.Remove(next.Id);
            _bowserFree[incident] = context.SimTime + RefuelTime;
            context.EmitPerceived(new UnitRefuelled(next.Id, incident, Math.Round(litres)), EventSources.Comms);
            context.EmitPerceived(new UnitFuelReported(next.Id, Math.Round(next.Fuel, 2)), EventSources.Comms);
        }
    }

    // ---- What the work gets through ----

    private void Consume(SimulationContext context)
    {
        var world = context.World;
        var logistics = world.Logistics;
        var hours = context.Delta.TotalHours;
        var hot = world.Weather.TemperatureC >= 25 ? 1.5 : 1;

        foreach (var unit in world.Units.Values)
        {
            if (!Ours(world, unit) || WorkingAt(world, unit) is not { } incident) continue;
            logistics.FirstArrival.TryAdd(incident, context.SimTime);

            // What the appliance carries becomes the incident's first stock.
            if (_unloaded.Add((unit.Id, incident)))
                foreach (var (kind, quantity) in Supplies.Onboard(unit.Type, People(unit)))
                    logistics.Add(incident, kind, quantity);
            if (unit.Disabled) continue;

            var type = logistics.IncidentTypes[incident];
            var people = People(unit);
            var protectiveWork = SupplyFactors.PpePerPersonHour(type);
            logistics.Draw(incident, SupplyKind.Water, people * (protectiveWork >= 0.3 ? 1.0 : 0.6) * hot * hours, context.SimTime);
            logistics.Draw(incident, SupplyKind.Food, people * 0.25 * hours, context.SimTime);
            if (context.SimTime - logistics.FirstArrival[incident] >= SanitationAfter)
                logistics.Draw(incident, SupplyKind.Sanitation, people * 0.04 * hours, context.SimTime);

            if (unit.Phase != ResponsePhase.Operating) continue;
            if (CrewFactors.IsFireCrew(unit.Type) && SupplyFactors.NeedsFoam(type) && Pumps(unit.Type))
                logistics.Draw(incident, SupplyKind.Foam, 480 * hours, context.SimTime); // 8 L/min of concentrate
            if (protectiveWork > 0 && ResourceGroups.AgencyFor(unit.Type) is AgencyType.Fire or AgencyType.Ems)
                logistics.Draw(incident, SupplyKind.Ppe, people * protectiveWork * hours, context.SimTime);
        }
    }

    // ---- Orders ----

    private void ProgressOrders(SimulationContext context)
    {
        var world = context.World;
        var logistics = world.Logistics;
        var pace = world.Interagency.MajorEmergency ? 0.6 : 1;
        foreach (var pending in logistics.Orders.ToList())
        {
            var order = pending.Order;
            if (!pending.Decided)
            {
                if (context.SimTime - pending.RequestedAt < Supplies.DecisionTime(order.Tier) * pace) continue;
                Decide(context, pending);
                continue;
            }
            if (pending.ArriveAt is not { } due || context.SimTime < due) continue;

            var incident = order.IncidentId;
            if (!logistics.FacilitiesOf(incident).Any() && !pending.HeldForDropPoint)
            {
                pending.HeldForDropPoint = true;
                pending.ArriveAt = context.SimTime + NoDropPointDelay;
                CommsNet.Chat(context, pending.Supplier,
                    $"{pending.Supplier}: our driver is at the cordon of {logistics.NumberOf(incident)} with the {Supplies.Name(order.Kind).ToLowerInvariant()} " +
                    "and nobody can say where to unload. No staging area or base camp has been set up.",
                    new SupplyDeliveryDelayed(order.OrderId, pending.ArriveAt.Value, "no staging area or base camp to unload at"), TimeSpan.Zero);
                continue;
            }

            logistics.Orders.Remove(pending);
            logistics.Add(incident, order.Kind, pending.Granted);
            var cost = pending.Granted * Supplies.UnitCost(order.Kind) * Supplies.Premium(order.Tier);
            logistics.Costs.Add(CostCategory.Supplies, cost, logistics.MajorEmergencySince is not null);
            Delivered(incident, order.Kind);
            context.EmitPerceived(new SuppliesDelivered(order.OrderId, incident, order.Kind, pending.Granted, pending.Supplier, Math.Round(cost, 2)), EventSources.Comms);
        }
    }

    private static void Decide(SimulationContext context, WorldLogistics.PendingSupplyOrder pending)
    {
        var world = context.World;
        var logistics = world.Logistics;
        var order = pending.Order;
        pending.Decided = true;
        var name = Supplies.Name(order.Kind).ToLowerInvariant();
        string? reason = null;
        int granted;
        switch (order.Tier)
        {
            case SupplyTier.Local:
                var left = (int)Math.Floor(logistics.LocalRemaining.GetValueOrDefault(order.Kind));
                granted = Math.Min(order.Quantity, left);
                if (granted <= 0) reason = $"the council stores and local suppliers have no more {name}. Order it from the regional level";
                else if (granted < order.Quantity) reason = $"that is all the {name} left locally";
                logistics.LocalRemaining[order.Kind] = left - Math.Max(granted, 0);
                pending.Supplier = Supplies.Supplier(SupplyTier.Local);
                break;
            case SupplyTier.Regional:
                granted = Math.Min(order.Quantity, Supplies.TypicalOrder(order.Kind) * RegionalCap);
                if (granted < order.Quantity) reason = $"the regional stores release up to {granted} at a time";
                pending.Supplier = "Regional emergency stores";
                break;
            default:
                granted = world.Interagency.MajorEmergency ? order.Quantity : 0;
                if (granted == 0) reason = "national reserves are released only for a declared major emergency (the EOC at partial activation or above)";
                pending.Supplier = order.Kind switch
                {
                    SupplyKind.Fuel => "National Oil Reserves Agency",
                    SupplyKind.Food or SupplyKind.Water or SupplyKind.Sanitation => "Civil Defence",
                    _ => "Defence Forces logistics",
                };
                break;
        }

        var approver = Supplies.Supplier(order.Tier);
        if (granted <= 0)
        {
            logistics.Orders.Remove(pending);
            CommsNet.Chat(context, approver, $"{approver}: we can't supply that order — {reason}.",
                new SupplyOrderDecided(order.OrderId, false, 0, approver, null, Capital(reason!) + "."), TimeSpan.Zero);
            return;
        }
        pending.Granted = granted;
        pending.ArriveAt = context.SimTime + Supplies.LeadTime(order.Tier);
        CommsNet.Chat(context, approver,
            $"{approver}: {Supplies.Quantity(order.Kind, granted)} of {name} for {logistics.NumberOf(order.IncidentId)} from {pending.Supplier}, " +
            $"due {pending.ArriveAt.Value.ToLocalTime():HH:mm}" + (reason is null ? "." : $" ({reason})."),
            new SupplyOrderDecided(order.OrderId, true, granted, pending.Supplier, pending.ArriveAt, reason is null ? null : Capital(reason)), TimeSpan.Zero);
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>A delivery lets the supply unit warn afresh about that supply.</summary>
    private void Delivered(Guid incident, SupplyKind kind)
    {
        _lowWarned.Remove((incident, kind));
        _outWarned.Remove((incident, kind));
    }

    // ---- Contracts ----

    /// <summary>When a contract's first delivery comes, and how often after.</summary>
    public static readonly TimeSpan ContractInterval = TimeSpan.FromHours(2);

    public static TimeSpan ContractFirstDelivery(ContractKind kind) => kind switch
    {
        ContractKind.FuelSupply => TimeSpan.FromMinutes(40),
        ContractKind.Catering or ContractKind.PlantHire => TimeSpan.FromMinutes(45),
        _ => TimeSpan.FromMinutes(60),
    };

    private void Contracts(SimulationContext context)
    {
        var world = context.World;
        var logistics = world.Logistics;
        foreach (var state in logistics.Contracts.Values)
        {
            var contract = state.Contract;
            if (contract.Kind == ContractKind.Other) continue;
            state.NextDeliveryAt ??= state.AwardedAt + ContractFirstDelivery(contract.Kind);
            if (context.SimTime < state.NextDeliveryAt) continue;

            if (contract.Kind == ContractKind.PlantHire)
            {
                if (_plantDelivered.Add(contract.ContractId))
                    DeliverPlant(context, contract);
                state.NextDeliveryAt = DateTimeOffset.MaxValue;
                continue;
            }
            if (contract.IncidentId is not { } incident) continue;
            state.NextDeliveryAt = context.SimTime + ContractInterval;
            state.Deliveries++;

            var people = Math.Max(10, world.Units.Values.Where(u => Ours(world, u) && WorkingAt(world, u) == incident).Sum(People));
            List<(SupplyKind Kind, double Quantity)> load = contract.Kind switch
            {
                ContractKind.Catering => [(SupplyKind.Food, Math.Ceiling(people * 0.6)), (SupplyKind.Water, Math.Ceiling(people * 1.5))],
                ContractKind.Sanitation => [(SupplyKind.Sanitation, 4)],
                _ => [(SupplyKind.Fuel, 1500)],
            };
            foreach (var (kind, quantity) in load)
            {
                logistics.Add(incident, kind, quantity);
                Delivered(incident, kind);
                context.EmitPerceived(new SuppliesDelivered(contract.ContractId, incident, kind, quantity, contract.Vendor, 0), EventSources.Comms);
            }
        }
    }

    /// <summary>Plant hire: an excavator and its operator join the roster at the edge of the area, for command to task.</summary>
    private void DeliverPlant(SimulationContext context, ContractAwarded contract)
    {
        var entry = CommandResponseSystem.EntryPoints[_plantUnits % CommandResponseSystem.EntryPoints.Length];
        var unitId = Guid.NewGuid();
        context.EmitPerceived(new UnitRegistered(unitId, $"Plant {++_plantUnits} ({contract.Vendor})", UnitType.HeavyMachinery, null, entry, contract.Vendor, 1,
            ["Excavation", "Lifting"]), EventSources.Engine);
        CommsNet.Chat(context, contract.Vendor, $"{contract.Vendor}: excavator and operator arrived, ready to be tasked.", null, TimeSpan.Zero);
    }

    // ---- The supply unit's reports ----

    private void Warn(SimulationContext context)
    {
        var logistics = context.World.Logistics;
        foreach (var (incident, stock) in logistics.Stocks)
        {
            foreach (var kind in Supplies.All)
            {
                var key = (incident, kind);
                var onHand = stock.GetValueOrDefault(kind);
                var perHour = logistics.PerHour(incident, kind, context.SimTime);
                // Restocked (a delivery, or an appliance arriving with its load): warn afresh next time.
                if (!logistics.ShortSince.ContainsKey(key)) _outWarned.Remove(key);
                if (perHour <= 0.01 || onHand / perHour > 1.5 * LowWarning.TotalHours) _lowWarned.Remove(key);

                if (logistics.ShortSince.ContainsKey(key))
                {
                    if (!_outWarned.Add(key)) continue;
                    _lowWarned.Add(key);
                    var number = logistics.NumberOf(incident);
                    context.EmitPerceived(new SupplyShortage(incident, kind, true, 0, null), EventSources.Comms);
                    context.EmitPerceived(new AlertRaised(Guid.NewGuid(), AlertCategory.ResourceShortage,
                        kind is SupplyKind.Fuel or SupplyKind.Foam or SupplyKind.Water or SupplyKind.Ppe ? AlertSeverity.Critical : AlertSeverity.Warning,
                        $"{Supplies.Name(kind)} exhausted at {number}", Effect(kind), incident, null), EventSources.Comms);
                    continue;
                }
                if (perHour <= 0.01 || onHand / perHour > LowWarning.TotalHours || !_lowWarned.Add(key)) continue;
                context.EmitPerceived(new SupplyShortage(incident, kind, false, Math.Round(onHand, 1), (int)Math.Ceiling(onHand / perHour * 60)), EventSources.Comms);
            }
        }
    }

    /// <summary>What running out means, for the alert.</summary>
    public static string Effect(SupplyKind kind) => kind switch
    {
        SupplyKind.Fuel => "The bowser has nothing left to give: appliances will run dry and stop.",
        SupplyKind.Food or SupplyKind.Water => "Crews are going without: they tire faster and rehab barely helps.",
        SupplyKind.Foam => "Crews are on water alone, which won't hold a fuel or chemical fire.",
        SupplyKind.MedicalSupplies => "Ambulance crews are improvising: each treatment takes twice as long.",
        SupplyKind.Batteries => "Flat handheld radios can't be swapped: crews will go quiet.",
        SupplyKind.Ppe => "No suits for entry: crews can't work properly in the hot zone.",
        _ => "No toilets or hand-washing at the scene: crews tire faster, and they're complaining.",
    };

    private void ReportLevels(SimulationContext context)
    {
        var world = context.World;
        var logistics = world.Logistics;
        foreach (var (incident, stock) in logistics.Stocks)
        {
            if (_nextLevels.TryGetValue(incident, out var due) && context.SimTime < due) continue;
            _nextLevels[incident] = context.SimTime + LevelReportInterval;
            var levels = Supplies.All
                .Select(k => new SupplyLevel(k, Math.Round(stock.GetValueOrDefault(k), 1), Math.Round(logistics.PerHour(incident, k, context.SimTime), 1)))
                .Where(l => l.OnHand > 0 || l.PerHour > 0 || logistics.ShortSince.ContainsKey((incident, l.Kind)))
                .ToList();
            var people = world.Units.Values.Where(u => Ours(world, u) && WorkingAt(world, u) == incident).Sum(People);
            if (levels.Count == 0 && people == 0) continue;
            context.EmitPerceived(new SupplyLevelsReported(incident, people, levels), EventSources.Comms);
        }
    }

    // ---- Finance ----

    private void Accrue(SimulationContext context)
    {
        var world = context.World;
        var logistics = world.Logistics;
        var hours = (decimal)context.Delta.TotalHours;
        var eligible = logistics.MajorEmergencySince is not null;
        foreach (var unit in world.Units.Values)
        {
            if (!Ours(world, unit)) continue;
            var working = unit.OrderedIncidentId is { } ordered && !world.AgencyTasks.ContainsKey(ordered)
                          || logistics.Staged.ContainsKey(unit.Id);
            if (!working) continue;

            var people = (decimal)People(unit);
            var overtime = unit.Crew.Rostered && context.SimTime >= unit.Crew.ShiftEnd;
            var pay = people * CostRates.PersonHour * hours * (overtime ? CostRates.OvertimeMultiplier : 1);
            var equipment = CostRates.EquipmentHour(unit.Type) * hours;
            if (logistics.MutualAidUnits.Contains(unit.Id))
            {
                logistics.Costs.Add(CostCategory.MutualAid, (pay + equipment) * CostRates.MutualAidAdministration, eligible);
                continue;
            }
            logistics.Costs.Add(overtime ? CostCategory.Overtime : CostCategory.Personnel, pay, eligible && overtime);
            logistics.Costs.Add(CostCategory.Equipment, equipment, eligible);
        }

        if (_nextCosts is { } next && context.SimTime < next) return;
        var total = logistics.Costs.Sum;
        if (total <= 0) return;
        _nextCosts = context.SimTime + CostReportInterval;
        if (Math.Abs(total - _lastReportedTotal) < 1) return;
        _lastReportedTotal = total;
        context.EmitPerceived(new CostsReported(logistics.Costs.Lines(), Math.Round(total, 2), Math.Round(logistics.Costs.Unclaimed, 2)), EventSources.Engine);
    }

    private static void DecideClaims(SimulationContext context)
    {
        var logistics = context.World.Logistics;
        foreach (var claim in logistics.Claims.ToList())
        {
            if (context.SimTime - claim.SubmittedAt < ClaimDecisionTime) continue;
            logistics.Claims.Remove(claim);
            var approved = Math.Round(Math.Min(claim.Claim.Amount, claim.Supported), 2);
            var text = $"{NationalDirectorate}: claim received. We will meet {CostRates.Money(approved)} of the {CostRates.Money(claim.Claim.Amount)} claimed." +
                       (claim.Disallowed.Count > 0 ? $" Not eligible: {string.Join("; ", claim.Disallowed)}." : "");
            CommsNet.Chat(context, NationalDirectorate, text,
                new ReimbursementDecided(claim.Claim.ClaimId, claim.Claim.Amount, approved, claim.Disallowed), TimeSpan.Zero);
        }
    }
}
