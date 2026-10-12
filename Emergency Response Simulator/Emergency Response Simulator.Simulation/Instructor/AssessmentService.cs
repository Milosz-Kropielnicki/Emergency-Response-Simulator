using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;

namespace Emergency_Response_Simulator.Simulation.Instructor;

/// <summary>
/// Live assessment (Phase 11 M6): records an assessor's note on a participant as ground truth, stamped with whoever the
/// command context names (the observer or instructor, on the server). Who may record notes is the server's to decide.
/// </summary>
public sealed class AssessmentService(ICopService cop, IEventPublisher publisher) : IAssessmentService
{
    public async Task<CommandResult> RecordNoteAsync(Guid participantId, string note, AssessmentRating? rating = null, long? aboutSequence = null,
        Competency? area = null, CancellationToken cancellationToken = default)
    {
        if (cop.Participants.FirstOrDefault(p => p.Id == participantId) is not { } participant)
            return CommandResult.Fail("That person isn't in the session.");
        if (!Roles.IsPlayer(participant.Role))
            return CommandResult.Fail($"{participant.DisplayName} is {Roles.Name(participant.Role)}: only players are assessed.");
        if (string.IsNullOrWhiteSpace(note) && rating is null)
            return CommandResult.Fail("Write a note, or give a rating.");
        if (aboutSequence is { } about && (about < 1 || about > cop.LastSequence))
            return CommandResult.Fail("That event isn't in the session.");
        if (CommandContext.Ambient.Actor?.ParticipantId == participantId)
            return CommandResult.Fail("Nobody assesses themselves.");

        var recorded = await publisher.PublishAsync(new AssessmentNoteRecorded(Guid.NewGuid(), participantId, rating, note?.Trim() ?? "", aboutSequence, area),
            EventVisibility.Truth, EventSources.Assessment, cancellationToken);
        return CommandResult.Ok(recorded.Sequence);
    }
}
