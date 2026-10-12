using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Core.Contracts;

/// <summary>
/// Live assessment (Phase 11 M6): observers and the instructor note how participants are doing as it happens, with an
/// optional rating on the rubric (Competent / Developing / Not yet competent), tagged to the event the note is about.
/// Notes are ground truth: the trainees never see them during the exercise; the review puts them on the timeline and
/// into each participant's score.
/// </summary>
public interface IAssessmentService
{
    /// <param name="aboutSequence">The event the note is about (e.g. their last command); null for a general note.</param>
    /// <param name="area">The competency area it assesses (Phase 12); null for an overall impression.</param>
    Task<CommandResult> RecordNoteAsync(Guid participantId, string note, AssessmentRating? rating = null, long? aboutSequence = null,
        Competency? area = null, CancellationToken cancellationToken = default);
}
