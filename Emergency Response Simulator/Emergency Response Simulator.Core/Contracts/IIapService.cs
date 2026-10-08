using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Core.Contracts;

/// <summary>
/// Incident Action Plan builder: "What should we do?" (Design Document §8). Plans are versioned per
/// operational period and go Draft → Pending approval → Approved (or back to Draft when returned).
/// Every step is an event, so plans appear in the history, replay and AAR. Read plans from
/// <see cref="ICopService.ActionPlans"/>.
/// </summary>
public interface IIapService
{
    /// <summary>
    /// Opens the incident's next operational period; <see cref="CommandResult.EntityId"/> is its id.
    /// Starting before the previous period's planned end cuts that period short.
    /// </summary>
    Task<CommandResult> StartOperationalPeriodAsync(
        Guid incidentId, DateTimeOffset start, DateTimeOffset end, string? focus = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// A new draft version for a period; <see cref="CommandResult.EntityId"/> is the plan id. Copies the period's
    /// latest version; failing that, carries the previous period's plan forward (dropping achieved objectives);
    /// failing that, starts from the current COP.
    /// </summary>
    Task<CommandResult> CreateDraftAsync(Guid periodId, string? preparedBy = null, CancellationToken cancellationToken = default);

    Task<CommandResult> SaveDraftAsync(Guid planId, IapContent content, CancellationToken cancellationToken = default);

    /// <summary>Sends the draft to the Incident Commander. Refused while the compliance check finds errors.</summary>
    Task<CommandResult> SubmitAsync(Guid planId, string submittedBy, CancellationToken cancellationToken = default);

    /// <summary>Puts the plan in force; any earlier approved version for the period is superseded.</summary>
    Task<CommandResult> ApproveAsync(Guid planId, string approvedBy, CancellationToken cancellationToken = default);

    /// <summary>Sends the plan back to the planner with comments.</summary>
    Task<CommandResult> ReturnAsync(Guid planId, string returnedBy, string comments, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pushes an approved plan to the COP: staffs positions, forms groups, dispatches and places units, and
    /// orders each unit whose assignment changed since the last briefing. <see cref="CommandResult.Detail"/>
    /// summarises what was done.
    /// </summary>
    Task<CommandResult> BriefAsync(Guid planId, CancellationToken cancellationToken = default);

    Task<CommandResult> SetObjectiveStatusAsync(
        Guid incidentId, Guid objectiveId, ObjectiveStatus status, CancellationToken cancellationToken = default);

    /// <summary>
    /// A copy of <paramref name="content"/> with the chosen sections filled from what the COP shows now:
    /// the situation, the command structure, committed units, channels, nearest hospitals and reported hazards.
    /// Existing entries are kept; only what is missing or out of date is added.
    /// </summary>
    Task<IapContent> PullFromCopAsync(
        Guid incidentId, IapContent content, IapPullParts parts = IapPullParts.All, CancellationToken cancellationToken = default);
}

[Flags]
public enum IapPullParts
{
    None = 0,
    Situation = 1,
    Organisation = 2,
    Assignments = 4,
    Communications = 8,
    Medical = 16,
    Safety = 32,
    All = Situation | Organisation | Assignments | Communications | Medical | Safety,
}
