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
}

public sealed record CommandResult(bool Succeeded, string? Error = null, long? EventSequence = null)
{
    public static CommandResult Ok(long sequence) => new(true, EventSequence: sequence);

    public static CommandResult Fail(string error) => new(false, error);
}
