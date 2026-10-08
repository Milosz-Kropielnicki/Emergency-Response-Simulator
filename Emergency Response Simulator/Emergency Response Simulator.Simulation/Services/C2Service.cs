using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;

namespace Emergency_Response_Simulator.Simulation.Services;

/// <summary>
/// Validates commands against the COP (what command believes, not ground truth) and records
/// accepted ones in the event stream. Phase 0 covers the core commands; Phase 4 extends this.
/// </summary>
public sealed class C2Service(ICopService cop, IEventPublisher publisher) : IC2Service
{
    public async Task<CommandResult> DispatchAsync(
        Guid unitId, Guid incidentId, Guid? orderedBy = null, CancellationToken cancellationToken = default)
    {
        if (cop.FindUnit(unitId) is not { } unit)
            return CommandResult.Fail("Unknown unit.");
        if (cop.FindIncident(incidentId) is not { } incident)
            return CommandResult.Fail("Unknown incident.");
        if (incident.Status == IncidentStatus.Closed)
            return CommandResult.Fail($"{incident.Number} is closed.");
        if (unit.Status != UnitStatus.Available)
            return CommandResult.Fail($"{unit.Callsign} is not available ({unit.Status}).");

        return await PublishAsync(new UnitDispatched(unitId, incidentId, orderedBy), cancellationToken);
    }

    public async Task<CommandResult> CancelDispatchAsync(
        Guid unitId, string? reason = null, CancellationToken cancellationToken = default)
    {
        if (cop.FindUnit(unitId) is not { } unit)
            return CommandResult.Fail("Unknown unit.");
        if (unit.AssignedIncidentId is not { } incidentId)
            return CommandResult.Fail($"{unit.Callsign} has no assignment to cancel.");

        return await PublishAsync(new UnitDispatchCancelled(unitId, incidentId, reason), cancellationToken);
    }

    public async Task<CommandResult> ReassignAsync(
        Guid unitId, Guid newIncidentId, Guid? orderedBy = null, CancellationToken cancellationToken = default)
    {
        if (cop.FindUnit(unitId) is not { } unit)
            return CommandResult.Fail("Unknown unit.");
        if (unit.Status == UnitStatus.OutOfService)
            return CommandResult.Fail($"{unit.Callsign} is out of service.");
        if (cop.FindIncident(newIncidentId) is not { } incident)
            return CommandResult.Fail("Unknown incident.");
        if (incident.Status == IncidentStatus.Closed)
            return CommandResult.Fail($"{incident.Number} is closed.");
        if (unit.AssignedIncidentId == newIncidentId)
            return CommandResult.Fail($"{unit.Callsign} is already assigned to {incident.Number}.");

        return await PublishAsync(new UnitDispatched(unitId, newIncidentId, orderedBy), cancellationToken);
    }

    public async Task<CommandResult> RequestResourcesAsync(
        Guid incidentId, string description, int quantity, Guid? requestedBy = null,
        CancellationToken cancellationToken = default)
    {
        if (cop.FindIncident(incidentId) is null)
            return CommandResult.Fail("Unknown incident.");
        if (quantity < 1)
            return CommandResult.Fail("Quantity must be at least 1.");
        if (string.IsNullOrWhiteSpace(description))
            return CommandResult.Fail("Describe what is being requested.");

        return await PublishAsync(
            new ResourceRequested(Guid.NewGuid(), incidentId, description.Trim(), quantity, requestedBy), cancellationToken);
    }

    public async Task<CommandResult> DeclareZoneAsync(
        ZoneType type, string name, IReadOnlyList<GeoPoint> boundary, Guid? incidentId = null,
        CancellationToken cancellationToken = default)
    {
        if (boundary.Count < 2)
            return CommandResult.Fail("A zone boundary needs at least two points.");
        if (incidentId is { } id && cop.FindIncident(id) is null)
            return CommandResult.Fail("Unknown incident.");

        return await PublishAsync(new ZoneDeclared(Guid.NewGuid(), type, name, boundary, incidentId), cancellationToken);
    }

    public async Task<CommandResult> LiftZoneAsync(Guid zoneId, CancellationToken cancellationToken = default)
    {
        if (cop.Zones.All(z => z.Id != zoneId))
            return CommandResult.Fail("Unknown or already lifted zone.");

        return await PublishAsync(new ZoneLifted(zoneId), cancellationToken);
    }

    private async Task<CommandResult> PublishAsync(DomainEvent payload, CancellationToken cancellationToken)
    {
        var appended = await publisher.PublishAsync(payload, EventVisibility.Perceived, EventSources.C2, cancellationToken);
        return CommandResult.Ok(appended.Sequence);
    }
}
