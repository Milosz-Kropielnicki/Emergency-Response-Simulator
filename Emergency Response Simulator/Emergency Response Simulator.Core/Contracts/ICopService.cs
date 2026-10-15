using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Core.Contracts;

/// <summary>
/// Common Operating Picture: "What is happening?" (Design Document §6).
/// The best-known representation of the situation, built only from perceived events.
/// It is not the ground truth.
/// </summary>
public interface ICopService
{
    /// <summary>Raised after the picture changes. May be raised on a background thread.</summary>
    event EventHandler? Changed;

    /// <summary>Simulation time of the latest information applied.</summary>
    DateTimeOffset AsOf { get; }

    /// <summary>Sequence number of the latest event applied.</summary>
    long LastSequence { get; }

    IReadOnlyList<Agency> Agencies { get; }
    IReadOnlyList<Incident> Incidents { get; }
    IReadOnlyList<Unit> Units { get; }
    IReadOnlyList<Report> Reports { get; }
    IReadOnlyList<Alert> Alerts { get; }

    /// <summary>Zones currently in force.</summary>
    IReadOnlyList<Zone> Zones { get; }

    /// <summary>Latest weather report received, or null if none yet. Not the true weather.</summary>
    PerceivedWeather? Weather { get; }

    /// <summary>Receiving hospitals as they last reported themselves (Design Document §17).</summary>
    IReadOnlyList<Hospital> Hospitals { get; }

    // Communications (Design Document §13)

    /// <summary>Everything heard on the radio, the 999 line and the agency chat, oldest first.</summary>
    IReadOnlyList<CommsEntry> CommsLog { get; }

    /// <summary>Every channel command knows of, with who is on air and how busy it is (copies).</summary>
    IReadOnlyList<ChannelState> Channels { get; }

    IReadOnlyList<ChannelPatch> Patches { get; }
    IReadOnlyList<MissedCall> MissedCalls { get; }

    // Crews and safety (Design Document §12)

    /// <summary>The crews on every unit, as rostered and as they have reported themselves.</summary>
    IReadOnlyList<Crew> Crews { get; }

    /// <summary>Personnel Accountability Reports called, oldest first.</summary>
    IReadOnlyList<ParCheck> ParChecks { get; }

    IReadOnlyList<Mayday> Maydays { get; }

    // Command and control (Design Document §9)
    IReadOnlyList<Order> Orders { get; }
    IReadOnlyList<ResourceRequest> ResourceRequests { get; }
    IReadOnlyList<ApprovalRequest> Approvals { get; }
    IReadOnlyList<Notification> Notifications { get; }

    // Incident Action Plans (Design Document §8)
    IReadOnlyList<OperationalPeriod> OperationalPeriods { get; }

    /// <summary>Every version of every plan, including superseded ones.</summary>
    IReadOnlyList<IncidentActionPlan> ActionPlans { get; }

    /// <summary>Progress on operational objectives, by objective id; missing means open.</summary>
    IReadOnlyDictionary<Guid, ObjectiveStatus> ObjectiveProgress { get; }

    // Multi-agency (Design Document §14)

    /// <summary>City, county, regional and national areas and the authorities that answer for them.</summary>
    IReadOnlyList<Jurisdiction> Jurisdictions { get; }

    IReadOnlyList<MutualAidAgreement> MutualAidAgreements { get; }
    IReadOnlyList<UnifiedCommandInvitation> UnifiedCommandInvitations { get; }
    IReadOnlyList<AssistanceRequest> AssistanceRequests { get; }

    /// <summary>Authority disputes, oldest first.</summary>
    IReadOnlyList<AuthorityDispute> Disputes { get; }

    /// <summary>The Emergency Operations Centre as command knows it (a copy).</summary>
    EocStatus Eoc { get; }

    /// <summary>Public statements, ours and other agencies', oldest first.</summary>
    IReadOnlyList<PublicStatement> PublicStatements { get; }

    IReadOnlyList<MediaEnquiry> MediaEnquiries { get; }

    /// <summary>Politicians' calls to command (Phase 11 M5), oldest first.</summary>
    IReadOnlyList<PoliticalCall> PoliticalCalls { get; }

    /// <summary>
    /// Plume projections command has run (Phase 13), newest first, superseded ones included. These are predictions from
    /// reported information, not where the cloud really is.
    /// </summary>
    IReadOnlyList<PlumeProjection> PlumeProjections { get; }

    /// <summary>
    /// The pressures from outside the response (Phase 14): rumours, volunteers, vulnerable residents in protective zones,
    /// missing-person enquiries, the family assistance centre, press conferences and the decision log. A copy.
    /// </summary>
    PublicSituation Public { get; }

    /// <summary>
    /// What keeps the operation going (Phase 15): supplies at each incident, orders, the fleet's fuel, defects, repairs
    /// and staging, contracts, costs and reimbursement claims. A copy.
    /// </summary>
    LogisticsSituation Logistics { get; }

    /// <summary>
    /// Health and medical surge (Phase 16): the patient tracking board, transport destinations, hospitals' specialist
    /// beds, blood and mortuaries, the dead, public-health threats and dispensing, and behavioural health. A copy.
    /// </summary>
    HealthSituation Health { get; }

    // Networked sessions (Phase 11)

    /// <summary>Everyone who has joined the session, present or not, in the order they joined.</summary>
    IReadOnlyList<Participant> Participants { get; }

    Incident? FindIncident(Guid incidentId);
    Unit? FindUnit(Guid unitId);
}

/// <summary>Planning queries shared by the builder, C2 and the attention monitor.</summary>
public static class CopPlanning
{
    /// <summary>The incident's period covering <paramref name="at"/>, or its latest one if all have ended.</summary>
    public static OperationalPeriod? CurrentPeriod(this ICopService cop, Guid incidentId, DateTimeOffset at)
    {
        var periods = cop.OperationalPeriods.Where(p => p.IncidentId == incidentId).OrderBy(p => p.Number).ToList();
        return periods.LastOrDefault(p => p.Start <= at) ?? periods.FirstOrDefault();
    }

    public static IReadOnlyList<OperationalPeriod> PeriodsOf(this ICopService cop, Guid incidentId) =>
        cop.OperationalPeriods.Where(p => p.IncidentId == incidentId).OrderBy(p => p.Number).ToList();

    /// <summary>The plan in force for a period: its approved (not superseded) version.</summary>
    public static IncidentActionPlan? ApprovedPlan(this ICopService cop, Guid periodId) =>
        cop.ActionPlans.Where(p => p.OperationalPeriodId == periodId && p.Status == IapStatus.Approved)
            .MaxBy(p => p.Version);

    /// <summary>The plan in force for the incident right now.</summary>
    public static IncidentActionPlan? PlanInForce(this ICopService cop, Guid incidentId, DateTimeOffset at) =>
        cop.CurrentPeriod(incidentId, at) is { } period ? cop.ApprovedPlan(period.Id) : null;

    public static IReadOnlyList<IncidentActionPlan> VersionsOf(this ICopService cop, Guid periodId) =>
        cop.ActionPlans.Where(p => p.OperationalPeriodId == periodId).OrderBy(p => p.Version).ToList();
}

/// <summary>Communications queries shared by C2, the comms hub and the attention monitor.</summary>
public static class CopComms
{
    /// <summary>Channels joined to this one through working patches, including itself.</summary>
    public static IReadOnlyCollection<string> Linked(this ICopService cop, string channelId)
    {
        var patches = cop.Patches.Where(p => p.ActiveFrom is not null).ToList();
        var seen = new HashSet<string> { channelId };
        var frontier = new Queue<string>([channelId]);
        while (frontier.TryDequeue(out var current))
        {
            foreach (var patch in patches)
            {
                var other = patch.ChannelA == current ? patch.ChannelB : patch.ChannelB == current ? patch.ChannelA : null;
                if (other is not null && seen.Add(other)) frontier.Enqueue(other);
            }
        }
        return seen;
    }

    /// <summary>Command can hear (and talk on) a channel: one of its own, or patched to one.</summary>
    public static bool ControlHears(this ICopService cop, string channelId) => cop.HeardChannels().Contains(channelId);

    /// <summary>Every channel command can hear: its own, and everything patched to them.</summary>
    public static IReadOnlySet<string> HeardChannels(this ICopService cop)
    {
        var heard = new HashSet<string>();
        foreach (var own in cop.Channels.Where(c => c.Info.Monitored))
            heard.UnionWith(cop.Linked(own.Info.Id));
        return heard;
    }
}

/// <summary>Multi-agency queries shared by C2, the agencies panel and the recommended-response bot (Design Document §14).</summary>
public static class CopInteragency
{
    /// <summary>Every jurisdiction covering a point, from the most local up.</summary>
    public static IReadOnlyList<Jurisdiction> JurisdictionsAt(this ICopService cop, GeoPoint point)
    {
        var p = point.ToPoint();
        return cop.Jurisdictions.Where(j => j.Boundary.Covers(p)).OrderBy(j => j.Level).ToList();
    }

    /// <summary>
    /// A joint information centre is working for the incident: a PIO is appointed, and the agencies agree their messages
    /// through unified command or the EOC (partial activation or above).
    /// </summary>
    public static bool JointInformationCentre(this ICopService cop, Guid? incidentId)
    {
        var incident = incidentId is { } id ? cop.FindIncident(id) : null;
        var pio = incident?.Command.IsStaffed(IcsRole.PublicInformation) ?? cop.Incidents.Any(i => i.Command.IsStaffed(IcsRole.PublicInformation));
        var shared = cop.Eoc.Level >= EocLevel.Partial || (incident?.Command.IsUnified ?? cop.Incidents.Any(i => i.Command.IsUnified));
        return pio && shared;
    }

    /// <summary>
    /// Command can task the unit: one of ours, one its agency released to us, or (at the incident) one whose agency
    /// shares command of it.
    /// </summary>
    public static bool UnderCommand(this ICopService cop, Unit unit, Guid? incidentId = null)
    {
        if (unit.Agency is not { AiControlled: true }) return true;
        if (unit.AttachedToCommand) return true;
        var incident = (incidentId ?? unit.AssignedIncidentId) is { } id ? cop.FindIncident(id) : null;
        return incident?.Command.InUnifiedCommand(unit.AgencyId) == true;
    }
}

/// <param name="WindFromDegrees">Direction the wind blows from, degrees clockwise from north.</param>
public sealed record PerceivedWeather(
    string Source,
    GeoPoint Location,
    double WindFromDegrees,
    double WindSpeedMps,
    double TemperatureC,
    double RelativeHumidity,
    DateTimeOffset ObservedAt);
