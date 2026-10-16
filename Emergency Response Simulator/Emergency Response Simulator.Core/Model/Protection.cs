using Emergency_Response_Simulator.Core.Geo;

namespace Emergency_Response_Simulator.Core.Model;

// ---- Phase 17: evacuation, shelter, critical infrastructure, cyber and GPS, the environment, as the COP holds them ----

/// <summary>A critical system a hazard, a power cut or an attack can take out (Phase 17).</summary>
public enum InfrastructureKind
{
    Power,

    /// <summary>Pumping stations and treatment works: mains pressure for taps, hospitals and fire hydrants.</summary>
    Water,

    Gas,

    /// <summary>Mobile masts: 999 calls from mobiles and the crews' mobile data.</summary>
    Telecom,

    /// <summary>Rail and tram lines (DART, Luas): electric, and they run through whatever is around them.</summary>
    Transport,
}

/// <summary>How an infrastructure asset stands, as its operator reports it.</summary>
public enum InfrastructureStatus
{
    Normal,

    /// <summary>Mains power lost: running on a standby generator or batteries, for a while.</summary>
    OnBackup,

    Failed,

    /// <summary>A gas main is leaking.</summary>
    Leaking,

    /// <summary>Supply shut off on purpose: a leaking gas main isolated.</summary>
    Isolated,

    /// <summary>A rail or tram service stopped through an area.</summary>
    Suspended,
}

public enum GnssInterferenceKind
{
    /// <summary>Satellite signals drowned out: receivers lose their fix.</summary>
    Jamming,

    /// <summary>False satellite signals: receivers report a confident position that is wrong.</summary>
    Spoofing,
}

/// <summary>What a ransomware attack has encrypted (Phase 17).</summary>
public enum CyberTarget
{
    /// <summary>The control room's computer-aided dispatch and the crews' mobile data terminals.</summary>
    Dispatch,

    /// <summary>A hospital's IT: records, results, bed management.</summary>
    Hospital,

    /// <summary>The water treatment works' control system (SCADA).</summary>
    WaterTreatment,

    /// <summary>The city's traffic signal control (SCATS).</summary>
    TrafficSignals,
}

public enum EnvironmentMedium
{
    Air,
    Water,
    Soil,
}

/// <summary>A monitoring reading, as the environmental team grades it against guideline values.</summary>
public enum PollutionLevel
{
    Clear,
    Low,
    Moderate,
    High,
}

/// <summary>The Phase 17 catalogue: names, operators and the numbers commands are checked against.</summary>
public static class Protection
{
    /// <summary>A rest centre takes about this long to set up once the council is asked: staff, keys, tables, signs.</summary>
    public static readonly TimeSpan ShelterSetup = TimeSpan.FromMinutes(30);

    public const int MaxTrafficControlPoints = 8;

    /// <summary>Seats on an evacuation bus (a double-decker standing, a coach seated).</summary>
    public const int BusSeats = 60;

    public static string Name(InfrastructureKind kind) => kind switch
    {
        InfrastructureKind.Power => "Power",
        InfrastructureKind.Water => "Water",
        InfrastructureKind.Gas => "Gas",
        InfrastructureKind.Telecom => "Telecoms",
        _ => "Rail & tram",
    };

    /// <summary>Who runs it, and so who reports on it and who command notifies.</summary>
    public static string Operator(InfrastructureKind kind) => kind switch
    {
        InfrastructureKind.Power => "ESB Networks",
        InfrastructureKind.Water => "Uisce Éireann",
        InfrastructureKind.Gas => "Gas Networks Ireland",
        InfrastructureKind.Telecom => "Mobile operators",
        _ => "Irish Rail / Luas",
    };

    public static InfrastructureKind? KindOf(HazardSiteKind site) => site switch
    {
        HazardSiteKind.Substation => InfrastructureKind.Power,
        HazardSiteKind.CellTower => InfrastructureKind.Telecom,
        HazardSiteKind.WaterPumping => InfrastructureKind.Water,
        HazardSiteKind.GasMain => InfrastructureKind.Gas,
        HazardSiteKind.Rail => InfrastructureKind.Transport,
        _ => null,
    };

    public static string Name(InfrastructureStatus status) => status switch
    {
        InfrastructureStatus.OnBackup => "on backup",
        InfrastructureStatus.Failed => "FAILED",
        InfrastructureStatus.Leaking => "LEAKING",
        InfrastructureStatus.Isolated => "isolated",
        InfrastructureStatus.Suspended => "suspended",
        _ => "normal",
    };

    public static string Name(CyberTarget target) => target switch
    {
        CyberTarget.Dispatch => "Control room CAD and mobile data",
        CyberTarget.Hospital => "Hospital IT",
        CyberTarget.WaterTreatment => "Water treatment controls (SCADA)",
        _ => "Traffic signal control (SCATS)",
    };

    /// <summary>What falling back to manual working means for each target, for the command result and the tab.</summary>
    public static string Fallback(CyberTarget target) => target switch
    {
        CyberTarget.Dispatch => "paper dispatch: crews give status and positions by voice",
        CyberTarget.Hospital => "paper records and runners: the department takes more patients again",
        CyberTarget.WaterTreatment => "operators run the works by hand, on site",
        _ => "signals switched to their fixed-time local plans",
    };

    public static string Name(EnvironmentMedium medium) => medium switch
    {
        EnvironmentMedium.Air => "Air quality",
        EnvironmentMedium.Water => "Water quality",
        _ => "Soil",
    };

    public static string Name(PollutionLevel level) => level switch
    {
        PollutionLevel.Clear => "clear",
        PollutionLevel.Low => "low",
        PollutionLevel.Moderate => "above guideline",
        _ => "HIGH",
    };

    /// <summary>"0:45" for a clearance estimate.</summary>
    public static string Clearance(int minutes) => $"{minutes / 60}:{minutes % 60:D2}";
}

/// <summary>An evacuation zone as command tracks it: the people, the route, the buses, re-entry (Phase 17).</summary>
public sealed class EvacuationZoneStatus
{
    public Guid ZoneId { get; init; }
    public required string Name { get; init; }
    public Guid? IncidentId { get; init; }
    public DateTimeOffset OrderedAt { get; init; }

    /// <summary>The gardaí's estimate of who lives or works there, then of who has left and who is still in.</summary>
    public int Estimated { get; set; }
    public int Left { get; set; }
    public int Remaining { get; set; }

    /// <summary>Minutes until the zone is clear at the present rate (the evacuation time estimate); null before anyone moves.</summary>
    public int? ClearanceMinutes { get; set; }

    public DateTimeOffset? ReportedAt { get; set; }

    /// <summary>The route evacuees are told to take, ending where they should go; empty when none is set.</summary>
    public List<GeoPoint> Route { get; } = [];

    /// <summary>The rest centre at the end of the route, if it ends at one.</summary>
    public Guid? ShelterId { get; set; }

    public int BusesRequested { get; set; }
    public int BusesWorking { get; set; }
    public DateTimeOffset? ReEntryAt { get; set; }
}

/// <summary>A traffic control point: gardaí at a junction keeping evacuation traffic moving (Phase 17).</summary>
public sealed class TrafficControlPoint
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public GeoPoint Location { get; init; }
    public DateTimeOffset PlacedAt { get; init; }
    public DateTimeOffset? StaffedAt { get; set; }
}

/// <summary>A rest centre for evacuees (Phase 17), as its manager reports it.</summary>
public sealed class Shelter
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public GeoPoint Location { get; init; }
    public int Capacity { get; init; }

    /// <summary>The incident whose evacuees it takes, and whose stock its meals, water and bedding come from.</summary>
    public Guid? IncidentId { get; init; }

    public DateTimeOffset OpenedAt { get; init; }
    public bool Ready { get; set; }
    public int Occupants { get; set; }
    public int Registered { get; set; }

    /// <summary>What it has run out of (meals, water, bedding).</summary>
    public List<SupplyKind> Short { get; } = [];

    public DateTimeOffset? SecurityArrangedAt { get; set; }
    public bool Secured { get; set; }

    /// <summary>Thefts, fights and the like reported from inside.</summary>
    public int Incidents { get; set; }

    public DateTimeOffset? ReportedAt { get; set; }
    public bool Full => Occupants >= Capacity;
}

/// <summary>A piece of critical infrastructure, as its operator last reported it (Phase 17).</summary>
public sealed class InfrastructureAsset
{
    public Guid Id { get; init; }
    public InfrastructureKind Kind { get; init; }
    public required string Name { get; init; }
    public GeoPoint Location { get; init; }

    /// <summary>The area it serves (or, for a line, the stretch that matters).</summary>
    public double RadiusMeters { get; init; }

    public InfrastructureStatus Status { get; set; }
    public string Detail { get; set; } = "";

    /// <summary>Customers without the service because of it.</summary>
    public int Customers { get; set; }

    public DateTimeOffset ReportedAt { get; set; }
    public DateTimeOffset? GeneratorRequestedAt { get; set; }
    public DateTimeOffset? IsolationRequestedAt { get; set; }
}

/// <summary>Satellite navigation interference that crews have noticed (Phase 17).</summary>
public sealed class GnssInterference
{
    public Guid Id { get; init; }
    public GnssInterferenceKind Kind { get; init; }
    public GeoPoint Centre { get; init; }
    public double RadiusMeters { get; init; }
    public DateTimeOffset ReportedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
}

/// <summary>A ransomware attack on a system the response relies on (Phase 17).</summary>
public sealed class CyberIncident
{
    public Guid Id { get; init; }
    public CyberTarget Target { get; init; }

    /// <summary>The system as its owners name it, e.g. "St. James's Hospital IT".</summary>
    public required string System { get; init; }

    public string Impact { get; set; } = "";
    public DateTimeOffset ReportedAt { get; init; }
    public DateTimeOffset? FallbackAt { get; set; }
    public DateTimeOffset? RestoredAt { get; set; }
}

/// <summary>An environmental monitoring team or instrument and its latest reading (Phase 17).</summary>
public sealed class EnvironmentalMonitor
{
    public Guid Id { get; init; }
    public EnvironmentMedium Medium { get; init; }
    public GeoPoint Location { get; init; }
    public DateTimeOffset DeployedAt { get; init; }
    public PollutionLevel? Level { get; set; }
    public string Reading { get; set; } = "";
    public DateTimeOffset? ReadingAt { get; set; }
}

/// <summary>Harm to the environment someone has reported: a fish kill, dead birds, contaminated ground.</summary>
public sealed record EnvironmentalImpact(Guid Id, Guid? IncidentId, EnvironmentMedium Medium, string What, GeoPoint Location, DateTimeOffset ReportedAt);

/// <summary>Evacuation, shelter, infrastructure, cyber and GPS, and the environment (Phase 17), as the COP holds them.</summary>
public sealed class ProtectionSituation
{
    public List<EvacuationZoneStatus> Evacuations { get; } = [];
    public List<TrafficControlPoint> ControlPoints { get; } = [];
    public List<Shelter> Shelters { get; } = [];

    /// <summary>Evacuees with nowhere to go (no rest centre, or all full), by incident, as the gardaí and council report.</summary>
    public Dictionary<Guid, int> Unsheltered { get; } = [];

    public List<InfrastructureAsset> Infrastructure { get; } = [];
    public List<GnssInterference> Gnss { get; } = [];

    /// <summary>Crews in an area of GPS interference report their position by voice.</summary>
    public bool ManualPositionReporting { get; set; }

    public List<CyberIncident> Cyber { get; } = [];
    public List<EnvironmentalMonitor> Monitors { get; } = [];

    /// <summary>Incidents whose firefighting run-off command has ordered contained, and when.</summary>
    public Dictionary<Guid, DateTimeOffset> RunoffContained { get; } = [];

    public List<EnvironmentalImpact> Impacts { get; } = [];

    public EvacuationZoneStatus? Evacuation(Guid zoneId) => Evacuations.FirstOrDefault(e => e.ZoneId == zoneId);

    public ProtectionSituation Copy()
    {
        var copy = new ProtectionSituation { ManualPositionReporting = ManualPositionReporting };
        foreach (var e in Evacuations)
        {
            var zone = new EvacuationZoneStatus
            {
                ZoneId = e.ZoneId, Name = e.Name, IncidentId = e.IncidentId, OrderedAt = e.OrderedAt, Estimated = e.Estimated, Left = e.Left,
                Remaining = e.Remaining, ClearanceMinutes = e.ClearanceMinutes, ReportedAt = e.ReportedAt, ShelterId = e.ShelterId,
                BusesRequested = e.BusesRequested, BusesWorking = e.BusesWorking, ReEntryAt = e.ReEntryAt,
            };
            zone.Route.AddRange(e.Route);
            copy.Evacuations.Add(zone);
        }
        copy.ControlPoints.AddRange(ControlPoints.Select(p => new TrafficControlPoint
        {
            Id = p.Id, Name = p.Name, Location = p.Location, PlacedAt = p.PlacedAt, StaffedAt = p.StaffedAt,
        }));
        foreach (var s in Shelters)
        {
            var shelter = new Shelter
            {
                Id = s.Id, Name = s.Name, Location = s.Location, Capacity = s.Capacity, IncidentId = s.IncidentId, OpenedAt = s.OpenedAt, Ready = s.Ready,
                Occupants = s.Occupants, Registered = s.Registered, SecurityArrangedAt = s.SecurityArrangedAt, Secured = s.Secured, Incidents = s.Incidents,
                ReportedAt = s.ReportedAt,
            };
            shelter.Short.AddRange(s.Short);
            copy.Shelters.Add(shelter);
        }
        foreach (var (k, v) in Unsheltered) copy.Unsheltered[k] = v;
        copy.Infrastructure.AddRange(Infrastructure.Select(a => new InfrastructureAsset
        {
            Id = a.Id, Kind = a.Kind, Name = a.Name, Location = a.Location, RadiusMeters = a.RadiusMeters, Status = a.Status, Detail = a.Detail,
            Customers = a.Customers, ReportedAt = a.ReportedAt, GeneratorRequestedAt = a.GeneratorRequestedAt, IsolationRequestedAt = a.IsolationRequestedAt,
        }));
        copy.Gnss.AddRange(Gnss.Select(g => new GnssInterference
        {
            Id = g.Id, Kind = g.Kind, Centre = g.Centre, RadiusMeters = g.RadiusMeters, ReportedAt = g.ReportedAt, EndedAt = g.EndedAt,
        }));
        copy.Cyber.AddRange(Cyber.Select(c => new CyberIncident
        {
            Id = c.Id, Target = c.Target, System = c.System, Impact = c.Impact, ReportedAt = c.ReportedAt, FallbackAt = c.FallbackAt, RestoredAt = c.RestoredAt,
        }));
        copy.Monitors.AddRange(Monitors.Select(m => new EnvironmentalMonitor
        {
            Id = m.Id, Medium = m.Medium, Location = m.Location, DeployedAt = m.DeployedAt, Level = m.Level, Reading = m.Reading, ReadingAt = m.ReadingAt,
        }));
        foreach (var (k, v) in RunoffContained) copy.RunoffContained[k] = v;
        copy.Impacts.AddRange(Impacts);
        return copy;
    }
}
