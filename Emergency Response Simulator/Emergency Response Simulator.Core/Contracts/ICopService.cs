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

/// <param name="WindFromDegrees">Direction the wind blows from, degrees clockwise from north.</param>
public sealed record PerceivedWeather(
    string Source,
    GeoPoint Location,
    double WindFromDegrees,
    double WindSpeedMps,
    double TemperatureC,
    double RelativeHumidity,
    DateTimeOffset ObservedAt);
