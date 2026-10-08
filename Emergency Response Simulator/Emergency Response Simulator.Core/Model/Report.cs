using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Core.Model;

/// <summary>
/// A single piece of incoming information with its provenance (Design Document §6.7, §11.1).
/// Reports may be wrong, late or contradict each other.
/// </summary>
public class Report
{
    public Guid Id { get; set; }

    public ReportSource Source { get; set; }

    /// <summary>Who said it, e.g. "911 caller" or "Police Unit 14".</summary>
    public required string SourceName { get; set; }

    public required string Claim { get; set; }
    public Confidence Confidence { get; set; }
    public VerificationStatus Verification { get; set; } = VerificationStatus.Reported;

    public Point? Location { get; set; }
    public double? LocationAccuracyMeters { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public Guid? IncidentId { get; set; }
    public Incident? Incident { get; set; }
}
