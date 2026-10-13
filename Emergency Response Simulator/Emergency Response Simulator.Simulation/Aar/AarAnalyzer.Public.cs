using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Aar;

/// <summary>
/// The public, the media and the law in the review (Phase 14).
/// <list type="bullet">
/// <item><b>Documentation</b>: every significant decision (protective zones declared or lifted, approvals, rumours
/// denied, volunteers registered or sent away) should have a decision-log entry, with its reasons, tied to it or
/// written within <see cref="LogWithin"/> of it.</item>
/// <item><b>Liability</b>: what would be hard to defend at an inquiry — an evacuation ordered without the authority to
/// order it, vulnerable people left behind in a zone, looting where nobody was guarding, volunteers hurt after nobody
/// took charge of them, a denial that was untrue, families left without news.</item>
/// <item><b>Findings</b>: how rumours, volunteers and families were handled.</item>
/// </list>
/// </summary>
public sealed partial class AarAnalyzer
{
    /// <summary>A decision-log entry written within this long of a decision documents it.</summary>
    public static readonly TimeSpan LogWithin = TimeSpan.FromMinutes(10);

    /// <summary>A family waiting longer than this for news is a liability.</summary>
    public static readonly TimeSpan FamiliesWaitLimit = TimeSpan.FromMinutes(60);

    private readonly List<(long Sequence, DateTimeOffset At, string Text)> _significant = [];
    private readonly List<(DateTimeOffset At, long? About)> _logEntries = [];
    private readonly Dictionary<Guid, ZoneType> _zoneTypes = [];
    private readonly List<AarLiability> _liabilities = [];
    private readonly List<(DateTimeOffset At, LegalExposure Exposure)> _exposures = [];
    private readonly Dictionary<Guid, DateTimeOffset> _callsAnswered = [];
    private readonly Dictionary<Guid, (DateTimeOffset At, string Missing)> _enquiriesOpen = [];
    private readonly List<(DateTimeOffset At, TimeSpan Waited, string Missing)> _enquiriesTraced = [];
    private readonly Dictionary<Guid, (DateTimeOffset At, string Claim, string Topic)> _rumours = [];
    private readonly Dictionary<Guid, bool> _rumourTruth = [];
    private readonly Dictionary<Guid, DateTimeOffset> _rumoursAnswered = [];
    private readonly HashSet<Guid> _rumoursDenied = [];
    private readonly List<(DateTimeOffset At, int Count)> _volunteersArrived = [];
    private readonly List<(DateTimeOffset At, VolunteerDecision Decision)> _volunteersManaged = [];
    private DateTimeOffset? _centreOpened;

    private void TrackPublic(SimEvent simEvent)
    {
        var at = simEvent.SimTime;
        var decision = simEvent.Visibility == EventVisibility.Perceived && simEvent.Source == EventSources.C2;
        switch (simEvent.Payload)
        {
            // What needed a decision-log entry.
            case ZoneDeclared e:
                _zoneTypes[e.ZoneId] = e.Type;
                if (decision && e.Type is ZoneType.EvacuationZone or ZoneType.ShelterInPlace)
                    _significant.Add((simEvent.Sequence, at, $"{Clock(at)} {MainName(e.Type)} declared ({e.Name})"));
                break;
            case ZoneLifted e when decision && _zoneTypes.GetValueOrDefault(e.ZoneId) is ZoneType.EvacuationZone or ZoneType.ShelterInPlace:
                _significant.Add((simEvent.Sequence, at, $"{Clock(at)} {MainName(_zoneTypes[e.ZoneId])} lifted"));
                break;
            case ApprovalDecided e when decision:
                _significant.Add((simEvent.Sequence, at, $"{Clock(at)} approval {(e.Approved ? "granted" : "refused")}"));
                break;
            case RumourRebutted e when decision:
                _rumoursAnswered.TryAdd(e.RumourId, at);
                _rumoursDenied.Add(e.RumourId);
                _significant.Add((simEvent.Sequence, at, $"{Clock(at)} rumour denied ({_rumours.GetValueOrDefault(e.RumourId).Topic})"));
                break;
            case VolunteersManaged e when decision:
                _volunteersManaged.Add((at, e.Decision));
                _significant.Add((simEvent.Sequence, at, $"{Clock(at)} volunteers {(e.Decision == VolunteerDecision.Register ? "registered" : "turned away")}"));
                break;
            case DecisionLogged e:
                _logEntries.Add((at, e.AboutSequence));
                break;

            // Liabilities.
            case LegalExposure e:
                _exposures.Add((at, e));
                break;
            case PoliticalCallAnswered e:
                _callsAnswered.TryAdd(e.CallId, at);
                break;
            case VulnerableStranded e:
                _liabilities.Add(new(at, "Vulnerable people left behind", $"{e.Count} people who could not leave on their own still in an evacuation zone ({e.Who})."));
                break;
            case LootingOccurred { Policed: false }:
                _liabilities.Add(new(at, "Unguarded evacuation zone", "Homes in an evacuation zone were looted with no gardaí there and no cordon."));
                break;
            case CasualtyInjured { Who: "a volunteer" } e:
                _liabilities.Add(new(at, "Volunteer injured", $"A volunteer nobody had taken charge of was hurt inside the cordon ({e.Cause})."));
                break;
            case CasualtyInjured { Who: { } who } e when who != "a volunteer":
                _liabilities.Add(new(at, "Vulnerable person injured", $"{Capital(who)} was hurt: {e.Cause}."));
                break;
            case DenialExposed e:
                _liabilities.Add(new(at, "Misinformation", $"Command denied something that was true. {e.What}"));
                break;

            // The rest of the public picture.
            case RumourSpreading e:
                _rumours.TryAdd(e.RumourId, (at, e.Claim, e.Topic));
                break;
            case RumourTruth e:
                _rumourTruth[e.RumourId] = e.IsTrue;
                break;
            case PressConferenceHeld e:
                foreach (var (id, rumour) in _rumours.Where(r => e.Statement.Contains(r.Value.Topic, StringComparison.OrdinalIgnoreCase)))
                    _rumoursAnswered.TryAdd(id, at);
                break;
            case VolunteersArrived e:
                _volunteersArrived.Add((at, e.Count));
                break;
            case FamilyAssistanceCentreOpened:
                _centreOpened ??= at;
                break;
            case MissingPersonReported e:
                _enquiriesOpen[e.EnquiryId] = (at, e.Missing);
                break;
            case MissingPersonTraced e when _enquiriesOpen.Remove(e.EnquiryId, out var asked):
                _enquiriesTraced.Add((at, at - asked.At, asked.Missing));
                break;
        }
    }

    private static string MainName(ZoneType type) => type == ZoneType.EvacuationZone ? "evacuation zone" : "shelter-in-place zone";

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>
    /// Which significant decisions the log covers: an entry tied to the decision, or the first unclaimed entry written
    /// within <see cref="LogWithin"/> after it.
    /// </summary>
    private AarDocumentation Documentation()
    {
        var unclaimed = _logEntries.Where(l => l.About is null).Select(l => l.At).Order().ToList();
        var tied = _logEntries.Where(l => l.About is not null).Select(l => l.About!.Value).ToHashSet();
        var undocumented = new List<string>();
        var documented = 0;
        foreach (var (sequence, at, text) in _significant.OrderBy(s => s.At))
        {
            if (tied.Contains(sequence))
            {
                documented++;
                continue;
            }
            var entry = unclaimed.FindIndex(l => l >= at && l - at <= LogWithin);
            if (entry >= 0)
            {
                unclaimed.RemoveAt(entry);
                documented++;
            }
            else undocumented.Add(text);
        }
        return new AarDocumentation(_significant.Count, documented, undocumented);
    }

    private List<AarLiability> Liabilities(DateTimeOffset end)
    {
        var all = new List<AarLiability>(_liabilities);
        foreach (var (at, exposure) in _exposures)
        {
            // Consulting as soon as the question was raised puts it right; later, it doesn't.
            var answered = exposure.SubjectId is { } call && _callsAnswered.TryGetValue(call, out var when) && when - at <= LogWithin;
            all.Add(new(at, exposure.Kind, exposure.Detail + (answered ? " Command consulted them as soon as they rang." : ""), answered));
        }
        foreach (var (at, waited, missing) in _enquiriesTraced.Where(t => t.Waited > FamiliesWaitLimit))
            all.Add(new(at, "Families left without news", $"The family of {missing} waited {Duration(waited)} to be told what had happened."));
        foreach (var (_, (at, missing)) in _enquiriesOpen.Where(o => end - o.Value.At > FamiliesWaitLimit))
            all.Add(new(at, "Families left without news", $"The family of {missing} was never told what had happened."));
        return [.. all.OrderBy(l => l.At)];
    }

    private IEnumerable<AarFinding> PublicFindings()
    {
        var log = Documentation();
        if (log.Significant > 0)
            yield return new AarFinding(log.Percent >= 75,
                $"Decision log: {log.Documented} of {log.Significant} significant decisions documented with their reasons ({log.Percent}%)" +
                (log.Undocumented.Count > 0 ? $"; not logged: {string.Join("; ", log.Undocumented.Take(4))}." : "."));

        foreach (var (id, (at, claim, topic)) in _rumours)
        {
            var isTrue = _rumourTruth.GetValueOrDefault(id);
            if (_rumoursAnswered.TryGetValue(id, out var answered))
            {
                yield return isTrue && _rumoursDenied.Contains(id)
                    ? new AarFinding(false, $"{Clock(answered)} Denied a rumour that was true ({topic}): \"{claim}\".")
                    : new AarFinding(true, $"{Clock(answered)} Answered a {(isTrue ? "true" : "false")} rumour ({topic}) {Duration(answered - at)} after it surfaced.");
            }
            else if (!isTrue)
                yield return new AarFinding(false, $"{Clock(at)} A false rumour ({topic}) was never answered: \"{claim}\".");
        }

        if (_volunteersArrived.Count > 0)
        {
            var first = _volunteersArrived[0].At;
            yield return _volunteersManaged.Count > 0 && _volunteersManaged[0] is var managed
                ? new AarFinding(managed.At - first <= TimeSpan.FromMinutes(10),
                    $"Spontaneous volunteers {(managed.Decision == VolunteerDecision.Register ? "registered" : "turned away")} {Duration(managed.At - first)} after they arrived.")
                : new AarFinding(false, "Spontaneous volunteers arrived and nobody took charge of them.");
        }

        if (_enquiriesTraced.Count + _enquiriesOpen.Count > 0)
            yield return _centreOpened is { } opened
                ? new AarFinding(_enquiriesTraced.All(t => t.Waited <= FamiliesWaitLimit),
                    $"Family assistance centre opened at {Clock(opened)}; {_enquiriesTraced.Count} enquiries traced" +
                    (_enquiriesTraced.Count > 0 ? $", in {Duration(TimeSpan.FromSeconds(_enquiriesTraced.Average(t => t.Waited.TotalSeconds)))} on average" : "") + ".")
                : new AarFinding(false, $"{_enquiriesOpen.Count} families asked about relatives and no family assistance centre was opened.");
    }
}
