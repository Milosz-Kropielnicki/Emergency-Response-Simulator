using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Core.Events;

/// <summary>One line of the history log / event timeline (Design Document §5.3, §6.6).</summary>
public sealed record TimelineEntry(
    long Sequence,
    DateTimeOffset SimTime,
    EventVisibility Visibility,
    string Category,
    string Text,
    Guid? IncidentId = null,
    Guid? UnitId = null,
    bool Important = false);

/// <summary>Turns events into the human-readable lines of the history log.</summary>
public static class EventDescriber
{
    /// <param name="unitName">Resolves a unit id to its callsign.</param>
    /// <param name="incidentName">Resolves an incident id to its number.</param>
    /// <returns>Null for events that are bookkeeping rather than history (e.g. AVL fixes).</returns>
    public static TimelineEntry? Describe(
        SimEvent simEvent,
        Func<Guid, string?> unitName,
        Func<Guid, string?> incidentName)
    {
        string Unit(Guid id) => unitName(id) ?? "Unknown unit";
        string Incident(Guid id) => incidentName(id) ?? "unknown incident";

        TimelineEntry Entry(string category, string text, Guid? incident = null, Guid? unit = null, bool important = false) =>
            new(simEvent.Sequence, simEvent.SimTime, simEvent.Visibility, category, text, incident, unit, important);

        return simEvent.Payload switch
        {
            CallReceived e => Entry("CALL RECEIVED", $"{e.Caller}: \"{e.Summary}\"" + Accuracy(e.LocationAccuracyMeters), important: true),
            IncidentCreated e => Entry("INCIDENT CREATED", $"{e.Number} {Humanize(e.Type)}, Priority: {e.Priority}" +
                (e.Address is null ? "" : $", {e.Address}"), e.IncidentId, important: true),
            IncidentUpdated e => Entry("INCIDENT UPDATE", $"{Incident(e.IncidentId)} {DescribeUpdate(e)}", e.IncidentId,
                important: e.Priority == IncidentPriority.Critical),
            IncidentCommanderAssigned e => Entry("COMMAND", $"{e.Name} assumes command of {Incident(e.IncidentId)}", e.IncidentId),
            UnitDispatched e => Entry("UNIT DISPATCHED", $"{Unit(e.UnitId)} → {Incident(e.IncidentId)}", e.IncidentId, e.UnitId),
            UnitDispatchCancelled e => Entry("UNIT CANCELLED", $"{Unit(e.UnitId)} stood down from {Incident(e.IncidentId)}" +
                (e.Reason is null ? "" : $" ({e.Reason})"), e.IncidentId, e.UnitId),
            UnitStatusChanged e => Entry("UNIT STATUS", $"{Unit(e.UnitId)} → {Humanize(e.Status)}", unit: e.UnitId),
            ReportReceived e => Entry(e.Source == ReportSource.FieldUnit ? "FIELD REPORT" : "REPORT",
                $"{e.SourceName}: \"{e.Claim}\" [{e.Confidence} confidence, {e.Verification}]" + Accuracy(e.LocationAccuracyMeters),
                e.IncidentId, e.FromUnitId, important: true),
            ReportAssessed e => Entry("ASSESSMENT", $"Report re-graded: {e.Verification}, {e.Confidence} confidence"),
            ReportLinked e => Entry("ASSESSMENT", $"Report attributed to {Incident(e.IncidentId)}", e.IncidentId),
            AlertRaised e => Entry($"⚠ {Humanize(e.Category).ToUpperInvariant()}", $"{e.Title}. {e.Message}", e.IncidentId, e.UnitId,
                important: e.Severity == AlertSeverity.Critical),
            AlertAcknowledged => Entry("ALERT", "Alert acknowledged"),
            ZoneDeclared e => Entry("ZONE", $"{Humanize(e.Type)} declared: {e.Name}", e.IncidentId),
            ZoneLifted => Entry("ZONE", "Zone lifted"),
            ResourceRequested e => Entry("RESOURCE REQUEST", e.Description + (e.Justification is { } why ? $" — \"{why}\"" : ""),
                e.IncidentId, important: true),
            ResourceRequestDecided e => Entry("RESOURCE REQUEST", (e.Approved
                ? $"Approved by {e.DecidedBy}" + (e.ExpectedAt is { } eta ? $", expected on scene {eta.ToLocalTime():HH:mm}" : "")
                : $"{e.DecidedBy}: {e.Reason}"), important: !e.Approved),
            ResourceRequestFulfilled e => Entry("RESOURCE REQUEST", $"{e.UnitIds.Count} requested unit(s) arrived in the area"),
            IcsPositionAssigned e => Entry("COMMAND", $"{e.Name} takes {Humanize(e.Role)} for {Incident(e.IncidentId)}", e.IncidentId),
            IcsGroupFormed e => Entry("COMMAND", $"{Humanize(e.Kind)} formed: {e.Name}" + (e.Supervisor is { } s ? $" (supervisor {s})" : ""), e.IncidentId),
            IcsGroupDisbanded e => Entry("COMMAND", "Group disbanded", e.IncidentId),
            UnitAssignedToGroup e => Entry("COMMAND", e.GroupId is null ? $"{Unit(e.UnitId)} reports directly to command"
                : $"{Unit(e.UnitId)} assigned to a group", e.IncidentId, e.UnitId),
            OrderIssued e => Entry("ORDER", $"To {e.TargetName}: \"{e.Text}\"", e.IncidentId, e.TargetKind == OrderTargetKind.Unit ? e.TargetId : null, important: true),
            OrderAcknowledged e => Entry("ORDER", (e.Garbled ? "Garbled read-back: " : "Read-back: ") + e.ReadBack, important: e.Garbled),
            OrderClosed e => Entry("ORDER", e.Completed ? "Order completed" : "Order cancelled"),
            ApprovalRequested e => Entry("APPROVAL", $"{e.RequestedBy} requests: {e.Subject}", e.IncidentId, important: true),
            ApprovalDecided e => Entry("APPROVAL", (e.Approved ? "Approved" : "Denied") + (e.Note is { } n ? $": {n}" : "")),
            NotificationSent e => Entry("NOTIFICATION", $"To {e.Recipient}: \"{e.Message}\"", e.IncidentId),
            NotificationAnswered e => Entry("NOTIFICATION", e.Reply),
            OperationalPeriodStarted e => Entry("IAP", $"Operational period {e.Number} for {Incident(e.IncidentId)}: " +
                $"{e.Start.ToLocalTime():HH:mm}–{e.End.ToLocalTime():HH:mm}" + (e.Focus is { } focus ? $" ({focus})" : ""), e.IncidentId, important: true),
            IapDraftCreated e => Entry("IAP", $"IAP version {e.Version} drafted for {Incident(e.IncidentId)}" +
                (e.PreparedBy is { } by ? $" by {by}" : "") + (e.BasedOnId is null ? "" : " from the previous version"), e.IncidentId),
            IapDraftSaved e => Entry("IAP", $"IAP draft saved: {e.Content.Objectives.Count} objective(s), {e.Content.Assignments.Count} assignment(s)"),
            IapSubmitted e => Entry("IAP", $"IAP submitted for approval by {e.SubmittedBy}", important: true),
            IapReturned e => Entry("IAP", $"IAP returned by {e.ReturnedBy}: \"{e.Comments}\"", important: true),
            IapApproved e => Entry("IAP", $"IAP approved by {e.ApprovedBy}; now in force", important: true),
            IapBriefed e => Entry("IAP", $"IAP briefed: {e.OrdersIssued} assignment order(s) issued"),
            ObjectiveStatusChanged e => Entry("IAP", $"Objective marked {Humanize(e.Status).ToLowerInvariant()}", e.IncidentId,
                important: e.Status == ObjectiveStatus.NotAchieved),
            WeatherObserved e => Entry("WEATHER", $"{e.Source}: wind from {e.WindFromDegrees:F0}° at {e.WindSpeedMps:F1} m/s, {e.TemperatureC:F0}°C"),
            AgencyRegistered e => Entry("SETUP", $"Agency on duty: {e.Name}"),
            UnitRegistered e => Entry("SETUP", $"{e.Callsign} ({ResourceGroups.Label(e.Type)}) available at {e.HomeStation ?? "station"}", unit: e.UnitId),
            SimulationStarted e => Entry("SIMULATION", $"Started at {e.TimeScale:0.#}× speed"),
            SimulationPaused => Entry("SIMULATION", "Paused"),
            TimeScaleChanged e => Entry("SIMULATION", $"Speed set to {e.TimeScale:0.#}×"),
            WorldIncidentStarted e => Entry("TRUTH", $"{Humanize(e.Type)} begins (severity {e.Severity:P0}, {e.ActualCasualties} casualties)"),
            WorldIncidentChanged e => Entry("TRUTH", e.Extinguished ? "Incident extinguished" :
                $"Incident now severity {e.Severity:P0}, {e.ActualCasualties} casualties"),
            WeatherChanged e => Entry("TRUTH", $"Wind actually from {e.WindFromDegrees:F0}° at {e.WindSpeedMps:F1} m/s"),
            RouteReported e => Entry("UNIT ROUTE", $"{Unit(e.UnitId)}: {e.DistanceMeters / 1000:F1} km by road, ETA {e.Eta.TotalMinutes:F1} min" +
                (e.Reason is null or "Dispatched" ? "" : $" — {e.Reason}"), e.IncidentId, e.UnitId,
                important: e.Reason?.StartsWith("Re-routed") == true),
            RoadObstructed e => Entry("TRUTH", $"Road blocked (unreported): {e.Description}"),
            RoadObstructionCleared => Entry("TRUTH", "Road obstruction cleared"),
            UnitBrokeDown e => Entry("TRUTH", $"{Unit(e.UnitId)} breaks down: {e.Fault}", unit: e.UnitId),
            UnitRadioFailed e => Entry("TRUTH", $"{Unit(e.UnitId)} radio {(e.Failed ? "fails" : "recovers")}", unit: e.UnitId),
            UnitPositionReported => null, // dozens per minute; shown on the map instead
            _ => Entry(simEvent.Type.ToUpperInvariant(), simEvent.Type),
        };
    }

    private static string DescribeUpdate(IncidentUpdated e)
    {
        var parts = new List<string>();
        if (e.Priority is { } priority) parts.Add($"Priority: {priority}");
        if (e.Status is { } status) parts.Add($"Status: {Humanize(status)}");
        if (e.CasualtiesReported is { } reported) parts.Add($"Casualties reported: {reported}");
        if (e.CasualtiesConfirmed is { } confirmed) parts.Add($"Casualties confirmed: {confirmed}");
        if (e.EvacuationRequired is { } evacuation) parts.Add(evacuation ? "Evacuation required" : "Evacuation not required");
        if (e.Threats is { Count: > 0 } threats) parts.Add($"Threats: {string.Join(", ", threats)}");
        return parts.Count == 0 ? "updated" : string.Join(" · ", parts);
    }

    private static string Accuracy(double? meters) => meters is { } m ? $" (±{m:F0} m)" : "";

    /// <summary>"StructureFire" → "Structure fire".</summary>
    public static string Humanize<T>(T value) where T : Enum
    {
        var name = value.ToString();
        return string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));
    }
}
