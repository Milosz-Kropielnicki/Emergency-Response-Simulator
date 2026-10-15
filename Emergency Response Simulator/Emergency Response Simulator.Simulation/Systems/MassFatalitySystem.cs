using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// The dead (Phase 16: morgue and mass fatality).
/// <list type="bullet">
/// <item><b>At the scene</b>: crews report the bodies they find. They stay where they are until recovered, and recovery
/// waits for the coroner to be notified (the Garda act for the coroner; bodies are not moved without that authority).
/// From <see cref="RecoveryStart"/> after the notification, recovery teams bring out <see cref="RecoveryBatch"/> bodies
/// every <see cref="RecoveryInterval"/>: to the temporary mortuary if one is open, else to a hospital mortuary with
/// space.</item>
/// <item><b>At hospitals</b>: a patient who dies goes to the hospital's mortuary. A full mortuary means the body is held
/// in the emergency department, taking a place a living patient needs, until a temporary mortuary opens and takes
/// it.</item>
/// <item><b>Identification</b>: disaster victim identification takes <see cref="IdentifyAtMortuary"/> at a temporary
/// mortuary (DVI teams, ante-mortem data from the family assistance centre) and <see cref="IdentifyAtHospital"/> at a
/// hospital. Families are told only once their relative is identified (<c>ReunificationSystem</c>).</item>
/// </list>
/// </summary>
public sealed class MassFatalitySystem : ISimulationSystem
{
    public static readonly TimeSpan RecoveryStart = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan RecoveryInterval = TimeSpan.FromMinutes(20);
    public const int RecoveryBatch = 4;
    public static readonly TimeSpan MortuaryTransfer = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan IdentifyAtMortuary = TimeSpan.FromMinutes(40);
    public static readonly TimeSpan IdentifyAtHospital = TimeSpan.FromMinutes(90);
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(2);

    private const string Dvi = "Garda Disaster Victim Identification";

    private readonly Dictionary<Guid, int> _reportedAtScene = [];
    private readonly Dictionary<Guid, DateTimeOffset> _lastReport = [];
    private readonly Dictionary<Guid, DateTimeOffset> _nextRecovery = [];
    private readonly HashSet<Guid> _morgueFullWarned = [];

    public int Order => 18;

    public void Attach(WorldState world) => world.Health.FatalitiesActive = true;

    public void Update(SimulationContext context)
    {
        Find(context);
        ReportScenes(context);
        Recover(context);
        ToMortuary(context);
        Identify(context);
    }

    /// <summary>The coroner's authority: when command notified the coroner, if it has.</summary>
    public static DateTimeOffset? CoronerNotified(WorldState world) =>
        world.Notified.Where(n => n.Key.Contains("coroner", StringComparison.OrdinalIgnoreCase)).Select(n => (DateTimeOffset?)n.Value).Min();

    /// <summary>The perceived incident command opened nearest a point (bodies are reported against it).</summary>
    private static Guid? IncidentAt(WorldState world, GeoPoint point) =>
        world.Logistics.IncidentLocations.Where(i => GeoMath.DistanceMeters(i.Value, point) <= 1500)
            .OrderBy(i => GeoMath.DistanceMeters(i.Value, point)).Select(i => (Guid?)i.Key).FirstOrDefault();

    // ---- Where the dead are ----

    private void Find(SimulationContext context)
    {
        var world = context.World;
        var health = world.Health;
        foreach (var casualty in world.Casualties.Values.Where(c => !c.Alive && !health.Bodies.ContainsKey(c.Id)))
        {
            var body = new WorldHealth.Body(casualty.Id, IncidentAt(world, casualty.Location), casualty.Location, context.SimTime);
            health.Bodies[casualty.Id] = body;
            if (casualty.HospitalId is not { } hospitalId || !world.Hospitals.TryGetValue(hospitalId, out var hospital))
            {
                body.Place = WorldHealth.BodyPlace.Scene;
                continue;
            }

            // Died in hospital (they had a place in the department) or in the ambulance (brought in).
            var hadPlace = health.DiedInHospital.Contains(casualty.Id);
            body.HospitalId = hospitalId;
            body.Location = hospital.Location;
            var care = health.Hospitals.GetValueOrDefault(hospitalId);
            if (care is not null && care.MorgueOccupied < care.MorgueSpaces)
            {
                care.MorgueOccupied++;
                body.Place = WorldHealth.BodyPlace.HospitalMorgue;
                body.InMortuarySince = context.SimTime;
                if (hadPlace) hospital.Delivered = Math.Max(0, hospital.Delivered - 1);
                continue;
            }
            body.Place = WorldHealth.BodyPlace.HeldInHospital;
            if (!hadPlace) hospital.Delivered++;
            if (care is not null && _morgueFullWarned.Add(hospitalId))
                context.EmitPerceived(new AlertRaised(Guid.NewGuid(), AlertCategory.ResourceShortage, AlertSeverity.Warning, $"Mortuary full at {hospital.Name}",
                    $"{hospital.Name}'s mortuary is full: the dead are being held in the emergency department, in places the living need. " +
                    "A temporary mortuary would take them.", null, null), EventSources.Comms);
        }
    }

    /// <summary>Crews at a scene say how many dead are there, when it changes.</summary>
    private void ReportScenes(SimulationContext context)
    {
        var world = context.World;
        var atScene = world.Health.Bodies.Values.Where(b => b.Place == WorldHealth.BodyPlace.Scene && b.IncidentId is not null)
            .GroupBy(b => b.IncidentId!.Value).ToDictionary(g => g.Key, g => g.Count());
        foreach (var incident in atScene.Keys.Union(_reportedAtScene.Keys).ToList())
        {
            var count = atScene.GetValueOrDefault(incident);
            if (_reportedAtScene.GetValueOrDefault(incident) == count) continue;
            if (_lastReport.TryGetValue(incident, out var last) && context.SimTime - last < ReportInterval) continue;
            // Somebody has to be there to see them.
            if (!world.Units.Values.Any(u => u.Phase is ResponsePhase.OnScene or ResponsePhase.Operating && u.OrderedIncidentId == incident)) continue;
            _reportedAtScene[incident] = count;
            _lastReport[incident] = context.SimTime;
            context.EmitPerceived(new BodiesReported(incident, count), EventSources.Comms);
        }
    }

    // ---- Recovery and the mortuary ----

    private void Recover(SimulationContext context)
    {
        var world = context.World;
        var health = world.Health;
        if (CoronerNotified(world) is not { } coroner || context.SimTime - coroner < RecoveryStart) return;

        foreach (var incident in health.Bodies.Values.Where(b => b.Place == WorldHealth.BodyPlace.Scene && b.IncidentId is not null)
                     .Select(b => b.IncidentId!.Value).Distinct().ToList())
        {
            if (_nextRecovery.TryGetValue(incident, out var next) && context.SimTime < next) continue;
            _nextRecovery[incident] = context.SimTime + RecoveryInterval;

            var batch = health.Bodies.Values.Where(b => b.Place == WorldHealth.BodyPlace.Scene && b.IncidentId == incident).Take(RecoveryBatch).ToList();
            var recovered = 0;
            string? to = null;
            foreach (var body in batch)
            {
                if (health.Mortuary is { } mortuary)
                {
                    (body.Place, body.Location, body.InMortuarySince) = (WorldHealth.BodyPlace.Mortuary, mortuary.Location, context.SimTime);
                    to = mortuary.Name;
                }
                else if (world.Hospitals.Values.Where(h => health.Hospitals.GetValueOrDefault(h.Id) is { } c && c.MorgueOccupied < c.MorgueSpaces)
                             .MinBy(h => GeoMath.DistanceMeters(h.Location, body.Location)) is { } hospital)
                {
                    health.Hospitals[hospital.Id].MorgueOccupied++;
                    (body.Place, body.HospitalId, body.Location, body.InMortuarySince) =
                        (WorldHealth.BodyPlace.HospitalMorgue, hospital.Id, hospital.Location, context.SimTime);
                    to = to is null || to == hospital.Name ? hospital.Name : "hospital mortuaries";
                }
                else break; // nowhere to take them
                recovered++;
            }
            if (recovered > 0)
                context.EmitPerceived(new BodiesRecovered(incident, recovered, to!), EventSources.Comms);
        }
    }

    /// <summary>Once a temporary mortuary is open, the dead held in emergency departments are moved to it.</summary>
    private static void ToMortuary(SimulationContext context)
    {
        var world = context.World;
        var health = world.Health;
        if (health.Mortuary is not { } mortuary || context.SimTime - mortuary.OpenedAt < MortuaryTransfer) return;
        foreach (var body in health.Bodies.Values.Where(b => b.Place == WorldHealth.BodyPlace.HeldInHospital).ToList())
        {
            if (body.HospitalId is { } h && world.Hospitals.TryGetValue(h, out var hospital))
                hospital.Delivered = Math.Max(0, hospital.Delivered - 1); // the place in the department is free again
            (body.Place, body.Location, body.InMortuarySince) = (WorldHealth.BodyPlace.Mortuary, mortuary.Location, context.SimTime);
        }
    }

    private static void Identify(SimulationContext context)
    {
        var world = context.World;
        foreach (var body in world.Health.Bodies.Values.Where(b => b.IdentifiedAt is null && b.InMortuarySince is not null))
        {
            var temporary = body.Place == WorldHealth.BodyPlace.Mortuary;
            var takes = (temporary ? IdentifyAtMortuary : IdentifyAtHospital) * (0.8 + SimRandom.For(body.Id, 165).NextDouble() * 0.4);
            if (context.SimTime - body.InMortuarySince!.Value < takes) continue;
            body.IdentifiedAt = context.SimTime;
            var where = temporary ? world.Health.Mortuary!.Value.Name
                : body.HospitalId is { } h && world.Hospitals.TryGetValue(h, out var hospital) ? $"{hospital.Name} mortuary" : "a hospital mortuary";
            CommsNet.Chat(context, Dvi, $"{Dvi}: a victim has been identified at {where}; the family can be informed.", new VictimIdentified(body.Id, where), TimeSpan.Zero);
        }
    }
}
