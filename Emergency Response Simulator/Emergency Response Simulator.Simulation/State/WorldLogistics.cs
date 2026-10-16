using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.State;

/// <summary>
/// Ground truth about sustaining the operation (Phase 15): what each incident really has on hand, what is on order and
/// on the road, staging areas and base camps, units at staging or with a mechanic, contracts, and what it has all cost.
/// Discrete changes come from the event stream; <c>LogisticsSystem</c> and <c>MaintenanceSystem</c> advance the rest
/// (consumption, fuel, wear) each tick. Without <see cref="Active"/> nobody runs short of anything.
/// </summary>
public sealed class WorldLogistics
{
    /// <summary>How far back consumption is averaged for the burn rate.</summary>
    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(30);

    /// <summary>Set by <c>LogisticsSystem</c> when it is attached: supplies are tracked and can run out.</summary>
    public bool Active { get; set; }

    /// <summary>A staging area or base camp.</summary>
    public sealed record Facility(Guid ZoneId, Guid? IncidentId, ZoneType Type, string Name, GeoPoint Location);

    public sealed class PendingSupplyOrder(SuppliesOrdered order, DateTimeOffset at)
    {
        public SuppliesOrdered Order { get; } = order;
        public DateTimeOffset RequestedAt { get; } = at;
        public bool Decided { get; set; }
        public int Granted { get; set; }
        public string Supplier { get; set; } = Supplies.Supplier(order.Tier);
        public DateTimeOffset? ArriveAt { get; set; }

        /// <summary>The driver has already been held up once for want of somewhere to unload.</summary>
        public bool HeldForDropPoint { get; set; }
    }

    public sealed class PendingRepair(Guid unitId, RepairKind kind, DateTimeOffset requestedAt)
    {
        public Guid UnitId { get; } = unitId;
        public RepairKind Kind { get; set; } = kind;
        public DateTimeOffset RequestedAt { get; } = requestedAt;

        /// <summary>When the mechanic (or the recovery truck) reaches the unit.</summary>
        public DateTimeOffset? StartAt { get; set; }

        public DateTimeOffset? DoneAt { get; set; }
        public bool Started { get; set; }
    }

    public sealed class ContractState(ContractAwarded contract, DateTimeOffset at)
    {
        public ContractAwarded Contract { get; } = contract;
        public DateTimeOffset AwardedAt { get; } = at;
        public DateTimeOffset? NextDeliveryAt { get; set; }
        public int Deliveries { get; set; }
    }

    public sealed class PendingClaim(ReimbursementClaimSubmitted claim, DateTimeOffset at, decimal supported, IReadOnlyList<string> disallowed)
    {
        public ReimbursementClaimSubmitted Claim { get; } = claim;
        public DateTimeOffset SubmittedAt { get; } = at;

        /// <summary>What the records support at the time of the claim: eligible costs not yet claimed.</summary>
        public decimal Supported { get; } = supported;

        public IReadOnlyList<string> Disallowed { get; } = disallowed;
    }

    private readonly Dictionary<(Guid Incident, SupplyKind Kind), List<(DateTimeOffset At, double Quantity)>> _use = [];

    /// <summary>What each incident really has on hand, by the perceived incident command opened.</summary>
    public Dictionary<Guid, Dictionary<SupplyKind, double>> Stocks { get; } = [];

    /// <summary>Supplies an incident needed and didn't have, since when: crews are doing without.</summary>
    public Dictionary<(Guid Incident, SupplyKind Kind), DateTimeOffset> ShortSince { get; } = [];

    /// <summary>What the council depot and local suppliers still have.</summary>
    public Dictionary<SupplyKind, double> LocalRemaining { get; } = Supplies.All.ToDictionary(k => k, Supplies.LocalAvailability);

    public Dictionary<Guid, IncidentType> IncidentTypes { get; } = [];
    public Dictionary<Guid, string> IncidentNumbers { get; } = [];
    public Dictionary<Guid, GeoPoint> IncidentLocations { get; } = [];

    /// <summary>When responders first got to work at each incident.</summary>
    public Dictionary<Guid, DateTimeOffset> FirstArrival { get; } = [];

    public Dictionary<Guid, Facility> Facilities { get; } = [];

    /// <summary>Units sent to (or waiting at) a staging area, by unit: the zone.</summary>
    public Dictionary<Guid, Guid> Staged { get; } = [];

    public List<PendingSupplyOrder> Orders { get; } = [];
    public Dictionary<Guid, PendingRepair> Repairs { get; } = [];
    public Dictionary<Guid, ContractState> Contracts { get; } = [];
    public List<PendingClaim> Claims { get; } = [];

    /// <summary>Units that came as mutual aid: their provider bills for them.</summary>
    public HashSet<Guid> MutualAidUnits { get; } = [];

    private readonly HashSet<Guid> _mutualAidRequests = [];

    /// <summary>When the EOC first reached partial activation: costs from then on can be claimed.</summary>
    public DateTimeOffset? MajorEmergencySince { get; private set; }

    public CostLedger Costs { get; } = new();

    public string NumberOf(Guid incidentId) => IncidentNumbers.GetValueOrDefault(incidentId) ?? "the incident";

    public double Stock(Guid incident, SupplyKind kind) =>
        Stocks.TryGetValue(incident, out var stock) ? stock.GetValueOrDefault(kind) : 0;

    public void Add(Guid incident, SupplyKind kind, double quantity)
    {
        if (quantity <= 0) return;
        if (!Stocks.TryGetValue(incident, out var stock)) Stocks[incident] = stock = [];
        stock[kind] = stock.GetValueOrDefault(kind) + quantity;
        ShortSince.Remove((incident, kind));
    }

    /// <summary>
    /// Takes what is wanted, or what there is. Wanting more than there is marks the supply short at the incident:
    /// whoever needed it goes without.
    /// </summary>
    public double Draw(Guid incident, SupplyKind kind, double wanted, DateTimeOffset at)
    {
        if (wanted <= 0) return 0;
        if (!Stocks.TryGetValue(incident, out var stock)) Stocks[incident] = stock = [];
        var have = stock.GetValueOrDefault(kind);
        var taken = Math.Min(have, wanted);
        stock[kind] = have - taken;
        if (taken > 0)
        {
            if (!_use.TryGetValue((incident, kind), out var log)) _use[(incident, kind)] = log = [];
            _firstUse.TryAdd((incident, kind), at);
            log.Add((at, taken));
            log.RemoveAll(u => at - u.At > RateWindow);
        }
        if (taken < wanted - 1e-9) ShortSince.TryAdd((incident, kind), at);
        return taken;
    }

    /// <summary>
    /// How fast a supply is going at an incident, per hour: over the last half hour, or since use began (but at least
    /// five minutes, so the first few draws don't swing it).
    /// </summary>
    public double PerHour(Guid incident, SupplyKind kind, DateTimeOffset now)
    {
        if (!_use.TryGetValue((incident, kind), out var log) || log.Count == 0) return 0;
        var recent = log.Where(u => now - u.At <= RateWindow).Sum(u => u.Quantity);
        var since = now - _firstUse[(incident, kind)];
        var span = since < TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : since > RateWindow ? RateWindow : since;
        return recent / span.TotalHours;
    }

    private readonly Dictionary<(Guid, SupplyKind), DateTimeOffset> _firstUse = [];

    /// <summary>Whether crews at the incident are going without.</summary>
    public bool Short(Guid? incident, SupplyKind kind) => Active && incident is { } id && ShortSince.ContainsKey((id, kind));

    public IEnumerable<Facility> FacilitiesOf(Guid incident) => Facilities.Values.Where(f => f.IncidentId == incident);

    public bool HasBaseCamp(Guid incident) => FacilitiesOf(incident).Any(f => f.Type == ZoneType.BaseCamp);

    public void Apply(DomainEvent payload, DateTimeOffset at)
    {
        switch (payload)
        {
            case IncidentCreated e:
                IncidentTypes[e.IncidentId] = e.Type;
                IncidentNumbers[e.IncidentId] = e.Number;
                IncidentLocations[e.IncidentId] = e.Location;
                break;

            case ZoneDeclared e when e.Type is ZoneType.StagingArea or ZoneType.BaseCamp && e.Boundary.Count > 0:
                Facilities[e.ZoneId] = new Facility(e.ZoneId, e.IncidentId, e.Type, e.Name,
                    new GeoPoint(e.Boundary.Average(p => p.Latitude), e.Boundary.Average(p => p.Longitude)));
                break;
            case ZoneLifted e:
                Facilities.Remove(e.ZoneId);
                foreach (var unit in Staged.Where(s => s.Value == e.ZoneId).Select(s => s.Key).ToList()) Staged.Remove(unit);
                break;
            case UnitStaged e:
                Staged[e.UnitId] = e.ZoneId;
                break;
            case UnitDispatched e:
                Staged.Remove(e.UnitId);
                break;

            case SuppliesOrdered e:
                Orders.Add(new PendingSupplyOrder(e, at));
                break;
            case SupplyDeliveryDelayed e when Orders.FirstOrDefault(o => o.Order.OrderId == e.OrderId) is { ArriveAt: not null } late:
                late.ArriveAt = e.ExpectedAt;
                break;
            case LocalStocksDepleted e:
                LocalRemaining[e.Kind] = 0;
                break;

            case RepairRequested e:
                Repairs[e.UnitId] = new PendingRepair(e.UnitId, e.Kind, at);
                break;
            case UnitServiceOrdered e:
                Repairs[e.UnitId] = new PendingRepair(e.UnitId, RepairKind.Service, at);
                break;

            case ContractAwarded e:
                Contracts[e.ContractId] = new ContractState(e, at);
                Costs.Add(CostCategory.Contracts, e.Value, MajorEmergencySince is not null && Documented(e));
                if (Documented(e) is false)
                    Costs.Disallow($"the {EventDescriber.Humanize(e.Kind).ToLowerInvariant()} contract with {e.Vendor} ({CostRates.Money(e.Value)}): " +
                                   "a direct award above the threshold with no written justification");
                else if (MajorEmergencySince is null)
                    Costs.Disallow($"the {EventDescriber.Humanize(e.Kind).ToLowerInvariant()} contract with {e.Vendor} ({CostRates.Money(e.Value)}): " +
                                   "let before the major emergency was declared");
                break;

            case ResourceRequested { Kind: ResourceRequestKind.MutualAid } e:
                _mutualAidRequests.Add(e.RequestId);
                break;
            case ResourceRequestFulfilled e when _mutualAidRequests.Contains(e.RequestId):
                MutualAidUnits.UnionWith(e.UnitIds);
                break;

            case EocLevelReached { Level: >= EocLevel.Partial }:
                MajorEmergencySince ??= at;
                break;

            case ReimbursementClaimSubmitted e:
                var disallowed = Costs.TakeDisallowed();
                if (Costs.Total.GetValueOrDefault(CostCategory.Personnel) > 0)
                    disallowed.Add($"straight-time pay of our own staff ({CostRates.Money(Costs.Total[CostCategory.Personnel])}): only overtime is claimable");
                var supported = Costs.Unclaimed;
                if (e.Amount > supported + 1)
                    disallowed.Add($"{CostRates.Money(e.Amount - supported)} claimed beyond what the cost records support");
                Claims.Add(new PendingClaim(e, at, supported, disallowed));
                Costs.Claimed += Math.Min(e.Amount, supported);
                break;
        }
    }

    private static bool Documented(ContractAwarded contract) =>
        contract.Value < CostRates.ProcurementThreshold || !string.IsNullOrWhiteSpace(contract.Justification);
}

/// <summary>
/// The finance section's books (Phase 15): what the response has cost, by category, and how much of it central government
/// would meet — costs since a major emergency was declared, except our own staff's straight time and contracts let
/// without the paperwork.
/// </summary>
public sealed class CostLedger
{
    private readonly List<string> _disallowed = [];

    public Dictionary<CostCategory, decimal> Total { get; } = [];
    public Dictionary<CostCategory, decimal> Eligible { get; } = [];
    public decimal Claimed { get; set; }

    public decimal Sum => Total.Values.Sum();
    public decimal EligibleSum => Eligible.Values.Sum();
    public decimal Unclaimed => Math.Max(0, EligibleSum - Claimed);

    public void Add(CostCategory category, decimal amount, bool eligible)
    {
        if (amount <= 0) return;
        Total[category] = Total.GetValueOrDefault(category) + amount;
        if (eligible) Eligible[category] = Eligible.GetValueOrDefault(category) + amount;
    }

    /// <summary>Notes something a claim can't include, for the next claim's decision to say why.</summary>
    public void Disallow(string why) => _disallowed.Add(why);

    public List<string> TakeDisallowed()
    {
        var taken = new List<string>(_disallowed);
        _disallowed.Clear();
        return taken;
    }

    public IReadOnlyList<CostLine> Lines() =>
        [.. Enum.GetValues<CostCategory>().Where(Total.ContainsKey).Select(c => new CostLine(c, Math.Round(Total[c], 2)))];
}

/// <summary>
/// What running short does to the work (Phase 15), for the systems that model the work. Every factor is 1 without the
/// logistics model, so nothing changes in a session (or test) that doesn't run it.
/// </summary>
public static class SupplyFactors
{
    /// <summary>Fires and releases that need foam: fuel, chemicals, vapour.</summary>
    public static bool NeedsFoam(IncidentType type) => type is IncidentType.HazmatRelease or IncidentType.Explosion;

    /// <summary>Chemical protective suits per responder-hour working at an incident of this type.</summary>
    public static double PpePerPersonHour(IncidentType type) => type switch
    {
        IncidentType.HazmatRelease => 1.0,
        IncidentType.Explosion => 0.3,
        IncidentType.StructureFire or IncidentType.WildlandFire => 0.05,
        _ => 0,
    };

    /// <summary>How much fire a crew knocks down: water alone won't hold a fuel or chemical fire, and without suits nobody goes in.</summary>
    public static double Suppression(WorldState world, WorldUnit unit)
    {
        var factor = WaterPressure(world, unit);
        var logistics = world.Logistics;
        if (!logistics.Active || unit.OrderedIncidentId is not { } incident) return factor;
        var type = logistics.IncidentTypes.GetValueOrDefault(incident, IncidentType.Other);
        if (NeedsFoam(type) && logistics.Short(incident, SupplyKind.Foam)) factor *= 0.35;
        if (type == IncidentType.HazmatRelease && logistics.Short(incident, SupplyKind.Ppe)) factor *= 0.5;
        return factor;
    }

    /// <summary>
    /// Hydrants with no mains pressure (Phase 17: a pumping station down) give an engine half its water, unless a tanker or
    /// water tender of ours is relaying water at the same incident.
    /// </summary>
    public static double WaterPressure(WorldState world, WorldUnit unit)
    {
        if (unit.Type is not (UnitType.Engine or UnitType.Ladder) || !world.Protection.LowWaterPressure(unit.Location)) return 1;
        var relay = world.Units.Values.Any(u => u.Type is UnitType.Tanker or UnitType.WaterTender && u.Phase == ResponsePhase.Operating
                                                && u.OrderedIncidentId == unit.OrderedIncidentId);
        return relay ? 0.9 : 0.5;
    }

    /// <summary>How much faster crews tire: thirsty, hungry, nowhere to wash.</summary>
    public static double Welfare(WorldState world, WorldUnit unit)
    {
        var logistics = world.Logistics;
        if (!logistics.Active || unit.OrderedIncidentId is not { } incident) return 1;
        var factor = 1.0;
        if (logistics.Short(incident, SupplyKind.Water)) factor *= 1.35;
        if (logistics.Short(incident, SupplyKind.Food)) factor *= 1.2;
        if (logistics.Short(incident, SupplyKind.Sanitation)) factor *= 1.1;
        return factor;
    }

    /// <summary>How well rehab works: slowly with nothing to eat or drink; better at a base camp.</summary>
    public static double Rehab(WorldState world, WorldUnit unit)
    {
        var logistics = world.Logistics;
        if (!logistics.Active || unit.OrderedIncidentId is not { } incident) return 1;
        if (logistics.Short(incident, SupplyKind.Water) || logistics.Short(incident, SupplyKind.Food)) return 0.6;
        return logistics.HasBaseCamp(incident) ? 1.3 : 1;
    }

    /// <summary>
    /// Takes the kits for a treatment from the incident's stock. Returns how much longer it takes: twice as long when
    /// the crew has to improvise.
    /// </summary>
    public static double Treat(WorldState world, WorldUnit crew, int kits, DateTimeOffset now)
    {
        var logistics = world.Logistics;
        if (!logistics.Active || crew.OrderedIncidentId is not { } incident || !logistics.IncidentTypes.ContainsKey(incident)) return 1;
        return logistics.Draw(incident, SupplyKind.MedicalSupplies, kits, now) >= kits ? 1 : 2;
    }

    /// <summary>Takes a spare battery pack for a radio swap: true if there was one (or the unit is not at an incident).</summary>
    public static bool SpareBattery(WorldState world, WorldUnit unit, DateTimeOffset now)
    {
        var logistics = world.Logistics;
        if (!logistics.Active || unit.OrderedIncidentId is not { } incident || !logistics.IncidentTypes.ContainsKey(incident)) return true;
        return logistics.Draw(incident, SupplyKind.Batteries, 1, now) >= 1;
    }
}
