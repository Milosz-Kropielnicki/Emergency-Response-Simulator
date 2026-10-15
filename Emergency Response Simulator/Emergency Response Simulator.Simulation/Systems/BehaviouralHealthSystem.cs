using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Behavioural health (Phase 16): the psychological toll on the public, and crisis counselling.
/// <list type="bullet">
/// <item><b>Distress</b>: around each incident, from the people it touched — survivors and witnesses at the scene (two
/// for each injured person, six for each death), evacuees in the rest centres, families asking after relatives, and the
/// anxious after a public-health threat. The scene, rest centres and family assistance centre report it every
/// <see cref="ReportInterval"/>.</item>
/// <item><b>The worried well</b>: unsupported, some of the distressed turn up at the nearest emergency department with
/// no injury and stay a couple of hours, taking places. A helpline cuts that by 60%, a team at the scene by 30%.</item>
/// <item><b>Crisis counselling</b>: a team arrives after <see cref="TeamArrival"/> and gives psychological first aid,
/// triaging <see cref="TeamPerHalfHour"/> people every half hour (the helpline <see cref="HelplinePerHalfHour"/>):
/// about 60% low risk, 30% moderate (follow-up), 10% high risk, who are referred for an acute psychiatric bed.</item>
/// </list>
/// </summary>
public sealed class BehaviouralHealthSystem : ISimulationSystem
{
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan TeamArrival = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan HelplineSetup = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan WorriedWellStay = TimeSpan.FromHours(2);
    public const int TeamPerHalfHour = 40;
    public const int HelplinePerHalfHour = 60;

    private readonly Dictionary<Guid, (int Scene, int RestCentre, int Families, int Anxious)> _pools = [];
    private readonly Dictionary<Guid, int> _reported = [];
    private readonly Dictionary<Guid, double> _capacity = [];
    private readonly Dictionary<Guid, int> _reportedSeen = [];
    private DateTimeOffset? _nextRound;

    public int Order => 23;

    public void Update(SimulationContext context)
    {
        var world = context.World;
        ExpireWorriedWell(world, context.SimTime);
        Counsel(context);
        if (_nextRound is { } next && context.SimTime < next) return;
        _nextRound = context.SimTime + ReportInterval;
        Assess(world);
        Report(context);
        WorriedWell(world, context.SimTime);
        ReportTriage(context);
    }

    /// <summary>Who is distressed around each incident command has opened.</summary>
    private void Assess(WorldState world)
    {
        foreach (var (incident, at) in world.Logistics.IncidentLocations)
        {
            bool Near(GeoPoint point) => GeoMath.DistanceMeters(point, at) <= 1500;
            var injured = world.Casualties.Values.Count(c => c.Alive && Near(c.Location));
            // The dead: as the mortuary system placed them, or (without it) where they fell.
            var dead = world.Casualties.Values.Count(c => !c.Alive
                                                          && (world.Health.Bodies.TryGetValue(c.Id, out var body) ? body.IncidentId == incident : Near(c.Location)));
            var evacuees = world.Civilians.Count(p => p.State is CivilianState.Evacuating or CivilianState.Safe && Near(p.Location));
            var families = world.Public.Enquiries.Values.Count(e => e.Enquiry.IncidentId == incident);
            var anxious = world.Health.Threats.Values.Where(t => t.IncidentId == incident).Sum(t => t.TotalExposed);
            _pools[incident] = (injured * 2 + dead * 6, (int)Math.Round(evacuees * 0.08), families + dead * 2, (int)Math.Round(anxious * 0.3));
            var (scene, rest, fam, anx) = _pools[incident];
            world.Health.Distress[incident] = Math.Max(0, scene + rest + fam + anx - Seen(world, incident));
        }
    }

    private static int Seen(WorldState world, Guid incident) =>
        world.Health.Counsellors.Where(c => c.Deployed.IncidentId == incident || c.Deployed.IncidentId is null).Sum(c => c.Seen);

    private void Report(SimulationContext context)
    {
        foreach (var (incident, distressed) in context.World.Health.Distress)
        {
            if (distressed == 0 && _reported.GetValueOrDefault(incident) == 0) continue;
            // An estimate, to the nearest five.
            var estimate = (int)Math.Round(distressed / 5.0) * 5;
            if (_reported.GetValueOrDefault(incident) == estimate) continue;
            _reported[incident] = estimate;
            context.EmitPerceived(new PsychosocialNeedReported(incident, estimate), EventSources.Comms);
        }
    }

    // ---- The worried well ----

    private static void WorriedWell(WorldState world, DateTimeOffset now)
    {
        foreach (var (incident, distressed) in world.Health.Distress)
        {
            if (distressed <= 0 || !world.Logistics.IncidentLocations.TryGetValue(incident, out var at)) continue;
            var helpline = Working(world, now).Any(c => c.Deployed.Setting == CounsellingSetting.Helpline && (c.Deployed.IncidentId is null || c.Deployed.IncidentId == incident));
            var atScene = Working(world, now).Any(c => c.Deployed.Setting == CounsellingSetting.Scene && c.Deployed.IncidentId == incident);
            var count = (int)Math.Round(distressed * 0.04 * (helpline ? 0.4 : 1) * (atScene ? 0.7 : 1));
            if (count <= 0) continue;
            if (world.Hospitals.Values.Where(h => world.Health.Accepts(h, null)).MinBy(h => GeoMath.DistanceMeters(h.Location, at)) is not { } hospital) continue;
            hospital.Baseline += count;
            if (!world.Health.Hospitals.TryGetValue(hospital.Id, out var care))
                world.Health.Hospitals[hospital.Id] = care = new WorldHealth.HospitalCare();
            care.WorriedWell.Add((now + WorriedWellStay, count));
        }
    }

    private static void ExpireWorriedWell(WorldState world, DateTimeOffset now)
    {
        foreach (var (id, care) in world.Health.Hospitals)
        {
            var leaving = care.WorriedWell.Where(w => now >= w.Leaves).ToList();
            if (leaving.Count == 0) continue;
            foreach (var lot in leaving) care.WorriedWell.Remove(lot);
            if (world.Hospitals.TryGetValue(id, out var hospital))
                hospital.Baseline = Math.Max(0, hospital.Baseline - leaving.Sum(l => l.Count));
        }
    }

    // ---- Crisis counselling ----

    private static IEnumerable<WorldHealth.Counselling> Working(WorldState world, DateTimeOffset now) =>
        world.Health.Counsellors.Where(c => now - c.DeployedAt >= (c.Deployed.Setting == CounsellingSetting.Helpline ? HelplineSetup : TeamArrival));

    private void Counsel(SimulationContext context)
    {
        var world = context.World;
        foreach (var team in Working(world, context.SimTime).ToList())
        {
            var setting = team.Deployed.Setting;
            // The family assistance centre's team works there once it is open.
            if (setting == CounsellingSetting.FamilyAssistanceCentre && world.Public.AssistanceCentre is null) continue;
            var id = team.Deployed.DeploymentId;
            var rate = setting == CounsellingSetting.Helpline ? HelplinePerHalfHour : TeamPerHalfHour;
            _capacity[id] = _capacity.GetValueOrDefault(id) + rate * context.Delta.TotalHours * 2;

            var pool = Pool(world, team);
            var alreadySeen = world.Health.Counsellors.Where(c => c.Deployed.Setting == setting && c.Deployed.IncidentId == team.Deployed.IncidentId).Sum(c => c.Seen);
            var n = (int)Math.Min(Math.Floor(_capacity[id]), Math.Max(0, pool - alreadySeen));
            if (n <= 0) continue;
            _capacity[id] -= n;
            team.Seen += n;
            // Psychological triage: a tenth at high risk, three tenths moderate, the rest low.
            var high = team.Seen / 10;
            var moderate = team.Seen * 3 / 10;
            for (var newlyHigh = high - team.High; newlyHigh > 0; newlyHigh--)
                if (PsychiatricBed(world, team)) team.Admitted++;
            (team.High, team.Moderate, team.Low) = (high, moderate, team.Seen - high - moderate);
        }
    }

    /// <summary>The people a team's setting reaches.</summary>
    private int Pool(WorldState world, WorldHealth.Counselling team)
    {
        var pools = team.Deployed.IncidentId is { } incident
            ? [_pools.GetValueOrDefault(incident)]
            : _pools.Values.ToList();
        return team.Deployed.Setting switch
        {
            CounsellingSetting.Scene => pools.Sum(p => p.Scene),
            CounsellingSetting.RestCentre => pools.Sum(p => p.RestCentre),
            CounsellingSetting.FamilyAssistanceCentre => pools.Sum(p => p.Families),
            _ => pools.Sum(p => p.Scene + p.RestCentre + p.Families + p.Anxious),
        };
    }

    /// <summary>A high-risk person referred for an acute psychiatric bed: the nearest hospital with one free.</summary>
    private static bool PsychiatricBed(WorldState world, WorldHealth.Counselling team)
    {
        var from = team.Deployed.IncidentId is { } incident && world.Logistics.IncidentLocations.TryGetValue(incident, out var at) ? at : (GeoPoint?)null;
        var bed = world.Hospitals.Values.Where(h => world.Health.Hospitals.GetValueOrDefault(h.Id)?.Free(Specialty.Psychiatric) > 0)
            .MinBy(h => from is { } f ? GeoMath.DistanceMeters(h.Location, f) : 0);
        if (bed is null) return false;
        var care = world.Health.Hospitals[bed.Id];
        care.Occupied[Specialty.Psychiatric] = care.Occupied.GetValueOrDefault(Specialty.Psychiatric) + 1;
        return true;
    }

    private void ReportTriage(SimulationContext context)
    {
        foreach (var team in context.World.Health.Counsellors)
        {
            var id = team.Deployed.DeploymentId;
            if (_reportedSeen.GetValueOrDefault(id) == team.Seen) continue;
            _reportedSeen[id] = team.Seen;
            context.EmitPerceived(new PsychologicalTriageReported(id, team.Seen, team.Low, team.Moderate, team.High, team.Admitted), EventSources.Comms);
        }
    }
}
