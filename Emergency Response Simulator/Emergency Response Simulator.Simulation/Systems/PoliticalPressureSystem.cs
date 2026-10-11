using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Political pressure (Phase 11 M5): people in public life ring command about a serious incident, as they do.
/// <list type="bullet">
/// <item><b>Who</b>: the local TD first, then the council's chief executive, then the minister's office. They ask for the
/// Incident Commander, or the PIO once command has one.</item>
/// <item><b>How often</b>: by what command has said about the incident: sooner and more often for a Critical incident than
/// a Medium one (Low ones don't make the news), more so with casualties or people told to evacuate or shelter.</item>
/// <item><b>Ignored</b>: a call nobody returns within <see cref="AnswerWindow"/> moves the pressure up: the next office
/// rings, knowing the last call wasn't returned. Ignore the minister's office and the minister goes public: a media
/// report and an alert, and command loses the chance to shape the story.</item>
/// <item><b>Answered</b>: the pressure eases a step and the next call comes later.</item>
/// </list>
/// The instructor can take the politicians over (<see cref="Instructor.AiRoles.Politicians"/>): then nobody rings
/// unless the instructor plays them, and silence has no consequences they don't choose.
/// </summary>
public sealed class PoliticalPressureSystem : ISimulationSystem
{
    public static readonly TimeSpan AnswerWindow = TimeSpan.FromMinutes(5);

    /// <summary>After the minister has gone public, the politicians leave command alone for this long.</summary>
    public static readonly TimeSpan QuietAfterFallout = TimeSpan.FromMinutes(20);

    public const string Td = "Deputy Ciara Nolan TD";
    public const string ChiefExecutive = "Owen Keane, Chief Executive, Dublin City Council";
    public const string MinistersOffice = "Private Secretary, Office of the Minister for Housing, Local Government and Heritage";

    private sealed class Pressure
    {
        public int Level { get; set; } = 1;
        public DateTimeOffset? NextCallAt { get; set; }
        public DateTimeOffset QuietUntil { get; set; }
    }

    private readonly Dictionary<Guid, Pressure> _incidents = [];
    private readonly HashSet<Guid> _eased = [];

    public int Order => 21;

    public void Update(SimulationContext context)
    {
        var world = context.World;
        var byInstructor = world.PlayedByHuman(Instructor.AiRoles.Politicians);
        Answered(context, byInstructor);
        Ignored(context, byInstructor);
        if (byInstructor) return;

        foreach (var (incidentId, facts) in world.Interagency.Incidents)
        {
            if (facts.Closed || facts.Priority < IncidentPriority.Medium) continue;
            var pressure = For(incidentId);
            if (pressure.NextCallAt is null)
            {
                var first = facts.CreatedAt + Scaled(world, incidentId, facts, facts.Priority switch
                {
                    IncidentPriority.Critical => TimeSpan.FromMinutes(4),
                    IncidentPriority.High => TimeSpan.FromMinutes(7),
                    _ => TimeSpan.FromMinutes(12),
                });
                pressure.NextCallAt = first > context.SimTime ? first : context.SimTime;
                continue;
            }
            if (context.SimTime < pressure.NextCallAt || context.SimTime < pressure.QuietUntil) continue;
            // One call at a time: while one is waiting, its fate decides what happens next.
            if (world.PoliticalCalls.Values.Any(c => c.Call.IncidentId == incidentId && c.AnsweredAt is null && !c.Escalated)) continue;

            Ring(context, incidentId, facts, (PoliticalOffice)pressure.Level, chasing: null);
            pressure.NextCallAt = context.SimTime + Interval(world, incidentId, facts);
        }
    }

    private Pressure For(Guid incidentId) => _incidents.TryGetValue(incidentId, out var pressure) ? pressure : _incidents[incidentId] = new Pressure();

    /// <summary>A call returned eases the pressure a step, and the next one comes a full interval later.</summary>
    private void Answered(SimulationContext context, bool byInstructor)
    {
        var world = context.World;
        foreach (var call in world.PoliticalCalls.Values.Where(c => c.AnsweredAt is not null && _eased.Add(c.Call.CallId)))
        {
            if (call.Call.IncidentId is not { } incidentId || !world.Interagency.Incidents.TryGetValue(incidentId, out var facts)) continue;
            var pressure = For(incidentId);
            pressure.Level = Math.Max(1, call.Call.Pressure - 1);
            pressure.NextCallAt = call.AnsweredAt!.Value + Interval(world, incidentId, facts);
            if (!byInstructor)
                CommsNet.Chat(context, call.Call.Caller, $"{call.Call.Caller}: Thanks for coming back to me. Keep me posted.", null);
        }
    }

    /// <summary>A call left unanswered too long: the next office up rings, or at the top the minister goes public.</summary>
    private void Ignored(SimulationContext context, bool byInstructor)
    {
        var world = context.World;
        foreach (var call in world.PoliticalCalls.Values.Where(c => c.AnsweredAt is null && !c.Escalated && context.SimTime - c.ReceivedAt >= AnswerWindow).ToList())
        {
            call.Escalated = true;
            if (byInstructor) continue; // the instructor decides what silence costs
            var incidentId = call.Call.IncidentId;
            var facts = incidentId is { } id ? world.Interagency.Incidents.GetValueOrDefault(id) : null;
            var pressure = incidentId is { } key ? For(key) : new Pressure();

            if (call.Call.Pressure >= 3)
            {
                pressure.Level = 3;
                pressure.QuietUntil = context.SimTime + QuietAfterFallout;
                var place = facts?.Place ?? "the incident";
                context.EmitTruth(new PoliticalFallout(call.Call.CallId, incidentId, 3, "The minister went public without a briefing"));
                context.EmitPerceived(new ReportReceived(Guid.NewGuid(), incidentId, ReportSource.Media, "RTÉ News",
                    $"The Minister says she has \"not been briefed\" about {place} and wants answers from the emergency services tonight.",
                    Confidence.High, VerificationStatus.Confirmed, facts?.Location, facts is null ? null : 500), EventSources.Comms);
                context.EmitPerceived(new AlertRaised(Guid.NewGuid(), AlertCategory.Critical, AlertSeverity.Warning, "Minister: \"not been briefed\"",
                    $"The minister's office went to the media after its call about {place} wasn't returned.", incidentId, null), EventSources.Comms);
                continue;
            }

            pressure.Level = call.Call.Pressure + 1;
            var next = (PoliticalOffice)pressure.Level;
            context.EmitTruth(new PoliticalFallout(call.Call.CallId, incidentId, pressure.Level, $"{Caller(next)} rings, chasing {call.Call.Caller}'s call"));
            if (incidentId is { } chased && facts is not null)
            {
                Ring(context, chased, facts, next, chasing: call.Call.Caller);
                pressure.NextCallAt = context.SimTime + Interval(world, chased, facts);
            }
        }
    }

    private static void Ring(SimulationContext context, Guid incidentId, IncidentFacts facts, PoliticalOffice office, string? chasing)
    {
        var caller = Caller(office);
        var place = facts.Place;
        var message = office switch
        {
            PoliticalOffice.LocalTd => $"Constituents near {place} are ringing me. What's going on, is it safe for them, and when will the roads reopen?",
            PoliticalOffice.CouncilChiefExecutive when chasing is not null =>
                $"Deputy Nolan tells me nobody has returned her call about {place}. I need a briefing for the council's crisis team now: casualties, evacuation, and what you need from us.",
            PoliticalOffice.CouncilChiefExecutive => $"I need a briefing on {place} for the council's crisis management team: casualties, evacuation, and what you need from us.",
            _ when chasing is not null =>
                $"The Minister has heard nothing from you about {place}, and the council's calls haven't been returned. She will make a statement herself within the hour unless she is briefed.",
            _ => $"The Minister wants an update on {place} before the six o'clock news: casualties, the risk to the public, and whether you have what you need.",
        };
        var to = To(context.World, incidentId);
        CommsNet.Chat(context, caller, $"{caller} for the {to}: {message}",
            new PoliticalCallReceived(Guid.NewGuid(), incidentId, office, caller, message, (int)office, to));
    }

    public static string Caller(PoliticalOffice office) => office switch
    {
        PoliticalOffice.LocalTd => Td,
        PoliticalOffice.CouncilChiefExecutive => ChiefExecutive,
        _ => MinistersOffice,
    };

    /// <summary>The PIO once command has one, else the Incident Commander.</summary>
    public static string To(WorldState world, Guid? incidentId) =>
        incidentId is { } id && world.Commands.TryGetValue(id, out var command) && command.IsStaffed(IcsRole.PublicInformation)
            ? "Public Information Officer"
            : "Incident Commander";

    /// <summary>Time between calls: by priority, shorter with casualties or people told to evacuate or shelter.</summary>
    private static TimeSpan Interval(WorldState world, Guid incidentId, IncidentFacts facts) => Scaled(world, incidentId, facts, facts.Priority switch
    {
        IncidentPriority.Critical => TimeSpan.FromMinutes(10),
        IncidentPriority.High => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromMinutes(25),
    });

    private static TimeSpan Scaled(WorldState world, Guid incidentId, IncidentFacts facts, TimeSpan basis)
    {
        var factor = 1.0;
        if (facts.Casualties >= 5) factor *= 0.7;
        if (world.Interagency.IncidentZones.TryGetValue(incidentId, out var zones)
            && world.ProtectiveActions.Values.Any(a => zones.Any(z => z.Intersects(a.Area))))
            factor *= 0.8;
        return basis * factor;
    }

    /// <summary>The office an instructor is playing, from who they say they are.</summary>
    public static PoliticalOffice OfficeOf(string caller) =>
        caller.Contains("minister", StringComparison.OrdinalIgnoreCase) || caller.Contains("taoiseach", StringComparison.OrdinalIgnoreCase)
            ? PoliticalOffice.MinistersOffice
            : caller.Contains("council", StringComparison.OrdinalIgnoreCase) || caller.Contains("chief executive", StringComparison.OrdinalIgnoreCase)
                ? PoliticalOffice.CouncilChiefExecutive
                : PoliticalOffice.LocalTd;
}
