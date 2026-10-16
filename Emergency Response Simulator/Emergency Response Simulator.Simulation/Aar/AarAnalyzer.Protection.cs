using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Aar;

/// <summary>
/// Population protection and critical systems in the review (Phase 17).
/// <list type="bullet">
/// <item><b>Evacuation</b>: for each zone, of everyone really in it, how many got out, how long until it was clear, who was
/// still in and who was hurt there (ground truth); routes never set; residents sent home to danger.</item>
/// <item><b>Rest centres</b>: evacuees there and registered, the fullest centre, what ran out, trouble inside, and people
/// with nowhere to go.</item>
/// <item><b>Infrastructure</b>: what failed, pumping stations left to run dry, gas that ignited before it was isolated,
/// trains run through danger.</item>
/// <item><b>Cyber and GPS</b>: how soon manual working followed each attack, whether the National Cyber Security Centre was
/// told, the boil water notice; voice position reports after GPS interference.</item>
/// <item><b>Environment</b>: run-off, how much reached water and how much was held back, wildlife killed, and whether the
/// EPA was told.</item>
/// </list>
/// </summary>
public sealed partial class AarAnalyzer
{
    /// <summary>A boil water notice should follow Uisce Éireann's advice within this long.</summary>
    public static readonly TimeSpan BoilNoticeWithin = TimeSpan.FromMinutes(30);

    private readonly Dictionary<Guid, (DateTimeOffset At, string Name)> _evacuationZones = [];
    private readonly HashSet<Guid> _routed = [];
    private readonly Dictionary<Guid, EvacuationOutcome> _evacuationOutcomes = [];
    private readonly Dictionary<(Guid Zone, string Reason), (DateTimeOffset At, int Count)> _unsafeReEntries = [];
    private readonly Dictionary<Guid, (string Name, int Capacity)> _shelters = [];
    private readonly Dictionary<Guid, (int Occupants, int Registered)> _shelterPeaks = [];
    private readonly Dictionary<Guid, HashSet<SupplyKind>> _shelterShort = [];
    private int _shelterTrouble;
    private readonly Dictionary<Guid, int> _unshelteredNow = [];
    private int _unshelteredPeak;
    private DateTimeOffset? _firstUnsheltered;
    private readonly Dictionary<Guid, (string Name, InfrastructureKind Kind, DateTimeOffset At)> _assetTrouble = [];
    private readonly HashSet<Guid> _assetsFailed = [];
    private readonly HashSet<Guid> _generators = [];
    private readonly Dictionary<Guid, DateTimeOffset> _gasLeaks = [];
    private readonly Dictionary<Guid, DateTimeOffset> _gasIsolations = [];
    private readonly HashSet<Guid> _gasIgnited = [];
    private readonly Dictionary<Guid, (DateTimeOffset First, DateTimeOffset Last, string Where)> _railExposed = [];
    private readonly Dictionary<Guid, (DateTimeOffset At, CyberTarget Target, string System)> _attacks = [];
    private readonly Dictionary<Guid, DateTimeOffset> _fallbacks = [];
    private readonly Dictionary<Guid, DateTimeOffset> _waterUnsafe = [];
    private readonly List<DateTimeOffset> _boilNotices = [];
    private int _gnss;
    private DateTimeOffset? _gnssReported, _voicePositionsAt;
    private readonly Dictionary<Guid, EnvironmentOutcome> _environment = [];
    private DateTimeOffset? _waterPolluted, _epaAt, _ncscAt;

    private void TrackProtection(SimEvent simEvent)
    {
        var at = simEvent.SimTime;
        var decision = simEvent.Visibility == EventVisibility.Perceived && simEvent.Source == EventSources.C2;
        switch (simEvent.Payload)
        {
            // Evacuation.
            case ZoneDeclared { Type: ZoneType.EvacuationZone } e when decision:
                _evacuationZones.TryAdd(e.ZoneId, (at, e.Name));
                break;
            case EvacuationRouteSet e:
                _routed.Add(e.ZoneId);
                break;
            case EvacuationOutcome e:
                _evacuationOutcomes[e.ZoneId] = e;
                break;
            case UnsafeReEntry e:
                _unsafeReEntries[(e.ZoneId, e.Reason)] = (_unsafeReEntries.TryGetValue((e.ZoneId, e.Reason), out var before) ? before.At : at, e.Count);
                break;
            case ReEntryAuthorised when decision:
                _significant.Add((simEvent.Sequence, at, $"{Clock(at)} re-entry authorised"));
                break;

            // Rest centres.
            case ShelterOpened e:
                _shelters.TryAdd(e.ShelterId, (e.Name, e.Capacity));
                break;
            case ShelterStatusReported e:
                var peak = _shelterPeaks.GetValueOrDefault(e.ShelterId);
                _shelterPeaks[e.ShelterId] = (Math.Max(peak.Occupants, e.Occupants), Math.Max(peak.Registered, e.Registered));
                if (!_shelterShort.TryGetValue(e.ShelterId, out var shortages)) _shelterShort[e.ShelterId] = shortages = [];
                shortages.UnionWith(e.Short);
                break;
            case ShelterIncident { Prevented: false }:
                _shelterTrouble++;
                break;
            case ShelterNeedReported e:
                _unshelteredNow[e.IncidentId] = e.People;
                _unshelteredPeak = Math.Max(_unshelteredPeak, _unshelteredNow.Values.Sum());
                if (e.People > 0) _firstUnsheltered ??= at;
                break;

            // Infrastructure.
            case InfrastructureStatusReported e:
                if (e.Status is InfrastructureStatus.Failed or InfrastructureStatus.Leaking or InfrastructureStatus.OnBackup)
                    _assetTrouble.TryAdd(e.SiteId, (e.Name, e.Kind, at));
                if (e.Status is InfrastructureStatus.Failed or InfrastructureStatus.Leaking) _assetsFailed.Add(e.SiteId);
                if (e.Status == InfrastructureStatus.Leaking) _gasLeaks.TryAdd(e.SiteId, at);
                if (e.Status == InfrastructureStatus.Isolated) _gasIsolations.TryAdd(e.SiteId, at);
                break;
            case GeneratorRequested e:
                _generators.Add(e.SiteId);
                break;
            case GasIsolationRequested when decision:
                _significant.Add((simEvent.Sequence, at, $"{Clock(at)} gas main isolated (supply cut)"));
                break;
            case GasIgnited e:
                _gasIgnited.Add(e.SiteId);
                break;
            case RailServiceExposed e:
                _railExposed[e.SiteId] = _railExposed.TryGetValue(e.SiteId, out var exposed) ? exposed with { Last = at } : (at, at, e.Where);
                break;

            // Cyber and GPS.
            case CyberIncidentReported e:
                _attacks.TryAdd(e.AttackId, (at, e.Target, e.System));
                break;
            case CyberFallbackActivated e:
                _fallbacks.TryAdd(e.AttackId, at);
                break;
            case DrinkingWaterUnsafe e:
                _waterUnsafe.TryAdd(e.AttackId, at);
                break;
            case PublicStatementIssued e when e.Text.Contains("boil", StringComparison.OrdinalIgnoreCase):
                _boilNotices.Add(at);
                break;
            case GnssInterferenceStarted:
                _gnss++;
                break;
            case GnssInterferenceReported:
                _gnssReported ??= at;
                break;
            case ManualPositionReportingSet { Enabled: true }:
                _voicePositionsAt ??= at;
                break;

            // Environment.
            case EnvironmentOutcome e:
                _environment[e.IncidentId ?? Guid.Empty] = e;
                break;
            case PollutionReachedWater:
                _waterPolluted ??= at;
                break;
            case NotificationSent e when Systems.EnvironmentSystem.IsEpa(e.Recipient):
                _epaAt ??= at;
                break;
            case NotificationSent e when Systems.CyberSystem.IsCyberCentre(e.Recipient):
                _ncscAt ??= at;
                break;
        }
    }

    /// <summary>What called for a protection decision, and how quickly it should have followed.</summary>
    private (DateTimeOffset At, string What, TimeSpan Good, TimeSpan Fair)? ProtectionTrigger(DomainEvent payload) => payload switch
    {
        EvacuationRouteSet e when _evacuationZones.TryGetValue(e.ZoneId, out var zone) =>
            (zone.At, $"{zone.Name} ordered", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)),
        EvacuationBusesRequested e when _evacuationZones.TryGetValue(e.ZoneId, out var zone) =>
            (zone.At, $"{zone.Name} ordered", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(25)),
        ShelterOpened when _firstUnsheltered is { } nowhere => (nowhere, "Evacuees reported with nowhere to go", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(25)),
        ShelterOpened when _evacuationZones.Count > 0 => (_evacuationZones.Values.Min(z => z.At), "First evacuation zone ordered", TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30)),
        GeneratorRequested e when _assetTrouble.TryGetValue(e.SiteId, out var asset) =>
            (asset.At, $"{asset.Name} reported on backup or failed", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(25)),
        GasIsolationRequested e when _gasLeaks.TryGetValue(e.SiteId, out var leak) =>
            (leak, "Gas leak reported", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)),
        ManualPositionReportingSet { Enabled: true } when _gnssReported is { } gnss =>
            (gnss, "GPS interference reported", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)),
        CyberFallbackActivated e when _attacks.TryGetValue(e.AttackId, out var attack) =>
            (attack.At, $"Ransomware reported ({attack.System})", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)),
        RunoffContainmentOrdered e when _incidents.TryGetValue(e.IncidentId, out var incident) =>
            (incident.At, $"{incident.Number} opened", TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60)),
        _ => null,
    };

    private AarProtection ProtectionSummary()
    {
        var outcomes = _evacuationOutcomes.Values.ToList();
        var cleared = outcomes.Where(o => o.ClearedAfterMinutes is not null).Select(o => o.ClearedAfterMinutes!.Value).ToList();
        var fullest = _shelterPeaks.Where(p => _shelters.ContainsKey(p.Key))
            .Select(p => (int)Math.Round(100.0 * p.Value.Occupants / Math.Max(1, _shelters[p.Key].Capacity))).DefaultIfEmpty(0).Max();
        var environment = _environment.Values.ToList();
        return new AarProtection(
            Math.Max(_evacuationZones.Count, outcomes.Count), outcomes.Sum(o => o.Population), outcomes.Sum(o => o.Out), outcomes.Sum(o => o.StillInside),
            outcomes.Sum(o => o.Hurt), cleared.Count > 0 ? cleared.Max() : null, _unsafeReEntries.Values.Sum(u => u.Count),
            _shelterPeaks.Values.Sum(p => p.Occupants), _shelterPeaks.Values.Sum(p => p.Registered), fullest, _shelterTrouble, _unshelteredPeak,
            _assetsFailed.Count, _gasIgnited.Count, _attacks.Count, _gnss,
            environment.Sum(e => e.RunoffLitres), environment.Sum(e => e.ToWaterLitres), environment.Sum(e => e.ContainedLitres), environment.Sum(e => e.Wildlife));
    }

    /// <summary>Liabilities: residents sent home to danger, trains run through it, no boil water notice, pollution not reported.</summary>
    private IEnumerable<AarLiability> ProtectionLiabilities()
    {
        foreach (var ((zone, reason), (at, count)) in _unsafeReEntries)
            yield return new AarLiability(at, "Residents sent home to danger",
                $"{count} resident(s) of {(_evacuationZones.TryGetValue(zone, out var z) ? z.Name : "an evacuation zone")} went home when {reason}.");
        foreach (var (first, last, where) in _railExposed.Values)
            yield return new AarLiability(first, "Trains through danger",
                $"Trains kept running through {where} for {Duration(last - first + TimeSpan.FromMinutes(15))}: the operator was never asked to stop them.");
        foreach (var (attack, unsafeAt) in _waterUnsafe)
            if (!_boilNotices.Any(b => b >= unsafeAt && b - unsafeAt <= BoilNoticeWithin))
                yield return new AarLiability(unsafeAt, "No boil water notice",
                    "Uisce Éireann could not vouch for the water and advised a boil water notice; the public was not told in time.");
        if (_waterPolluted is { } polluted && _epaAt is null)
            yield return new AarLiability(polluted, "Pollution not reported",
                "Contaminated run-off reached water and the EPA was never notified, as the water pollution acts require.");
    }

    private IEnumerable<AarFinding> ProtectionFindings()
    {
        // Evacuation.
        foreach (var (zoneId, (orderedAt, name)) in _evacuationZones)
        {
            if (_evacuationOutcomes.TryGetValue(zoneId, out var outcome) && outcome.Population > 0)
            {
                var percent = (int)Math.Round(100.0 * outcome.Out / outcome.Population);
                yield return new AarFinding(percent >= 85,
                    $"{name}: {outcome.Out} of {outcome.Population} people got out ({percent}%)" +
                    (outcome.ClearedAfterMinutes is { } clear ? $", clear after {Duration(TimeSpan.FromMinutes(clear))}" : ", never clear") +
                    (outcome.StillInside > 0 ? $"; {outcome.StillInside} still inside" : "") + (outcome.Hurt > 0 ? $"; {outcome.Hurt} hurt there." : "."));
            }
            if (!_routed.Contains(zoneId))
                yield return new AarFinding(false, $"{name}: no evacuation route was set, so people went straight out, whatever lay that way.");
        }
        foreach (var ((zone, reason), (_, count)) in _unsafeReEntries)
            yield return new AarFinding(false, $"{count} resident(s) of {(_evacuationZones.TryGetValue(zone, out var z) ? z.Name : "an evacuation zone")} went home when {reason}.");

        // Rest centres.
        var summary = ProtectionSummary();
        if (summary.ShelterArrivals > 0)
        {
            var registered = (int)Math.Round(100.0 * summary.Registered / summary.ShelterArrivals);
            yield return new AarFinding(registered >= 90, $"Up to {summary.ShelterArrivals} evacuees in rest centres, {summary.Registered} registered ({registered}%).");
            if (summary.ShelterPeak > 100)
                yield return new AarFinding(false, $"The fullest rest centre reached {summary.ShelterPeak}% of its capacity.");
        }
        foreach (var (shelter, shortages) in _shelterShort.Where(s => s.Value.Count > 0))
            yield return new AarFinding(false, $"{(_shelters.TryGetValue(shelter, out var s) ? s.Name : "A rest centre")} ran out of " +
                                               $"{string.Join(" and ", shortages.Select(k => Supplies.Name(k).ToLowerInvariant()))}.");
        if (_shelterTrouble > 0)
            yield return new AarFinding(false, $"{_shelterTrouble} incident(s) of trouble in rest centres with no security to stop them.");
        if (_unshelteredPeak > 0)
            yield return new AarFinding(false, $"Up to {_unshelteredPeak} evacuees had nowhere to go.");

        // Infrastructure.
        foreach (var (site, (name, kind, at)) in _assetTrouble.Where(a => a.Value.Kind == InfrastructureKind.Water))
            if (!_generators.Contains(site))
                yield return new AarFinding(false, $"{name} lost its power at {Clock(at)} and no generator was sent.");
        foreach (var (site, leak) in _gasLeaks)
        {
            var name = _assetTrouble.TryGetValue(site, out var asset) ? asset.Name : "A gas main";
            yield return _gasIgnited.Contains(site)
                ? new AarFinding(false, $"Gas leaking at {name} from {Clock(leak)} ignited before it was isolated.")
                : _gasIsolations.TryGetValue(site, out var isolated)
                    ? new AarFinding(isolated - leak <= TimeSpan.FromMinutes(30), $"Gas leaking at {name} was isolated {Duration(isolated - leak)} after it was reported.")
                    : new AarFinding(false, $"Gas leaking at {name} was never isolated.");
        }
        foreach (var (first, last, where) in _railExposed.Values)
            yield return new AarFinding(false, $"Trains ran through {where} from {Clock(first)}: nobody told the operator to stop them.");

        // Cyber and GPS.
        foreach (var (attack, (at, _, system)) in _attacks)
            yield return _fallbacks.TryGetValue(attack, out var manual)
                ? new AarFinding(manual - at <= TimeSpan.FromMinutes(15), $"Ransomware on the {system}: manual working {Duration(manual - at)} after it was reported.")
                : new AarFinding(false, $"Ransomware on the {system}: nobody ordered manual working.");
        if (_attacks.Count > 0 && _ncscAt is null)
            yield return new AarFinding(false, "The National Cyber Security Centre was never told of the attack: recovery took twice as long.");
        foreach (var (_, unsafeAt) in _waterUnsafe)
            yield return _boilNotices.FirstOrDefault(b => b >= unsafeAt) is var notice && notice != default
                ? new AarFinding(notice - unsafeAt <= BoilNoticeWithin, $"Boil water notice issued {Duration(notice - unsafeAt)} after Uisce Éireann's advice.")
                : new AarFinding(false, "Uisce Éireann advised a boil water notice and none was issued.");
        if (_gnssReported is { } gnss)
            yield return _voicePositionsAt is { } voice
                ? new AarFinding(voice - gnss <= TimeSpan.FromMinutes(15), $"Crews gave positions by voice {Duration(voice - gnss)} after GPS interference was reported.")
                : new AarFinding(false, "GPS interference was reported and crews were never told to give positions by voice: the COP showed them in the wrong place, or not at all.");

        // Environment.
        if (summary.RunoffLitres > 0)
            yield return new AarFinding(summary.ToWaterLitres < 5000,
                $"{summary.RunoffLitres:N0} L of contaminated run-off; {summary.ToWaterLitres:N0} L reached water, {summary.ContainedLitres:N0} L was contained.");
        if (summary.Wildlife > 0)
            yield return new AarFinding(false, $"{summary.Wildlife} fish and birds killed.");
    }
}
