using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Aar;

/// <summary>
/// Logistics and finance in the review (Phase 15).
/// <list type="bullet">
/// <item><b>Supplies</b>: everything that ran out, for how long, and whether the supply unit's warning was acted on.</item>
/// <item><b>The fleet</b>: appliances that ran dry; breakdowns, whether the crew had warned of them, and how long the unit
/// was off the road; worn appliances serviced before they failed.</item>
/// <item><b>Facilities</b>: a long operation with a lot of people and no base camp.</item>
/// <item><b>Money</b>: the finance section's last figures, the claim to central government and what it disallowed, and
/// contracts let directly above the threshold with no written justification (a liability, and a decision to log).</item>
/// </list>
/// Orders, repairs and servicing are timed from the warning that called for them.
/// </summary>
public sealed partial class AarAnalyzer
{
    /// <summary>A base camp is expected once this many people have been working this long.</summary>
    public const int BaseCampPersonnel = 25;

    public static readonly TimeSpan BaseCampAfter = TimeSpan.FromHours(2);

    private readonly Dictionary<(Guid, SupplyKind), DateTimeOffset> _supplyLow = [];
    private readonly Dictionary<(Guid, SupplyKind), DateTimeOffset> _supplyOut = [];
    private readonly Dictionary<(Guid, SupplyKind), DateTimeOffset> _supplyOrdered = [];
    private readonly List<AarSupplyOutage> _outages = [];
    private readonly Dictionary<Guid, (DateTimeOffset At, string Defect)> _defectWarnings = [];
    private readonly HashSet<Guid> _heeded = [];
    private readonly Dictionary<Guid, (DateTimeOffset At, string Fault, DateTimeOffset? Warned, DateTimeOffset? Requested)> _brokenDown = [];
    private readonly List<AarBreakdown> _breakdowns = [];
    private IReadOnlyList<CostLine> _costLines = [];
    private decimal _costTotal, _reimbursable, _claimed, _approved;
    private readonly List<string> _disallowed = [];
    private int _supplyOrders, _ranDry, _serviced, _peakPersonnel;
    private DateTimeOffset? _crowdSince, _majorEmergencyAt;
    private bool _baseCamp;

    private void TrackLogistics(SimEvent simEvent)
    {
        var at = simEvent.SimTime;
        var decision = simEvent.Visibility == EventVisibility.Perceived && simEvent.Source == EventSources.C2;
        switch (simEvent.Payload)
        {
            // Supplies.
            case SupplyShortage e:
                (e.Exhausted ? _supplyOut : _supplyLow).TryAdd((e.IncidentId, e.Kind), at);
                break;
            case SuppliesOrdered e:
                _supplyOrders++;
                if (_supplyLow.ContainsKey((e.IncidentId, e.Kind)) || _supplyOut.ContainsKey((e.IncidentId, e.Kind)))
                    _supplyOrdered.TryAdd((e.IncidentId, e.Kind), at);
                if (decision && e.Tier == SupplyTier.National)
                    _significant.Add((simEvent.Sequence, at, $"{Clock(at)} national reserves asked for ({Supplies.Name(e.Kind).ToLowerInvariant()})"));
                break;
            case SuppliesDelivered e:
                Restocked((e.IncidentId, e.Kind), at);
                break;
            case SupplyLevelsReported e:
                // Back in stock without a delivery (an appliance arrived with some): the outage is over.
                foreach (var level in e.Levels.Where(l => l.OnHand > 0 && _supplyOut.ContainsKey((e.IncidentId, l.Kind))))
                    Restocked((e.IncidentId, level.Kind), at);
                if (e.Personnel >= BaseCampPersonnel) _crowdSince ??= at;
                _peakPersonnel = Math.Max(_peakPersonnel, e.Personnel);
                break;
            case ZoneDeclared { Type: ZoneType.BaseCamp }:
                _baseCamp = true;
                break;

            // The fleet.
            case UnitFuelReported { Level: <= 0 }:
                _ranDry++;
                break;
            case UnitDefectReported { Immobilised: false } e:
                _defectWarnings.TryAdd(e.UnitId, (at, e.Defect));
                break;
            case UnitDefectReported e:
                _brokenDown[e.UnitId] = (at, e.Defect, _defectWarnings.TryGetValue(e.UnitId, out var warned) && !_heeded.Contains(e.UnitId) ? warned.At : null, null);
                break;
            case UnitServiceOrdered e when _defectWarnings.ContainsKey(e.UnitId) && _heeded.Add(e.UnitId):
                _serviced++;
                break;
            case RepairRequested e when _brokenDown.TryGetValue(e.UnitId, out var broken):
                _brokenDown[e.UnitId] = broken with { Requested = broken.Requested ?? at };
                break;
            case UnitRepaired e:
                if (e.Kind != RepairKind.Service && _brokenDown.Remove(e.UnitId, out var fixedUp))
                    _breakdowns.Add(new(Callsign(e.UnitId), fixedUp.At, fixedUp.Fault, fixedUp.Warned, fixedUp.Requested, at));
                _defectWarnings.Remove(e.UnitId);
                _heeded.Remove(e.UnitId);
                break;

            // Money.
            case ContractAwarded e when e.Value >= CostRates.ProcurementThreshold:
                if (decision) _significant.Add((simEvent.Sequence, at, $"{Clock(at)} contract let to {e.Vendor} ({CostRates.Money(e.Value)})"));
                if (string.IsNullOrWhiteSpace(e.Justification))
                    _liabilities.Add(new(at, "Procurement", $"A {CostRates.Money(e.Value)} {EventDescriber.Humanize(e.Kind).ToLowerInvariant()} contract was " +
                                                            $"awarded directly to {e.Vendor} with no written justification: an auditor would question it, and it can't be claimed."));
                break;
            case CostsReported e:
                (_costLines, _costTotal, _reimbursable) = (e.Lines, e.Total, e.Reimbursable);
                break;
            case ReimbursementClaimSubmitted e:
                _claimed += e.Amount;
                _reimbursable = Math.Max(0, _reimbursable - e.Amount);
                break;
            case ReimbursementDecided e:
                _approved += e.Approved;
                _disallowed.AddRange(e.Disallowed);
                break;
            case EocLevelReached { Level: >= EocLevel.Partial }:
                _majorEmergencyAt ??= at;
                break;
        }
    }

    /// <summary>A supply is back in stock: any outage is over, and the next warning starts afresh.</summary>
    private void Restocked((Guid Incident, SupplyKind Kind) key, DateTimeOffset at)
    {
        if (_supplyOut.Remove(key, out var from))
            _outages.Add(new(IncidentNumber(key.Incident), key.Kind, from, at, _supplyLow.TryGetValue(key, out var low) ? low : null,
                _supplyOrdered.TryGetValue(key, out var ordered) ? ordered : null));
        _supplyLow.Remove(key);
        _supplyOrdered.Remove(key);
    }

    private string IncidentNumber(Guid incidentId) => _incidents.TryGetValue(incidentId, out var incident) ? incident.Number : "an incident";

    /// <summary>What prompted an order, a repair or a service, and how quickly it should have followed.</summary>
    private (DateTimeOffset At, string What, TimeSpan Good, TimeSpan Fair)? LogisticsTrigger(DomainEvent payload) => payload switch
    {
        SuppliesOrdered e when _supplyOut.TryGetValue((e.IncidentId, e.Kind), out var outAt) && !_supplyLow.ContainsKey((e.IncidentId, e.Kind)) =>
            (outAt, $"{Supplies.Name(e.Kind)} ran out", TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(8)),
        SuppliesOrdered e when _supplyLow.TryGetValue((e.IncidentId, e.Kind), out var lowAt) =>
            (lowAt, $"Supply unit warned {Supplies.Name(e.Kind).ToLowerInvariant()} was low", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)),
        RepairRequested e when _brokenDown.TryGetValue(e.UnitId, out var broken) =>
            (broken.At, $"{Callsign(e.UnitId)} broke down", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)),
        UnitServiceOrdered e when _defectWarnings.TryGetValue(e.UnitId, out var warning) =>
            (warning.At, $"{Callsign(e.UnitId)} reported {warning.Defect.ToLowerInvariant()}", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20)),
        _ => null,
    };

    private AarLogistics LogisticsSummary()
    {
        var outages = _outages.Concat(_supplyOut.Select(o => new AarSupplyOutage(IncidentNumber(o.Key.Item1), o.Key.Item2, o.Value, null,
                _supplyLow.TryGetValue(o.Key, out var low) ? low : null, _supplyOrdered.TryGetValue(o.Key, out var ordered) ? ordered : null)))
            .OrderBy(o => o.From).ToList();
        var breakdowns = _breakdowns.Concat(_brokenDown.Select(b => new AarBreakdown(Callsign(b.Key), b.Value.At, b.Value.Fault, b.Value.Warned, b.Value.Requested, null)))
            .OrderBy(b => b.At).ToList();
        return new AarLogistics(_costLines, _costTotal, _claimed, _approved, outages, breakdowns, _supplyOrders, _ranDry, _serviced);
    }

    private IEnumerable<AarFinding> LogisticsFindings()
    {
        var summary = LogisticsSummary();
        foreach (var outage in summary.Outages)
        {
            var name = Supplies.Name(outage.Kind);
            var lasted = outage.To is { } to ? $"for {Duration(to - outage.From)}" : "and never came back";
            var warning = outage.WarnedAt is { } warned
                ? outage.OrderedAt is { } ordered ? $"; warned at {Clock(warned)}, ordered at {Clock(ordered)}" : $"; warned at {Clock(warned)}, nothing ordered"
                : outage.OrderedAt is { } after ? $"; ordered at {Clock(after)}" : "";
            yield return new AarFinding(false, $"{Clock(outage.From)} {name} ran out at {outage.Incident} {lasted}{warning}.");
        }
        if (summary.Orders > 0 && summary.Outages.Count == 0)
            yield return new AarFinding(true, $"Supplies kept up with the operation: {summary.Orders} order(s), and nothing ran out.");

        if (summary.RanDry > 0)
            yield return new AarFinding(false, $"{summary.RanDry} appliance(s) ran out of fuel: nobody got a bowser to them in time.");

        foreach (var breakdown in summary.Breakdowns)
        {
            if (breakdown.WarnedAt is { } warned)
                yield return new AarFinding(false, $"{Clock(breakdown.At)} {breakdown.Unit} broke down ({breakdown.Fault}) {Duration(breakdown.At - warned)} after its crew warned of it.");
            if (breakdown.RepairRequestedAt is { } asked)
                yield return new AarFinding(asked - breakdown.At <= TimeSpan.FromMinutes(10),
                    $"{breakdown.Unit}'s repair arranged {Duration(asked - breakdown.At)} after it broke down" +
                    (breakdown.BackAt is { } back ? $"; back in service after {Duration(back - breakdown.At)}." : "; still off the road at the end."));
            else
                yield return new AarFinding(false, $"{breakdown.Unit} broke down at {Clock(breakdown.At)} and nobody arranged a repair.");
        }
        if (summary.Serviced > 0)
            yield return new AarFinding(true, $"{summary.Serviced} worn appliance(s) sent for servicing when their crews warned, before they failed.");

        if (_crowdSince is { } crowded && End - crowded >= BaseCampAfter && !_baseCamp)
            yield return new AarFinding(false, $"Up to {_peakPersonnel} people worked for {Duration(End - crowded)} with no base camp to eat, rest and wash.");

        if (summary.Claimed > 0)
            yield return new AarFinding(summary.Approved >= summary.Claimed * 0.9m,
                $"Claimed {CostRates.Money(summary.Claimed)} from central government; {CostRates.Money(summary.Approved)} approved" +
                (_disallowed.Count > 0 ? $". Disallowed: {string.Join("; ", _disallowed.Distinct().Take(4))}." : "."));
        else if (_majorEmergencyAt is not null && _reimbursable >= 1_000m)
            yield return new AarFinding(false, $"{CostRates.Money(_reimbursable)} of the major emergency's eligible costs were never claimed from central government.");
    }
}
