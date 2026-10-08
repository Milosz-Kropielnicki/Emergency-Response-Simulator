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

    public async Task<CommandResult> EstablishHazardZonesAsync(
        GeoPoint center, double hotRadiusMeters, double warmRadiusMeters, double coldRadiusMeters,
        Guid? incidentId = null, CancellationToken cancellationToken = default)
    {
        if (hotRadiusMeters <= 0 || warmRadiusMeters <= hotRadiusMeters || coldRadiusMeters <= warmRadiusMeters)
            return CommandResult.Fail("Radii must increase: hot < warm < cold.");
        if (incidentId is { } id && cop.FindIncident(id) is null)
            return CommandResult.Fail("Unknown incident.");

        var label = incidentId is { } linked ? $" {cop.FindIncident(linked)!.Number}" : "";
        var last = CommandResult.Fail("No zones declared.");
        // Cold first so the hotter zones are drawn on top.
        foreach (var (type, radius, name) in new[]
                 {
                     (ZoneType.ColdZone, coldRadiusMeters, "Cold zone"),
                     (ZoneType.WarmZone, warmRadiusMeters, "Warm zone"),
                     (ZoneType.HotZone, hotRadiusMeters, "Hot zone"),
                 })
        {
            last = await PublishAsync(new ZoneDeclared(Guid.NewGuid(), type, $"{name}{label} ({radius:F0} m)",
                Wgs84.Circle(center, radius), incidentId), cancellationToken);
        }
        return last;
    }

    public async Task<CommandResult> CreateIncidentAsync(
        IncidentType type, IncidentPriority priority, GeoPoint location, string? address = null, string? name = null,
        Guid? fromReportId = null, CancellationToken cancellationToken = default)
    {
        if (fromReportId is { } reportId && cop.Reports.All(r => r.Id != reportId))
            return CommandResult.Fail("Unknown report.");

        var incidentId = Guid.NewGuid();
        var number = $"INC-{cop.Incidents.Count + 1:D5}";
        var created = await PublishAsync(
            new IncidentCreated(incidentId, number, type, priority, location, address, name), cancellationToken);

        if (fromReportId is { } source)
            await PublishAsync(new ReportLinked(source, incidentId), cancellationToken);

        return created with { EntityId = incidentId };
    }

    public async Task<CommandResult> UpdateIncidentAsync(
        Guid incidentId, IncidentPriority? priority = null, IncidentStatus? status = null,
        int? casualtiesReported = null, int? casualtiesConfirmed = null, bool? evacuationRequired = null,
        IReadOnlyList<string>? threats = null, CancellationToken cancellationToken = default)
    {
        if (cop.FindIncident(incidentId) is not { } incident)
            return CommandResult.Fail("Unknown incident.");
        if (casualtiesReported < 0 || casualtiesConfirmed < 0)
            return CommandResult.Fail("Casualty counts cannot be negative.");
        if (status == IncidentStatus.Closed && incident.AssignedUnits.Count > 0)
            return CommandResult.Fail($"Release the {incident.AssignedUnits.Count} assigned unit(s) before closing {incident.Number}.");

        var update = new IncidentUpdated(incidentId,
            Changed(priority, incident.Priority), Changed(status, incident.Status),
            Changed(casualtiesReported, incident.CasualtiesReported), Changed(casualtiesConfirmed, incident.CasualtiesConfirmed),
            Changed(evacuationRequired, incident.EvacuationRequired),
            threats is not null && !threats.SequenceEqual(incident.Threats) ? threats : null);

        if (update == new IncidentUpdated(incidentId))
            return CommandResult.Fail("Nothing changed.");

        return await PublishAsync(update, cancellationToken);
    }

    public async Task<CommandResult> AssignIncidentCommanderAsync(
        Guid incidentId, string name, Guid? userId = null, CancellationToken cancellationToken = default)
    {
        if (cop.FindIncident(incidentId) is null)
            return CommandResult.Fail("Unknown incident.");
        if (string.IsNullOrWhiteSpace(name))
            return CommandResult.Fail("Name the incident commander.");

        return await PublishAsync(new IncidentCommanderAssigned(incidentId, name.Trim(), userId), cancellationToken);
    }

    public async Task<CommandResult> UpdateUnitStatusAsync(
        Guid unitId, UnitStatus status, CancellationToken cancellationToken = default)
    {
        if (cop.FindUnit(unitId) is not { } unit)
            return CommandResult.Fail("Unknown unit.");
        if (!UnitStatusRules.CanTransition(unit.Status, status))
            return CommandResult.Fail(
                $"{unit.Callsign} cannot go from {EventDescriber.Humanize(unit.Status)} to {EventDescriber.Humanize(status)}.");

        return await PublishAsync(new UnitStatusChanged(unitId, status), cancellationToken);
    }

    public async Task<CommandResult> AssessReportAsync(
        Guid reportId, VerificationStatus verification, Confidence confidence, CancellationToken cancellationToken = default)
    {
        if (cop.Reports.FirstOrDefault(r => r.Id == reportId) is not { } report)
            return CommandResult.Fail("Unknown report.");
        if (report.Verification == verification && report.Confidence == confidence)
            return CommandResult.Fail("Nothing changed.");

        return await PublishAsync(new ReportAssessed(reportId, verification, confidence), cancellationToken);
    }

    public async Task<CommandResult> LinkReportAsync(Guid reportId, Guid incidentId, CancellationToken cancellationToken = default)
    {
        if (cop.Reports.FirstOrDefault(r => r.Id == reportId) is not { } report)
            return CommandResult.Fail("Unknown report.");
        if (cop.FindIncident(incidentId) is null)
            return CommandResult.Fail("Unknown incident.");
        if (report.IncidentId == incidentId)
            return CommandResult.Fail("Already attributed to that incident.");

        return await PublishAsync(new ReportLinked(reportId, incidentId), cancellationToken);
    }

    public async Task<CommandResult> AcknowledgeAlertAsync(Guid alertId, Guid? userId = null, CancellationToken cancellationToken = default)
    {
        if (cop.Alerts.FirstOrDefault(a => a.Id == alertId) is not { } alert)
            return CommandResult.Fail("Unknown alert.");
        if (alert.AcknowledgedAt is not null)
            return CommandResult.Fail("Already acknowledged.");

        return await PublishAsync(new AlertAcknowledged(alertId, userId), cancellationToken);
    }

    /// <summary>The new value if it differs from the current one, otherwise null (no change).</summary>
    private static T? Changed<T>(T? requested, T current) where T : struct =>
        requested is { } value && !EqualityComparer<T>.Default.Equals(value, current) ? value : null;

    private async Task<CommandResult> PublishAsync(DomainEvent payload, CancellationToken cancellationToken)
    {
        var appended = await publisher.PublishAsync(payload, EventVisibility.Perceived, EventSources.C2, cancellationToken);
        return CommandResult.Ok(appended.Sequence);
    }
}
