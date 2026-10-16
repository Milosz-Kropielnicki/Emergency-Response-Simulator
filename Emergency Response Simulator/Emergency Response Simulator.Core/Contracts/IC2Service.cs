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
    /// Runs the dispersion model on what command knows and puts the result on the COP (Phase 13): the low, moderate and
    /// high concentration zones, what they cover on the ground (schools, hospitals, homes), and an alert for anywhere
    /// sensitive inside them. It uses the <i>reported</i> weather, so the answer is only as good as what command has
    /// been told. The simulator re-runs it when the reported wind moves; a revised release rate is command's to put in,
    /// by projecting again.
    /// </summary>
    /// <param name="location">Where the release is; null takes the incident's location.</param>
    /// <param name="rateKgPerSecond">How fast it is coming out. Null takes what has been reported, or a default guess.</param>
    Task<CommandResult> PredictPlumeAsync(
        string substance, Guid? incidentId = null, GeoPoint? location = null, double? rateKgPerSecond = null,
        double durationMinutes = 30, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Staffs a position. With <paramref name="userId"/> a session participant fills it (Phase 11 M3): they take that role
    /// in the session (the function stops falling to the IC), and a blank <paramref name="name"/> means theirs.
    /// </summary>
    Task<CommandResult> AssignIcsPositionAsync(
        Guid incidentId, IcsRole role, string name, Guid? userId = null, CancellationToken cancellationToken = default);

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

    // ---- Communications (Design Document §13) ----

    /// <summary>
    /// Push-to-talk on a channel command can hear. <paramref name="heldSeconds"/> is how long the button was held:
    /// shorter than the message needs and the end is lost. Crews answer only if they hear their call sign.
    /// </summary>
    Task<CommandResult> TransmitAsync(string channelId, string? to, string text, double? heldSeconds = null, CancellationToken cancellationToken = default);

    /// <summary>"Say again": ask the sender of a message to repeat it.</summary>
    Task<CommandResult> RequestRepeatAsync(Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>Close the loop on an order: the read-back was right, or it was wrong and the order goes out again.</summary>
    Task<CommandResult> ConfirmReadBackAsync(Guid orderId, bool correct, CancellationToken cancellationToken = default);

    /// <summary>Move a crew to another of our channels, e.g. a tactical channel to take load off the main one.</summary>
    Task<CommandResult> AssignChannelAsync(Guid unitId, string channelId, CancellationToken cancellationToken = default);

    /// <summary>Patch two channels together (a technician takes a few minutes), e.g. a mutual-aid service's radio to ours.</summary>
    Task<CommandResult> PatchChannelsAsync(string channelA, string channelB, CancellationToken cancellationToken = default);

    Task<CommandResult> RemovePatchAsync(Guid patchId, CancellationToken cancellationToken = default);

    /// <summary>Ring back a 999 caller who hung up before being answered.</summary>
    Task<CommandResult> CallBackAsync(Guid callId, CancellationToken cancellationToken = default);

    // ---- Crews and safety (Design Document §12) ----

    /// <summary>Personnel Accountability Report: every crew at the incident (or everywhere) counts its people over the radio.</summary>
    Task<CommandResult> RequestParAsync(Guid? incidentId, string reason = "Routine PAR", CancellationToken cancellationToken = default);

    /// <summary>Evacuation signal: every crew at the incident withdraws, reports a PAR, and operations go defensive.</summary>
    Task<CommandResult> SignalEvacuationAsync(Guid incidentId, CancellationToken cancellationToken = default);

    /// <summary>Clear a channel for emergency traffic only (a Mayday), or lift it.</summary>
    Task<CommandResult> DeclareEmergencyTrafficAsync(string channelId, bool active, CancellationToken cancellationToken = default);

    /// <summary>Declare a Mayday for a crew member nobody can account for (e.g. missing at a PAR).</summary>
    Task<CommandResult> DeclareMaydayAsync(Guid unitId, string? member, string details, CancellationToken cancellationToken = default);

    /// <summary>Send a crew at the scene in to rescue a firefighter in trouble (rapid intervention team).</summary>
    Task<CommandResult> DeployRescueTeamAsync(Guid maydayId, Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>Send a crew to rehab: rest, fluids, cooling and a medical check before it goes back to work.</summary>
    Task<CommandResult> SendToRehabAsync(Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>Ask for a fresh crew to take over a unit; a full briefing takes longer, a quick changeover loses information.</summary>
    Task<CommandResult> RequestReliefAsync(Guid unitId, bool fullBriefing, CancellationToken cancellationToken = default);

    /// <summary>Ask the peer support team to see a crew after a traumatic incident (psychological first aid).</summary>
    Task<CommandResult> ArrangePeerSupportAsync(Guid unitId, CancellationToken cancellationToken = default);

    // ---- Multi-agency (Design Document §14) ----

    /// <summary>
    /// Ask another agency's commander to share command of the incident (Unified Command). An agency with no stake in it
    /// declines; one that joins lets command task its units there and shares what it knows at once.
    /// </summary>
    Task<CommandResult> InviteToUnifiedCommandAsync(Guid incidentId, Guid agencyId, CancellationToken cancellationToken = default);

    /// <summary>Ask an agency run by another control room to release some of its units to the incident.</summary>
    Task<CommandResult> RequestAgencyAssistanceAsync(Guid agencyId, Guid incidentId, int quantity, string task,
        CancellationToken cancellationToken = default);

    /// <summary>Ask an agency to share what it knows.</summary>
    Task<CommandResult> RequestAgencyBriefingAsync(Guid agencyId, CancellationToken cancellationToken = default);

    /// <summary>Answer an authority dispute: concede, negotiate, assert command's authority, or escalate to the EOC.</summary>
    Task<CommandResult> ResolveDisputeAsync(Guid disputeId, DisputeResolution resolution, CancellationToken cancellationToken = default);

    /// <summary>Stand the Emergency Operations Centre up (or down) to a level; staffing up takes time.</summary>
    Task<CommandResult> SetEocLevelAsync(EocLevel level, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>Send the EOC a situation report.</summary>
    Task<CommandResult> SubmitSitrepAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// A statement to the public and the media, through the PIO (or the IC if there is none). With a joint information
    /// centre working, it goes out as every agency's agreed line.
    /// </summary>
    Task<CommandResult> IssuePublicStatementAsync(string text, Guid? incidentId = null, CancellationToken cancellationToken = default);

    // ---- Public, media, political & legal (Phase 14) ----

    /// <summary>
    /// A press conference: the PIO (and the IC, if there is one) in front of the cameras with a statement. It answers
    /// every journalist waiting, deals with each rumour the statement speaks to, and carries more weight than a written
    /// statement. It is also a public statement, with everything that follows from one.
    /// </summary>
    Task<CommandResult> HoldPressConferenceAsync(string statement, Guid? incidentId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Says publicly that a rumour isn't true, which stops it spreading. Be sure: denying something that turns out to
    /// be true is misinformation from the authorities, and comes out.
    /// </summary>
    Task<CommandResult> RebutRumourAsync(Guid rumourId, string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks for the people who can't leave an evacuation zone on their own to be got out: ambulance transport, care
    /// staff and outreach workers, who arrive after a while and work through the zone.
    /// </summary>
    Task<CommandResult> RequestAssistedEvacuationAsync(Guid zoneId, CancellationToken cancellationToken = default);

    /// <summary>Registers the volunteers at an incident's cordon (to work away from the hazard), or turns them away.</summary>
    Task<CommandResult> ManageVolunteersAsync(Guid incidentId, VolunteerDecision decision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a family assistance centre: relatives' enquiries are traced from then on, evacuees register there, and
    /// families of the hurt and the dead get a family liaison officer.
    /// </summary>
    Task<CommandResult> OpenFamilyAssistanceCentreAsync(string name, GeoPoint location, Guid? incidentId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// An entry in the decision log: what was decided, and why, written at the time. The review checks the significant
    /// decisions (protective zones, approvals, rumours denied) were logged.
    /// </summary>
    /// <param name="aboutSequence">The recorded command it explains, if any.</param>
    Task<CommandResult> RecordDecisionAsync(string decision, string rationale, long? aboutSequence = null, CancellationToken cancellationToken = default);

    // ---- Logistics & resource sustainment (Phase 15) ----

    /// <summary>
    /// Orders supplies for an incident from one tier of the supply chain: the council and local suppliers (quick, but
    /// they run out), the regional level (more, slower, needs a justification) or national reserves (any quantity,
    /// slowest, for a declared major emergency). Deliveries go to the incident's staging area or base camp.
    /// </summary>
    Task<CommandResult> OrderSuppliesAsync(Guid incidentId, SupplyKind kind, int quantity, SupplyTier tier, string? justification = null,
        CancellationToken cancellationToken = default);

    /// <summary>Sends an available unit to wait at a staging area, ready to commit, where it is refuelled and can be serviced.</summary>
    Task<CommandResult> StageUnitAsync(Guid unitId, Guid zoneId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a worn unit for preventive servicing before it fails: out of service for a short while, then much less
    /// likely to break down. Quicker at a staging area or base camp, where a mechanic is on hand.
    /// </summary>
    Task<CommandResult> ServiceUnitAsync(Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>Asks fleet maintenance to repair a broken-down unit: a mechanic to it (minor faults), or recovery to the workshop.</summary>
    Task<CommandResult> RequestRepairAsync(Guid unitId, RepairKind kind, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lets a contract: catering, sanitation and fuel supply deliver to the incident on a schedule; plant hire brings an
    /// excavator. Above the procurement threshold a direct award needs a written justification, or it can't be claimed.
    /// </summary>
    Task<CommandResult> AwardContractAsync(ContractKind kind, string vendor, string scope, decimal value, string? justification = null,
        Guid? incidentId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims the eligible costs of a declared major emergency from central government: overtime, apparatus, mutual
    /// aid, supplies, repairs and properly let contracts since the declaration. Straight time is not claimable.
    /// </summary>
    Task<CommandResult> SubmitReimbursementClaimAsync(CancellationToken cancellationToken = default);

    // ---- Medical, mass casualty & public health (Phase 16) ----

    /// <summary>
    /// Says where an incident's patients with one need go (major trauma to the trauma centre, burns to the burns unit,
    /// children to the children's hospital), or, with no hospital, sends them back to the nearest that can take them.
    /// Crews follow it while the hospital is open; without one, they go to the nearest.
    /// </summary>
    Task<CommandResult> SetTransportDestinationAsync(Guid incidentId, Specialty? need, Guid? hospitalId, CancellationToken cancellationToken = default);

    /// <summary>Asks the Irish Blood Transfusion Service for blood for a hospital.</summary>
    Task<CommandResult> RequestBloodAsync(Guid hospitalId, int units, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a temporary mortuary: the dead recovered from the scene go there instead of filling hospital mortuaries,
    /// and are identified faster. Recovery itself waits for the coroner to be notified.
    /// </summary>
    Task<CommandResult> OpenTemporaryMortuaryAsync(string name, GeoPoint location, Guid? incidentId = null, CancellationToken cancellationToken = default);

    /// <summary>Orders the cases of an infectious disease isolated. The order is HSE Public Health's to make: consult them.</summary>
    Task<CommandResult> OrderIsolationAsync(Guid threatId, CancellationToken cancellationToken = default);

    /// <summary>Orders the contacts of an infectious disease quarantined. The order is HSE Public Health's to make: consult them.</summary>
    Task<CommandResult> OrderQuarantineAsync(Guid threatId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a point of dispensing for a threat's countermeasure (antibiotics, iodine tablets, vaccine). It takes a while
    /// to set up, draws its doses from the incident's prophylaxis stock, and people come when they are told where it is.
    /// </summary>
    Task<CommandResult> OpenDispensingPointAsync(Guid threatId, string name, GeoPoint location, CancellationToken cancellationToken = default);

    /// <summary>Sends a crisis counselling team to the scene, the rest centres or the family assistance centre, or opens a helpline.</summary>
    Task<CommandResult> DeployCrisisCounsellingAsync(Guid? incidentId, CounsellingSetting setting, CancellationToken cancellationToken = default);

    // ---- Evacuation, shelter & infrastructure (Phase 17) ----

    /// <summary>
    /// Tells an evacuation zone's people which way to go: a route of at least two points, the last where they make for.
    /// Without one they go straight out from the middle of the zone, whatever lies that way. With a rest centre, the
    /// route ends there and the evacuees who need shelter go to it.
    /// </summary>
    Task<CommandResult> SetEvacuationRouteAsync(Guid zoneId, IReadOnlyList<GeoPoint> route, Guid? shelterId = null, CancellationToken cancellationToken = default);

    /// <summary>Asks the Garda for a traffic control point at a junction: evacuation traffic flows faster through it, and dark signals are worked by hand.</summary>
    Task<CommandResult> PlaceTrafficControlPointAsync(string name, GeoPoint location, CancellationToken cancellationToken = default);

    /// <summary>Asks the bus companies for buses to carry the people without a car out of an evacuation zone, to its route's end or rest centre.</summary>
    Task<CommandResult> RequestEvacuationBusesAsync(Guid zoneId, int buses, CancellationToken cancellationToken = default);

    /// <summary>Lets an evacuation zone's residents go home, and lifts the zone. Checked against what command knows of the hazard there.</summary>
    Task<CommandResult> AuthoriseReEntryAsync(Guid zoneId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the council to open a rest centre: evacuees with nowhere else to go come here once it is set up, are
    /// registered, fed and given a bed — from the incident's stock of food, water and bedding.
    /// </summary>
    Task<CommandResult> OpenShelterAsync(string name, GeoPoint location, int capacity, Guid? incidentId = null, CancellationToken cancellationToken = default);

    /// <summary>Arranges security at a rest centre (gardaí or a contracted firm).</summary>
    Task<CommandResult> ArrangeShelterSecurityAsync(Guid shelterId, CancellationToken cancellationToken = default);

    /// <summary>Sends a mobile generator to an infrastructure asset without mains power (a pumping station, a mast).</summary>
    Task<CommandResult> DeployGeneratorAsync(Guid siteId, CancellationToken cancellationToken = default);

    /// <summary>Asks Gas Networks Ireland to isolate a leaking gas main: the leak stops, and so does supply to everyone it feeds.</summary>
    Task<CommandResult> IsolateGasAsync(Guid siteId, CancellationToken cancellationToken = default);

    /// <summary>Tells crews in an area of GPS interference to give their positions by voice (or stops it).</summary>
    Task<CommandResult> SetManualPositionReportingAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Tells the users of a system hit by ransomware to fall back to manual working until it is restored.</summary>
    Task<CommandResult> ActivateCyberFallbackAsync(Guid attackId, CancellationToken cancellationToken = default);

    /// <summary>Sends an environmental monitoring team to sample the air, water or soil at a place; it reports readings until stood down.</summary>
    Task<CommandResult> DeployEnvironmentalMonitoringAsync(EnvironmentMedium medium, GeoPoint location, CancellationToken cancellationToken = default);

    /// <summary>Orders an incident's firefighting run-off contained (drain covers, booms, bunding) before it reaches a river or canal.</summary>
    Task<CommandResult> ContainRunoffAsync(Guid incidentId, CancellationToken cancellationToken = default);

    // ---- AI role players (Phase 11 M5) ----

    /// <summary>
    /// Call back a politician who rang (the local TD, the council chief executive, the minister's office). Left unanswered,
    /// the pressure moves up to the next office, and at the top it goes public.
    /// </summary>
    Task<CommandResult> AnswerPoliticalCallAsync(Guid callId, string answer, CancellationToken cancellationToken = default);

    /// <summary>
    /// For the participant running another agency (an agency chief): answer command's request to it, for units
    /// (<paramref name="quantity"/> of its free units, nearest first) or to join unified command.
    /// </summary>
    Task<CommandResult> AnswerAgencyRequestAsync(Guid itemId, bool approve, int? quantity = null, string? text = null,
        CancellationToken cancellationToken = default);
}

/// <param name="EntityId">Id of anything the command created, e.g. a new incident.</param>
/// <param name="Detail">A summary of what a compound command did, e.g. "6 orders issued, 1 group formed".</param>
public sealed record CommandResult(bool Succeeded, string? Error = null, long? EventSequence = null, Guid? EntityId = null,
    string? Detail = null)
{
    public static CommandResult Ok(long sequence, Guid? entityId = null) => new(true, EventSequence: sequence, EntityId: entityId);

    public static CommandResult Fail(string error) => new(false, error);
}
