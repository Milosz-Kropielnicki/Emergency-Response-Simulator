using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Cyber and GPS degradation (Phase 17).
/// <list type="bullet">
/// <item><b>GPS jamming</b>: vehicles inside lose their fix and their AVL stops (<see cref="CommsNet.Data"/>), so the COP
/// shows them where they last were; a 999 caller's phone location is the network's rough cell estimate. A crew notices
/// after <see cref="JammingNoticed"/>.</item>
/// <item><b>GPS spoofing</b>: worse, because nothing looks wrong: AVL and callers' locations are confidently out by the
/// spoofed offset. A crew notices it after <see cref="SpoofingNoticed"/>, when the map and the street disagree.</item>
/// <item><b>Voice position reports</b>: once command orders it, committed crews inside an area of interference give their
/// position by radio every <see cref="VoicePositionInterval"/> — right, but it takes airtime.</item>
/// <item><b>Ransomware</b>: the owners find it after <see cref="DiscoveryDelay"/>. Control room CAD: mobile data stops, so
/// status messages are held and positions lost, until command orders paper working and crews go by voice. A hospital:
/// emergency capacity down (<see cref="InfrastructureNetworkSystem"/>). Water treatment: unless operators go manual within
/// <see cref="WaterFallbackWindow"/>, the water can't be guaranteed and Uisce Éireann advises a boil water notice.
/// Traffic signals: junctions around the incidents run erratically (<c>TrafficSystem</c>). Systems come back from backups
/// after <see cref="RecoveryTime"/>, or <see cref="AssistedRecoveryTime"/> after the National Cyber Security Centre is told.</item>
/// </list>
/// </summary>
public sealed class CyberSystem(IRoutingService? routing = null) : ISimulationSystem
{
    public static readonly TimeSpan JammingNoticed = TimeSpan.FromMinutes(6);
    public static readonly TimeSpan SpoofingNoticed = TimeSpan.FromMinutes(12);
    public static readonly TimeSpan VoicePositionInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DiscoveryDelay = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan WaterFallbackWindow = TimeSpan.FromMinutes(45);
    public static readonly TimeSpan RecoveryTime = TimeSpan.FromHours(4);
    public static readonly TimeSpan AssistedRecoveryTime = TimeSpan.FromHours(2);

    /// <summary>The area a treatment works' unsafe water reaches.</summary>
    public const double UnsafeWaterRadiusMeters = 3000;

    private readonly Dictionary<(Guid Interference, Guid Unit), DateTimeOffset> _insideSince = [];
    private readonly HashSet<Guid> _gnssReported = [];
    private readonly Dictionary<Guid, DateTimeOffset> _voiceFix = [];

    public int Order => 9; // before units report (10): their fixes go through CommsNet.Data, which reads this

    public void Update(SimulationContext context)
    {
        Gnss(context);
        VoicePositions(context);
        Attacks(context);
    }

    public static bool IsCyberCentre(string recipient) =>
        recipient.Contains("NCSC", StringComparison.OrdinalIgnoreCase) || recipient.Contains("cyber", StringComparison.OrdinalIgnoreCase);

    // ---- GPS ----

    private void Gnss(SimulationContext context)
    {
        var world = context.World;
        foreach (var interference in world.Protection.Gnss.Values.ToList())
        {
            if (context.SimTime >= world.Protection.GnssSince[interference.InterferenceId] + TimeSpan.FromMinutes(interference.DurationMinutes))
            {
                context.EmitTruth(new GnssInterferenceEnded(interference.InterferenceId));
                continue;
            }
            if (_gnssReported.Contains(interference.InterferenceId)) continue;

            var noticed = interference.Kind == GnssInterferenceKind.Jamming ? JammingNoticed : SpoofingNoticed;
            foreach (var unit in world.Units.Values.Where(u => u.OrderedIncidentId is not null && !u.RadioFailed))
            {
                var key = (interference.InterferenceId, unit.Id);
                if (GeoMath.DistanceMeters(unit.Location, interference.Centre) > interference.RadiusMeters)
                {
                    _insideSince.Remove(key);
                    continue;
                }
                _insideSince.TryAdd(key, context.SimTime);
                if (context.SimTime - _insideSince[key] < noticed) continue;

                _gnssReported.Add(interference.InterferenceId);
                var road = Road(unit.Location);
                var claim = interference.Kind == GnssInterferenceKind.Jamming
                    ? $"our GPS has dropped out{road}, sat-nav has no fix. AVL won't be showing us"
                    : $"the sat-nav and AVL have us about {Math.Round(interference.OffsetMeters / 50) * 50:F0} m from where we are{road}. Something is spoofing GPS";
                // Rough idea of the area: about where they are, a bit bigger.
                CommsNet.Voice(context, unit, $"Control, {unit.Callsign}: {claim}.",
                    new GnssInterferenceReported(interference.InterferenceId, interference.Kind, unit.Location, Math.Round(interference.RadiusMeters * 1.2 / 100) * 100), 2);
                break;
            }
        }

        // Crews who noticed it notice it stop.
        foreach (var id in _gnssReported.Where(id => !world.Protection.Gnss.ContainsKey(id)).ToList())
        {
            _gnssReported.Remove(id);
            context.EmitPerceived(new GnssInterferenceEnded(id), EventSources.Comms);
        }
    }

    /// <summary>Committed crews in an area of interference give their position by voice, once command has asked.</summary>
    private void VoicePositions(SimulationContext context)
    {
        var world = context.World;
        if (!world.Protection.ManualPositions || world.Protection.Gnss.Count == 0) return;
        foreach (var unit in world.Units.Values.Where(u => u.OrderedIncidentId is not null && !u.RadioFailed))
        {
            if (world.Protection.GnssAt(unit.Location) is null) continue;
            if (_voiceFix.TryGetValue(unit.Id, out var last) && context.SimTime - last < VoicePositionInterval) continue;
            _voiceFix[unit.Id] = context.SimTime;
            CommsNet.Voice(context, unit, $"Control, {unit.Callsign}: position{(Road(unit.Location) is { Length: > 0 } road ? road : " unchanged")}.",
                new UnitPositionReported(unit.Id, unit.Location, unit.SpeedKph, unit.Heading, null));
        }
    }

    private string Road(GeoPoint point) =>
        routing?.NearestRoad(point) is { } road && road.DistanceMeters < 120 ? $" on {road.Name}" : "";

    // ---- Ransomware ----

    private static void Attacks(SimulationContext context)
    {
        var world = context.World;
        foreach (var attack in world.Protection.Attacks.Values.Where(a => a.Active))
        {
            var (owner, system) = Owner(world, attack);
            if (!attack.Reported && context.SimTime - attack.StartedAt >= DiscoveryDelay)
            {
                attack.Reported = true;
                var impact = Impact(attack.Target);
                CommsNet.Chat(context, owner, $"{owner}: ransomware has encrypted the {system}. {impact}",
                    new CyberIncidentReported(attack.Started.AttackId, attack.Target, system, impact), TimeSpan.Zero);
            }

            // Water treatment left without hands on the controls: the water can't be vouched for.
            if (attack is { Target: CyberTarget.WaterTreatment, FallbackAt: null, WaterUnsafe: false } && context.SimTime - attack.StartedAt >= WaterFallbackWindow)
            {
                attack.WaterUnsafe = true;
                var at = WaterWorks(world);
                context.EmitTruth(new DrinkingWaterUnsafe(attack.Started.AttackId, at, UnsafeWaterRadiusMeters));
                const string advice = "Chlorination can't be verified with the controls down: we advise a boil water notice for the area it supplies.";
                CommsNet.Chat(context, owner, $"{owner}: {advice}", new CyberIncidentReported(attack.Started.AttackId, attack.Target, system, advice), TimeSpan.Zero);
            }

            // Working on paper, the crews read their held messages over the radio.
            if (attack is { Target: CyberTarget.Dispatch, FallbackAt: not null })
                foreach (var unit in world.Units.Values.Where(u => u.PendingData.Count > 0 && !u.RadioFailed))
                {
                    foreach (var (payload, _) in unit.PendingData) CommsNet.Voice(context, unit, CommsNet.VoiceText(unit, payload), payload);
                    unit.PendingData.Clear();
                }

            var restoreAt = attack.StartedAt + RecoveryTime;
            if (world.Notified.Where(n => IsCyberCentre(n.Key)).Select(n => (DateTimeOffset?)n.Value).Min() is { } told)
            {
                var assisted = (told > attack.StartedAt ? told : attack.StartedAt) + AssistedRecoveryTime;
                if (assisted < restoreAt) restoreAt = assisted;
            }
            if (context.SimTime < restoreAt) continue;
            attack.RestoredAt = context.SimTime;
            // Terminals back: what they held goes now (with simulated comms, CommsSystem sends it as coverage allows).
            if (attack.Target == CyberTarget.Dispatch && !world.Radio.Active)
                foreach (var unit in world.Units.Values.Where(u => u.PendingData.Count > 0 && !u.RadioFailed))
                {
                    foreach (var (payload, source) in unit.PendingData) context.EmitPerceived(payload, source);
                    unit.PendingData.Clear();
                }
            CommsNet.Chat(context, owner, $"{owner}: the {system} is restored from backups and back in use.", new CyberSystemsRestored(attack.Started.AttackId), TimeSpan.Zero);
        }
    }

    private static (string Owner, string System) Owner(WorldState world, WorldProtection.Attack attack) => attack.Target switch
    {
        CyberTarget.Dispatch => ("Control room ICT", "computer-aided dispatch and mobile data"),
        CyberTarget.Hospital => attack.Started.HospitalId is { } id && world.Hospitals.TryGetValue(id, out var hospital)
            ? (hospital.Name, $"{hospital.Name} IT (records, results, bed management)")
            : ("The hospital", "hospital IT (records, results, bed management)"),
        CyberTarget.WaterTreatment => ("Uisce Éireann", "treatment works control system"),
        _ => ("Dublin City Council traffic control", "city traffic signal control system"),
    };

    private static string Impact(CyberTarget target) => target switch
    {
        CyberTarget.Dispatch => "Mobile data is down: crews' status buttons and AVL aren't reaching the control room.",
        CyberTarget.Hospital => "We are on paper and runners: emergency department capacity is down.",
        CyberTarget.WaterTreatment => "We can't see or run the treatment process remotely.",
        _ => "Signals around the incidents are stuck or cycling wrongly: expect queues at junctions.",
    };

    /// <summary>Where unsafe water is: the area's pumping station, else the largest incident.</summary>
    private static GeoPoint WaterWorks(WorldState world) =>
        world.Sites.Values.FirstOrDefault(s => s.Kind == HazardSiteKind.WaterPumping)?.Location
        ?? world.Incidents.Values.OrderByDescending(i => i.Severity).FirstOrDefault()?.Location
        ?? new GeoPoint(53.344, -6.26);
}
