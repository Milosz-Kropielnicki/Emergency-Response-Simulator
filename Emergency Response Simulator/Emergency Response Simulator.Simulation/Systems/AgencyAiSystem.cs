using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Other agencies' control rooms, run by the simulation (Design Document §10.3, §14). Their units appear on the COP
/// but take orders only from their own agency:
/// <list type="bullet">
/// <item><b>Routine work</b>: every so often each agency sends one of its units to an ordinary job somewhere in the
/// city (a car fire, a fall, a minor collision), which ties it up for a while.</item>
/// <item><b>Initiative at the major incident</b>: their own callers tell them about it, so some minutes after it starts
/// they act without being asked, and tell command what they have done: police send a car to hold a traffic cordon,
/// and to direct traffic at junctions whose signals have failed; the ambulance service sends crews when casualties
/// are waiting; a neighbouring fire station sends an engine to a large fire nobody is fighting.</item>
/// </list>
/// They go to where the incident really is, because their information comes from their own sources, not the COP.
/// </summary>
public sealed class AgencyAiSystem(RoutingService? routing = null) : ISimulationSystem
{
    /// <summary>Average time between routine jobs for each AI-run agency.</summary>
    public static readonly TimeSpan MeanJobInterval = TimeSpan.FromMinutes(15);

    /// <summary>How long other agencies take to hear about a major incident from their own callers.</summary>
    public static readonly TimeSpan AwarenessDelay = TimeSpan.FromMinutes(6);

    /// <summary>A unit sent on its own agency's initiative goes home once the incident has been over this long.</summary>
    public static readonly TimeSpan StandDownAfter = TimeSpan.FromMinutes(10);

    private static readonly string[] FireJobs = ["Automatic fire alarm, office block", "Car on fire", "Bin fire spreading to a shed", "Person locked out, child inside"];
    private static readonly string[] EmsJobs = ["Fall, elderly person", "Breathing difficulty", "Chest pains", "Cyclist down after a collision"];
    private static readonly string[] PoliceJobs = ["Minor collision blocking a lane", "Shoplifter detained", "Report of a burglary in progress", "Traffic lights stuck on red"];

    private readonly Dictionary<Guid, DateTimeOffset> _nextJob = [];
    private readonly Dictionary<Guid, Random> _random = [];
    private readonly HashSet<string> _initiatives = [];
    private readonly Dictionary<Guid, Func<WorldState, bool>> _initiativeOver = [];
    private readonly Dictionary<string, DateTimeOffset> _lastSent = [];

    public int Order => 18;

    public void Update(SimulationContext context)
    {
        var ai = context.World.Agencies.Where(a => a.Value.AiControlled).ToList();
        if (ai.Count == 0) return;

        ClearFinishedTasks(context);
        foreach (var (agencyId, agency) in ai)
            RoutineWork(context, agencyId, agency);
        Initiative(context, ai);
    }

    private Random RandomFor(Guid agencyId) =>
        _random.TryGetValue(agencyId, out var random) ? random : _random[agencyId] = SimRandom.For(agencyId, 5);

    // ---- Routine work ----

    private void RoutineWork(SimulationContext context, Guid agencyId, WorldAgency agency)
    {
        var random = RandomFor(agencyId);
        if (!_nextJob.TryGetValue(agencyId, out var due))
        {
            _nextJob[agencyId] = context.SimTime + Exponential(random, MeanJobInterval);
            return;
        }
        if (context.SimTime < due) return;
        _nextJob[agencyId] = context.SimTime + Exponential(random, MeanJobInterval);

        var units = Available(context.World, agencyId).ToList();
        if (units.Count == 0) return; // everyone busy: the job waits in their own queue, invisible to command

        var unit = units[random.Next(units.Count)];
        var jobs = agency.Type switch { AgencyType.Fire => FireJobs, AgencyType.Ems => EmsJobs, _ => PoliceJobs };
        var job = jobs[random.Next(jobs.Length)];
        var location = GeoMath.Destination(unit.Home, random.NextDouble() * 360, 300 + random.NextDouble() * 1700);
        var road = routing?.NearestRoad(location) is { } nearest && nearest.DistanceMeters < 100 ? $", {nearest.Name}" : "";

        context.EmitPerceived(new UnitTasked(unit.Id, Guid.NewGuid(), agency.Name, job + road, location), EventSources.Comms);
    }

    private static TimeSpan Exponential(Random random, TimeSpan mean) =>
        TimeSpan.FromSeconds(-Math.Log(1 - random.NextDouble()) * mean.TotalSeconds);

    private static IEnumerable<WorldUnit> Available(WorldState world, Guid agencyId) =>
        world.Units.Values.Where(u => u.AgencyId == agencyId && u.Phase == ResponsePhase.Idle && u.OrderedIncidentId is null && !u.BrokenDown)
            .OrderBy(u => u.Callsign, StringComparer.Ordinal);

    /// <summary>Routine jobs take 10–25 minutes on scene; initiative tasks last until the incident is over.</summary>
    private void ClearFinishedTasks(SimulationContext context)
    {
        var world = context.World;
        foreach (var task in world.AgencyTasks.Values.ToList())
        {
            if (!world.Units.TryGetValue(task.UnitId, out var unit) || unit.OrderedIncidentId != task.TaskId)
            {
                world.AgencyTasks.Remove(task.TaskId); // re-tasked or stood down by someone else
                continue;
            }

            if (_initiativeOver.TryGetValue(task.TaskId, out var over))
            {
                if (!over(world)) continue;
                task.ClearAt ??= context.SimTime + StandDownAfter;
            }
            else if (unit.Phase == ResponsePhase.Operating)
            {
                task.ClearAt ??= context.SimTime + TimeSpan.FromMinutes(10 + SimRandom.For(task.TaskId).NextDouble() * 15);
            }

            if (task.ClearAt is not { } clearAt || context.SimTime < clearAt) continue;

            world.AgencyTasks.Remove(task.TaskId);
            world.ReportedIncidentLocations.Remove(task.TaskId);
            _initiativeOver.Remove(task.TaskId);
            unit.Phase = ResponsePhase.Idle;
            unit.OrderedIncidentId = null;
            unit.SpeedKph = 0;
            unit.Route = [];
            if (!unit.RadioFailed)
                context.EmitPerceived(new UnitStatusChanged(unit.Id, UnitStatus.Available), EventSources.Comms);
        }
    }

    // ---- Initiative ----

    private void Initiative(SimulationContext context, List<KeyValuePair<Guid, WorldAgency>> ai)
    {
        var world = context.World;
        foreach (var incident in world.Incidents.Values.Where(i => !i.Extinguished))
        {
            if (context.SimTime - incident.StartedAt < AwarenessDelay || incident.Severity < 0.3) continue;
            var label = EventDescriber.Humanize(incident.Type).ToLowerInvariant();
            bool Over(WorldState w) => !w.Incidents.TryGetValue(incident.Id, out var i) || i.Extinguished;

            // Police: a traffic cordon on the approach, upwind of the scene.
            foreach (var (agencyId, agency) in ai.Where(a => a.Value.Type == AgencyType.Police))
            {
                var spot = GeoMath.Destination(incident.Location, world.Weather.WindFromDegrees, 200);
                Send(context, $"cordon:{incident.Id}", agencyId, agency, spot, $"Traffic control and outer cordon for the {label}",
                    unit => $"{unit.Callsign} is setting up a traffic cordon about 200 m from the {label}" +
                            $"{RoadAt(spot)}, keeping the approach clear for you.", Over);
            }

            // Ambulance service: more crews when casualties are waiting and too few crews are treating them.
            var waiting = world.Casualties.Values.Count(c => c.IncidentId == incident.Id && c.Alive && c.State == CasualtyState.AwaitingTreatment);
            var crews = world.Units.Values.Count(u => u.Type is UnitType.AmbulanceAls or UnitType.AmbulanceBls
                                                      && u.OrderedIncidentId is not null
                                                      && GeoMath.DistanceMeters(u.Destination, incident.Location) < 500);
            if (waiting >= 4 && waiting > crews * 3 && context.SimTime - incident.StartedAt >= AwarenessDelay + TimeSpan.FromMinutes(2))
            {
                foreach (var (agencyId, agency) in ai.Where(a => a.Value.Type == AgencyType.Ems))
                {
                    var key = $"ems:{incident.Id}";
                    var sent = _initiatives.Count(k => k.StartsWith(key));
                    if (sent >= 2 || (_lastSent.TryGetValue(key, out var lastSent) && context.SimTime - lastSent < TimeSpan.FromMinutes(5)))
                        continue;
                    if (Send(context, $"{key}:{sent}", agencyId, agency, incident.Location, $"Casualties at the {label}",
                            unit => $"Our control has had calls about casualties at the {label}. We are sending {unit.Callsign} to help.", Over))
                        _lastSent[key] = context.SimTime;
                }
            }

            // Neighbouring fire station: a large fire with nobody fighting it.
            var fire = world.Hazards.Values.FirstOrDefault(h => h.IncidentId == incident.Id && !h.Ended && h.Model is FireSpreadModel)?.Model as FireSpreadModel;
            if (fire is { BurningAreaSquareMeters: >= 2500 } && context.SimTime - incident.StartedAt >= TimeSpan.FromMinutes(10)
                && !world.Units.Values.Any(u => HazardSystem.SuppressionRate(u.Type) > 0 && u.Phase == ResponsePhase.Operating
                                                && fire.DistanceToFire(u.Location) <= HazardSystem.HoseReachMeters))
            {
                foreach (var (agencyId, agency) in ai.Where(a => a.Value.Type == AgencyType.Fire))
                {
                    Send(context, $"fire:{incident.Id}", agencyId, agency, incident.Location, $"Large fire, {label}",
                        unit => $"Large fire visible from our station. {unit.Callsign} is responding on our own initiative and will report to your incident commander on arrival.",
                        Over);
                }
            }
        }

        // Police: someone to direct traffic at a junction whose lights have failed.
        foreach (var outage in world.Outages.Values.Where(o => context.SimTime - o.StartedAt >= TimeSpan.FromMinutes(5)))
        {
            var junction = BusiestDarkJunction(outage) ?? outage.Centre;
            foreach (var (agencyId, agency) in ai.Where(a => a.Value.Type == AgencyType.Police))
            {
                Send(context, $"signals:{outage.Id}", agencyId, agency, junction, $"Manual traffic control{RoadAt(junction)} (signals out)",
                    unit => $"Traffic lights are out{RoadAt(junction)}; {unit.Callsign} is directing traffic there.",
                    w => !w.Outages.ContainsKey(outage.Id));
            }
        }
    }

    /// <summary>
    /// Sends the nearest free unit of the agency, once per key, and tells command. With nobody free, the agency diverts
    /// a unit from routine work: the major incident comes first. False if every unit is already on the incident.
    /// </summary>
    private bool Send(SimulationContext context, string key, Guid agencyId, WorldAgency agency, GeoPoint where, string task,
        Func<WorldUnit, string> message, Func<WorldState, bool> over)
    {
        if (_initiatives.Contains(key)) return false;
        var world = context.World;
        var unit = Available(world, agencyId).MinBy(u => GeoMath.DistanceMeters(u.Location, where))
                   ?? world.AgencyTasks.Values
                       .Where(t => !_initiativeOver.ContainsKey(t.TaskId))
                       .Select(t => world.Units.GetValueOrDefault(t.UnitId))
                       .OfType<WorldUnit>()
                       .Where(u => u.AgencyId == agencyId && !u.BrokenDown)
                       .MinBy(u => GeoMath.DistanceMeters(u.Location, where));
        if (unit is null) return false;

        // Drop whatever routine job it had; the new task replaces it.
        if (unit.OrderedIncidentId is { } routine && world.AgencyTasks.Remove(routine))
            world.ReportedIncidentLocations.Remove(routine);

        _initiatives.Add(key);
        var taskId = Guid.NewGuid();
        _initiativeOver[taskId] = over;
        context.EmitPerceived(new UnitTasked(unit.Id, taskId, agency.Name, task, where), EventSources.Comms);
        context.EmitPerceived(new ReportReceived(Guid.NewGuid(), null, ReportSource.Agency, agency.Name, message(unit),
            Confidence.High, VerificationStatus.Confirmed, where, 50), EventSources.Comms);
        return true;
    }

    /// <summary>The dark junction with the most main roads into it, nearest the outage's centre.</summary>
    private GeoPoint? BusiestDarkJunction(PowerOutage outage)
    {
        if (routing?.Network is not { } network) return null;
        var node = network.EdgesNear(outage.Centre, outage.RadiusMeters)
            .Select(e => e.To)
            .Distinct()
            .Select(n => (Node: n, Main: network.IncomingEdges(n).Count(e => network.Edge(e).Class <= RoadClass.Tertiary)))
            .Where(x => x.Main >= 3 && outage.Covers(network.NodeLocation(x.Node)))
            .OrderByDescending(x => x.Main)
            .ThenBy(x => GeoMath.DistanceMeters(network.NodeLocation(x.Node), outage.Centre))
            .Select(x => (int?)x.Node)
            .FirstOrDefault();
        return node is { } n2 ? network.NodeLocation(n2) : null;
    }

    private string RoadAt(GeoPoint point) =>
        routing?.NearestRoad(point) is { } road && road.DistanceMeters < 100 ? $" on {road.Name}" : "";
}
