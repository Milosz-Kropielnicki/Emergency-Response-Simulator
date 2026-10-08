using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Core.Contracts;

/// <summary>
/// Command and Control: "Make it happen" (Design Document §9).
/// Every accepted command is appended to the event stream, so it is visible on the COP and in the AAR.
/// </summary>
public interface IC2Service
{
    Task<CommandResult> DispatchAsync(
        Guid unitId, Guid incidentId, Guid? orderedBy = null, CancellationToken cancellationToken = default);

    Task<CommandResult> CancelDispatchAsync(
        Guid unitId, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>Moves a committed unit to a different incident.</summary>
    Task<CommandResult> ReassignAsync(
        Guid unitId, Guid newIncidentId, Guid? orderedBy = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Additional resources, mutual aid or specialist teams (Design Document §9, §16). Mutual aid and
    /// specialist teams need an outside approval; approved resources arrive and join the roster.
    /// </summary>
    Task<CommandResult> RequestResourcesAsync(
        Guid incidentId, ResourceRequestKind kind, UnitType unitType, int quantity, string? justification = null,
        CancellationToken cancellationToken = default);

    Task<CommandResult> DeclareZoneAsync(
        ZoneType type, string name, IReadOnlyList<GeoPoint> boundary, Guid? incidentId = null,
        CancellationToken cancellationToken = default);

    Task<CommandResult> LiftZoneAsync(Guid zoneId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hot, warm and cold zones as concentric circles around a point (Design Document §6.2).
    /// Radii in metres; each must be larger than the last.
    /// </summary>
    Task<CommandResult> EstablishHazardZonesAsync(
        GeoPoint center, double hotRadiusMeters, double warmRadiusMeters, double coldRadiusMeters,
        Guid? incidentId = null, CancellationToken cancellationToken = default);

    // ---- Incidents (Design Document §6.5) ----

    /// <summary>Opens an incident; <see cref="CommandResult.EntityId"/> is the new incident's id.</summary>
    Task<CommandResult> CreateIncidentAsync(
        IncidentType type, IncidentPriority priority, GeoPoint location, string? address = null, string? name = null,
        Guid? fromReportId = null, CancellationToken cancellationToken = default);

    /// <summary>Changes only the values supplied.</summary>
    Task<CommandResult> UpdateIncidentAsync(
        Guid incidentId, IncidentPriority? priority = null, IncidentStatus? status = null,
        int? casualtiesReported = null, int? casualtiesConfirmed = null, bool? evacuationRequired = null,
        IReadOnlyList<string>? threats = null, CancellationToken cancellationToken = default);

    Task<CommandResult> AssignIncidentCommanderAsync(
        Guid incidentId, string name, Guid? userId = null, CancellationToken cancellationToken = default);

    // ---- Resources ----

    /// <summary>Records a status change, enforcing the allowed transitions in <see cref="UnitStatusRules"/>.</summary>
    Task<CommandResult> UpdateUnitStatusAsync(
        Guid unitId, UnitStatus status, CancellationToken cancellationToken = default);

    // ---- Information (Design Document §6.7, §6.9) ----

    Task<CommandResult> AssessReportAsync(
        Guid reportId, VerificationStatus verification, Confidence confidence, CancellationToken cancellationToken = default);

    Task<CommandResult> LinkReportAsync(Guid reportId, Guid incidentId, CancellationToken cancellationToken = default);

    Task<CommandResult> AcknowledgeAlertAsync(Guid alertId, Guid? userId = null, CancellationToken cancellationToken = default);

    // ---- ICS structure (Design Document §8.1) ----

    Task<CommandResult> AssignIcsPositionAsync(
        Guid incidentId, IcsRole role, string name, CancellationToken cancellationToken = default);

    /// <summary>Forms a group or division under Operations; <see cref="CommandResult.EntityId"/> is its id.</summary>
    Task<CommandResult> FormGroupAsync(
        Guid incidentId, string name, IcsGroupKind kind, string? supervisor = null, CancellationToken cancellationToken = default);

    Task<CommandResult> DisbandGroupAsync(Guid incidentId, Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>Places a committed unit under a group's supervisor (or, with null, directly under Operations / the IC).</summary>
    Task<CommandResult> AssignUnitToGroupAsync(Guid unitId, Guid? groupId, CancellationToken cancellationToken = default);

    // ---- Orders, approvals, notifications (Design Document §9) ----

    /// <summary>A directive to a unit, group or ICS position; the recipient reads it back.</summary>
    Task<CommandResult> IssueOrderAsync(
        Guid? incidentId, OrderTargetKind targetKind, Guid? targetId, string text, IcsRole? position = null,
        CancellationToken cancellationToken = default);

    Task<CommandResult> CloseOrderAsync(Guid orderId, bool completed, CancellationToken cancellationToken = default);

    Task<CommandResult> DecideApprovalAsync(Guid approvalId, bool approve, string? note = null, CancellationToken cancellationToken = default);

    Task<CommandResult> NotifyAsync(string recipient, string message, Guid? incidentId = null, CancellationToken cancellationToken = default);
}

/// <param name="EntityId">Id of anything the command created, e.g. a new incident.</param>
/// <param name="Detail">A summary of what a compound command did, e.g. "6 orders issued, 1 group formed".</param>
public sealed record CommandResult(bool Succeeded, string? Error = null, long? EventSequence = null, Guid? EntityId = null,
    string? Detail = null)
{
    public static CommandResult Ok(long sequence, Guid? entityId = null) => new(true, EventSequence: sequence, EntityId: entityId);

    public static CommandResult Fail(string error) => new(false, error);
}
