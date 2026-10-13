using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// The media's coverage of the response, and the public confidence behind it (Phase 12). Ground truth tracks how far the
/// public trusts the response (0–100, starting at <see cref="Start"/>); command sees only what the coverage says.
/// <list type="bullet">
/// <item><b>Raised by</b> command's public statements, more for clear protective advice, and less for a stream of them;
/// and a minister's visit to a scene that is safe to visit.</item>
/// <item><b>Lowered by</b> journalists left waiting until they run the story anyway, people told to evacuate or shelter
/// with no public advice from command, another agency contradicting the official line, a minister going public because
/// nobody briefed her, and a VIP visit while the hazard is live.</item>
/// <item><b>Coverage</b>: while a newsworthy incident runs, an outlet reports on the response every
/// <see cref="StoryInterval"/>, and at once when the tone turns, supportive, mixed or critical as confidence stands.
/// It reaches command as a media report, as the PIO's media monitoring would.</item>
/// </list>
/// The instructor can take the media over (<see cref="Instructor.AiRoles.Media"/>): then no coverage runs unless they
/// play a journalist, though confidence is still tracked for the review.
/// </summary>
public sealed class MediaCoverageSystem : ISimulationSystem
{
    public const int Start = 60;

    public static readonly TimeSpan StoryInterval = TimeSpan.FromMinutes(12);

    /// <summary>An enquiry left this long is a story run without command's side.</summary>
    public static readonly TimeSpan EnquiryRunsAfter = TimeSpan.FromMinutes(10);

    /// <summary>A protective action with no public advice from command this long after it.</summary>
    public static readonly TimeSpan AdviceDueAfter = TimeSpan.FromMinutes(10);

    private readonly HashSet<Guid> _counted = [];
    private DateTimeOffset? _lastStatement;
    private DateTimeOffset? _nextStory;
    private MediaTone? _lastTone;
    private int _stories;

    public int Order => 22;

    public static MediaTone Tone(int confidence) => confidence >= 65 ? MediaTone.Supportive : confidence >= 40 ? MediaTone.Mixed : MediaTone.Critical;

    public void Update(SimulationContext context)
    {
        var world = context.World;
        var ia = world.Interagency;
        var confidence = world.PublicConfidence;
        void Change(int by, string why)
        {
            var next = Math.Clamp(confidence + by, 0, 100);
            if (next == confidence) return;
            context.EmitTruth(new PublicConfidenceChanged(next, next - confidence, why));
            confidence = next;
        }

        foreach (var (statement, at) in ia.Statements)
        {
            if (!_counted.Add(statement.StatementId)) continue;
            if (statement.Conflicting)
            {
                Change(-8, $"{statement.Issuer} contradicted the official line");
                continue;
            }
            var advice = Interagency.IsWarning(statement.Text);
            // A steady flow of updates helps; a flood of them adds little.
            var frequent = _lastStatement is { } last && at - last < TimeSpan.FromMinutes(5);
            _lastStatement = at;
            Change(frequent ? 2 : advice ? 8 : 5, advice ? $"{statement.Issuer} gave clear protective advice" : $"{statement.Issuer} updated the public");
        }

        foreach (var (id, enquiry) in ia.Enquiries)
            if (context.SimTime - enquiry.At >= EnquiryRunsAfter && _counted.Add(id))
                Change(-4, $"{enquiry.Enquiry.Outlet} ran the story without a word from command");

        foreach (var action in world.ProtectiveActions.Values)
        {
            if (action.Type is not (ZoneType.EvacuationZone or ZoneType.ShelterInPlace) || context.SimTime - action.DeclaredAt < AdviceDueAfter) continue;
            var advised = ia.Statements.Any(s => !s.Statement.Conflicting && s.At >= action.DeclaredAt - TimeSpan.FromMinutes(5) && Interagency.IsWarning(s.Statement.Text));
            if (!advised && _counted.Add(action.ZoneId))
                Change(-8, $"People were told to {(action.Type == ZoneType.ShelterInPlace ? "shelter" : "evacuate")} with no public advice from command");
        }

        if (!world.PlayedByHuman(Instructor.AiRoles.Politicians))
            foreach (var call in world.PoliticalCalls.Values.Where(c => c.Call.Pressure >= 3 && c.Escalated && c.AnsweredAt is null))
                if (_counted.Add(call.Call.CallId))
                    Change(-10, "The minister went public: she had not been briefed");

        foreach (var visit in world.PoliticalVisits.Where(v => v.Approved))
            if (_counted.Add(visit.ApprovalId))
                Change(visit.HazardLive ? -6 : 3, visit.HazardLive
                    ? $"{visit.Visitor}'s visit took officers away while the hazard was live"
                    : $"{visit.Visitor} visited the scene and thanked the crews");

        PublicPressures(context, Change);

        Coverage(context, confidence);
    }

    /// <summary>A false rumour that reaches this many people (thousands) unanswered costs trust.</summary>
    public const int RumourDamageReach = 50;

    /// <summary>A family left this long without news, with no family assistance centre, says so publicly.</summary>
    public static readonly TimeSpan FamiliesWaitFor = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The pressures from outside the response (Phase 14): rumours and how command answered them, press conferences,
    /// looting, families waiting for news, and community leaders nobody called back.
    /// </summary>
    private void PublicPressures(SimulationContext context, Action<int, string> change)
    {
        var world = context.World;
        var pub = world.Public;

        foreach (var rumour in pub.Rumours.Values.Where(r => r.IsTrue == false))
        {
            if (rumour.Spreading && rumour.ReachThousands >= RumourDamageReach && _counted.Add(Salt(rumour.Id, 1)))
                change(-5, $"A false rumour ({rumour.Topic}) reached {rumour.ReachThousands}k people unanswered");
            if (!rumour.Spreading && _counted.Add(Salt(rumour.Id, 2)))
                change(3, $"Command put the record straight on a false rumour ({rumour.Topic})");
        }
        foreach (var rumour in pub.Rumours.Values.Where(r => r.DenialExposed))
            if (_counted.Add(Salt(rumour.Id, 3)))
                change(-15, $"Command denied something that turned out to be true ({rumour.Topic})");

        for (var i = 0; i < pub.PressConferences.Count; i++)
        {
            if (!_counted.Add(Salt(Guid.Empty, 100 + i))) continue;
            var recent = i > 0 && pub.PressConferences[i] - pub.PressConferences[i - 1] < TimeSpan.FromMinutes(20);
            change(recent ? 3 : 10, recent ? "Another press conference" : "A press conference put the authorities in front of the public");
        }

        for (var i = 0; i < pub.Looting.Count; i++)
            if (!pub.Looting[i].Policed && _counted.Add(Salt(pub.Looting[i].ZoneId, 200 + i)))
                change(-6, "Homes looted in an evacuation zone with no gardaí there");

        if (pub.AssistanceCentre is null)
            foreach (var (id, (at, _)) in pub.Enquiries)
                if (context.SimTime - at >= FamiliesWaitFor && _counted.Add(Salt(id, 4)))
                    change(-2, "A family waited half an hour for news, with nowhere to go for it");

        foreach (var call in world.PoliticalCalls.Values.Where(c => c.Call.Office == PoliticalOffice.CommunityLeader && c.Escalated && c.AnsweredAt is null))
            if (!world.PlayedByHuman(Instructor.AiRoles.Politicians) && _counted.Add(Salt(call.Call.CallId, 5)))
                change(-4, $"{call.Call.Caller} told the media nobody had spoken to them");
    }

    /// <summary>A distinct key per thing counted, so each costs or earns once.</summary>
    private static Guid Salt(Guid id, int kind) => SimRandom.Derive(id, 7000 + kind);

    private void Coverage(SimulationContext context, int confidence)
    {
        var world = context.World;
        var story = world.Interagency.Incidents.Where(i => !i.Value.Closed && i.Value.Priority >= IncidentPriority.Medium)
            .OrderByDescending(i => i.Value.Priority).ThenBy(i => i.Value.CreatedAt).Select(i => (Id: (Guid?)i.Key, Facts: i.Value)).FirstOrDefault();
        if (story.Facts is not { } facts)
        {
            _nextStory = null;
            return;
        }
        _nextStory ??= facts.CreatedAt + StoryInterval;
        var tone = Tone(confidence);
        var turned = _lastTone is { } before && before != tone;
        if (context.SimTime < _nextStory && !turned) return;
        _nextStory = context.SimTime + StoryInterval;
        _lastTone = tone;
        if (world.PlayedByHuman(Instructor.AiRoles.Media)) return; // the instructor plays the press

        var random = SimRandom.For(story.Id!.Value, 70 + _stories++);
        var outlet = InteragencySystem.Outlets[random.Next(InteragencySystem.Outlets.Length)];
        var place = facts.Place;
        var claim = tone switch
        {
            MediaTone.Supportive => $"Coverage: emergency services at {place} praised for clear, regular information. Residents say they know what to do.",
            MediaTone.Mixed => $"Coverage: questions over the response at {place}. Residents say updates have been patchy and they want to know what happens next.",
            _ => $"Coverage: anger and confusion over {place}. \"Nobody is telling us anything,\" one resident says; callers to radio phone-ins question who is in charge.",
        };
        CommsNet.Chat(context, outlet, $"{outlet}: {claim}", new ReportReceived(Guid.NewGuid(), story.Id, ReportSource.Media, outlet, claim,
            Confidence.Medium, VerificationStatus.Reported, facts.Location, 500), TimeSpan.Zero);
    }
}
