using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Core.Contracts;

/// <summary>
/// Command and Control: "Make it happen" (Design Document §9).
/// Every accepted command is appended to the event stream, so it is visible on the COP and in the AAR.
/// Orders, notifications and approvals are added in Phase 4.
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

    /// <summary>Mutual aid, additional resources or specialist teams.</summary>
    Task<CommandResult> RequestResourcesAsync(
        Guid incidentId, string description, int quantity, Guid? requestedBy = null,
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
}

/// <param name="EntityId">Id of anything the command created, e.g. a new incident.</param>
public sealed record CommandResult(bool Succeeded, string? Error = null, long? EventSequence = null, Guid? EntityId = null)
{
    public static CommandResult Ok(long sequence, Guid? entityId = null) => new(true, EventSequence: sequence, EntityId: entityId);

    public static CommandResult Fail(string error) => new(false, error);
}
