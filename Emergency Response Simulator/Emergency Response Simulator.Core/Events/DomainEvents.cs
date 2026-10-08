using System.Text.Json.Serialization;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Core.Events;

/// <summary>
/// Base type for every event payload. Each subtype must be registered below so it
/// round-trips through JSON (the event store keeps payloads as jsonb).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
// Scenario setup
[JsonDerivedType(typeof(AgencyRegistered), nameof(AgencyRegistered))]
[JsonDerivedType(typeof(UnitRegistered), nameof(UnitRegistered))]
// Perceived: what reaches the organisation
[JsonDerivedType(typeof(CallReceived), nameof(CallReceived))]
[JsonDerivedType(typeof(IncidentCreated), nameof(IncidentCreated))]
[JsonDerivedType(typeof(IncidentUpdated), nameof(IncidentUpdated))]
[JsonDerivedType(typeof(UnitDispatched), nameof(UnitDispatched))]
[JsonDerivedType(typeof(UnitDispatchCancelled), nameof(UnitDispatchCancelled))]
[JsonDerivedType(typeof(UnitStatusChanged), nameof(UnitStatusChanged))]
[JsonDerivedType(typeof(UnitPositionReported), nameof(UnitPositionReported))]
[JsonDerivedType(typeof(ReportReceived), nameof(ReportReceived))]
[JsonDerivedType(typeof(AlertRaised), nameof(AlertRaised))]
[JsonDerivedType(typeof(AlertAcknowledged), nameof(AlertAcknowledged))]
[JsonDerivedType(typeof(ZoneDeclared), nameof(ZoneDeclared))]
[JsonDerivedType(typeof(ZoneLifted), nameof(ZoneLifted))]
[JsonDerivedType(typeof(ResourceRequested), nameof(ResourceRequested))]
[JsonDerivedType(typeof(WeatherObserved), nameof(WeatherObserved))]
[JsonDerivedType(typeof(IncidentCommanderAssigned), nameof(IncidentCommanderAssigned))]
[JsonDerivedType(typeof(ReportAssessed), nameof(ReportAssessed))]
[JsonDerivedType(typeof(ReportLinked), nameof(ReportLinked))]
// Truth: the world as it really is
[JsonDerivedType(typeof(WorldIncidentStarted), nameof(WorldIncidentStarted))]
[JsonDerivedType(typeof(WorldIncidentChanged), nameof(WorldIncidentChanged))]
[JsonDerivedType(typeof(WeatherChanged), nameof(WeatherChanged))]
[JsonDerivedType(typeof(UnitRadioFailed), nameof(UnitRadioFailed))]
// Engine control
[JsonDerivedType(typeof(SimulationStarted), nameof(SimulationStarted))]
[JsonDerivedType(typeof(SimulationPaused), nameof(SimulationPaused))]
[JsonDerivedType(typeof(TimeScaleChanged), nameof(TimeScaleChanged))]
public abstract record DomainEvent;

// ---- Scenario setup ----

public sealed record AgencyRegistered(Guid AgencyId, string Name, string ShortName, AgencyType Type) : DomainEvent;

public sealed record UnitRegistered(
    Guid UnitId,
    string Callsign,
    UnitType Type,
    Guid? AgencyId,
    GeoPoint Location,
    string? HomeStation,
    int CrewSize,
    IReadOnlyList<string> Capabilities) : DomainEvent;

// ---- Perceived ----

public sealed record CallReceived(
    Guid CallId,
    string Caller,
    string Summary,
    GeoPoint? Location,
    double? LocationAccuracyMeters) : DomainEvent;

public sealed record IncidentCreated(
    Guid IncidentId,
    string Number,
    IncidentType Type,
    IncidentPriority Priority,
    GeoPoint Location,
    string? Address,
    string? Name) : DomainEvent;

/// <summary>Only non-null fields changed.</summary>
public sealed record IncidentUpdated(
    Guid IncidentId,
    IncidentPriority? Priority = null,
    IncidentStatus? Status = null,
    int? CasualtiesReported = null,
    int? CasualtiesConfirmed = null,
    bool? EvacuationRequired = null,
    IReadOnlyList<string>? Threats = null) : DomainEvent;

public sealed record UnitDispatched(Guid UnitId, Guid IncidentId, Guid? OrderedBy) : DomainEvent;

// Status changes and position fixes also count as contact from the unit for comms-failure detection.

public sealed record UnitDispatchCancelled(Guid UnitId, Guid IncidentId, string? Reason) : DomainEvent;

public sealed record UnitStatusChanged(Guid UnitId, UnitStatus Status) : DomainEvent;

/// <summary>An AVL fix as transmitted by the vehicle (Design Document §7.4).</summary>
public sealed record UnitPositionReported(
    Guid UnitId,
    GeoPoint Location,
    double SpeedKph,
    double Heading,
    TimeSpan? Eta) : DomainEvent;

public sealed record ReportReceived(
    Guid ReportId,
    Guid? IncidentId,
    ReportSource Source,
    string SourceName,
    string Claim,
    Confidence Confidence,
    VerificationStatus Verification,
    GeoPoint? Location,
    double? LocationAccuracyMeters,
    Guid? FromUnitId = null) : DomainEvent;

/// <summary>Command re-grades a report after corroboration, e.g. Reported → Confirmed.</summary>
public sealed record ReportAssessed(
    Guid ReportId,
    VerificationStatus Verification,
    Confidence Confidence) : DomainEvent;

/// <summary>A report (or call) is attributed to an incident.</summary>
public sealed record ReportLinked(Guid ReportId, Guid IncidentId) : DomainEvent;

public sealed record IncidentCommanderAssigned(Guid IncidentId, string Name, Guid? UserId) : DomainEvent;

public sealed record AlertRaised(
    Guid AlertId,
    AlertCategory Category,
    AlertSeverity Severity,
    string Title,
    string Message,
    Guid? IncidentId,
    Guid? UnitId) : DomainEvent;

public sealed record AlertAcknowledged(Guid AlertId, Guid? UserId) : DomainEvent;

public sealed record ZoneDeclared(
    Guid ZoneId,
    ZoneType Type,
    string Name,
    IReadOnlyList<GeoPoint> Boundary,
    Guid? IncidentId) : DomainEvent;

public sealed record ZoneLifted(Guid ZoneId) : DomainEvent;

public sealed record ResourceRequested(
    Guid RequestId,
    Guid IncidentId,
    string Description,
    int Quantity,
    Guid? RequestedBy) : DomainEvent;

/// <summary>
/// Weather as reported to command by a met service or station. May lag or differ from the true
/// <see cref="WeatherChanged"/>; the COP's weather view shows only this.
/// </summary>
public sealed record WeatherObserved(
    string Source,
    GeoPoint Location,
    double WindFromDegrees,
    double WindSpeedMps,
    double TemperatureC,
    double RelativeHumidity) : DomainEvent;

// ---- Truth ----

/// <summary>Something really started happening. The COP only learns of it through later perceived events.</summary>
public sealed record WorldIncidentStarted(
    Guid WorldIncidentId,
    IncidentType Type,
    GeoPoint Location,
    double Severity,
    int ActualCasualties) : DomainEvent;

public sealed record WorldIncidentChanged(
    Guid WorldIncidentId,
    double Severity,
    int ActualCasualties,
    bool Extinguished) : DomainEvent;

/// <param name="WindFromDegrees">Meteorological convention: the direction the wind blows from.</param>
public sealed record WeatherChanged(
    double WindFromDegrees,
    double WindSpeedMps,
    double TemperatureC,
    double RelativeHumidity) : DomainEvent;

/// <summary>A unit's radio and data link really fail (or recover). Command only notices the silence.</summary>
public sealed record UnitRadioFailed(Guid UnitId, bool Failed) : DomainEvent;

// ---- Engine control ----

public sealed record SimulationStarted(double TimeScale) : DomainEvent;

public sealed record SimulationPaused : DomainEvent;

public sealed record TimeScaleChanged(double TimeScale) : DomainEvent;
