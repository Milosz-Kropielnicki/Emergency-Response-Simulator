using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Systems;

namespace Emergency_Response_Simulator.Simulation.Aar;

/// <summary>
/// The public and the politicians in depth (Phase 12): where public confidence ended, its lowest point and what pulled it
/// down most; and how command answered requests to visit the scene.
/// </summary>
public sealed partial class AarAnalyzer
{
    private readonly List<(DateTimeOffset At, PublicConfidenceChanged Change)> _confidence = [];
    private readonly List<(DateTimeOffset At, PoliticalVisitDecided Visit)> _visits = [];

    private void TrackMedia(SimEvent simEvent)
    {
        switch (simEvent.Payload)
        {
            case PublicConfidenceChanged e:
                _confidence.Add((simEvent.SimTime, e));
                break;
            case PoliticalVisitDecided e:
                _visits.Add((simEvent.SimTime, e));
                break;
        }
    }

    private IEnumerable<AarFinding> MediaFindings()
    {
        if (_confidence.Count > 0)
        {
            var end = _confidence[^1].Change.Confidence;
            var lowest = _confidence.MinBy(c => c.Change.Confidence);
            yield return new AarFinding(end >= MediaCoverageSystem.Start,
                $"Public confidence ended at {end} of 100 ({Tone(end)}), from {MediaCoverageSystem.Start} at the start" +
                (lowest.Change.Confidence < end ? $"; lowest {lowest.Change.Confidence} at {Clock(lowest.At)}." : "."));
            foreach (var (at, drop) in _confidence.Where(c => c.Change.Change <= -8).OrderBy(c => c.Change.Change).Take(3).OrderBy(c => c.At))
                yield return new AarFinding(false, $"{Clock(at)} Public confidence {drop.Change}: {LowerFirst(drop.Reason)}.");
        }

        foreach (var (at, visit) in _visits)
        {
            if (visit.Approved && visit.HazardLive)
                yield return new AarFinding(false, $"{Clock(at)} Approved {visit.Visitor}'s visit to the scene while the hazard was live: a VIP party inside the cordon, and an officer taken off the incident to escort them.");
            else if (!visit.Approved && visit.HazardLive)
                yield return new AarFinding(true, $"{Clock(at)} Put off {visit.Visitor}'s visit until the scene was safe.");
            else if (visit.Approved)
                yield return new AarFinding(true, $"{Clock(at)} Hosted {visit.Visitor}'s visit once the scene was safe: good for public confidence.");
        }
    }

    private static string Tone(int confidence) => MediaCoverageSystem.Tone(confidence) switch
    {
        MediaTone.Supportive => "coverage supportive",
        MediaTone.Mixed => "coverage mixed",
        _ => "coverage critical",
    };
}
