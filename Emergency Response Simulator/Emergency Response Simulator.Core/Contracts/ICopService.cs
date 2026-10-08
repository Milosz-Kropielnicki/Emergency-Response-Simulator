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

    // Command and control (Design Document §9)
    IReadOnlyList<Order> Orders { get; }
    IReadOnlyList<ResourceRequest> ResourceRequests { get; }
    IReadOnlyList<ApprovalRequest> Approvals { get; }
    IReadOnlyList<Notification> Notifications { get; }

    Incident? FindIncident(Guid incidentId);
    Unit? FindUnit(Guid unitId);
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
