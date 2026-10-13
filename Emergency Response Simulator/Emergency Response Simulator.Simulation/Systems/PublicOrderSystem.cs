using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// The public around the response (Phase 14): who is known to need help, who takes advantage, and who turns up.
/// <list type="bullet">
/// <item><b>The vulnerable-persons register</b>: some minutes after an evacuation or shelter zone is declared, the
/// council says how many elderly and disabled people on its register live inside it. Nobody's register is complete
/// (most, not all, are on it), and it knows nothing of rough-sleepers or pets. A community leader command has
/// engaged knows the area better and says so, rough-sleepers and pet owners included.</item>
/// <item><b>Looting</b>: empty homes in an evacuation zone, with no gardaí there and no police cordon, get broken into
/// before long. A neighbour who stayed rings 999. Gardaí on the spot catch most attempts.</item>
/// <item><b>Spontaneous volunteers</b>: a serious incident on the news brings people to the cordon wanting to help, more
/// the longer nobody takes charge of them. Left alone, some go past the cordon on their own, and near the hazard some
/// get hurt. Registered, they staff the family assistance centre; turned away, most go home.</item>
/// </list>
/// What command learns arrives as reports, calls and register returns; the truth of it is in <see cref="WorldPublic"/>
/// and the review.
/// </summary>
public sealed class PublicOrderSystem : ISimulationSystem
{
    /// <summary>The council's register comes back this long after a zone is declared.</summary>
    public static readonly TimeSpan RegisterDelay = TimeSpan.FromMinutes(8);

    /// <summary>Share of elderly and disabled people who are on the council's register.</summary>
    public const double RegisteredShare = 0.7;

    /// <summary>An evacuation zone in force this long, with homes empty, attracts looters.</summary>
    public static readonly TimeSpan LootingAfter = TimeSpan.FromMinutes(20);

    public static readonly TimeSpan LootingInterval = TimeSpan.FromMinutes(10);

    /// <summary>Volunteers turn up this long after a serious incident starts.</summary>
    public static readonly TimeSpan VolunteersAfter = TimeSpan.FromMinutes(15);

    /// <summary>Volunteers nobody has taken charge of for this long start going past the cordon.</summary>
    public static readonly TimeSpan SelfDeployAfter = TimeSpan.FromMinutes(10);

    private readonly HashSet<Guid> _registers = [];
    private readonly HashSet<Guid> _leaderReports = [];
    private readonly Dictionary<Guid, DateTimeOffset> _engagedSeen = [];
    private readonly Dictionary<Guid, (DateTimeOffset Next, int Count, int Tries)> _looting = [];
    private readonly Dictionary<Guid, DateTimeOffset> _volunteersNext = [];
    private readonly Dictionary<Guid, DateTimeOffset> _selfDeployNext = [];

    public int Order => 16; // after the civilians have moved

    public void Update(SimulationContext context)
    {
        Registers(context);
        Looting(context);
        Volunteers(context);
    }

    // ---- The vulnerable-persons register ----

    /// <summary>Whether someone is on the council's register: stable per person, most of them.</summary>
    private static bool Registered(Civilian person) => (uint)(person.Id * 2654435761u) % 100 < RegisteredShare * 100;

    private void Registers(SimulationContext context)
    {
        var world = context.World;
        foreach (var zone in world.ProtectiveActions.Values)
        {
            var inside = world.Civilians.Where(p => zone.Area.Contains(p.Home.ToPoint())).ToList();

            if (context.SimTime - zone.DeclaredAt >= RegisterDelay && _registers.Add(zone.ZoneId))
            {
                var listed = inside.Where(Registered).ToList();
                context.EmitPerceived(new VulnerableResidentsReported(zone.ZoneId, "Dublin City Council vulnerable persons register",
                    listed.Count(p => p.Is(Vulnerability.Elderly) && !p.Is(Vulnerability.MobilityImpaired)),
                    listed.Count(p => p.Is(Vulnerability.MobilityImpaired)), 0, 0), EventSources.Comms);
            }

            // A community leader command has engaged knows who is really there, five minutes after they are asked.
            if (!world.Public.EngagedZones.Contains(zone.ZoneId)) continue;
            var engagedAt = _engagedSeen.TryGetValue(zone.ZoneId, out var seen) ? seen : _engagedSeen[zone.ZoneId] = context.SimTime;
            if (context.SimTime - engagedAt < TimeSpan.FromMinutes(5) || !_leaderReports.Add(zone.ZoneId)) continue;
            context.EmitPerceived(new VulnerableResidentsReported(zone.ZoneId, "Community leader",
                inside.Count(p => p.Is(Vulnerability.Elderly) && !p.Is(Vulnerability.MobilityImpaired)),
                inside.Count(p => p.Is(Vulnerability.MobilityImpaired)),
                inside.Count(p => p.Is(Vulnerability.PetOwner)),
                inside.Count(p => p.Is(Vulnerability.Homeless))), EventSources.Comms);
        }
    }

    // ---- Looting ----

    /// <summary>Gardaí are there: a police unit at the zone, or a police cordon over it.</summary>
    public static bool Policed(WorldState world, ProtectiveAction zone)
    {
        if (world.PoliceCordons.Values.Any(c => c.Intersects(zone.Area))) return true;
        var centre = GeoPoint.FromPoint(zone.Area.Centroid);
        return world.Units.Values.Any(u => u.Phase is ResponsePhase.OnScene or ResponsePhase.Operating
                                           && u.AgencyId is { } agency && world.Agencies.TryGetValue(agency, out var owner) && owner.Type == AgencyType.Police
                                           && (zone.Area.Contains(u.Location.ToPoint()) || GeoMath.DistanceMeters(u.Location, centre) < 250));
    }

    private void Looting(SimulationContext context)
    {
        var world = context.World;
        foreach (var zone in world.ProtectiveActions.Values.Where(a => a.Type == ZoneType.EvacuationZone))
        {
            if (context.SimTime - zone.DeclaredAt < LootingAfter) continue;
            var state = _looting.GetValueOrDefault(zone.ZoneId);
            if (state.Count >= 3 || context.SimTime < state.Next) continue;
            _looting[zone.ZoneId] = (context.SimTime + LootingInterval, state.Count, state.Tries + 1);

            // Empty homes are what draws them.
            var empty = world.Civilians.Where(p => zone.Area.Contains(p.Home.ToPoint()) && !zone.Area.Contains(p.Location.ToPoint())).ToList();
            if (empty.Count < 10) continue;

            // A fresh draw every try: stable per zone and try, so a session replays the same.
            var policed = Policed(world, zone);
            var random = SimRandom.For(zone.ZoneId, 50 + state.Tries);
            if (random.NextDouble() >= (policed ? 0.1 : 0.45)) continue;

            _looting[zone.ZoneId] = (context.SimTime + LootingInterval, state.Count + 1, state.Tries + 1);
            var where = empty[random.Next(empty.Count)].Home;
            context.EmitTruth(new LootingOccurred(zone.ZoneId, where, policed));
            if (policed)
            {
                context.EmitPerceived(new ReportReceived(Guid.NewGuid(), null, ReportSource.FieldUnit, "Gardaí in the evacuation zone",
                    "Two men stopped coming out of an evacuated house with a television. Arrested.", Confidence.High, VerificationStatus.Confirmed,
                    where, 50), EventSources.Comms);
                continue;
            }
            // Someone who stayed behind sees it.
            CommsNet.EmergencyCall(context, new PendingCall
            {
                Caller = "999 caller (resident who stayed)", Summary = "There's people breaking into the houses across the road. Everyone's been evacuated and there's no guards anywhere.",
                Location = GeoMath.Destination(where, random.NextDouble() * 360, 60), AccuracyMeters = 120, Patience = TimeSpan.FromMinutes(2),
            });
        }
    }

    // ---- Spontaneous volunteers ----

    private void Volunteers(SimulationContext context)
    {
        var world = context.World;
        foreach (var (incidentId, facts) in world.Interagency.Incidents)
        {
            if (facts.Closed) continue;
            var serious = facts.Priority == IncidentPriority.Critical || facts.Priority == IncidentPriority.High && facts.Casualties >= 3;
            if (!serious || context.SimTime - facts.CreatedAt < VolunteersAfter) continue;

            var group = world.Public.Volunteers.GetValueOrDefault(incidentId);
            if (group?.Decision == VolunteerDecision.TurnAway) continue; // word gets round: no more come

            // More turn up the longer the incident runs, up to a crowd.
            var next = _volunteersNext.GetValueOrDefault(incidentId);
            if (context.SimTime >= next && (group?.Count ?? 0) < 80)
            {
                var random = SimRandom.For(incidentId, 60 + (group?.Count ?? 0));
                var arriving = group is null ? 8 + random.Next(8) : 4 + random.Next(7);
                _volunteersNext[incidentId] = context.SimTime + TimeSpan.FromMinutes(5);
                var at = GeoMath.Destination(facts.Location, 200, 150);
                context.EmitPerceived(new VolunteersArrived(incidentId, arriving, at), EventSources.Comms);
                if (group is null)
                    context.EmitPerceived(new ReportReceived(Guid.NewGuid(), incidentId, ReportSource.FieldUnit, "Crews at the cordon",
                        $"About {arriving} members of the public at the cordon at {facts.Place} wanting to help. Some have first-aid training. Who's taking them?",
                        Confidence.High, VerificationStatus.Reported, at, 50), EventSources.Comms);
            }

            // Nobody in charge of them: some go in on their own, and near the hazard some get hurt.
            if (group is not { Decision: null } waiting || context.SimTime - waiting.ArrivedAt < SelfDeployAfter) continue;
            if (context.SimTime < _selfDeployNext.GetValueOrDefault(incidentId)) continue;
            _selfDeployNext[incidentId] = context.SimTime + TimeSpan.FromMinutes(5);
            var roll = SimRandom.For(incidentId, 70 + waiting.SelfDeployed);
            var going = Math.Min(waiting.Count - waiting.SelfDeployed, 3 + roll.Next(4));
            if (going <= 0) continue;
            context.EmitPerceived(new VolunteersSelfDeployed(incidentId, going), EventSources.Comms);
            context.EmitPerceived(new ReportReceived(Guid.NewGuid(), incidentId, ReportSource.FieldUnit, "Crews at the cordon",
                $"{going} of the volunteers have gone past the cordon towards {facts.Place} on their own. We can't stop them and work.",
                Confidence.High, VerificationStatus.Reported, waiting.Location, 50), EventSources.Comms);

            var danger = world.Hazards.Values.Any(h => !h.Ended && GeoMath.DistanceMeters(h.Origin, facts.Location) < 400);
            for (var i = 0; i < going && danger; i++)
            {
                if (roll.NextDouble() >= 0.2) continue;
                var where = GeoMath.Destination(facts.Location, roll.NextDouble() * 360, 40 + roll.NextDouble() * 80);
                var triage = roll.NextDouble() < 0.3 ? Triage.Immediate : Triage.Urgent;
                const string cause = "hurt helping inside the cordon without protective equipment";
                var injured = new CasualtyInjured(Guid.NewGuid(), incidentId, where, triage, cause, "a volunteer");
                world.Casualties.TryAdd(injured.CasualtyId, new WorldCasualty
                {
                    Id = injured.CasualtyId, IncidentId = incidentId, Location = where, Triage = triage, State = CasualtyState.AwaitingTreatment,
                    Cause = cause, InjuredAt = context.SimTime,
                });
                context.EmitTruth(injured);
            }
        }
    }
}
