using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Aar;

/// <summary>
/// Live assessment (Phase 11 M6): observers' notes and rubric ratings, on the timeline beside what they were about, and
/// in each participant's score alongside how their own decisions rated.
/// </summary>
public sealed partial class AarAnalyzer
{
    private readonly List<(SimEvent Event, AssessmentNoteRecorded Note)> _notes = [];

    private void TrackNote(SimEvent simEvent, AssessmentNoteRecorded note) => _notes.Add((simEvent, note));

    private List<AarNote> Notes()
    {
        var described = _timeline.ToDictionary(t => t.Sequence, t => t.Text);
        return [.. _notes.Select(n => new AarNote(n.Event.SimTime, n.Note.ParticipantId, _people.GetValueOrDefault(n.Note.ParticipantId)?.Name ?? "A participant",
            n.Note.Rating, n.Note.Note, ActorLabel(n.Event), n.Note.AboutSequence,
            n.Note.AboutSequence is { } about ? described.GetValueOrDefault(about) : null))];
    }

    /// <summary>
    /// A participant's observer ratings, and their score combining how their decisions rated with how observers rated
    /// them (an even split when there are both), put back on the rubric.
    /// </summary>
    private (int Competent, int Developing, int NotYet, int? ObserverScore, int? Combined, AssessmentRating? Level) Assessed(Guid participantId, int? decisionScore)
    {
        var ratings = _notes.Where(n => n.Note.ParticipantId == participantId && n.Note.Rating is not null).Select(n => n.Note.Rating!.Value).ToList();
        int? observer = ratings.Count == 0 ? null : (int)Math.Round(ratings.Average(Rubric.Score));
        int? combined = (decisionScore, observer) switch
        {
            ({ } decisions, { } observed) => (int)Math.Round((decisions + observed) / 2.0),
            ({ } decisions, null) => decisions,
            (null, { } observed) => observed,
            _ => null,
        };
        return (ratings.Count(r => r == AssessmentRating.Competent), ratings.Count(r => r == AssessmentRating.Developing),
            ratings.Count(r => r == AssessmentRating.NotYetCompetent), observer, combined, combined is { } score ? Rubric.Level(score) : null);
    }
}
