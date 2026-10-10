namespace Emergency_Response_Simulator.Core.Model;

public enum AgencyType
{
    Fire,
    Ems,
    Police,
    CoastGuard,
    EmergencyManagement,
    PublicHealth,
    Utility,
    Other,
}

/// <summary>Unit lifecycle from Design Document §6.4.</summary>
public enum UnitStatus
{
    Available,
    Dispatched,
    EnRoute,
    OnScene,
    Operating,
    Transporting,
    Cancelled,
    OutOfService,
}

/// <summary>Apparatus and team types from Design Document §6.3.</summary>
public enum UnitType
{
    // Fire
    Engine,
    Ladder,
    Tanker,
    Rescue,
    Hazmat,
    Command,
    Wildland,
    WaterTender,

    // EMS
    AmbulanceAls,
    AmbulanceBls,
    MedicalSupervisor,
    MassCasualtyUnit,
    MedevacHelicopter,

    // Police
    Patrol,
    Traffic,
    Swat,
    Motorcycle,
    Supervisor,
    SearchTeam,
    PrisonerTransport,

    // Specialised
    SearchAndRescue,
    Engineering,
    UtilityCrew,
    BombDisposal,
    Drone,
    Helicopter,
    HeavyMachinery,
    Bus,
    Other,
}

public enum ResourceKind
{
    Unit,
    Personnel,
    Equipment,
    Supply,
    Facility,
    Team,
}

public enum IncidentType
{
    StructureFire,
    WildlandFire,
    Explosion,
    HazmatRelease,
    RoadAccident,
    Flood,
    StructuralCollapse,
    MassCasualty,
    CrimeScene,
    ActiveThreat,
    MissingPerson,
    UtilityFailure,
    Other,
}

public enum IncidentPriority
{
    Low,
    Medium,
    High,
    Critical,
}

public enum IncidentStatus
{
    Reported,
    Uncontrolled,
    Contained,
    Controlled,
    Closed,
}

public enum Confidence
{
    Low,
    Medium,
    High,
}

/// <summary>How well established a piece of information is (Design Document §6.7, §11.1).</summary>
public enum VerificationStatus
{
    Known,
    Reported,
    Estimated,
    Suspected,
    Confirmed,
}

public enum ReportSource
{
    EmergencyCall,
    FieldUnit,
    Agency,
    Sensor,
    Media,
    Public,
    Instructor,
}

/// <summary>Alert classes from Design Document §6.9.</summary>
public enum AlertCategory
{
    Critical,
    ResourceShortage,
    SituationChange,
    CommunicationFailure,
    Safety,
    Hazard,

    /// <summary>The plan and reality have diverged, or a planning deadline is near (§8.6–8.7).</summary>
    Planning,
}

public enum AlertSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>Operational boundaries and dynamic GIS objects (Design Document §6.2, §7.2).</summary>
public enum ZoneType
{
    IncidentPerimeter,
    HotZone,
    WarmZone,
    ColdZone,
    EvacuationZone,
    ShelterInPlace,
    SearchArea,
    PoliceCordon,
    FireExclusion,
    TrafficControl,
    RoadClosure,
    StagingArea,
    CommandPost,
    LandingZone,
    PlumeLow,
    PlumeModerate,
    PlumeHigh,
}

public enum GisLayerKind
{
    /// <summary>Roads, buildings, hospitals: relatively unchanging base data.</summary>
    Static,

    /// <summary>Closures, perimeters, zones: created on top of the static data during an incident.</summary>
    Dynamic,
}

/// <summary>ICS and EOC positions a user can hold (Design Document §8.1, §21).</summary>
public enum UserRole
{
    Dispatcher,
    IncidentCommander,
    Operations,
    Planning,
    Logistics,
    FinanceAdmin,
    Safety,
    Liaison,
    PublicInformation,
    AgencyChief,
    Instructor,
    Observer,
}

public enum IapStatus
{
    Draft,
    PendingApproval,
    Approved,
    Superseded,
}

/// <summary>Physical hazards the simulation engine models (Design Document §10.4).</summary>
public enum HazardKind
{
    Fire,
    Flood,
    Plume,
}

/// <summary>Things in the world a hazard can set off when it reaches them (Design Document §10.5).</summary>
public enum HazardSiteKind
{
    /// <summary>Stored chemicals: a fire reaching them starts a toxic release.</summary>
    ChemicalStore,

    /// <summary>An electricity substation: fire or flood takes out power to the area it feeds.</summary>
    Substation,
}

/// <summary>Casualty priority (triage sieve): P1 immediate, P2 urgent, P3 delayed.</summary>
public enum Triage
{
    Immediate,
    Urgent,
    Delayed,
    Deceased,
}

/// <summary>Where a casualty is in the chain from injury to hospital.</summary>
public enum CasualtyState
{
    AwaitingTreatment,
    TreatedOnScene,
    Transporting,
    AtHospital,
    Deceased,
}
