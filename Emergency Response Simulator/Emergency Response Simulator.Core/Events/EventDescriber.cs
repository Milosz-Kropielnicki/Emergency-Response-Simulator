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
            HospitalRegistered e => Entry("SETUP", $"Receiving hospital: {e.Name} (ED {e.Occupied}/{e.EdCapacity})"),
            HospitalStatusReported e => Entry("HOSPITAL", $"{e.Name}: ED {e.Occupied}/{e.Capacity}" +
                (e.OnDiversion ? " — ON DIVERSION" : "") + (e.Note is { } note ? $". {note}" : ""), important: e.OnDiversion),
            UnitTasked e => Entry("AGENCY", $"{Unit(e.UnitId)} tasked by {e.TaskedBy}: {e.Task}", unit: e.UnitId),
            HazardStarted e => Entry("TRUTH", $"{Humanize(e.Kind)} hazard begins: {e.Description}"),
            HazardFootprintChanged e => Entry("TRUTH", $"Hazard now covers {e.AreaSquareMeters:N0} m²"),
            HazardRateChanged e => Entry("TRUTH", $"Hazard rate now {e.Rate:0.##}: {e.Reason}"),
            HazardEnded e => Entry("TRUTH", $"Hazard ended: {e.Reason}"),
            HazardSitePlaced e => Entry("TRUTH", $"{Humanize(e.Kind)} at risk if a hazard reaches it: {e.Name}"),
            CasualtyInjured e => Entry("TRUTH", $"Casualty ({TriageLabel(e.Triage)}): {e.Cause}"),
            CasualtyChanged e => Entry("TRUTH", $"Casualty now {TriageLabel(e.Triage)}, {Humanize(e.State).ToLowerInvariant()}"),
            HospitalCapacityChanged e => Entry("TRUTH", $"Hospital capacity now {e.Capacity}: {e.Reason}"),
            PowerOutageStarted e => Entry("TRUTH", $"Power out within {e.RadiusMeters:F0} m: {e.Cause}"),
            PowerRestored => Entry("TRUTH", "Power restored"),
            CascadeOccurred e => Entry("CASCADE", $"{e.Cause} → {e.Effect}", unit: e.UnitId),
            RadioCallMade e => Entry("RADIO", $"Control → {e.To ?? "all stations"} on {e.ChannelId}: \"{e.Text}\""),
            CommsLogged e => e.FromControl ? null : Entry(e.ChannelId == RadioPlan.Calls ? "999 LINE" : e.ChannelId == RadioPlan.Chat ? "CHAT" : "RADIO",
                $"{e.ChannelId} · {e.From}" + (e.To is { } to ? $" → {to}" : "") + $": \"{e.Text}\"" +
                (e.Quality == CommsQuality.Clear ? "" : $" [{e.Quality.ToString().ToLowerInvariant()}]"),
                unit: e.UnitId, important: e.Quality == CommsQuality.Garbled),
            RepeatRequested => Entry("RADIO", "Control: say again"),
            ReadBackConfirmed e => Entry("ORDER", e.Correct ? "Read-back confirmed: correct" : "Read-back wrong: order repeated"),
            UnitChannelAssigned e => Entry("RADIO", $"{Unit(e.UnitId)} moved to {e.ChannelId}", unit: e.UnitId),
            ChannelPatchRequested e => Entry("RADIO", $"Patch requested: {e.ChannelA} ↔ {e.ChannelB}"),
            ChannelsPatched => Entry("RADIO", "Channel patch working", important: true),
            ChannelPatchRemoved => Entry("RADIO", "Channel patch removed"),
            CallMissed e => Entry("999 LINE", $"Missed call: caller hung up after {e.Waited.TotalSeconds:F0} s" + Accuracy(e.AccuracyMeters), important: true),
            CallbackMade => Entry("999 LINE", "Calling back a missed caller"),
            TransmissionLost e => Entry("TRUTH", $"Unheard on {e.ChannelId}: {e.From}: \"{e.Text}\" ({e.Reason})"),
            RadioDeadZonePlaced e => Entry("TRUTH", $"Radio black spot: {e.Description}"),
            CellTowerFailed e => Entry("TRUTH", $"Mobile mast down: {e.Cause}"),
            CellTowerRestored => Entry("TRUTH", "Mobile mast back in service"),
            RadioBatteryChanged e => Entry("TRUTH", $"{Unit(e.UnitId)} radio battery at {e.Level:P0}", unit: e.UnitId),
            CrewRostered e => Entry("CREW", $"{Unit(e.UnitId)} crew of {e.Members.Count} on duty ({e.OnShiftFor.TotalHours:0.#} h into a {e.ShiftLength.TotalHours:0} h shift)", unit: e.UnitId),
            CrewConditionReported e => Entry("CREW", $"{Unit(e.UnitId)}: \"{e.Note}\"", unit: e.UnitId,
                important: e.Condition is CrewCondition.Exhausted or CrewCondition.Shaken),
            CrewRehabOrdered e => Entry("CREW", $"{Unit(e.UnitId)} sent to rehab", unit: e.UnitId),
            CrewRehabEnded e => Entry("CREW", $"{Unit(e.UnitId)}: {e.Note}", unit: e.UnitId),
            CrewReliefRequested e => Entry("CREW", $"Relief crew requested for {Unit(e.UnitId)} ({(e.FullBriefing ? "full handover briefing" : "quick changeover")})", unit: e.UnitId),
            CrewRelieved e => Entry("CREW", $"{Unit(e.UnitId)} relieved: new crew of {e.Members.Count}" + (e.Briefed ? "" : " (no formal briefing)"), unit: e.UnitId),
            CrewMemberStoodDown e => Entry("CREW", $"{Unit(e.UnitId)}: {e.Reason}; stood down", unit: e.UnitId, important: true),
            PeerSupportArranged e => Entry("CREW", $"Peer support arranged for {Unit(e.UnitId)}", unit: e.UnitId),
            PeerSupportGiven e => Entry("CREW", $"Peer support: {e.Note}", unit: e.UnitId),
            ParRequested e => Entry("SAFETY", $"PAR called: {e.Reason}", e.IncidentId, important: true),
            ParReported e => Entry("SAFETY", $"{Unit(e.UnitId)} PAR: {e.Accounted} of {e.Expected}" +
                (e.Missing.Count > 0 ? $", MISSING {string.Join(", ", e.Missing)}" : ""), unit: e.UnitId, important: e.Missing.Count > 0),
            EvacuationSignalled e => Entry("SAFETY", "EVACUATION SIGNAL: all crews withdraw from the building", e.IncidentId, important: true),
            EmergencyTrafficDeclared e => Entry("SAFETY", e.Active ? $"Emergency traffic declared on {e.ChannelId}" : $"Emergency traffic lifted on {e.ChannelId}",
                important: e.Active),
            MaydayDeclared e => Entry("MAYDAY", (e.Unclear ? "Unclear Mayday: " : "MAYDAY: ") + e.Details, unit: e.UnitId, important: true),
            RescueTeamDeployed e => Entry("MAYDAY", $"{Unit(e.UnitId)} sent in as the rescue team", unit: e.UnitId, important: true),
            MaydayResolved e => Entry("MAYDAY", $"Mayday resolved: {e.Outcome}", important: true),
            OrderDeclined e => Entry("ORDER", $"Order declined: {e.Reason}", important: true),
            CrewWelfareChanged e => Entry("TRUTH", $"{Unit(e.UnitId)} crew now really {e.Band} (fatigue {e.Fatigue:P0}, stress {e.Stress:P0})", unit: e.UnitId),
            FirefighterInDistress e => Entry("TRUTH", $"Firefighter on {Unit(e.UnitId)} in distress: {e.Cause} ({e.AirMinutes:F0} min of air)", unit: e.UnitId),
            DistressEnded e => Entry("TRUTH", $"Firefighter distress over: {e.Outcome}"),
            StructureCollapsed e => Entry("TRUTH", $"Structural collapse: {e.Description}"),
            HandoverInformationLost e => Entry("TRUTH", $"{Unit(e.UnitId)} relief crew never told: {string.Join("; ", e.Items)}", unit: e.UnitId),
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

    public static string TriageLabel(Triage triage) => triage switch
    {
        Triage.Immediate => "P1",
        Triage.Urgent => "P2",
        Triage.Delayed => "P3",
        _ => "deceased",
    };

    /// <summary>"StructureFire" → "Structure fire".</summary>
    public static string Humanize<T>(T value) where T : Enum
    {
        var name = value.ToString();
        return string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));
    }
}
