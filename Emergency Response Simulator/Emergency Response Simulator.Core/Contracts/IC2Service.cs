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

    Task<CommandResult> AssignIcsPositionAsync(
        Guid incidentId, IcsRole role, string name, CancellationToken cancellationToken = default);

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
}

/// <param name="EntityId">Id of anything the command created, e.g. a new incident.</param>
/// <param name="Detail">A summary of what a compound command did, e.g. "6 orders issued, 1 group formed".</param>
public sealed record CommandResult(bool Succeeded, string? Error = null, long? EventSequence = null, Guid? EntityId = null,
    string? Detail = null)
{
    public static CommandResult Ok(long sequence, Guid? entityId = null) => new(true, EventSequence: sequence, EntityId: entityId);

    public static CommandResult Fail(string error) => new(false, error);
}
