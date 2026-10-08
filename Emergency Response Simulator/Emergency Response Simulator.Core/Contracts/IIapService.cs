using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Core.Contracts;

/// <summary>
/// Incident Action Plan builder: "What should we do?" (Design Document §8).
/// Implemented in Phase 5.
/// </summary>
public interface IIapService
{
    Task<IReadOnlyList<OperationalPeriod>> GetOperationalPeriodsAsync(
        Guid incidentId, CancellationToken cancellationToken = default);

    Task<OperationalPeriod> StartOperationalPeriodAsync(
        Guid incidentId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default);

    /// <summary>All versions of every plan for the incident, newest first.</summary>
    Task<IReadOnlyList<IncidentActionPlan>> GetPlansAsync(
        Guid incidentId, CancellationToken cancellationToken = default);

    /// <summary>The approved plan for the current operational period, if any.</summary>
    Task<IncidentActionPlan?> GetCurrentPlanAsync(
        Guid incidentId, CancellationToken cancellationToken = default);

    /// <summary>Starts a new draft, copying the previous version's content when there is one.</summary>
    Task<IncidentActionPlan> CreateDraftAsync(
        Guid incidentId, Guid operationalPeriodId, Guid? preparedBy, CancellationToken cancellationToken = default);

    Task SaveDraftAsync(IncidentActionPlan plan, CancellationToken cancellationToken = default);

    Task<CommandResult> SubmitForApprovalAsync(Guid planId, CancellationToken cancellationToken = default);

    /// <summary>Approves the plan and marks earlier versions for the same period as superseded.</summary>
    Task<CommandResult> ApproveAsync(Guid planId, Guid approvedBy, CancellationToken cancellationToken = default);
}
