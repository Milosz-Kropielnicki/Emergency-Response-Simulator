using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Core.Contracts;

/// <summary>
/// Data standards and interoperability (Phase 20): "What do other systems need from us, and what can they tell us?"
/// Exports the picture in the formats emergency services exchange (CAP, EDXL, NIEM, HL7), takes the same formats in
/// from outside (a met service's warning, a hospital's bed status, a CAD system's incidents, a vehicle tracker), and
/// checks the response against NIMS/ICS. Exports come from the COP, so they hold only what command knows.
/// </summary>
public interface IIntegrationService
{
    /// <summary>The standards this simulator speaks, and which way.</summary>
    Task<IReadOnlyList<DataStandard>> GetStandardsAsync(CancellationToken cancellationToken = default);

    /// <summary>The picture (or one incident's part of it) as a document in a standard (<see cref="DataStandards"/>).</summary>
    Task<StandardDocument> ExportAsync(string standard, Guid? incidentId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// A document from an outside system, in a standard. What it says reaches the COP as information received (a report,
    /// an alert, a hospital's status, a patient's arrival), never as ground truth.
    /// </summary>
    Task<ImportResult> ImportAsync(string standard, string content, CancellationToken cancellationToken = default);

    /// <summary>Positions from an outside vehicle-tracking system, by callsign or unit id.</summary>
    Task<ImportResult> FeedPositionsAsync(IReadOnlyList<ExternalPosition> positions, CancellationToken cancellationToken = default);

    /// <summary>The response checked against NIMS/ICS: terminology, organisation, forms and resource typing.</summary>
    Task<NimsReport> CheckNimsAsync(Guid? incidentId = null, CancellationToken cancellationToken = default);
}

/// <summary>Keys of the standards <see cref="IIntegrationService"/> exports and imports.</summary>
public static class DataStandards
{
    public const string Cap = "cap";
    public const string EdxlDe = "edxl-de";
    public const string EdxlHave = "edxl-have";
    public const string EdxlRm = "edxl-rm";
    public const string EdxlSitRep = "edxl-sitrep";
    public const string Niem = "niem";
    public const string Hl7 = "hl7v2";

    public static IReadOnlyList<DataStandard> All { get; } =
    [
        new(Cap, "CAP", "1.2", "application/cap+xml", true, true,
            "Common Alerting Protocol: public warnings (evacuation, shelter-in-place, public-health alerts) out; outside warnings in."),
        new(EdxlDe, "EDXL-DE", "1.0", "application/xml", true, true,
            "Distribution Element: an envelope routing the other EDXL and CAP messages; imports unwrap it and take each in turn."),
        new(EdxlHave, "EDXL-HAVE", "1.0", "application/xml", true, true,
            "Hospital AVailability Exchange: each receiving hospital's emergency department, diversion and specialist beds."),
        new(EdxlRm, "EDXL-RM", "1.0", "application/xml", true, false,
            "Resource Messaging: command's open resource requests, typed to NIMS, for mutual aid and logistics systems."),
        new(EdxlSitRep, "EDXL-SitRep", "1.0", "application/xml", true, false,
            "Situation Report: the incident, casualties, resources and actions, as a structured report to the EOC."),
        new(Niem, "NIEM", "5.0", "application/xml", true, true,
            "NIEM Core and Emergency Management: incidents and resources for CAD and records systems; a CAD's incidents in as reports."),
        new(Hl7, "HL7 v2", "2.5.1", "application/hl7-v2", true, true,
            "Patient notifications to hospitals (ADT pre-arrival, arrival, transfer, death) out; hospitals' ADT messages in, answered with an ACK."),
    ];

    public static DataStandard? Find(string key) =>
        All.FirstOrDefault(s => string.Equals(s.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <param name="MediaType">What the document is served as.</param>
/// <param name="Exports">The simulator can write it.</param>
/// <param name="Imports">The simulator can read it.</param>
public sealed record DataStandard(string Key, string Name, string Version, string MediaType, bool Exports, bool Imports, string Description)
{
    public string Label => $"{Name} {Version}";
}

/// <summary>A document in a standard, ready to send or save.</summary>
public sealed record StandardDocument(string Standard, string MediaType, string FileName, string Content, int Items);

/// <param name="Accepted">How many items (alerts, hospitals, incidents, messages, positions) reached the COP.</param>
/// <param name="Notes">What was taken, and what was skipped and why.</param>
/// <param name="Reply">What to send back, if the standard expects an answer (an HL7 ACK).</param>
public sealed record ImportResult(bool Succeeded, string Message, int Accepted, IReadOnlyList<string> Notes, string? Reply = null)
{
    public static ImportResult Failed(string message) => new(false, message, 0, []);
}

/// <summary>A fix from an outside tracking system.</summary>
/// <param name="Unit">The unit's callsign (e.g. "Engine 12") or id.</param>
/// <param name="Status">The unit's status, if the system reports it.</param>
public sealed record ExternalPosition(string Unit, double Latitude, double Longitude, double SpeedKph = 0, double Heading = 0,
    UnitStatus? Status = null);
