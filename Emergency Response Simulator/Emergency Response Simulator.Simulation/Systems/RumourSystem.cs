using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Rumours and misinformation (Phase 14).
/// <list type="bullet">
/// <item><b>What starts them</b>: a serious incident with little said about it. Some rumours are true (a second
/// explosion, a cloud of gas, looting in the evacuated streets, a hospital turning people away); some are not
/// (contaminated water, a cover-up). Whether each is true is ground truth, decided from the world when it starts; the
/// trainees only ever see the claim and how far it has gone.</item>
/// <item><b>Spreading</b>: unanswered, a rumour reaches more people every few minutes. A false one that has gone far
/// confuses people (fewer follow the official advice) and brings worried 999 calls.</item>
/// <item><b>Answering</b>: a rebuttal or a press conference that speaks to it stops it. But denying something that
/// turns out to be true is misinformation from the authorities: it comes out a little later, and costs far more trust
/// than the rumour did. Check before you deny.</item>
/// </list>
/// </summary>
public sealed class RumourSystem : ISimulationSystem
{
    public static readonly TimeSpan FirstRumourAfter = TimeSpan.FromMinutes(12);
    public static readonly TimeSpan RumourInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan SpreadInterval = TimeSpan.FromMinutes(5);
    public const int MaxRumoursPerIncident = 4;

    /// <summary>A denial of something true comes out this long after it was made.</summary>
    public static readonly TimeSpan DenialExposedAfter = TimeSpan.FromMinutes(15);

    private sealed record Template(string Topic, Func<string, string> Claim, Func<WorldState, bool> True);

    /// <summary>In the order they come up for an incident: a mix of the true and the false.</summary>
    private static readonly Template[] Library =
    [
        new("explosion", place => $"Hearing there's been a SECOND explosion near {place}, more on the way, get out now!!",
            world => world.Incidents.Count >= 2),
        new("water", place => $"Don't drink the tap water anywhere near {place}, it's been contaminated. Pass it on",
            _ => false),
        new("gas", place => $"Toxic gas heading for the schools near {place} and they're keeping it quiet",
            world => world.Hazards.Values.Any(h => !h.Ended && h.Kind == HazardKind.Plume)),
        new("dead", place => $"Dozens dead at {place}, the real number is being hidden",
            world => world.Casualties.Values.Count(c => !c.Alive) >= 12),
        new("hospital", _ => "St James's is turning people away, don't bother going there",
            world => world.Hospitals.Values.Any(h => h.OnDiversion)),
        new("terror", place => $"{place} was a terror attack, the guards are covering it up",
            _ => false),
    ];

    private const string LootingTopic = "looting";

    private readonly Dictionary<Guid, (DateTimeOffset Next, int Count)> _incidents = [];
    private readonly Dictionary<Guid, DateTimeOffset> _spreadNext = [];
    private readonly Dictionary<Guid, int> _worriedCalls = [];
    private readonly HashSet<Guid> _lootingRumoured = [];
    private readonly HashSet<Guid> _exposed = [];

    public int Order => 18;

    public void Update(SimulationContext context)
    {
        Start(context);
        Spread(context);
        Expose(context);
    }

    /// <summary>Starts a rumour: what people are saying (perceived), and whether it is so (truth).</summary>
    public static void Start(SimulationContext context, Guid? incidentId, string topic, string claim, bool isTrue, int reach = 2)
    {
        var id = Guid.NewGuid();
        context.EmitPerceived(new RumourSpreading(id, incidentId, claim, topic, reach), EventSources.Comms);
        context.EmitTruth(new RumourTruth(id, isTrue));
    }

    private void Start(SimulationContext context)
    {
        var world = context.World;
        foreach (var (incidentId, facts) in world.Interagency.Incidents)
        {
            if (facts.Closed || facts.Priority < IncidentPriority.Medium) continue;
            if (!_incidents.TryGetValue(incidentId, out var state))
            {
                // Silence feeds rumour: with nothing said about it, the first comes sooner.
                _incidents[incidentId] = (facts.CreatedAt + FirstRumourAfter, 0);
                continue;
            }
            if (state.Count >= MaxRumoursPerIncident || context.SimTime < state.Next) continue;
            var said = world.Interagency.Statements.Any(s => !s.Statement.Conflicting && (s.Statement.IncidentId is null || s.Statement.IncidentId == incidentId));
            _incidents[incidentId] = (context.SimTime + (said ? RumourInterval * 1.5 : RumourInterval), state.Count + 1);

            var template = Library[state.Count % Library.Length];
            Start(context, incidentId, template.Topic, template.Claim(facts.Place), template.True(world));
        }

        // Looting nobody stopped gets out, and it is true.
        foreach (var (zoneId, at, policed) in world.Public.Looting)
        {
            if (policed || context.SimTime - at < TimeSpan.FromMinutes(5) || !_lootingRumoured.Add(zoneId)) continue;
            Start(context, null, LootingTopic, "Looters going through the evacuated houses and not a guard in sight. Disgraceful", isTrue: true, reach: 5);
        }
    }

    private void Spread(SimulationContext context)
    {
        var world = context.World;
        foreach (var rumour in world.Public.Rumours.Values.Where(r => r.Spreading))
        {
            var next = _spreadNext.TryGetValue(rumour.Id, out var due) ? due : _spreadNext[rumour.Id] = rumour.StartedAt + SpreadInterval;
            if (context.SimTime < next) continue;
            _spreadNext[rumour.Id] = context.SimTime + SpreadInterval;

            var reach = Math.Min(500, (int)Math.Ceiling(rumour.ReachThousands * 1.6) + 1);
            if (reach != rumour.ReachThousands)
                context.EmitPerceived(new RumourSpreading(rumour.Id, rumour.IncidentId, rumour.Claim, rumour.Topic, reach), EventSources.Comms);

            // A frightening one that has gone a long way brings worried people onto the 999 line.
            if (reach < 20 || rumour.Topic is not ("explosion" or "gas" or "water")) continue;
            var calls = _worriedCalls.GetValueOrDefault(rumour.Id);
            if (calls >= 3) continue;
            _worriedCalls[rumour.Id] = calls + 1;
            var near = rumour.IncidentId is { } incident && world.Interagency.Incidents.TryGetValue(incident, out var facts) ? facts.Location : (GeoPoint?)null;
            if (near is not { } around) continue;
            var random = SimRandom.For(rumour.Id, calls);
            CommsNet.EmergencyCall(context, new PendingCall
            {
                Caller = "999 caller (worried resident)",
                Summary = $"I've seen online that {rumour.Claim.TrimEnd('!', '.').ToLowerInvariant()}. Is it true? What should we do?",
                Location = GeoMath.Destination(around, random.NextDouble() * 360, 600 + random.NextDouble() * 900),
                AccuracyMeters = 800, Patience = TimeSpan.FromMinutes(2),
            });
        }
    }

    /// <summary>A denial of something true does not survive long: footage, a leak, a hospital source.</summary>
    private void Expose(SimulationContext context)
    {
        var world = context.World;
        foreach (var rumour in world.Public.Rumours.Values.Where(r => r is { Denied: true, IsTrue: true, DenialExposed: false }))
        {
            if (rumour.RebuttedAt is not { } denied || context.SimTime - denied < DenialExposedAfter || !_exposed.Add(rumour.Id)) continue;
            var what = $"The authorities denied it, but it was true: {rumour.Claim}";
            context.EmitPerceived(new DenialExposed(rumour.Id, what), EventSources.Comms);
            var near = rumour.IncidentId is { } incident && world.Interagency.Incidents.TryGetValue(incident, out var facts) ? facts.Location : (GeoPoint?)null;
            context.EmitPerceived(new ReportReceived(Guid.NewGuid(), rumour.IncidentId, ReportSource.Media, "RTÉ News",
                $"Footage contradicts the authorities' denial ({rumour.Topic}). Questions over whether the public can trust official information.",
                Confidence.High, VerificationStatus.Confirmed, near, near is null ? null : 500), EventSources.Comms);
            context.EmitPerceived(new AlertRaised(Guid.NewGuid(), AlertCategory.SituationChange, AlertSeverity.Critical, "Denial contradicted",
                $"Command denied a rumour that has turned out to be true ({rumour.Topic}). Correct the record now, and verify before denying anything else.",
                rumour.IncidentId, null), EventSources.Comms);
        }
    }
}
