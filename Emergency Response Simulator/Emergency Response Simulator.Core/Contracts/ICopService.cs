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

/// <param name="WindFromDegrees">Direction the wind blows from, degrees clockwise from north.</param>
public sealed record PerceivedWeather(
    string Source,
    GeoPoint Location,
    double WindFromDegrees,
    double WindSpeedMps,
    double TemperatureC,
    double RelativeHumidity,
    DateTimeOffset ObservedAt);
