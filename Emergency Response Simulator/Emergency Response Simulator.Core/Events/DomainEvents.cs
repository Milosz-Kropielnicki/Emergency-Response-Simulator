using System.Text.Json.Serialization;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Core.Events;

/// <summary>
/// Base type for every event payload. Each subtype must be registered below so it
/// round-trips through JSON (the event store keeps payloads as jsonb).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
// Scenario setup
[JsonDerivedType(typeof(AgencyRegistered), nameof(AgencyRegistered))]
[JsonDerivedType(typeof(UnitRegistered), nameof(UnitRegistered))]
// Perceived: what reaches the organisation
[JsonDerivedType(typeof(CallReceived), nameof(CallReceived))]
[JsonDerivedType(typeof(IncidentCreated), nameof(IncidentCreated))]
[JsonDerivedType(typeof(IncidentUpdated), nameof(IncidentUpdated))]
[JsonDerivedType(typeof(UnitDispatched), nameof(UnitDispatched))]
[JsonDerivedType(typeof(UnitDispatchCancelled), nameof(UnitDispatchCancelled))]
[JsonDerivedType(typeof(UnitStatusChanged), nameof(UnitStatusChanged))]
[JsonDerivedType(typeof(UnitPositionReported), nameof(UnitPositionReported))]
[JsonDerivedType(typeof(ReportReceived), nameof(ReportReceived))]
[JsonDerivedType(typeof(AlertRaised), nameof(AlertRaised))]
[JsonDerivedType(typeof(AlertAcknowledged), nameof(AlertAcknowledged))]
[JsonDerivedType(typeof(ZoneDeclared), nameof(ZoneDeclared))]
[JsonDerivedType(typeof(ZoneLifted), nameof(ZoneLifted))]
[JsonDerivedType(typeof(ResourceRequested), nameof(ResourceRequested))]
[JsonDerivedType(typeof(WeatherObserved), nameof(WeatherObserved))]
[JsonDerivedType(typeof(IncidentCommanderAssigned), nameof(IncidentCommanderAssigned))]
[JsonDerivedType(typeof(RouteReported), nameof(RouteReported))]
[JsonDerivedType(typeof(ReportAssessed), nameof(ReportAssessed))]
[JsonDerivedType(typeof(ReportLinked), nameof(ReportLinked))]
// Command and control (Phase 4)
[JsonDerivedType(typeof(IcsPositionAssigned), nameof(IcsPositionAssigned))]
[JsonDerivedType(typeof(IcsGroupFormed), nameof(IcsGroupFormed))]
[JsonDerivedType(typeof(IcsGroupDisbanded), nameof(IcsGroupDisbanded))]
[JsonDerivedType(typeof(UnitAssignedToGroup), nameof(UnitAssignedToGroup))]
[JsonDerivedType(typeof(OrderIssued), nameof(OrderIssued))]
[JsonDerivedType(typeof(OrderAcknowledged), nameof(OrderAcknowledged))]
[JsonDerivedType(typeof(OrderClosed), nameof(OrderClosed))]
[JsonDerivedType(typeof(ResourceRequestDecided), nameof(ResourceRequestDecided))]
[JsonDerivedType(typeof(ResourceRequestFulfilled), nameof(ResourceRequestFulfilled))]
[JsonDerivedType(typeof(ApprovalRequested), nameof(ApprovalRequested))]
[JsonDerivedType(typeof(ApprovalDecided), nameof(ApprovalDecided))]
[JsonDerivedType(typeof(NotificationSent), nameof(NotificationSent))]
[JsonDerivedType(typeof(NotificationAnswered), nameof(NotificationAnswered))]
// Incident Action Plan (Phase 5)
[JsonDerivedType(typeof(OperationalPeriodStarted), nameof(OperationalPeriodStarted))]
[JsonDerivedType(typeof(IapDraftCreated), nameof(IapDraftCreated))]
[JsonDerivedType(typeof(IapDraftSaved), nameof(IapDraftSaved))]
[JsonDerivedType(typeof(IapSubmitted), nameof(IapSubmitted))]
[JsonDerivedType(typeof(IapReturned), nameof(IapReturned))]
[JsonDerivedType(typeof(IapApproved), nameof(IapApproved))]
[JsonDerivedType(typeof(IapBriefed), nameof(IapBriefed))]
[JsonDerivedType(typeof(ObjectiveStatusChanged), nameof(ObjectiveStatusChanged))]
// Simulation world, as reported (Phase 6)
[JsonDerivedType(typeof(HospitalRegistered), nameof(HospitalRegistered))]
[JsonDerivedType(typeof(HospitalStatusReported), nameof(HospitalStatusReported))]
[JsonDerivedType(typeof(UnitTasked), nameof(UnitTasked))]
// Communications (Phase 7)
[JsonDerivedType(typeof(RadioCallMade), nameof(RadioCallMade))]
[JsonDerivedType(typeof(CommsLogged), nameof(CommsLogged))]
[JsonDerivedType(typeof(RepeatRequested), nameof(RepeatRequested))]
[JsonDerivedType(typeof(ReadBackConfirmed), nameof(ReadBackConfirmed))]
[JsonDerivedType(typeof(UnitChannelAssigned), nameof(UnitChannelAssigned))]
[JsonDerivedType(typeof(ChannelPatchRequested), nameof(ChannelPatchRequested))]
[JsonDerivedType(typeof(ChannelsPatched), nameof(ChannelsPatched))]
[JsonDerivedType(typeof(ChannelPatchRemoved), nameof(ChannelPatchRemoved))]
[JsonDerivedType(typeof(CallMissed), nameof(CallMissed))]
[JsonDerivedType(typeof(CallbackMade), nameof(CallbackMade))]
// Crews and safety (Phase 8)
[JsonDerivedType(typeof(CrewRostered), nameof(CrewRostered))]
[JsonDerivedType(typeof(CrewConditionReported), nameof(CrewConditionReported))]
[JsonDerivedType(typeof(CrewRehabOrdered), nameof(CrewRehabOrdered))]
[JsonDerivedType(typeof(CrewRehabEnded), nameof(CrewRehabEnded))]
[JsonDerivedType(typeof(CrewReliefRequested), nameof(CrewReliefRequested))]
[JsonDerivedType(typeof(CrewRelieved), nameof(CrewRelieved))]
[JsonDerivedType(typeof(CrewMemberStoodDown), nameof(CrewMemberStoodDown))]
[JsonDerivedType(typeof(PeerSupportArranged), nameof(PeerSupportArranged))]
[JsonDerivedType(typeof(PeerSupportGiven), nameof(PeerSupportGiven))]
[JsonDerivedType(typeof(ParRequested), nameof(ParRequested))]
[JsonDerivedType(typeof(ParReported), nameof(ParReported))]
[JsonDerivedType(typeof(EvacuationSignalled), nameof(EvacuationSignalled))]
[JsonDerivedType(typeof(EmergencyTrafficDeclared), nameof(EmergencyTrafficDeclared))]
[JsonDerivedType(typeof(MaydayDeclared), nameof(MaydayDeclared))]
[JsonDerivedType(typeof(RescueTeamDeployed), nameof(RescueTeamDeployed))]
[JsonDerivedType(typeof(MaydayResolved), nameof(MaydayResolved))]
[JsonDerivedType(typeof(OrderDeclined), nameof(OrderDeclined))]
// Truth: the world as it really is
[JsonDerivedType(typeof(WorldIncidentStarted), nameof(WorldIncidentStarted))]
[JsonDerivedType(typeof(WorldIncidentChanged), nameof(WorldIncidentChanged))]
[JsonDerivedType(typeof(WeatherChanged), nameof(WeatherChanged))]
[JsonDerivedType(typeof(UnitRadioFailed), nameof(UnitRadioFailed))]
[JsonDerivedType(typeof(RoadObstructed), nameof(RoadObstructed))]
[JsonDerivedType(typeof(UnitBrokeDown), nameof(UnitBrokeDown))]
[JsonDerivedType(typeof(RoadObstructionCleared), nameof(RoadObstructionCleared))]
// Truth: hazards, casualties, infrastructure and cascades (Phase 6)
[JsonDerivedType(typeof(HazardStarted), nameof(HazardStarted))]
[JsonDerivedType(typeof(HazardFootprintChanged), nameof(HazardFootprintChanged))]
[JsonDerivedType(typeof(HazardRateChanged), nameof(HazardRateChanged))]
[JsonDerivedType(typeof(HazardEnded), nameof(HazardEnded))]
[JsonDerivedType(typeof(HazardSitePlaced), nameof(HazardSitePlaced))]
[JsonDerivedType(typeof(CasualtyInjured), nameof(CasualtyInjured))]
[JsonDerivedType(typeof(CasualtyChanged), nameof(CasualtyChanged))]
[JsonDerivedType(typeof(HospitalCapacityChanged), nameof(HospitalCapacityChanged))]
[JsonDerivedType(typeof(PowerOutageStarted), nameof(PowerOutageStarted))]
[JsonDerivedType(typeof(PowerRestored), nameof(PowerRestored))]
[JsonDerivedType(typeof(CascadeOccurred), nameof(CascadeOccurred))]
// Truth: communications (Phase 7)
[JsonDerivedType(typeof(TransmissionLost), nameof(TransmissionLost))]
[JsonDerivedType(typeof(RadioDeadZonePlaced), nameof(RadioDeadZonePlaced))]
[JsonDerivedType(typeof(CellTowerFailed), nameof(CellTowerFailed))]
[JsonDerivedType(typeof(CellTowerRestored), nameof(CellTowerRestored))]
[JsonDerivedType(typeof(RadioBatteryChanged), nameof(RadioBatteryChanged))]
// Truth: crews (Phase 8)
[JsonDerivedType(typeof(CrewWelfareChanged), nameof(CrewWelfareChanged))]
[JsonDerivedType(typeof(FirefighterInDistress), nameof(FirefighterInDistress))]
[JsonDerivedType(typeof(DistressEnded), nameof(DistressEnded))]
[JsonDerivedType(typeof(StructureCollapsed), nameof(StructureCollapsed))]
[JsonDerivedType(typeof(HandoverInformationLost), nameof(HandoverInformationLost))]
// Engine control
[JsonDerivedType(typeof(SimulationStarted), nameof(SimulationStarted))]
[JsonDerivedType(typeof(SimulationPaused), nameof(SimulationPaused))]
[JsonDerivedType(typeof(TimeScaleChanged), nameof(TimeScaleChanged))]
public abstract record DomainEvent;

// ---- Scenario setup ----

/// <param name="AiControlled">Run by the simulation (another control room), not by the trainee.</param>
/// <param name="RadioChannel">The agency's own radio system, if it is not on ours (command needs a patch to talk to it).</param>
public sealed record AgencyRegistered(Guid AgencyId, string Name, string ShortName, AgencyType Type, bool AiControlled = false,
    string? RadioChannel = null) : DomainEvent;

/// <summary>A receiving hospital and its emergency department as known at the start; also its true starting state.</summary>
public sealed record HospitalRegistered(Guid HospitalId, string Name, GeoPoint Location, int EdCapacity, int Occupied) : DomainEvent;

public sealed record UnitRegistered(
    Guid UnitId,
    string Callsign,
    UnitType Type,
    Guid? AgencyId,
    GeoPoint Location,
    string? HomeStation,
    int CrewSize,
    IReadOnlyList<string> Capabilities) : DomainEvent;

// ---- Perceived ----

public sealed record CallReceived(
    Guid CallId,
    string Caller,
    string Summary,
    GeoPoint? Location,
    double? LocationAccuracyMeters) : DomainEvent;

public sealed record IncidentCreated(
    Guid IncidentId,
    string Number,
    IncidentType Type,
    IncidentPriority Priority,
    GeoPoint Location,
    string? Address,
    string? Name) : DomainEvent;

/// <summary>Only non-null fields changed.</summary>
public sealed record IncidentUpdated(
    Guid IncidentId,
    IncidentPriority? Priority = null,
    IncidentStatus? Status = null,
    int? CasualtiesReported = null,
    int? CasualtiesConfirmed = null,
    bool? EvacuationRequired = null,
    IReadOnlyList<string>? Threats = null) : DomainEvent;

public sealed record UnitDispatched(Guid UnitId, Guid IncidentId, Guid? OrderedBy) : DomainEvent;

// Status changes and position fixes also count as contact from the unit for comms-failure detection.

public sealed record UnitDispatchCancelled(Guid UnitId, Guid IncidentId, string? Reason) : DomainEvent;

public sealed record UnitStatusChanged(Guid UnitId, UnitStatus Status) : DomainEvent;

/// <summary>
/// The route a unit's navigation has planned (or re-planned), as sent by its mobile data terminal.
/// Lets command see where the unit intends to drive and notice when it deviates.
/// </summary>
public sealed record RouteReported(
    Guid UnitId,
    Guid? IncidentId,
    IReadOnlyList<GeoPoint> Path,
    double DistanceMeters,
    TimeSpan Eta,
    string? Reason) : DomainEvent;

/// <summary>An AVL fix as transmitted by the vehicle (Design Document §7.4).</summary>
public sealed record UnitPositionReported(
    Guid UnitId,
    GeoPoint Location,
    double SpeedKph,
    double Heading,
    TimeSpan? Eta) : DomainEvent;

public sealed record ReportReceived(
    Guid ReportId,
    Guid? IncidentId,
    ReportSource Source,
    string SourceName,
    string Claim,
    Confidence Confidence,
    VerificationStatus Verification,
    GeoPoint? Location,
    double? LocationAccuracyMeters,
    Guid? FromUnitId = null) : DomainEvent;

/// <summary>Command re-grades a report after corroboration, e.g. Reported → Confirmed.</summary>
public sealed record ReportAssessed(
    Guid ReportId,
    VerificationStatus Verification,
    Confidence Confidence) : DomainEvent;

/// <summary>A report (or call) is attributed to an incident.</summary>
public sealed record ReportLinked(Guid ReportId, Guid IncidentId) : DomainEvent;

public sealed record IncidentCommanderAssigned(Guid IncidentId, string Name, Guid? UserId) : DomainEvent;

public sealed record AlertRaised(
    Guid AlertId,
    AlertCategory Category,
    AlertSeverity Severity,
    string Title,
    string Message,
    Guid? IncidentId,
    Guid? UnitId) : DomainEvent;

public sealed record AlertAcknowledged(Guid AlertId, Guid? UserId) : DomainEvent;

public sealed record ZoneDeclared(
    Guid ZoneId,
    ZoneType Type,
    string Name,
    IReadOnlyList<GeoPoint> Boundary,
    Guid? IncidentId) : DomainEvent;

public sealed record ZoneLifted(Guid ZoneId) : DomainEvent;

public sealed record ResourceRequested(
    Guid RequestId,
    Guid IncidentId,
    string Description,
    int Quantity,
    Guid? RequestedBy,
    ResourceRequestKind Kind = ResourceRequestKind.AdditionalResources,
    UnitType? UnitType = null,
    string? Justification = null) : DomainEvent;

// ---- Command and control (Phase 4) ----

/// <summary>Someone takes an ICS position for an incident (§8.1).</summary>
public sealed record IcsPositionAssigned(Guid IncidentId, IcsRole Role, string Name) : DomainEvent;

public sealed record IcsGroupFormed(Guid IncidentId, Guid GroupId, string Name, IcsGroupKind Kind, string? Supervisor) : DomainEvent;

public sealed record IcsGroupDisbanded(Guid IncidentId, Guid GroupId) : DomainEvent;

/// <summary>Puts a unit under a group's supervisor, or (null group) back under Operations / the IC.</summary>
public sealed record UnitAssignedToGroup(Guid UnitId, Guid IncidentId, Guid? GroupId) : DomainEvent;

public sealed record OrderIssued(
    Guid OrderId,
    Guid? IncidentId,
    OrderTargetKind TargetKind,
    Guid? TargetId,
    string TargetName,
    string Text) : DomainEvent;

/// <summary>The recipient's read-back closes the loop; a garbled read-back must be corrected (§13).</summary>
public sealed record OrderAcknowledged(Guid OrderId, string ReadBack, bool Garbled = false) : DomainEvent;

/// <summary>An order is marked completed or cancelled by command.</summary>
public sealed record OrderClosed(Guid OrderId, bool Completed) : DomainEvent;

/// <param name="ExpectedAt">When the provider says the resources will be on scene.</param>
public sealed record ResourceRequestDecided(
    Guid RequestId,
    bool Approved,
    string DecidedBy,
    string? Reason,
    DateTimeOffset? ExpectedAt) : DomainEvent;

/// <summary>Requested resources have arrived in the area and are now on the roster.</summary>
public sealed record ResourceRequestFulfilled(Guid RequestId, IReadOnlyList<Guid> UnitIds) : DomainEvent;

/// <summary>Someone asks command for a decision, e.g. authority to evacuate.</summary>
public sealed record ApprovalRequested(
    Guid ApprovalId,
    Guid? IncidentId,
    string Subject,
    string Details,
    string RequestedBy) : DomainEvent;

public sealed record ApprovalDecided(Guid ApprovalId, bool Approved, string? Note) : DomainEvent;

public sealed record NotificationSent(Guid NotificationId, Guid? IncidentId, string Recipient, string Message) : DomainEvent;

public sealed record NotificationAnswered(Guid NotificationId, string Reply) : DomainEvent;

// ---- Incident Action Plan (Design Document §8.2–8.7, §19) ----

/// <summary>
/// A new planning window. If it starts before the previous period's planned end, that period is cut short.
/// </summary>
public sealed record OperationalPeriodStarted(
    Guid PeriodId,
    Guid IncidentId,
    int Number,
    DateTimeOffset Start,
    DateTimeOffset End,
    string? Focus) : DomainEvent;

/// <summary>A new version of the plan for a period, usually copied from an earlier one.</summary>
public sealed record IapDraftCreated(
    Guid PlanId,
    Guid IncidentId,
    Guid PeriodId,
    int Version,
    Guid? BasedOnId,
    string? PreparedBy,
    IapContent Content) : DomainEvent;

/// <summary>The planner saved the draft. Carries the whole document so every saved state can be replayed.</summary>
public sealed record IapDraftSaved(Guid PlanId, IapContent Content) : DomainEvent;

public sealed record IapSubmitted(Guid PlanId, string SubmittedBy) : DomainEvent;

/// <summary>The approver sent the plan back; it becomes an editable draft again.</summary>
public sealed record IapReturned(Guid PlanId, string ReturnedBy, string Comments) : DomainEvent;

/// <summary>The plan is in force. Earlier approved versions for the same period are superseded.</summary>
public sealed record IapApproved(Guid PlanId, string ApprovedBy) : DomainEvent;

/// <summary>The plan's assignments were pushed to the units as orders.</summary>
public sealed record IapBriefed(Guid PlanId, int OrdersIssued) : DomainEvent;

/// <summary>Progress on an operational objective, tracked across versions and periods.</summary>
public sealed record ObjectiveStatusChanged(Guid IncidentId, Guid ObjectiveId, ObjectiveStatus Status) : DomainEvent;

// ---- Simulation world, as reported (Phase 6) ----

/// <summary>A hospital tells command how full its emergency department is (it may already be out of date).</summary>
public sealed record HospitalStatusReported(Guid HospitalId, string Name, int Occupied, int Capacity, bool OnDiversion, string? Note) : DomainEvent;

/// <summary>
/// A unit's own agency gives it a job outside command's incidents: an AI-run control room handling its routine
/// calls, or acting on its own initiative at the major incident (Design Document §10.3).
/// </summary>
public sealed record UnitTasked(Guid UnitId, Guid TaskId, string TaskedBy, string Task, GeoPoint Location) : DomainEvent;

// ---- Communications (Phase 7, Design Document §13) ----

/// <summary>
/// Command keys up and speaks on a channel (push-to-talk). Letting go of the button before the message is finished
/// cuts it off.
/// </summary>
/// <param name="HeldSeconds">How long the button was held, or null for a message sent without push-to-talk timing.</param>
public sealed record RadioCallMade(Guid MessageId, string ChannelId, string? To, string Text, double? HeldSeconds = null) : DomainEvent;

/// <summary>
/// A message as it was heard: on a radio channel (by command, whoever was listening), on the 999 line, or in the
/// agency chat. Garbled or broken messages carry what could be made out.
/// </summary>
public sealed record CommsLogged(
    Guid MessageId,
    string ChannelId,
    string From,
    string? To,
    string Text,
    CommsQuality Quality,
    double DurationSeconds,
    Guid? UnitId = null,
    bool FromControl = false,
    IReadOnlyList<string>? Notes = null) : DomainEvent;

/// <summary>"Say again": command asks the sender of a broken or garbled message to repeat it.</summary>
public sealed record RepeatRequested(Guid MessageId) : DomainEvent;

/// <summary>Closing the loop on an order: command confirms the read-back, or says it was wrong and repeats the order.</summary>
public sealed record ReadBackConfirmed(Guid OrderId, bool Correct) : DomainEvent;

/// <summary>A unit is told to work on another channel, e.g. a tactical channel to take traffic off the main one.</summary>
public sealed record UnitChannelAssigned(Guid UnitId, string ChannelId) : DomainEvent;

/// <summary>Command asks for two channels to be patched together; a technician sets it up.</summary>
public sealed record ChannelPatchRequested(Guid PatchId, string ChannelA, string ChannelB) : DomainEvent;

/// <summary>The patch is working: what is said on either channel is heard on both.</summary>
public sealed record ChannelsPatched(Guid PatchId) : DomainEvent;

public sealed record ChannelPatchRemoved(Guid PatchId) : DomainEvent;

/// <summary>A 999 caller hung up before anyone answered. The number shows for a callback.</summary>
public sealed record CallMissed(Guid CallId, GeoPoint? Location, double? AccuracyMeters, TimeSpan Waited) : DomainEvent;

/// <summary>Command rings a missed caller back.</summary>
public sealed record CallbackMade(Guid CallId) : DomainEvent;

// ---- Crews and safety (Phase 8, Design Document §12) ----

/// <summary>One responder as the duty roster lists them.</summary>
/// <param name="Lapsed">Qualifications they trained for but whose certificate has expired.</param>
public sealed record CrewMemberInfo(Guid MemberId, string Name, CrewRole Role, IReadOnlyList<string> Qualifications,
    IReadOnlyList<string>? Lapsed = null);

/// <summary>Who is riding a unit, and how far into their shift they are.</summary>
public sealed record CrewRostered(Guid UnitId, IReadOnlyList<CrewMemberInfo> Members, TimeSpan OnShiftFor, TimeSpan ShiftLength) : DomainEvent;

/// <summary>A crew's officer tells command how the crew is doing (crews tend to understate it).</summary>
public sealed record CrewConditionReported(Guid UnitId, CrewCondition Condition, string Note) : DomainEvent;

/// <summary>Command sends a crew to the rehab area to rest, drink, cool down and be checked over.</summary>
public sealed record CrewRehabOrdered(Guid UnitId) : DomainEvent;

/// <summary>The crew reports it has finished rehab and is fit to go back to work.</summary>
public sealed record CrewRehabEnded(Guid UnitId, string Note) : DomainEvent;

/// <summary>Command asks for a fresh crew to take over a unit.</summary>
/// <param name="FullBriefing">The outgoing crew hands over properly (slower); otherwise a quick changeover that loses information.</param>
public sealed record CrewReliefRequested(Guid ReliefId, Guid UnitId, bool FullBriefing) : DomainEvent;

/// <summary>A new crew has taken over the unit.</summary>
public sealed record CrewRelieved(Guid ReliefId, Guid UnitId, IReadOnlyList<CrewMemberInfo> Members, TimeSpan ShiftLength, bool Briefed) : DomainEvent;

/// <summary>A responder is taken off the crew: exhaustion, an acute stress reaction, an injury.</summary>
public sealed record CrewMemberStoodDown(Guid UnitId, Guid MemberId, string Reason) : DomainEvent;

/// <summary>Command asks the peer support (critical incident stress) team to see a crew.</summary>
public sealed record PeerSupportArranged(Guid UnitId) : DomainEvent;

public sealed record PeerSupportGiven(Guid UnitId, string Note) : DomainEvent;

/// <summary>Command calls for a Personnel Accountability Report: every crew at the incident counts its people.</summary>
public sealed record ParRequested(Guid ParId, Guid? IncidentId, string Reason) : DomainEvent;

/// <summary>A crew's answer to a PAR.</summary>
/// <param name="Missing">Names of members the crew can't account for.</param>
public sealed record ParReported(Guid ParId, Guid UnitId, int Accounted, int Expected, IReadOnlyList<string> Missing) : DomainEvent;

/// <summary>
/// Evacuation signal (air horns and "evacuate, evacuate, evacuate" on the radio): every crew at the incident withdraws
/// from the building and reports a PAR once out. Operations there go defensive.
/// </summary>
public sealed record EvacuationSignalled(Guid SignalId, Guid IncidentId) : DomainEvent;

/// <summary>Command clears a channel for emergency traffic only (or lifts it): routine traffic must wait.</summary>
public sealed record EmergencyTrafficDeclared(string ChannelId, bool Active) : DomainEvent;

/// <summary>A Mayday as heard (or declared by command for a missing member). Unclear when the call was garbled.</summary>
public sealed record MaydayDeclared(Guid MaydayId, Guid UnitId, string? Member, string Details, bool Unclear = false) : DomainEvent;

/// <summary>Command sends a crew to rescue the firefighter in trouble (rapid intervention).</summary>
public sealed record RescueTeamDeployed(Guid MaydayId, Guid UnitId) : DomainEvent;

/// <summary>The rescue crew (or the firefighter) reports how the Mayday ended.</summary>
public sealed record MaydayResolved(Guid MaydayId, string Outcome) : DomainEvent;

/// <summary>A crew can't carry out an order, e.g. it has nobody qualified for the task.</summary>
public sealed record OrderDeclined(Guid OrderId, string Reason) : DomainEvent;

/// <summary>
/// Weather as reported to command by a met service or station. May lag or differ from the true
/// <see cref="WeatherChanged"/>; the COP's weather view shows only this.
/// </summary>
public sealed record WeatherObserved(
    string Source,
    GeoPoint Location,
    double WindFromDegrees,
    double WindSpeedMps,
    double TemperatureC,
    double RelativeHumidity) : DomainEvent;

// ---- Truth ----

/// <summary>Something really started happening. The COP only learns of it through later perceived events.</summary>
public sealed record WorldIncidentStarted(
    Guid WorldIncidentId,
    IncidentType Type,
    GeoPoint Location,
    double Severity,
    int ActualCasualties) : DomainEvent;

public sealed record WorldIncidentChanged(
    Guid WorldIncidentId,
    double Severity,
    int ActualCasualties,
    bool Extinguished) : DomainEvent;

/// <param name="WindFromDegrees">Meteorological convention: the direction the wind blows from.</param>
public sealed record WeatherChanged(
    double WindFromDegrees,
    double WindSpeedMps,
    double TemperatureC,
    double RelativeHumidity) : DomainEvent;

/// <summary>A unit's radio and data link really fail (or recover). Command only notices the silence.</summary>
public sealed record UnitRadioFailed(Guid UnitId, bool Failed) : DomainEvent;

/// <summary>
/// A road is really blocked (crash, fallen tree, flooding) but nobody has reported it yet.
/// Drivers find out when they reach it.
/// </summary>
public sealed record RoadObstructed(Guid ObstructionId, IReadOnlyList<GeoPoint> Line, string Description) : DomainEvent;

public sealed record RoadObstructionCleared(Guid ObstructionId) : DomainEvent;

/// <summary>A vehicle really breaks down. AVL shows it stop; the crew reports the cause a little later.</summary>
public sealed record UnitBrokeDown(Guid UnitId, string Fault) : DomainEvent;

/// <summary>A hazard really begins: fire takes hold, water starts rising, a toxic release starts (§10.4).</summary>
/// <param name="Rate">Flood: inflow in m³/s. Plume: release rate in kg/s. Fire: unused (0).</param>
/// <param name="Inventory">Plume: kilograms available to release; the release stops when it runs out.</param>
public sealed record HazardStarted(
    Guid HazardId,
    Guid? WorldIncidentId,
    HazardKind Kind,
    GeoPoint Origin,
    string Description,
    double Rate = 0,
    string? Substance = null,
    double Inventory = 0) : DomainEvent;

/// <summary>Where the hazard really is now: the burning area, the flooded area, the toxic cloud above its lowest threshold.</summary>
/// <param name="Areas">Outer rings of the affected areas.</param>
/// <param name="Intensity">Fire: area burning now (m²). Flood: deepest water (m). Plume: release rate (kg/s).</param>
public sealed record HazardFootprintChanged(
    Guid HazardId,
    IReadOnlyList<IReadOnlyList<GeoPoint>> Areas,
    double AreaSquareMeters,
    double Intensity) : DomainEvent;

/// <summary>A release rate or inflow really changes (§10.6): drums rupture, a river keeps rising, a leak is plugged.</summary>
public sealed record HazardRateChanged(Guid HazardId, double Rate, string Reason) : DomainEvent;

public sealed record HazardEnded(Guid HazardId, string Reason) : DomainEvent;

/// <summary>Something a hazard can set off: a chemical store, a substation (§10.5). Nothing happens until a hazard reaches it.</summary>
/// <param name="Quantity">Chemical store: kilograms held.</param>
/// <param name="ServiceRadiusMeters">Substation: the area it supplies.</param>
public sealed record HazardSitePlaced(
    Guid SiteId,
    HazardSiteKind Kind,
    string Name,
    GeoPoint Location,
    string? Substance = null,
    double Quantity = 0,
    double ServiceRadiusMeters = 0) : DomainEvent;

/// <summary>Someone is really hurt. Command knows only what callers, crews and hospitals tell it.</summary>
public sealed record CasualtyInjured(Guid CasualtyId, Guid? WorldIncidentId, GeoPoint Location, Triage Triage, string Cause) : DomainEvent;

public sealed record CasualtyChanged(Guid CasualtyId, Triage Triage, CasualtyState State, Guid? HospitalId = null) : DomainEvent;

/// <summary>A hospital's true emergency capacity changes: surge plan activated, running on generators.</summary>
public sealed record HospitalCapacityChanged(Guid HospitalId, int Capacity, string Reason) : DomainEvent;

public sealed record PowerOutageStarted(Guid OutageId, GeoPoint Centre, double RadiusMeters, string Cause) : DomainEvent;

public sealed record PowerRestored(Guid OutageId) : DomainEvent;

/// <summary>
/// One link in a chain of knock-on effects (§10.5), e.g. "Power out around the substation" → "6 junctions' traffic
/// signals dark" → "Ambulance 14 delayed 2.5 min". Recorded for the instructor and the AAR.
/// </summary>
public sealed record CascadeOccurred(string Cause, string Effect, Guid? UnitId = null, Guid? HazardId = null) : DomainEvent;

/// <summary>A transmission nobody heard: out of coverage, a dead battery, a channel nobody monitors, a full queue.</summary>
public sealed record TransmissionLost(Guid MessageId, string ChannelId, string From, string Text, string Reason) : DomainEvent;

/// <summary>A radio black spot: basements, underpasses, between tall buildings.</summary>
public sealed record RadioDeadZonePlaced(Guid ZoneId, GeoPoint Centre, double RadiusMeters, string Description) : DomainEvent;

/// <summary>A mobile mast goes down: phones (999 calls) and mobile data (AVL, status) stop working around it.</summary>
public sealed record CellTowerFailed(Guid SiteId, string Cause) : DomainEvent;

public sealed record CellTowerRestored(Guid SiteId) : DomainEvent;

/// <summary>A crew's handheld radio battery level really changes (0–1), e.g. set by a scenario.</summary>
public sealed record RadioBatteryChanged(Guid UnitId, double Level) : DomainEvent;

// ---- Truth: crews (Phase 8) ----

/// <summary>A crew's real fatigue and stress (0–1, crew average) moved into a new band: fresh, tired, exhausted.</summary>
public sealed record CrewWelfareChanged(Guid UnitId, double Fatigue, double Stress, string Band) : DomainEvent;

/// <summary>A firefighter is really in trouble: trapped, lost, out of air. Command only knows if a Mayday gets through.</summary>
/// <param name="AirMinutes">Breathing air left when it happened.</param>
public sealed record FirefighterInDistress(Guid DistressId, Guid UnitId, Guid MemberId, string Cause, GeoPoint Location, double AirMinutes) : DomainEvent;

public sealed record DistressEnded(Guid DistressId, string Outcome) : DomainEvent;

/// <summary>Part of a burning building really comes down; anyone working inside nearby is caught.</summary>
public sealed record StructureCollapsed(Guid HazardId, GeoPoint Location, string Description) : DomainEvent;

/// <summary>What a relief crew was never told at a rushed handover.</summary>
public sealed record HandoverInformationLost(Guid UnitId, IReadOnlyList<string> Items) : DomainEvent;

// ---- Engine control ----

public sealed record SimulationStarted(double TimeScale) : DomainEvent;

public sealed record SimulationPaused : DomainEvent;

public sealed record TimeScaleChanged(double TimeScale) : DomainEvent;
