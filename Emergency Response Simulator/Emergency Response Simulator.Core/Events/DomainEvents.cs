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
// Multi-agency and interoperability (Phase 10)
[JsonDerivedType(typeof(JurisdictionDeclared), nameof(JurisdictionDeclared))]
[JsonDerivedType(typeof(MutualAidAgreementRegistered), nameof(MutualAidAgreementRegistered))]
[JsonDerivedType(typeof(UnifiedCommandInvited), nameof(UnifiedCommandInvited))]
[JsonDerivedType(typeof(UnifiedCommandJoined), nameof(UnifiedCommandJoined))]
[JsonDerivedType(typeof(UnifiedCommandDeclined), nameof(UnifiedCommandDeclined))]
[JsonDerivedType(typeof(LeadAgencyChanged), nameof(LeadAgencyChanged))]
[JsonDerivedType(typeof(AgencyAssistanceRequested), nameof(AgencyAssistanceRequested))]
[JsonDerivedType(typeof(AgencyAssistanceDecided), nameof(AgencyAssistanceDecided))]
[JsonDerivedType(typeof(AgencyBriefingRequested), nameof(AgencyBriefingRequested))]
[JsonDerivedType(typeof(InformationShared), nameof(InformationShared))]
[JsonDerivedType(typeof(AgencyRelationChanged), nameof(AgencyRelationChanged))]
[JsonDerivedType(typeof(DisputeRaised), nameof(DisputeRaised))]
[JsonDerivedType(typeof(DisputeResolutionChosen), nameof(DisputeResolutionChosen))]
[JsonDerivedType(typeof(DisputeResolved), nameof(DisputeResolved))]
[JsonDerivedType(typeof(EocActivationRequested), nameof(EocActivationRequested))]
[JsonDerivedType(typeof(EocSeatStaffed), nameof(EocSeatStaffed))]
[JsonDerivedType(typeof(EocLevelReached), nameof(EocLevelReached))]
[JsonDerivedType(typeof(SitrepRequested), nameof(SitrepRequested))]
[JsonDerivedType(typeof(SitrepSubmitted), nameof(SitrepSubmitted))]
[JsonDerivedType(typeof(PublicStatementIssued), nameof(PublicStatementIssued))]
[JsonDerivedType(typeof(MediaEnquiryReceived), nameof(MediaEnquiryReceived))]
// Sessions and participants (Phase 11)
[JsonDerivedType(typeof(SessionOpened), nameof(SessionOpened))]
[JsonDerivedType(typeof(SessionEnded), nameof(SessionEnded))]
[JsonDerivedType(typeof(ParticipantJoined), nameof(ParticipantJoined))]
[JsonDerivedType(typeof(ParticipantLeft), nameof(ParticipantLeft))]
[JsonDerivedType(typeof(RoleAssigned), nameof(RoleAssigned))]
[JsonDerivedType(typeof(RoleHandedOver), nameof(RoleHandedOver))]
[JsonDerivedType(typeof(RoleHandoverBegun), nameof(RoleHandoverBegun))]
[JsonDerivedType(typeof(RoleHandoverCancelled), nameof(RoleHandoverCancelled))]
// Instructor console (Phase 11 M4): ground truth, so trainees never see the hand behind the scenario
[JsonDerivedType(typeof(InjectFired), nameof(InjectFired))]
[JsonDerivedType(typeof(InjectHeld), nameof(InjectHeld))]
[JsonDerivedType(typeof(InjectReleased), nameof(InjectReleased))]
[JsonDerivedType(typeof(InjectSkipped), nameof(InjectSkipped))]
[JsonDerivedType(typeof(InstructorRolePlay), nameof(InstructorRolePlay))]
[JsonDerivedType(typeof(AiRoleTakenOver), nameof(AiRoleTakenOver))]
[JsonDerivedType(typeof(AiRoleHandedBack), nameof(AiRoleHandedBack))]
[JsonDerivedType(typeof(InstructorBookmarked), nameof(InstructorBookmarked))]
// AI role players (Phase 11 M5)
[JsonDerivedType(typeof(PoliticalCallReceived), nameof(PoliticalCallReceived))]
[JsonDerivedType(typeof(PoliticalCallAnswered), nameof(PoliticalCallAnswered))]
[JsonDerivedType(typeof(PoliticalFallout), nameof(PoliticalFallout))]
[JsonDerivedType(typeof(AgencyControlChanged), nameof(AgencyControlChanged))]
[JsonDerivedType(typeof(AgencyRequestAnswered), nameof(AgencyRequestAnswered))]
// Observer mode (Phase 11 M6)
[JsonDerivedType(typeof(AssessmentNoteRecorded), nameof(AssessmentNoteRecorded))]
[JsonDerivedType(typeof(ObserverTruthChanged), nameof(ObserverTruthChanged))]
// Voice (Phase 11 M7)
[JsonDerivedType(typeof(RadioAudioRecorded), nameof(RadioAudioRecorded))]
[JsonDerivedType(typeof(VoiceConsentRecorded), nameof(VoiceConsentRecorded))]
// Multiplayer completion (Phase 12)
[JsonDerivedType(typeof(ExerciseControlMessage), nameof(ExerciseControlMessage))]
[JsonDerivedType(typeof(PublicConfidenceChanged), nameof(PublicConfidenceChanged))]
[JsonDerivedType(typeof(PoliticalVisitDecided), nameof(PoliticalVisitDecided))]
// Truth: multi-agency (Phase 10)
[JsonDerivedType(typeof(InformationHeld), nameof(InformationHeld))]
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
// Plume & hazard models (Phase 13)
[JsonDerivedType(typeof(PlumePredicted), nameof(PlumePredicted))]
[JsonDerivedType(typeof(PlumeProjected), nameof(PlumeProjected))]
// Public, media, political & legal (Phase 14)
[JsonDerivedType(typeof(VulnerableResidentsReported), nameof(VulnerableResidentsReported))]
[JsonDerivedType(typeof(AssistedEvacuationRequested), nameof(AssistedEvacuationRequested))]
[JsonDerivedType(typeof(AssistedEvacuationProgress), nameof(AssistedEvacuationProgress))]
[JsonDerivedType(typeof(VulnerableStranded), nameof(VulnerableStranded))]
[JsonDerivedType(typeof(PetOwnersReturned), nameof(PetOwnersReturned))]
[JsonDerivedType(typeof(LootingOccurred), nameof(LootingOccurred))]
[JsonDerivedType(typeof(VolunteersArrived), nameof(VolunteersArrived))]
[JsonDerivedType(typeof(VolunteersManaged), nameof(VolunteersManaged))]
[JsonDerivedType(typeof(VolunteersSelfDeployed), nameof(VolunteersSelfDeployed))]
[JsonDerivedType(typeof(MissingPersonReported), nameof(MissingPersonReported))]
[JsonDerivedType(typeof(FamilyAssistanceCentreOpened), nameof(FamilyAssistanceCentreOpened))]
[JsonDerivedType(typeof(MissingPersonTraced), nameof(MissingPersonTraced))]
[JsonDerivedType(typeof(RumourSpreading), nameof(RumourSpreading))]
[JsonDerivedType(typeof(RumourTruth), nameof(RumourTruth))]
[JsonDerivedType(typeof(RumourRebutted), nameof(RumourRebutted))]
[JsonDerivedType(typeof(DenialExposed), nameof(DenialExposed))]
[JsonDerivedType(typeof(PressConferenceHeld), nameof(PressConferenceHeld))]
[JsonDerivedType(typeof(DecisionLogged), nameof(DecisionLogged))]
[JsonDerivedType(typeof(LegalExposure), nameof(LegalExposure))]
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
/// <param name="Jurisdiction">The area it answers for (Phase 10), by name.</param>
/// <param name="Priority">What its commander pushes for at a joint incident.</param>
/// <param name="PriorityTerms">Words an IAP objective must use for the agency to agree it covers that priority.</param>
public sealed record AgencyRegistered(Guid AgencyId, string Name, string ShortName, AgencyType Type, bool AiControlled = false,
    string? RadioChannel = null, string? Jurisdiction = null, string? Priority = null, IReadOnlyList<string>? PriorityTerms = null) : DomainEvent;

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
/// <param name="UserId">The session participant who fills it, when it is one (Phase 11 M3).</param>
public sealed record IcsPositionAssigned(Guid IncidentId, IcsRole Role, string Name, Guid? UserId = null) : DomainEvent;

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
/// <param name="Quantity">How many the provider will send, when fewer than asked (Phase 10: agreement caps, cover kept back).</param>
public sealed record ResourceRequestDecided(
    Guid RequestId,
    bool Approved,
    string DecidedBy,
    string? Reason,
    DateTimeOffset? ExpectedAt,
    int? Quantity = null) : DomainEvent;

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

// ---- Multi-agency and interoperability (Phase 10, Design Document §14) ----

/// <summary>An area some authority answers for: city, county, regional (state) or national (federal).</summary>
/// <param name="RequiresConsent">Measures inside need the authority's agreement (a port estate, a canal basin).</param>
public sealed record JurisdictionDeclared(
    string JurisdictionId,
    string Name,
    JurisdictionLevel Level,
    string Authority,
    Guid? AuthorityAgencyId,
    IReadOnlyList<GeoPoint> Boundary,
    bool RequiresConsent = false,
    string? Powers = null) : DomainEvent;

/// <summary>A standing mutual-aid agreement with a neighbouring service.</summary>
/// <param name="ExcludedTasks">Tasks it doesn't cover, as words that would appear in an order.</param>
public sealed record MutualAidAgreementRegistered(
    Guid AgreementId,
    string Provider,
    AgencyType ProviderType,
    string? ProviderJurisdiction,
    IReadOnlyList<UnitType> UnitTypes,
    int MaxUnits,
    string ApprovedBy,
    TimeSpan DecisionTime,
    TimeSpan LeadTime,
    bool NeedsMajorEmergency,
    string Terms,
    IReadOnlyList<string> ExcludedTasks,
    string? RadioChannel = null) : DomainEvent;

/// <summary>Command asks an agency to send its commander to share command of an incident.</summary>
public sealed record UnifiedCommandInvited(Guid IncidentId, Guid AgencyId) : DomainEvent;

/// <summary>An agency's commander is at the command post, sharing command.</summary>
public sealed record UnifiedCommandJoined(Guid IncidentId, Guid? AgencyId, string Agency, string Commander, string? Priority) : DomainEvent;

public sealed record UnifiedCommandDeclined(Guid IncidentId, Guid AgencyId, string Reason) : DomainEvent;

/// <summary>Which agency leads the response changes, e.g. to the police once a fire scene becomes a crime scene.</summary>
public sealed record LeadAgencyChanged(Guid IncidentId, string Agency, string Reason) : DomainEvent;

/// <summary>Command asks another agency to commit some of its own units to an incident.</summary>
public sealed record AgencyAssistanceRequested(Guid RequestId, Guid AgencyId, Guid IncidentId, int Quantity, string Task) : DomainEvent;

/// <summary>The agency's answer: which units it releases to command (perhaps fewer than asked), and why.</summary>
public sealed record AgencyAssistanceDecided(Guid RequestId, int Granted, IReadOnlyList<Guid> UnitIds, string Reason) : DomainEvent;

/// <summary>Command asks an agency to share what it knows.</summary>
public sealed record AgencyBriefingRequested(Guid AgencyId) : DomainEvent;

/// <summary>Something an agency knew reaches command (the claim itself arrives as the report).</summary>
/// <param name="Via">How it came: "unified command", "liaison officer", "EOC", "briefing", "volunteered".</param>
public sealed record InformationShared(Guid ItemId, Guid ReportId, string Agency, string Via) : DomainEvent;

/// <summary>How an agency feels about command changes, and command can tell.</summary>
public sealed record AgencyRelationChanged(Guid AgencyId, AgencyStanding Standing, string Reason) : DomainEvent;

/// <summary>An agency objects: it claims the lead, the area, or the limits of an agreement.</summary>
public sealed record DisputeRaised(
    Guid DisputeId,
    Guid? IncidentId,
    DisputeKind Kind,
    Guid? AgencyId,
    string Agency,
    string Subject,
    string Position,
    string Basis,
    Guid? ZoneId = null,
    Guid? OrderId = null) : DomainEvent;

/// <summary>Command decides how to answer an objection.</summary>
public sealed record DisputeResolutionChosen(Guid DisputeId, DisputeResolution Resolution) : DomainEvent;

/// <summary>The dispute is settled: conceded, agreed, overruled or decided by the EOC.</summary>
public sealed record DisputeResolved(Guid DisputeId, DisputeResolution Resolution, string Outcome) : DomainEvent;

/// <summary>Command asks for the Emergency Operations Centre to be stood up (or down) to a level.</summary>
public sealed record EocActivationRequested(EocLevel Level, string? Reason) : DomainEvent;

/// <summary>Someone has arrived at the EOC and taken their seat.</summary>
public sealed record EocSeatStaffed(string Function, string? Agency) : DomainEvent;

/// <summary>The EOC is fully staffed for a level (or stood down to it).</summary>
public sealed record EocLevelReached(EocLevel Level) : DomainEvent;

/// <summary>The EOC wants a situation report by a time.</summary>
public sealed record SitrepRequested(DateTimeOffset DueAt) : DomainEvent;

public sealed record SitrepSubmitted(Guid SitrepId, string Text) : DomainEvent;

/// <summary>A statement to the public: ours, or another agency's that may contradict it.</summary>
/// <param name="Joint">Agreed through a joint information centre.</param>
/// <param name="Conflicting">Another agency's own statement that contradicts the official line.</param>
public sealed record PublicStatementIssued(Guid StatementId, Guid? IncidentId, string Issuer, string Text, bool Joint,
    bool Conflicting = false) : DomainEvent;

/// <summary>A journalist wants to know what is going on.</summary>
public sealed record MediaEnquiryReceived(Guid EnquiryId, Guid? IncidentId, string Outlet, string Question) : DomainEvent;

// ---- Sessions and participants (Phase 11 M2) ----

/// <summary>An instructor opened a networked session on a scenario; people can join it from the lobby.</summary>
public sealed record SessionOpened(string Scenario, string Title, Guid CreatedBy) : DomainEvent;

/// <summary>The instructor ended the session: no more commands; the record stays for the review.</summary>
public sealed record SessionEnded(string Reason, Guid? EndedBy) : DomainEvent;

/// <summary>Someone joined (or rejoined) the session, in a role.</summary>
public sealed record ParticipantJoined(Guid ParticipantId, string DisplayName, UserRole Role) : DomainEvent;

/// <summary>Someone left, or dropped and didn't come back in time.</summary>
public sealed record ParticipantLeft(Guid ParticipantId, string Reason) : DomainEvent;

/// <summary>The instructor put a participant in a role.</summary>
public sealed record RoleAssigned(Guid ParticipantId, UserRole Role) : DomainEvent;

/// <summary>The holder of a role handed it to someone else, and took <paramref name="FromNewRole"/> themselves.</summary>
/// <param name="HandoverId">The handover begun earlier (Phase 11 M3); none for an immediate one in the lobby.</param>
/// <param name="Briefed">A full briefing was given. Otherwise it was a quick changeover and <paramref name="Items"/> were never passed on.</param>
/// <param name="Items">The role's open business at the time: what the briefing covered, or what a quick changeover left out.</param>
public sealed record RoleHandedOver(Guid FromParticipantId, Guid ToParticipantId, UserRole Role, UserRole FromNewRole,
    Guid? HandoverId = null, bool Briefed = true, IReadOnlyList<string>? Items = null) : DomainEvent;

/// <summary>
/// A role holder began handing over (Phase 11 M3, the Phase 8 handover model): they keep the role until
/// <paramref name="CompletesAt"/> (simulation time), longer with a full briefing than with a quick changeover.
/// </summary>
public sealed record RoleHandoverBegun(Guid HandoverId, Guid FromParticipantId, Guid ToParticipantId, UserRole Role, UserRole FromNewRole,
    bool FullBriefing, DateTimeOffset CompletesAt) : DomainEvent;

/// <summary>A handover that didn't finish: one of the two left, or their roles changed meanwhile.</summary>
public sealed record RoleHandoverCancelled(Guid HandoverId, string Reason) : DomainEvent;

// ---- Instructor console (Phase 11 M4). All ground truth: trainees never see the hand behind the scenario. ----

/// <summary>An inject went off: on time from the script, early at the instructor's word, or ad hoc from a template.</summary>
/// <param name="Number">Its number in the scenario script; 0 for an ad-hoc inject.</param>
/// <param name="Early">Fired by the instructor before its scripted time.</param>
/// <param name="Template">The template an ad-hoc inject was made from.</param>
public sealed record InjectFired(int Number, string Description, bool Early = false, string? Template = null) : DomainEvent;

/// <summary>The instructor held a scripted inject: it won't go off at its time until released.</summary>
public sealed record InjectHeld(int Number, string Description) : DomainEvent;

/// <summary>A held inject is back on the script; if its time has passed it goes off at once.</summary>
public sealed record InjectReleased(int Number, string Description) : DomainEvent;

/// <summary>The instructor dropped a scripted inject from this run.</summary>
public sealed record InjectSkipped(int Number, string Description) : DomainEvent;

/// <summary>
/// The instructor spoke or decided as someone in the world: a crew, an agency, a caller, a journalist, an official, or
/// an AI role they had taken over. <paramref name="Refers"/> are the ids of what it produced in the world (the message,
/// call, enquiry or decision), so the review can mark those as instructor role-play.
/// </summary>
public sealed record InstructorRolePlay(Guid RolePlayId, string Kind, string As, string? Channel, string Text, IReadOnlyList<Guid> Refers) : DomainEvent;

/// <summary>The instructor took over a role the simulation was playing; its decisions wait for them.</summary>
public sealed record AiRoleTakenOver(string RoleKey, string Name) : DomainEvent;

/// <summary>The simulation plays the role again.</summary>
public sealed record AiRoleHandedBack(string RoleKey, string Name) : DomainEvent;

/// <summary>The instructor marked a moment to come back to in the debrief.</summary>
public sealed record InstructorBookmarked(Guid BookmarkId, string Note, Guid? ParticipantId = null) : DomainEvent;

/// <summary>Truth: something an agency knows, which command only learns if the agency shares it (information silos).</summary>
/// <param name="Urgent">Matters to life safety: shared first, even by an agency that is not cooperating well.</param>
public sealed record InformationHeld(Guid ItemId, Guid AgencyId, string Claim, GeoPoint? Location, bool Urgent = false) : DomainEvent;

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
/// <param name="Who">Who they were, when it matters to the review (Phase 14): "an elderly resident", "a rough-sleeper", "a volunteer".</param>
public sealed record CasualtyInjured(Guid CasualtyId, Guid? WorldIncidentId, GeoPoint Location, Triage Triage, string Cause, string? Who = null) : DomainEvent;

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

// ---- AI role players (Phase 11 M5) ----

/// <summary>
/// A politician or their office rings command about an incident: the local TD, the council's chief executive, the
/// minister's office. <paramref name="Pressure"/> 1–3 rises each time a call goes unanswered.
/// </summary>
/// <param name="To">Who they asked for: the Incident Commander, or the PIO once there is one.</param>
/// <param name="ZoneId">The protective zone a community leader rings about (Phase 14); engaging them helps there.</param>
public sealed record PoliticalCallReceived(Guid CallId, Guid? IncidentId, PoliticalOffice Office, string Caller, string Message, int Pressure, string To,
    Guid? ZoneId = null) : DomainEvent;

/// <summary>Command called a politician back (C2).</summary>
public sealed record PoliticalCallAnswered(Guid CallId, string Answer) : DomainEvent;

/// <summary>Ground truth: a call went unanswered too long and the pressure moved up, or (at the top) went public.</summary>
public sealed record PoliticalFallout(Guid CallId, Guid? IncidentId, int Pressure, string What) : DomainEvent;

/// <summary>
/// Who runs an agency's control room changed (Phase 11 M5): the simulation, or a participant playing it (an agency
/// chief). A participant may task its units and answers for it when command asks it for units or into unified command.
/// </summary>
public sealed record AgencyControlChanged(Guid AgencyId, AgencyController Controller, Guid? ParticipantId = null, string? By = null) : DomainEvent;

/// <summary>The participant running an agency answered command's request to it: for units, or to join unified command (C2).</summary>
/// <param name="ItemId">The assistance request's id, or the invitation's (<see cref="Model.Interagency.InvitationId"/>).</param>
public sealed record AgencyRequestAnswered(Guid ItemId, Guid AgencyId, bool Approve, int? Quantity, string? Text) : DomainEvent;

// ---- Observer mode (Phase 11 M6) ----

/// <summary>
/// An observer's (or the instructor's) note on a participant, with an optional rating against the rubric, tagged to the
/// event it is about. Ground truth: trainees never see it during the exercise; assessors all do.
/// </summary>
/// <param name="AboutSequence">The event the note is about, e.g. the participant's command; null for a general note.</param>
/// <param name="Area">The competency area it assesses (Phase 12); null for an overall impression.</param>
public sealed record AssessmentNoteRecorded(Guid NoteId, Guid ParticipantId, AssessmentRating? Rating, string Note, long? AboutSequence = null,
    Competency? Area = null) : DomainEvent;

/// <summary>The instructor let an observer see ground truth, or took it away.</summary>
public sealed record ObserverTruthChanged(Guid ParticipantId, bool Granted) : DomainEvent;

// ---- Voice (Phase 11 M7) ----

/// <summary>
/// A spoken transmission was recorded, as it was heard on the channel. Its transcript is what entered the radio
/// traffic (<see cref="RadioCallMade"/> <paramref name="MessageId"/>); the audio is attached for playback.
/// </summary>
/// <param name="Transcript">What the speech recogniser made of it; null when there is no recogniser or it heard nothing.</param>
/// <param name="MessageId">The radio call the transcript became, when it became one.</param>
public sealed record RadioAudioRecorded(Guid ClipId, string ChannelId, Guid? SpeakerId, string Speaker, double DurationSeconds, CommsQuality Quality,
    string? Transcript = null, Guid? MessageId = null) : DomainEvent;

/// <summary>A participant agreed to have what they say on the radio recorded (kept for <paramref name="RetentionDays"/>), or didn't.</summary>
public sealed record VoiceConsentRecorded(Guid ParticipantId, bool Consented, int RetentionDays) : DomainEvent;

// ---- Multiplayer completion (Phase 12) ----

/// <summary>
/// The instructor speaking to the players as the exercise's controller, outside the world ("EXERCISE: pause for a
/// teaching point", "Alex, you're the IC now"): to everyone, or to one participant. It is not radio traffic and nobody
/// in the simulation hears it.
/// </summary>
/// <param name="ToParticipantId">One participant; null for everyone.</param>
public sealed record ExerciseControlMessage(Guid MessageId, string Text, Guid? ToParticipantId = null) : DomainEvent;

/// <summary>
/// Ground truth: how far the public trusts the response (0–100), and what moved it: timely statements and clear
/// protective advice raise it; journalists left waiting, contradictions and a minister going public lower it.
/// </summary>
public sealed record PublicConfidenceChanged(int Confidence, int Change, string Reason) : DomainEvent;

/// <summary>
/// Ground truth: command answered a politician's request to visit the scene, and whether the hazard there was still
/// live (a visit inside a live cordon puts a VIP party where the crews need to work).
/// </summary>
public sealed record PoliticalVisitDecided(Guid ApprovalId, Guid? IncidentId, string Visitor, bool Approved, bool HazardLive) : DomainEvent;

// ---- Plume & hazard models (Phase 13) ----

/// <summary>
/// Ground truth: the dispersion model's picture of where the real cloud is now, from the true weather and the true
/// release. Recorded so a replay rebuilds the same cloud without calling the model service again.
/// </summary>
/// <param name="Model">Which model produced it, e.g. "gaussian-plume-v1" or the in-engine fallback.</param>
/// <param name="Reason">Why it was run now: the first prediction, a wind shift, a revised release rate, or the interval.</param>
public sealed record PlumePredicted(
    Guid HazardId,
    string Model,
    string Substance,
    string MaterialClass,
    IReadOnlyList<Contracts.PlumeZone> Zones,
    double FrontMeters,
    double RearMeters,
    double EffectiveHeightMeters,
    bool DenseGas,
    string StabilityClass,
    string Advisory,
    string Reason) : DomainEvent;

/// <summary>
/// Command ran a plume projection on what it knows: the reported release and the reported weather, which may not be
/// the truth. Perceived, so it belongs on the COP and in the review as the picture command was working from.
/// </summary>
/// <param name="Impacts">What the zones cover on the ground: schools, hospitals, homes (Phase 13's GIS overlay).</param>
/// <param name="Automatic">Re-run by the simulator because the reported wind or release rate moved, rather than asked for.</param>
/// <param name="Supersedes">The projection this replaces, if any.</param>
public sealed record PlumeProjected(
    Guid ProjectionId,
    Guid? IncidentId,
    GeoPoint ReleaseLocation,
    string Substance,
    string MaterialClass,
    double ReleaseRateKgPerSecond,
    double DurationMinutes,
    double WindFromDegrees,
    double WindSpeedMps,
    string StabilityClass,
    IReadOnlyList<Contracts.PlumeZone> Zones,
    IReadOnlyList<PlumeImpact> Impacts,
    string Model,
    string Advisory,
    bool Automatic = false,
    Guid? Supersedes = null) : DomainEvent;

/// <summary>
/// What one zone of a projection covers on the ground (Phase 13). Named places are listed; homes are only counted,
/// because a street of them is a number, not a list.
/// </summary>
/// <param name="Kind">What they are, for the message: "School", "Hospital", "Home"…</param>
/// <param name="Names">Up to a handful of names, for the alert and the panel; empty when they have none.</param>
public sealed record PlumeImpact(string Level, string LayerKey, string Kind, int Count, IReadOnlyList<string> Names);

// ---- Public, media, political & legal (Phase 14) ----

/// <summary>
/// What a register or a local source says about the vulnerable people in a protective zone: the council's
/// vulnerable-persons register (elderly and disabled people who asked to be on it), or a community leader who knows
/// the area (rough-sleepers, pet owners). Nobody's register is complete.
/// </summary>
public sealed record VulnerableResidentsReported(Guid ZoneId, string Source, int Elderly, int MobilityImpaired, int PetOwners, int Homeless)
    : DomainEvent;

/// <summary>Command asked for the people who can't get out of a zone on their own to be got out: ambulance transport, care staff, a bus, outreach.</summary>
public sealed record AssistedEvacuationRequested(Guid ZoneId) : DomainEvent;

/// <summary>How the assisted evacuation of a zone is going, as the teams doing it report.</summary>
public sealed record AssistedEvacuationProgress(Guid ZoneId, int Helped, int Remaining) : DomainEvent;

/// <summary>Ground truth: people who can't leave on their own are still in an evacuation zone long after the order.</summary>
public sealed record VulnerableStranded(Guid ZoneId, int Count, string Who) : DomainEvent;

/// <summary>Ground truth: evacuated pet owners went back past the cordon for their animals.</summary>
public sealed record PetOwnersReturned(Guid ZoneId, int Count) : DomainEvent;

/// <summary>Ground truth: empty homes in an evacuation zone are being broken into.</summary>
/// <param name="Policed">A garda unit was there or a police cordon covered it: it was stopped quickly.</param>
public sealed record LootingOccurred(Guid ZoneId, GeoPoint Location, bool Policed) : DomainEvent;

/// <summary>Members of the public have turned up at the cordon wanting to help.</summary>
public sealed record VolunteersArrived(Guid IncidentId, int Count, GeoPoint Location) : DomainEvent;

/// <summary>Command registered the volunteers at an incident, or turned them away.</summary>
public sealed record VolunteersManaged(Guid IncidentId, VolunteerDecision Decision) : DomainEvent;

/// <summary>Volunteers nobody took charge of went past the cordon on their own.</summary>
public sealed record VolunteersSelfDeployed(Guid IncidentId, int Count) : DomainEvent;

/// <summary>A relative rings the public enquiry line about someone they can't reach.</summary>
public sealed record MissingPersonReported(Guid EnquiryId, Guid? IncidentId, string Caller, string Missing, string LastKnown) : DomainEvent;

/// <summary>Command opened a family assistance centre: news for relatives, registration for evacuees, support for families.</summary>
public sealed record FamilyAssistanceCentreOpened(Guid CentreId, Guid? IncidentId, string Name, GeoPoint Location) : DomainEvent;

/// <summary>The centre traced someone a relative asked about, and whether the family was supported (Phase 14: victim assistance).</summary>
public sealed record MissingPersonTraced(Guid EnquiryId, TraceOutcome Outcome, string Detail, bool Supported) : DomainEvent;

/// <summary>Social-media monitoring picked up a rumour, or saw it spread further.</summary>
public sealed record RumourSpreading(Guid RumourId, Guid? IncidentId, string Claim, string Topic, int ReachThousands) : DomainEvent;

/// <summary>Ground truth: whether a rumour is true. Command never sees this; denying a true one backfires.</summary>
public sealed record RumourTruth(Guid RumourId, bool IsTrue) : DomainEvent;

/// <summary>Command put out a rebuttal of a rumour.</summary>
public sealed record RumourRebutted(Guid RumourId, string Text) : DomainEvent;

/// <summary>It came out that the authorities denied something that was true: misinformation, from command.</summary>
public sealed record DenialExposed(Guid RumourId, string What) : DomainEvent;

/// <summary>
/// A press conference: the spokespeople in front of the cameras, a statement, questions. It answers every journalist
/// waiting, deals with the rumours it addresses, and carries more weight than a written statement.
/// </summary>
public sealed record PressConferenceHeld(Guid ConferenceId, Guid? IncidentId, string Statement, IReadOnlyList<string> Spokespeople) : DomainEvent;

/// <summary>An entry in command's decision log: what was decided, and why, at the time (Phase 14: legal).</summary>
/// <param name="AboutSequence">The recorded command it explains, when tied to one.</param>
public sealed record DecisionLogged(Guid EntryId, string Decision, string Rationale, long? AboutSequence = null) : DomainEvent;

/// <summary>
/// Ground truth: something command did (or didn't) that would be hard to defend afterwards (Phase 14: legal and
/// liability), e.g. ordering an evacuation without the authority that the Major Emergency framework gives the Garda.
/// </summary>
/// <param name="SubjectId">What it is about: the call that raised it, so the review can see whether it was answered.</param>
public sealed record LegalExposure(string Kind, string Detail, Guid? SubjectId = null) : DomainEvent;
