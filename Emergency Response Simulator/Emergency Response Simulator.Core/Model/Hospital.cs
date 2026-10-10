using Emergency_Response_Simulator.Core.Geo;

namespace Emergency_Response_Simulator.Core.Model;

/// <summary>
/// A receiving hospital as command last heard about it (Design Document §17): its own reports of emergency
/// department load. Not the hospital's true state, which may have moved on since the last report.
/// COP state, not stored in the database.
/// </summary>
public sealed class Hospital
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public GeoPoint Location { get; init; }

    /// <summary>Emergency department places, as last reported.</summary>
    public int Capacity { get; set; }

    public int Occupied { get; set; }
    public bool OnDiversion { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset ReportedAt { get; set; }

    public int Available => Math.Max(0, Capacity - Occupied);
    public double Load => Capacity <= 0 ? 1 : (double)Occupied / Capacity;
}
