using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Model;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Server.Api;

// What the REST API returns for the COP's objects: flat, without the model's back-references (an incident's units point
// back at the incident), with places as latitude and longitude and areas as WKT, and units carrying their NIMS type.

public sealed record IncidentDto(Guid Id, string Number, string? Name, IncidentType Type, IncidentPriority Priority, IncidentStatus Status,
    double Latitude, double Longitude, string? Address, DateTimeOffset ReportedAt, DateTimeOffset LastUpdatedAt, string? IncidentCommander,
    IReadOnlyList<string> Units, IReadOnlyList<string> Threats, int CasualtiesReported, int CasualtiesConfirmed, bool EvacuationRequired,
    IReadOnlyDictionary<string, string> Positions, IReadOnlyList<string> UnifiedCommand);

public sealed record UnitDto(Guid Id, string Callsign, UnitType Type, string NimsType, string? Agency, UnitStatus Status, double? Latitude,
    double? Longitude, double SpeedKph, double Heading, DateTimeOffset? LastAvlUpdate, string? Incident, string? Tasking, string? Channel,
    bool CommsConnected, int CrewSize);

public sealed record ZoneDto(Guid Id, string Name, ZoneType Type, string? Incident, Geometry Area, DateTimeOffset EffectiveFrom, DateTimeOffset? EffectiveUntil);

public sealed record HospitalDto(Guid Id, string Name, double Latitude, double Longitude, int Capacity, int Occupied, int Available, bool OnDiversion,
    string? Note, DateTimeOffset ReportedAt, IReadOnlyList<SpecialtyBeds> Specialties);

public sealed record AlertDto(Guid Id, AlertCategory Category, AlertSeverity Severity, string Title, string Message, DateTimeOffset RaisedAt,
    bool Acknowledged, Guid? IncidentId);

public sealed record ReportDto(Guid Id, ReportSource Source, string SourceName, string Claim, Confidence Confidence, VerificationStatus Verification,
    double? Latitude, double? Longitude, double? AccuracyMeters, DateTimeOffset ReceivedAt, Guid? IncidentId);

public static class ApiDtos
{
    public static IncidentDto Incident(ICopService cop, Incident i) => new(i.Id, i.Number, i.Name, i.Type, i.Priority, i.Status,
        i.Location.Y, i.Location.X, i.Address, i.ReportedAt, i.LastUpdatedAt, i.IncidentCommanderName,
        i.AssignedUnits.Select(u => cop.FindUnit(u.Id)?.Callsign ?? u.Callsign).ToList(), i.Threats, i.CasualtiesReported, i.CasualtiesConfirmed,
        i.EvacuationRequired, i.Command.Positions.ToDictionary(p => NimsTerminology.Title(p.Key), p => p.Value),
        i.Command.Unified.Select(m => $"{m.Agency}: {m.Commander}").ToList());

    public static UnitDto Unit(ICopService cop, Unit u) => new(u.Id, u.Callsign, u.Type, NimsResourceTypes.Of(u.Type).Label, u.Agency?.ShortName,
        u.Status, u.Location?.Y, u.Location?.X, u.SpeedKph, u.Heading, u.LastAvlUpdate,
        u.AssignedIncidentId is { } id ? cop.FindIncident(id)?.Number : null, u.Tasking, u.Channel, u.CommsConnected, u.CrewSize);

    public static ZoneDto Zone(ICopService cop, Zone z) => new(z.Id, z.Name, z.Type, z.IncidentId is { } id ? cop.FindIncident(id)?.Number : null,
        z.Area, z.EffectiveFrom, z.EffectiveUntil);

    public static HospitalDto Hospital(ICopService cop, Hospital h) => new(h.Id, h.Name, h.Location.Latitude, h.Location.Longitude, h.Capacity,
        h.Occupied, h.Available, h.OnDiversion, h.Note, h.ReportedAt, cop.Health.Hospitals.GetValueOrDefault(h.Id)?.Specialties ?? []);

    public static AlertDto Alert(Alert a) => new(a.Id, a.Category, a.Severity, a.Title, a.Message, a.RaisedAt, a.AcknowledgedAt is not null, a.IncidentId);

    public static ReportDto Report(Report r) => new(r.Id, r.Source, r.SourceName, r.Claim, r.Confidence, r.Verification, r.Location?.Y, r.Location?.X,
        r.LocationAccuracyMeters, r.ReceivedAt, r.IncidentId);
}
