using System.Globalization;

namespace Emergency_Response_Simulator.Core.Model;

// ---- Phase 15: logistics and resource sustainment, as the COP holds it ----

/// <summary>What an operation runs on once it has gone on for a while (Phase 15). Each is stocked per incident.</summary>
public enum SupplyKind
{
    /// <summary>Diesel for the appliances, delivered by bowser and pumped into each tank. Litres.</summary>
    Fuel,

    /// <summary>Meals for the people working. Meals.</summary>
    Food,

    /// <summary>Drinking water. Litres.</summary>
    Water,

    /// <summary>Foam concentrate: needed on fuel and chemical fires and to suppress vapour. Litres.</summary>
    Foam,

    /// <summary>Dressings, airways, splints, oxygen: one kit per casualty treated (two for a P1). Kits.</summary>
    MedicalSupplies,

    /// <summary>Handheld radio battery packs, one per swap. Packs.</summary>
    Batteries,

    /// <summary>Chemical protective suits, gloves, filters: single use on a hazardous-materials job. Sets.</summary>
    Ppe,

    /// <summary>Portable toilets and hand-washing, serviced: one unit serves about 25 person-hours. Units.</summary>
    Sanitation,
}

/// <summary>
/// Who an order goes to (Phase 15). The Irish tiers line up with the usual local / state / federal ones: the council
/// and its contractors; the regional coordination group and the region's stores; the national level and its reserves.
/// </summary>
public enum SupplyTier
{
    /// <summary>The council depot and local suppliers: quick, cheap, and limited in what they hold.</summary>
    Local,

    /// <summary>The Regional Coordination Group and the region's emergency stores (the "state" level): more, slower, approved.</summary>
    Regional,

    /// <summary>National reserves and the Defence Forces (the "federal" level): any quantity, slowest, for declared major emergencies.</summary>
    National,
}

public enum SupplyOrderStatus
{
    Requested,
    Approved,
    Declined,
    Delivered,
}

/// <summary>What gets a unit back on the road (Phase 15).</summary>
public enum RepairKind
{
    /// <summary>A fleet mechanic comes out to it: minor faults only (a tyre, a belt, a hose).</summary>
    FieldRepair,

    /// <summary>Recovered to the workshop: anything, but it is gone for hours.</summary>
    Workshop,

    /// <summary>Preventive servicing before a worn appliance fails: out of service for a short while.</summary>
    Service,
}

/// <summary>Standing contracts the finance section can let (Phase 15). Each but <see cref="Other"/> delivers on a schedule.</summary>
public enum ContractKind
{
    /// <summary>Hot meals and drinking water to the scene every couple of hours.</summary>
    Catering,

    /// <summary>Portable toilets and hand-washing, set up and serviced.</summary>
    Sanitation,

    /// <summary>A fuel company's bowser, topping up the incident's fuel every couple of hours.</summary>
    FuelSupply,

    /// <summary>Plant hire: an excavator and operator, joining the roster.</summary>
    PlantHire,

    /// <summary>Anything else (security, lighting, accommodation): a cost, nothing more.</summary>
    Other,
}

/// <summary>Where the money goes (Phase 15).</summary>
public enum CostCategory
{
    /// <summary>Our own people's straight time.</summary>
    Personnel,

    /// <summary>Hours past the end of a shift, at time and a half.</summary>
    Overtime,

    /// <summary>Our own apparatus, at standard hourly rates.</summary>
    Equipment,

    /// <summary>What the services that sent mutual aid will bill us: their people and their apparatus.</summary>
    MutualAid,

    Supplies,
    Contracts,
    Repairs,
}

/// <summary>The supply catalogue: measures, prices, what each tier holds, and what appliances carry (Phase 15).</summary>
public static class Supplies
{
    public static IReadOnlyList<SupplyKind> All { get; } = Enum.GetValues<SupplyKind>();

    public static string Name(SupplyKind kind) => kind switch
    {
        SupplyKind.MedicalSupplies => "Medical supplies",
        SupplyKind.Ppe => "PPE",
        _ => kind.ToString(),
    };

    /// <summary>What it is counted in.</summary>
    public static string Measure(SupplyKind kind) => kind switch
    {
        SupplyKind.Fuel or SupplyKind.Water or SupplyKind.Foam => "L",
        SupplyKind.Food => "meals",
        SupplyKind.MedicalSupplies => "kits",
        SupplyKind.Batteries => "packs",
        SupplyKind.Ppe => "sets",
        _ => "units",
    };

    /// <summary>"1,200 L", "40 meals", "0.6 units" (small amounts to a decimal place, so a little doesn't read as none).</summary>
    public static string Quantity(SupplyKind kind, double quantity) =>
        (quantity is > 0 and < 10 && Math.Abs(quantity - Math.Round(quantity)) > 0.05
            ? Math.Round(quantity, 1).ToString("0.#", CultureInfo.InvariantCulture)
            : Math.Floor(quantity).ToString("N0", CultureInfo.InvariantCulture)) + $" {Measure(kind)}";

    /// <summary>Price of one measure from a local supplier, in euro.</summary>
    public static decimal UnitCost(SupplyKind kind) => kind switch
    {
        SupplyKind.Fuel => 1.75m,
        SupplyKind.Food => 12m,
        SupplyKind.Water => 0.60m,
        SupplyKind.Foam => 6m,
        SupplyKind.MedicalSupplies => 85m,
        SupplyKind.Batteries => 40m,
        SupplyKind.Ppe => 220m,
        _ => 150m,
    };

    /// <summary>A sensible order: about what a busy incident gets through in a couple of hours.</summary>
    public static int TypicalOrder(SupplyKind kind) => kind switch
    {
        SupplyKind.Fuel => 2000,
        SupplyKind.Food => 60,
        SupplyKind.Water => 200,
        SupplyKind.Foam => 1000,
        SupplyKind.MedicalSupplies => 20,
        SupplyKind.Batteries => 24,
        SupplyKind.Ppe => 30,
        _ => 6,
    };

    /// <summary>The most one order may ask for.</summary>
    public static int MaxOrder(SupplyKind kind) => TypicalOrder(kind) * 10;

    /// <summary>How much the council depot and local suppliers can supply in all, before an order has to go further.</summary>
    public static double LocalAvailability(SupplyKind kind) => kind switch
    {
        SupplyKind.Foam => 1500,
        SupplyKind.Ppe => 40,
        SupplyKind.MedicalSupplies => 30,
        SupplyKind.Batteries => 48,
        _ => TypicalOrder(kind) * 4,
    };

    public static string TierName(SupplyTier tier) => tier switch
    {
        SupplyTier.Local => "Local (council and suppliers)",
        SupplyTier.Regional => "Regional (state)",
        _ => "National (federal)",
    };

    /// <summary>Who supplies, or approves, an order at each tier.</summary>
    public static string Supplier(SupplyTier tier) => tier switch
    {
        SupplyTier.Local => "Dublin City Council Stores",
        SupplyTier.Regional => "Regional Coordination Group",
        _ => "National Emergency Coordination Group",
    };

    /// <summary>What it costs over the local price: transport, and emergency procurement further away.</summary>
    public static decimal Premium(SupplyTier tier) => tier switch
    {
        SupplyTier.Local => 1m,
        SupplyTier.Regional => 1.15m,
        _ => 1.35m,
    };

    public static TimeSpan DecisionTime(SupplyTier tier) => tier switch
    {
        SupplyTier.Local => TimeSpan.FromMinutes(1),
        SupplyTier.Regional => TimeSpan.FromMinutes(4),
        _ => TimeSpan.FromMinutes(6),
    };

    public static TimeSpan LeadTime(SupplyTier tier) => tier switch
    {
        SupplyTier.Local => TimeSpan.FromMinutes(25),
        SupplyTier.Regional => TimeSpan.FromMinutes(50),
        _ => TimeSpan.FromMinutes(90),
    };

    /// <summary>What an appliance carries when it arrives, which becomes the incident's first stock.</summary>
    public static IReadOnlyDictionary<SupplyKind, double> Onboard(UnitType type, int crew)
    {
        var load = new Dictionary<SupplyKind, double>
        {
            [SupplyKind.Water] = crew * 1.0,
            [SupplyKind.Food] = crew * 0.5,
            [SupplyKind.Sanitation] = 0.1,
        };
        switch (type)
        {
            case UnitType.Engine or UnitType.Wildland:
                load[SupplyKind.Foam] = 200;
                load[SupplyKind.Ppe] = 6;
                load[SupplyKind.MedicalSupplies] = 2;
                load[SupplyKind.Batteries] = 4;
                break;
            case UnitType.Hazmat:
                load[SupplyKind.Foam] = 600;
                load[SupplyKind.Ppe] = 20;
                load[SupplyKind.Batteries] = 6;
                break;
            case UnitType.Ladder or UnitType.Rescue or UnitType.Tanker or UnitType.WaterTender:
                load[SupplyKind.Foam] = type == UnitType.Tanker ? 100 : 0;
                load[SupplyKind.Ppe] = 4;
                load[SupplyKind.MedicalSupplies] = 1;
                load[SupplyKind.Batteries] = 3;
                break;
            case UnitType.AmbulanceAls:
                load[SupplyKind.MedicalSupplies] = 6;
                load[SupplyKind.Ppe] = 4;
                load[SupplyKind.Batteries] = 2;
                break;
            case UnitType.AmbulanceBls:
                load[SupplyKind.MedicalSupplies] = 4;
                load[SupplyKind.Ppe] = 2;
                load[SupplyKind.Batteries] = 2;
                break;
            case UnitType.MassCasualtyUnit:
                load[SupplyKind.MedicalSupplies] = 40;
                load[SupplyKind.Ppe] = 10;
                load[SupplyKind.Batteries] = 4;
                break;
            default:
                load[SupplyKind.Batteries] = 2;
                break;
        }
        return load;
    }

    /// <summary>A unit's fuel tank, in litres.</summary>
    public static double TankLitres(UnitType type) => type switch
    {
        UnitType.Ladder => 250,
        UnitType.Tanker or UnitType.WaterTender => 300,
        UnitType.Engine or UnitType.Hazmat or UnitType.Rescue or UnitType.Wildland or UnitType.Command => 200,
        UnitType.AmbulanceAls or UnitType.AmbulanceBls or UnitType.MedicalSupervisor => 80,
        UnitType.Patrol or UnitType.Traffic or UnitType.Supervisor => 60,
        UnitType.Motorcycle => 20,
        _ => 120,
    };
}

/// <summary>Standard rates the finance section costs the response at (Phase 15). Illustrative, in euro.</summary>
public static class CostRates
{
    /// <summary>One responder for an hour, straight time.</summary>
    public const decimal PersonHour = 38m;

    public const decimal OvertimeMultiplier = 1.5m;

    /// <summary>What a mutual-aid provider adds to its bill for administration.</summary>
    public const decimal MutualAidAdministration = 1.10m;

    /// <summary>
    /// Above this a contract let without competition needs a written justification for the emergency direct award;
    /// without one an auditor questions it, and it can't be claimed back.
    /// </summary>
    public const decimal ProcurementThreshold = 25_000m;

    /// <summary>An appliance for an hour, at the standard (reimbursement) rate.</summary>
    public static decimal EquipmentHour(UnitType type) => type switch
    {
        UnitType.Engine or UnitType.Wildland => 120m,
        UnitType.Ladder => 160m,
        UnitType.Hazmat => 220m,
        UnitType.Tanker or UnitType.WaterTender or UnitType.Rescue => 110m,
        UnitType.AmbulanceAls => 85m,
        UnitType.AmbulanceBls => 70m,
        UnitType.MassCasualtyUnit => 150m,
        UnitType.HeavyMachinery => 140m,
        UnitType.Patrol or UnitType.Traffic or UnitType.Motorcycle or UnitType.Supervisor => 45m,
        _ => 60m,
    };

    public static string Money(decimal amount) => "€" + Math.Round(amount).ToString("N0", CultureInfo.InvariantCulture);

    public static string Name(CostCategory category) => category switch
    {
        CostCategory.MutualAid => "Mutual aid (billed)",
        CostCategory.Personnel => "Personnel (straight time)",
        _ => category.ToString(),
    };
}

/// <summary>One supply at one incident, as the supply unit last reported it.</summary>
public sealed class SupplyStock
{
    public SupplyKind Kind { get; init; }
    public double OnHand { get; set; }

    /// <summary>How fast it is going, over the last half hour.</summary>
    public double PerHour { get; set; }

    /// <summary>The supply unit warned it will run out soon.</summary>
    public DateTimeOffset? LowSince { get; set; }

    /// <summary>It has run out, and crews are doing without.</summary>
    public DateTimeOffset? OutSince { get; set; }

    /// <summary>Hours left at the current rate; null when nothing is being used.</summary>
    public double? HoursLeft => PerHour > 0.01 ? OnHand / PerHour : null;
}

/// <summary>What one incident has to work with, from the supply unit's reports.</summary>
public sealed class IncidentSupplies
{
    public Guid IncidentId { get; init; }

    /// <summary>Responders working at the incident when last reported (our own and mutual aid).</summary>
    public int Personnel { get; set; }

    public DateTimeOffset ReportedAt { get; set; }
    public List<SupplyStock> Stocks { get; } = [];

    public SupplyStock? Stock(SupplyKind kind) => Stocks.FirstOrDefault(s => s.Kind == kind);
}

/// <summary>An order for supplies, from request to delivery.</summary>
public sealed class SupplyOrder
{
    public Guid Id { get; init; }
    public Guid IncidentId { get; init; }
    public SupplyKind Kind { get; init; }
    public int Quantity { get; init; }
    public SupplyTier Tier { get; init; }
    public string? Justification { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public SupplyOrderStatus Status { get; set; } = SupplyOrderStatus.Requested;
    public string? Supplier { get; set; }

    /// <summary>How much is coming, when less than asked.</summary>
    public int? Granted { get; set; }

    public DateTimeOffset? ExpectedAt { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public decimal Cost { get; set; }

    public bool Open => Status is SupplyOrderStatus.Requested or SupplyOrderStatus.Approved;
}

/// <summary>One appliance's fuel, defects, repairs and staging, as its crew and the workshop have reported them.</summary>
public sealed class FleetRecord
{
    public Guid UnitId { get; init; }

    /// <summary>Fuel as the crew last said, 0–1; null until they have said.</summary>
    public double? Fuel { get; set; }

    public DateTimeOffset? FuelReportedAt { get; set; }
    public string? Defect { get; set; }
    public DateTimeOffset? DefectReportedAt { get; set; }

    /// <summary>It has broken down and can't move or work.</summary>
    public bool Immobilised { get; set; }

    /// <summary>The crew says a mechanic can fix it where it is.</summary>
    public bool FieldRepairable { get; set; }

    public RepairKind? Repair { get; set; }
    public DateTimeOffset? RepairExpectedAt { get; set; }
    public string? RepairProvider { get; set; }
    public DateTimeOffset? RepairedAt { get; set; }

    /// <summary>The staging area it was sent to, and when it got there (null while driving).</summary>
    public Guid? StagingZoneId { get; set; }

    public DateTimeOffset? StagedAt { get; set; }

    public bool UnderRepair => Repair is not null;
    public bool Dry => Fuel is <= 0;
}

/// <summary>A contract the finance section let.</summary>
public sealed class LogisticsContract
{
    public Guid Id { get; init; }
    public Guid? IncidentId { get; init; }
    public ContractKind Kind { get; init; }
    public required string Vendor { get; init; }
    public required string Scope { get; init; }
    public decimal Value { get; init; }
    public string? Justification { get; init; }
    public DateTimeOffset AwardedAt { get; init; }

    /// <summary>Under the threshold, or justified in writing: it would stand up to an audit.</summary>
    public bool Documented => Value < CostRates.ProcurementThreshold || !string.IsNullOrWhiteSpace(Justification);

    public int Deliveries { get; set; }
}

/// <summary>A claim to central government for what the major emergency cost.</summary>
public sealed class ReimbursementClaim
{
    public Guid Id { get; init; }
    public decimal Amount { get; init; }
    public DateTimeOffset SubmittedAt { get; init; }
    public decimal? Approved { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public List<string> Disallowed { get; } = [];
    public bool Waiting => DecidedAt is null;
}

/// <summary>The finance section's latest figures.</summary>
public sealed record CostLine(CostCategory Category, decimal Amount);

/// <summary>Everything that keeps the operation going (Phase 15), as the COP holds it.</summary>
public sealed class LogisticsSituation
{
    public Dictionary<Guid, IncidentSupplies> Incidents { get; } = [];
    public List<SupplyOrder> Orders { get; } = [];
    public Dictionary<Guid, FleetRecord> Fleet { get; } = [];
    public List<LogisticsContract> Contracts { get; } = [];
    public List<ReimbursementClaim> Claims { get; } = [];

    /// <summary>Costs by category, as the finance section last reported them.</summary>
    public List<CostLine> Costs { get; } = [];

    public decimal TotalCost { get; set; }

    /// <summary>What could be claimed back now: eligible costs since the major emergency was declared, not yet claimed.</summary>
    public decimal Reimbursable { get; set; }

    public DateTimeOffset? CostsReportedAt { get; set; }

    /// <summary>When the EOC first reached partial activation: a major emergency, whose costs central government may meet.</summary>
    public DateTimeOffset? MajorEmergencySince { get; set; }

    public FleetRecord? FleetOf(Guid unitId) => Fleet.GetValueOrDefault(unitId);

    /// <summary>A copy, so a reader never sees it change underneath them.</summary>
    public LogisticsSituation Copy()
    {
        var copy = new LogisticsSituation
        {
            TotalCost = TotalCost, Reimbursable = Reimbursable, CostsReportedAt = CostsReportedAt, MajorEmergencySince = MajorEmergencySince,
        };
        foreach (var (id, incident) in Incidents)
        {
            var supplies = new IncidentSupplies { IncidentId = id, Personnel = incident.Personnel, ReportedAt = incident.ReportedAt };
            supplies.Stocks.AddRange(incident.Stocks.Select(s => new SupplyStock
            {
                Kind = s.Kind, OnHand = s.OnHand, PerHour = s.PerHour, LowSince = s.LowSince, OutSince = s.OutSince,
            }));
            copy.Incidents[id] = supplies;
        }
        copy.Orders.AddRange(Orders.Select(o => new SupplyOrder
        {
            Id = o.Id, IncidentId = o.IncidentId, Kind = o.Kind, Quantity = o.Quantity, Tier = o.Tier, Justification = o.Justification,
            RequestedAt = o.RequestedAt, Status = o.Status, Supplier = o.Supplier, Granted = o.Granted, ExpectedAt = o.ExpectedAt,
            Reason = o.Reason, DeliveredAt = o.DeliveredAt, Cost = o.Cost,
        }));
        foreach (var (id, f) in Fleet)
            copy.Fleet[id] = new FleetRecord
            {
                UnitId = f.UnitId, Fuel = f.Fuel, FuelReportedAt = f.FuelReportedAt, Defect = f.Defect, DefectReportedAt = f.DefectReportedAt,
                Immobilised = f.Immobilised, FieldRepairable = f.FieldRepairable, Repair = f.Repair, RepairExpectedAt = f.RepairExpectedAt,
                RepairProvider = f.RepairProvider, RepairedAt = f.RepairedAt, StagingZoneId = f.StagingZoneId, StagedAt = f.StagedAt,
            };
        copy.Contracts.AddRange(Contracts.Select(c => new LogisticsContract
        {
            Id = c.Id, IncidentId = c.IncidentId, Kind = c.Kind, Vendor = c.Vendor, Scope = c.Scope, Value = c.Value,
            Justification = c.Justification, AwardedAt = c.AwardedAt, Deliveries = c.Deliveries,
        }));
        foreach (var claim in Claims)
        {
            var c = new ReimbursementClaim { Id = claim.Id, Amount = claim.Amount, SubmittedAt = claim.SubmittedAt, Approved = claim.Approved, DecidedAt = claim.DecidedAt };
            c.Disallowed.AddRange(claim.Disallowed);
            copy.Claims.Add(c);
        }
        copy.Costs.AddRange(Costs);
        return copy;
    }
}
