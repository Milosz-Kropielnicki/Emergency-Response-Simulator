using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Core.Contracts;

/// <summary>
/// The instructor console (Phase 11 M4): running the scenario live while the trainees work. Everything here is recorded
/// as ground truth, stamped with the instructor, so the review shows it and the trainees don't see it happen.
/// <list type="bullet">
/// <item>Injects: fire one now, hold it, release it, skip it, or fire an ad-hoc inject from the template library.</item>
/// <item>Role-play: say something as a crew, an agency, a caller, a journalist or an official.</item>
/// <item>Take over a role the simulation plays (the Regional Duty Officer, an agency's control room, the media…) and
/// make its decisions by hand; hand it back to let the simulation play it again.</item>
/// <item>Bookmarks for the debrief.</item>
/// </list>
/// Commands that can't be carried out fail with a reason, as C2 commands do.
/// </summary>
public interface IInstructorService
{
    // ---- Injects ----

    Task<IReadOnlyList<InjectInfo>> ListInjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>Fires a scripted inject now, whatever its time or condition.</summary>
    Task<CommandResult> FireInjectAsync(int number, CancellationToken cancellationToken = default);

    Task<CommandResult> HoldInjectAsync(int number, CancellationToken cancellationToken = default);

    Task<CommandResult> ReleaseInjectAsync(int number, CancellationToken cancellationToken = default);

    Task<CommandResult> SkipInjectAsync(int number, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InjectTemplate>> ListTemplatesAsync(CancellationToken cancellationToken = default);

    /// <summary>Fires an inject made from a template; values left out take the template's defaults.</summary>
    Task<CommandResult> FireAdHocAsync(string template, IReadOnlyDictionary<string, string>? values = null, CancellationToken cancellationToken = default);

    // ---- Role-play ----

    Task<CommandResult> RolePlayAsync(RolePlayRequest request, CancellationToken cancellationToken = default);

    // ---- Roles the simulation plays ----

    Task<IReadOnlyList<AiRoleInfo>> ListAiRolesAsync(CancellationToken cancellationToken = default);

    Task<CommandResult> TakeOverAsync(string roleKey, CancellationToken cancellationToken = default);

    /// <summary>Gives the role back to the simulation, from the instructor or from the participant playing it.</summary>
    Task<CommandResult> HandBackAsync(string roleKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts a participant (an agency chief) in charge of an agency's control room (Phase 11 M5): they task its units and
    /// answer for it. It goes back to the simulation if they leave.
    /// </summary>
    Task<CommandResult> GiveToParticipantAsync(string roleKey, Guid participantId, CancellationToken cancellationToken = default);

    /// <summary>Decisions waiting in the roles the instructor has taken over.</summary>
    Task<IReadOnlyList<AiRoleItem>> ListWaitingAsync(CancellationToken cancellationToken = default);

    Task<CommandResult> DecideAsync(AiRoleDecision decision, CancellationToken cancellationToken = default);

    // ---- Debrief ----

    Task<CommandResult> BookmarkAsync(string note, Guid? participantId = null, CancellationToken cancellationToken = default);

    // ---- Exercise control (Phase 12) ----

    /// <summary>
    /// Speaks to the players as the exercise's controller, outside the world: to everyone, or to one participant. Their
    /// screens show it at once; nobody in the simulation hears it, and the review lists it.
    /// </summary>
    Task<CommandResult> SendExerciseMessageAsync(string text, Guid? participantId = null, CancellationToken cancellationToken = default);
}
