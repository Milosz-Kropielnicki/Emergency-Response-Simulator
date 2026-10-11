using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Systems;

namespace Emergency_Response_Simulator.Simulation.Aar;

/// <summary>
/// Political pressure (Phase 11 M5): how command dealt with the politicians who rang. Returning a call is a decision,
/// timed from the call; the findings say how many were returned, how quickly, and what silence cost.
/// </summary>
public sealed partial class AarAnalyzer
{
    private readonly Dictionary<Guid, (DateTimeOffset At, PoliticalCallReceived Call)> _politicalCalls = [];
    private readonly Dictionary<Guid, DateTimeOffset> _politicalAnswers = [];
    private readonly List<(DateTimeOffset At, PoliticalFallout Fallout)> _fallout = [];

    private void TrackPolitical(SimEvent simEvent)
    {
        switch (simEvent.Payload)
        {
            case PoliticalCallReceived e:
                _politicalCalls[e.CallId] = (simEvent.SimTime, e);
                break;
            case PoliticalCallAnswered e:
                _politicalAnswers[e.CallId] = simEvent.SimTime;
                break;
            case PoliticalFallout e:
                _fallout.Add((simEvent.SimTime, e));
                break;
        }
    }

    private IEnumerable<AarFinding> PoliticalFindings()
    {
        if (_politicalCalls.Count == 0) yield break;
        var answered = _politicalCalls.Where(c => _politicalAnswers.ContainsKey(c.Key)).ToList();
        var times = answered.Select(c => _politicalAnswers[c.Key] - c.Value.At).ToList();
        var prompt = times.Count(t => t <= PoliticalPressureSystem.AnswerWindow);
        var summary = $"Political calls: {answered.Count} of {_politicalCalls.Count} returned" +
                      (times.Count > 0 ? $", in {Duration(TimeSpan.FromSeconds(times.Average(t => t.TotalSeconds)))} on average" : "") + ".";
        yield return new AarFinding(prompt == _politicalCalls.Count, summary);
        foreach (var (at, fallout) in _fallout)
        {
            var who = _politicalCalls.TryGetValue(fallout.CallId, out var call) ? call.Call.Caller : "A politician";
            yield return new AarFinding(false, $"{Clock(at)} {who}'s call went unanswered: {LowerFirst(fallout.What)}.");
        }
    }

    private static string LowerFirst(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
