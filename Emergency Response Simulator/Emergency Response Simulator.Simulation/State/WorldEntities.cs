using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Hazards;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Simulation.State;

// Ground-truth entities added in Phase 6 (Design Document §10.3–10.6). Like the rest of WorldState they are
// advanced by simulation systems one tick at a time and never shown to the trainee directly.

/// <param name="RadioChannel">The agency's own radio system, when it is not on ours.</param>
public sealed record WorldAgency(string Name, AgencyType Type, bool AiControlled = false, string? RadioChannel = null);

/// <summary>A physical hazard and the model that advances it.</summary>
public sealed class WorldHazard
{
    public Guid Id { get; init; }
    public HazardKind Kind { get; init; }
    public Guid? IncidentId { get; init; }
    public GeoPoint Origin { get; init; }
    public required string Description { get; init; }
    public string? Substance { get; init; }

    /// <summary>Flood inflow (m³/s) or plume release rate (kg/s).</summary>
    public double Rate { get; set; }

    /// <summary>Plume: kilograms left to release.</summary>
    public double Inventory { get; set; }

    public DateTimeOffset StartedAt { get; init; }
    public bool Ended { get; set; }

    /// <summary>Created by the hazard system on its first tick.</summary>
    public IHazardModel? Model { get; set; }

    /// <summary>The footprint last recorded in the event stream, and when.</summary>
    public Geometry? Footprint { get; set; }
    public double FootprintArea { get; set; }
    public DateTimeOffset? FootprintAt { get; set; }
}

/// <summary>A chemical store or substation a hazard can set off.</summary>
public sealed class HazardSite
{
    public Guid Id { get; init; }
    public HazardSiteKind Kind { get; init; }
    public required string Name { get; init; }
    public GeoPoint Location { get; init; }
    public string? Substance { get; init; }
    public double Quantity { get; init; }
    public double ServiceRadiusMeters { get; init; }

    /// <summary>Set off: the release has started or the substation has failed.</summary>
    public bool Triggered { get; set; }
}

public sealed class PowerOutage
{
    /// <summary>How long the utility takes to restore supply when nobody has asked it to hurry.</summary>
    public static readonly TimeSpan DefaultRepairTime = TimeSpan.FromMinutes(90);

    public Guid Id { get; init; }
    public GeoPoint Centre { get; init; }
    public double RadiusMeters { get; init; }
    public required string Cause { get; init; }
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>The substation behind it, if any: power stays off while a hazard still affects it.</summary>
    public Guid? SiteId { get; set; }

    public DateTimeOffset RestoreAt { get; set; }
    public bool Reported { get; set; }

    public bool Covers(GeoPoint point) => GeoMath.DistanceMeters(Centre, point) <= RadiusMeters;
}

public sealed class WorldCasualty
{
    public Guid Id { get; init; }
    public Guid? IncidentId { get; init; }
    public GeoPoint Location { get; set; }
    public Triage Triage { get; set; }
    public CasualtyState State { get; set; }
    public required string Cause { get; init; }
    public DateTimeOffset InjuredAt { get; init; }

    /// <summary>When the casualty's condition next worsens if nothing is done (see MedicalSystem).</summary>
    public DateTimeOffset? DeteriorateAt { get; set; }

    /// <summary>The ambulance crew treating or carrying the casualty.</summary>
    public Guid? CrewId { get; set; }

    public Guid? HospitalId { get; set; }

    public bool Alive => Triage != Triage.Deceased;
    public bool NeedsCare => State is CasualtyState.AwaitingTreatment or CasualtyState.TreatedOnScene;
}

public sealed class WorldHospital
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public GeoPoint Location { get; init; }

    /// <summary>Emergency department places today, after surge plans and power problems.</summary>
    public int Capacity { get; set; }

    /// <summary>Patients already in the department from ordinary demand.</summary>
    public int Baseline { get; set; }

    /// <summary>Casualties from the simulation delivered here.</summary>
    public int Delivered { get; set; }

    public int Occupied => Baseline + Delivered;
    public bool OnDiversion => Occupied >= Capacity;

    public bool SurgeActivated { get; set; }
    public bool OnGenerator { get; set; }
    public int NormalCapacity { get; init; }

    /// <summary>What the hospital last told command, so it reports again when that is out of date.</summary>
    public int? ReportedOccupied { get; set; }
    public bool ReportedDiversion { get; set; }
    public DateTimeOffset? ReportedAt { get; set; }
}

public enum CivilianState
{
    /// <summary>Going about their day, unaware.</summary>
    Normal,

    /// <summary>Has seen, smelt or heard the hazard and is deciding what to do.</summary>
    Aware,

    /// <summary>Moving away from danger, on foot or by car.</summary>
    Evacuating,

    /// <summary>Drawn towards the scene to look (convergence).</summary>
    Converging,

    /// <summary>Indoors with windows shut.</summary>
    Sheltering,

    Injured,

    /// <summary>Out of the area.</summary>
    Safe,
}

/// <summary>One person in the area (Design Document §10.3: evacuation, panic, convergence).</summary>
public sealed class Civilian
{
    public int Id { get; init; }
    public GeoPoint Location { get; set; }
    public CivilianState State { get; set; }
    public GeoPoint? Target { get; set; }

    /// <summary>Whether they leave by car when evacuating.</summary>
    public bool HasCar { get; init; }

    /// <summary>Running from immediate danger: faster and less careful.</summary>
    public bool Panicking { get; set; }

    public DateTimeOffset? AwareSince { get; set; }

    /// <summary>When a door-knock or alert reaches them after an evacuation order.</summary>
    public DateTimeOffset? WarnedAt { get; set; }

    /// <summary>Toxic dose so far (mg·min/m³).</summary>
    public double Dose { get; set; }

    public Guid? VehicleId { get; set; }
    public bool HasCalled { get; set; }

    /// <summary>When they get round to ringing 999 (people take a minute or two to react and dial).</summary>
    public DateTimeOffset? CallAt { get; set; }

    /// <summary>Their language when they have little English, else null (Design Document §13).</summary>
    public string? Language { get; init; }

    /// <summary>The evacuation order they ignore (non-compliance), so they are not asked again.</summary>
    public Guid? IgnoredOrder { get; set; }
}

/// <summary>A private car on the road network, e.g. evacuees driving out of a declared zone.</summary>
public sealed class WorldVehicle
{
    public Guid Id { get; init; }
    public GeoPoint Location { get; set; }
    public GeoPoint Destination { get; init; }
    public List<int> Occupants { get; init; } = [];
    public IReadOnlyList<Core.Contracts.RouteLeg> Route { get; set; } = [];
    public int LegIndex { get; set; }
    public double LegProgressMeters { get; set; }
    public bool Planned { get; set; }
    public bool Arrived { get; set; }
}

/// <summary>An evacuation or shelter-in-place order command has declared: civilians inside respond to it.</summary>
public sealed record ProtectiveAction(Guid ZoneId, ZoneType Type, Geometry Area, DateTimeOffset DeclaredAt);

/// <summary>One recorded link in a chain of knock-on effects.</summary>
public sealed record CascadeRecord(DateTimeOffset At, string Cause, string Effect, Guid? UnitId);
