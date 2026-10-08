using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Core.Contracts;

/// <summary>
/// Automatic Vehicle Location: "Where are my resources right now?" (Design Document §7.4).
/// Implemented in Phase 3; fixes are published as <c>UnitPositionReported</c> events.
/// </summary>
public interface IAvlService
{
    /// <summary>Raised for every fix received. May be raised on a background thread.</summary>
    event EventHandler<AvlFix>? FixReceived;

    IReadOnlyList<AvlFix> LatestFixes { get; }

    AvlFix? GetLatest(Guid unitId);
}

/// <summary>What a vehicle transmits: ID, position, time, speed, heading, status.</summary>
public sealed record AvlFix(
    Guid UnitId,
    GeoPoint Location,
    DateTimeOffset Timestamp,
    double SpeedKph,
    double Heading,
    UnitStatus Status);
