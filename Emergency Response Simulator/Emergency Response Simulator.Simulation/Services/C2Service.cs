using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;

namespace Emergency_Response_Simulator.Simulation.Services;

/// <summary>
/// Validates commands against the COP (what command believes, not ground truth) and records
/// accepted ones in the event stream.
/// </summary>
public sealed class C2Service(ICopService cop, IEventPublisher publisher) : IC2Service
{
    /// <summary>
    /// Units of an AI-run agency (Design Document §10.3) take orders from their own control room; command can see
    /// them, and ask for them through a request or notification, but not dispatch them.
    /// </summary>
    private static CommandResult? NotUnderCommand(Unit unit) => unit.Agency is { AiControlled: true } agency
        ? CommandResult.Fail($"{unit.Callsign} belongs to {agency.Name}, which is not under your command. Ask them through a notification or mutual-aid request.")
        : null;

    public async Task<CommandResult> DispatchAsync(
        Guid unitId, Guid incidentId, Guid? orderedBy = null, CancellationToken cancellationToken = default)
    {
        if (cop.FindUnit(unitId) is not { } unit)
            return CommandResult.Fail("Unknown unit.");
        if (NotUnderCommand(unit) is { } refused)
            return refused;
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
        if (NotUnderCommand(unit) is { } refused)
            return refused;
        if (unit.AssignedIncidentId is not { } incidentId)
            return CommandResult.Fail($"{unit.Callsign} has no assignment to cancel.");

        return await PublishAsync(new UnitDispatchCancelled(unitId, incidentId, reason), cancellationToken);
    }

    public async Task<CommandResult> ReassignAsync(
        Guid unitId, Guid newIncidentId, Guid? orderedBy = null, CancellationToken cancellationToken = default)
    {
        if (cop.FindUnit(unitId) is not { } unit)
            return CommandResult.Fail("Unknown unit.");
        if (NotUnderCommand(unit) is { } refused)
            return refused;
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
        Guid incidentId, ResourceRequestKind kind, UnitType unitType, int quantity, string? justification = null,
        CancellationToken cancellationToken = default)
    {
        if (cop.FindIncident(incidentId) is not { } incident)
            return CommandResult.Fail("Unknown incident.");
        if (quantity is < 1 or > 20)
            return CommandResult.Fail("Request between 1 and 20 resources.");
        if (kind != ResourceRequestKind.AdditionalResources && string.IsNullOrWhiteSpace(justification))
            return CommandResult.Fail("Mutual aid and specialist teams need a justification for the approver.");

        var requestId = Guid.NewGuid();
        var description = $"{quantity} × {ResourceGroups.Label(unitType)} ({EventDescriber.Humanize(kind).ToLowerInvariant()}) for {incident.Number}";
        var result = await PublishAsync(new ResourceRequested(requestId, incidentId, description, quantity, null,
            kind, unitType, justification?.Trim()), cancellationToken);
        return result with { EntityId = requestId };
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
        if (NotUnderCommand(unit) is { } refused)
            return refused;
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

    public async Task<CommandResult> AssignIcsPositionAsync(
        Guid incidentId, IcsRole role, string name, CancellationToken cancellationToken = default)
    {
        if (cop.FindIncident(incidentId) is not { } incident)
            return CommandResult.Fail("Unknown incident.");
        if (string.IsNullOrWhiteSpace(name))
            return CommandResult.Fail("Name the person taking the position.");
        if (incident.Command.Positions.GetValueOrDefault(role) == name.Trim())
            return CommandResult.Fail("Nothing changed.");

        return await PublishAsync(new IcsPositionAssigned(incidentId, role, name.Trim()), cancellationToken);
    }

    public async Task<CommandResult> FormGroupAsync(
        Guid incidentId, string name, IcsGroupKind kind, string? supervisor = null, CancellationToken cancellationToken = default)
    {
        if (cop.FindIncident(incidentId) is not { } incident)
            return CommandResult.Fail("Unknown incident.");
        if (string.IsNullOrWhiteSpace(name))
            return CommandResult.Fail("Name the group or division.");
        if (incident.Command.Groups.Any(g => string.Equals(g.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            return CommandResult.Fail($"{incident.Number} already has a \"{name.Trim()}\".");

        var groupId = Guid.NewGuid();
        var result = await PublishAsync(new IcsGroupFormed(incidentId, groupId, name.Trim(), kind,
            string.IsNullOrWhiteSpace(supervisor) ? null : supervisor.Trim()), cancellationToken);
        return result with { EntityId = groupId };
    }

    public async Task<CommandResult> DisbandGroupAsync(Guid incidentId, Guid groupId, CancellationToken cancellationToken = default)
    {
        if (cop.FindIncident(incidentId) is not { } incident || incident.Command.Groups.All(g => g.Id != groupId))
            return CommandResult.Fail("Unknown group.");
        return await PublishAsync(new IcsGroupDisbanded(incidentId, groupId), cancellationToken);
    }

    public async Task<CommandResult> AssignUnitToGroupAsync(Guid unitId, Guid? groupId, CancellationToken cancellationToken = default)
    {
        if (cop.FindUnit(unitId) is not { } unit)
            return CommandResult.Fail("Unknown unit.");
        if (unit.AssignedIncident is not { } incident)
            return CommandResult.Fail($"{unit.Callsign} is not committed to an incident.");
        if (groupId is { } id && incident.Command.Groups.All(g => g.Id != id))
            return CommandResult.Fail($"That group belongs to a different incident than {unit.Callsign}'s.");
        if (incident.Command.GroupOf(unitId)?.Id == groupId)
            return CommandResult.Fail("Nothing changed.");

        return await PublishAsync(new UnitAssignedToGroup(unitId, incident.Id, groupId), cancellationToken);
    }

    public async Task<CommandResult> IssueOrderAsync(
        Guid? incidentId, OrderTargetKind targetKind, Guid? targetId, string text, IcsRole? position = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return CommandResult.Fail("Write the order.");
        var incident = incidentId is { } iid ? cop.FindIncident(iid) : null;
        if (incidentId is not null && incident is null)
            return CommandResult.Fail("Unknown incident.");

        string? targetName = targetKind switch
        {
            OrderTargetKind.Unit => targetId is { } u ? cop.FindUnit(u)?.Callsign : null,
            OrderTargetKind.Group => incident?.Command.Groups.FirstOrDefault(g => g.Id == targetId) is { } g
                ? g.Supervisor is { } supervisor ? $"{g.Name} ({supervisor})" : g.Name
                : null,
            OrderTargetKind.Position => position is { } role && incident?.Command.Positions.TryGetValue(role, out var holder) == true
                ? $"{EventDescriber.Humanize(role)} ({holder})"
                : null,
            _ => null,
        };
        if (targetName is null)
            return CommandResult.Fail(targetKind == OrderTargetKind.Position
                ? "That position is not staffed for this incident."
                : $"Unknown {targetKind.ToString().ToLowerInvariant()}.");
        if (targetKind == OrderTargetKind.Unit && targetId is { } radioUnit && cop.FindUnit(radioUnit) is { Channel: { } unitChannel } heard
            && !cop.ControlHears(unitChannel))
            return CommandResult.Fail($"{heard.Callsign} is on {unitChannel}, which you can't reach. Patch {unitChannel} to one of your channels first.");

        var orderId = Guid.NewGuid();
        var result = await PublishAsync(new OrderIssued(orderId, incidentId, targetKind,
            targetKind == OrderTargetKind.Position ? null : targetId, targetName, text.Trim()), cancellationToken);
        return result with { EntityId = orderId };
    }

    public async Task<CommandResult> CloseOrderAsync(Guid orderId, bool completed, CancellationToken cancellationToken = default)
    {
        if (cop.Orders.FirstOrDefault(o => o.Id == orderId) is not { } order)
            return CommandResult.Fail("Unknown order.");
        if (order.Status is OrderStatus.Completed or OrderStatus.Cancelled)
            return CommandResult.Fail("That order is already closed.");

        return await PublishAsync(new OrderClosed(orderId, completed), cancellationToken);
    }

    public async Task<CommandResult> DecideApprovalAsync(Guid approvalId, bool approve, string? note = null,
        CancellationToken cancellationToken = default)
    {
        if (cop.Approvals.FirstOrDefault(a => a.Id == approvalId) is not { } approval)
            return CommandResult.Fail("Unknown approval request.");
        if (approval.Status != ApprovalStatus.Pending)
            return CommandResult.Fail("That request has already been decided.");

        return await PublishAsync(new ApprovalDecided(approvalId, approve, string.IsNullOrWhiteSpace(note) ? null : note.Trim()),
            cancellationToken);
    }

    // ---- Communications ----

    public async Task<CommandResult> TransmitAsync(string channelId, string? to, string text, double? heldSeconds = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return CommandResult.Fail("Say something.");
        if (cop.Channels.FirstOrDefault(c => c.Info.Id == channelId) is not { Info.Kind: ChannelKind.Radio })
            return CommandResult.Fail("Unknown radio channel.");
        if (!cop.ControlHears(channelId))
            return CommandResult.Fail($"Control has no radio on {channelId}. Patch it to one of your channels first.");

        var messageId = Guid.NewGuid();
        var result = await PublishAsync(new RadioCallMade(messageId, channelId, string.IsNullOrWhiteSpace(to) ? null : to.Trim(), text.Trim(),
            heldSeconds), cancellationToken);
        return result with { EntityId = messageId };
    }

    public async Task<CommandResult> RequestRepeatAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        if (cop.CommsLog.FirstOrDefault(m => m.Id == messageId) is not { } message)
            return CommandResult.Fail("Unknown message.");
        if (message.FromControl || message.ChannelId is RadioPlan.Calls or RadioPlan.Chat)
            return CommandResult.Fail("Only a radio message from the field can be repeated.");
        if (message.RepeatRequested)
            return CommandResult.Fail("Already asked to say again.");
        return await PublishAsync(new RepeatRequested(messageId), cancellationToken);
    }

    public async Task<CommandResult> ConfirmReadBackAsync(Guid orderId, bool correct, CancellationToken cancellationToken = default)
    {
        if (cop.Orders.FirstOrDefault(o => o.Id == orderId) is not { } order)
            return CommandResult.Fail("Unknown order.");
        if (order.Status != OrderStatus.Acknowledged)
            return CommandResult.Fail("There is no read-back to confirm yet.");
        if (order.ReadBackConfirmedAt is not null)
            return CommandResult.Fail("Read-back already confirmed.");
        return await PublishAsync(new ReadBackConfirmed(orderId, correct), cancellationToken);
    }

    public async Task<CommandResult> AssignChannelAsync(Guid unitId, string channelId, CancellationToken cancellationToken = default)
    {
        if (cop.FindUnit(unitId) is not { } unit)
            return CommandResult.Fail("Unknown unit.");
        if (NotUnderCommand(unit) is { } refused)
            return refused;
        if (RadioPlan.Find(channelId) is not { Kind: ChannelKind.Radio })
            return CommandResult.Fail("Crews can only be moved to one of your own radio channels.");
        if (unit.Agency?.RadioChannel is { } foreign)
            return CommandResult.Fail($"{unit.Callsign}'s radios only work on {foreign}. Patch {foreign} to your channel instead.");
        if (unit.Channel == channelId)
            return CommandResult.Fail($"{unit.Callsign} is already on {channelId}.");
        if (unit.Channel is { } current && !cop.ControlHears(current))
            return CommandResult.Fail($"You can't reach {unit.Callsign} on {current} to tell it.");
        return await PublishAsync(new UnitChannelAssigned(unitId, channelId), cancellationToken);
    }

    public async Task<CommandResult> PatchChannelsAsync(string channelA, string channelB, CancellationToken cancellationToken = default)
    {
        var channels = cop.Channels.Where(c => c.Info.Kind == ChannelKind.Radio).Select(c => c.Info.Id).ToHashSet();
        if (!channels.Contains(channelA) || !channels.Contains(channelB))
            return CommandResult.Fail("Choose two radio channels.");
        if (channelA == channelB)
            return CommandResult.Fail("Choose two different channels.");
        if (cop.Patches.Any(p => (p.ChannelA == channelA && p.ChannelB == channelB) || (p.ChannelA == channelB && p.ChannelB == channelA)))
            return CommandResult.Fail($"{channelA} and {channelB} are already patched (or being patched).");

        var patchId = Guid.NewGuid();
        var result = await PublishAsync(new ChannelPatchRequested(patchId, channelA, channelB), cancellationToken);
        return result with { EntityId = patchId };
    }

    public async Task<CommandResult> RemovePatchAsync(Guid patchId, CancellationToken cancellationToken = default)
    {
        if (cop.Patches.All(p => p.Id != patchId))
            return CommandResult.Fail("Unknown patch.");
        return await PublishAsync(new ChannelPatchRemoved(patchId), cancellationToken);
    }

    public async Task<CommandResult> CallBackAsync(Guid callId, CancellationToken cancellationToken = default)
    {
        if (cop.MissedCalls.FirstOrDefault(c => c.Id == callId) is not { } missed)
            return CommandResult.Fail("Unknown missed call.");
        if (missed.CalledBackAt is not null)
            return CommandResult.Fail("Already called back.");
        return await PublishAsync(new CallbackMade(callId), cancellationToken);
    }

    public async Task<CommandResult> NotifyAsync(string recipient, string message, Guid? incidentId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recipient))
            return CommandResult.Fail("Choose who to notify.");
        if (string.IsNullOrWhiteSpace(message))
            return CommandResult.Fail("Write the message.");
        if (incidentId is { } id && cop.FindIncident(id) is null)
            return CommandResult.Fail("Unknown incident.");

        var notificationId = Guid.NewGuid();
        var result = await PublishAsync(new NotificationSent(notificationId, incidentId, recipient.Trim(), message.Trim()), cancellationToken);
        return result with { EntityId = notificationId };
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
