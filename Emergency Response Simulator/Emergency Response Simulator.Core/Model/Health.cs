using Emergency_Response_Simulator.Core.Geo;

namespace Emergency_Response_Simulator.Core.Model;

// ---- Phase 16: medical surge, mass fatality, public health and behavioural health, as the COP holds them ----

/// <summary>A specialist service a patient may need, and a hospital may have beds for (Phase 16).</summary>
public enum Specialty
{
    /// <summary>A major trauma centre: theatres, interventional radiology, a trauma team on site.</summary>
    Trauma,

    /// <summary>A burns unit (the National Burns Service).</summary>
    Burns,

    /// <summary>Children: a paediatric emergency department and wards.</summary>
    Paediatric,

    /// <summary>Acute psychiatric care: for those psychological triage finds at high risk.</summary>
    Psychiatric,
}

/// <summary>Beds for one specialty at a hospital, and how many are in use.</summary>
public sealed record SpecialtyBeds(Specialty Specialty, int Beds, int Occupied = 0)
{
    public int Free => Math.Max(0, Beds - Occupied);
}

/// <summary>What a public-health threat is (Phase 16).</summary>
public enum HealthThreatKind
{
    /// <summary>A biological agent released into the air (anthrax-like): not passed person to person; antibiotics prevent illness.</summary>
    Biological,

    /// <summary>Radioactive material: iodine tablets protect the thyroid if taken in time; no illness during the exercise.</summary>
    Radiological,

    /// <summary>An infectious disease that spreads person to person: isolation of cases and quarantine of contacts slow it.</summary>
    Infectious,
}

/// <summary>Where a crisis counselling team works (Phase 16: behavioural health).</summary>
public enum CounsellingSetting
{
    /// <summary>At the scene: survivors, witnesses, the walking wounded.</summary>
    Scene,

    /// <summary>Where evacuees are: the rest centres.</summary>
    RestCentre,

    /// <summary>With the families at the family assistance centre (Phase 14): the bereaved and those waiting for news.</summary>
    FamilyAssistanceCentre,

    /// <summary>A telephone helpline, publicised: reaches everyone, and keeps the worried well out of emergency departments.</summary>
    Helpline,
}

/// <summary>The health catalogue: names, and what each threat needs (Phase 16).</summary>
public static class Health
{
    public static string Name(Specialty specialty) => specialty switch
    {
        Specialty.Trauma => "Major trauma",
        Specialty.Burns => "Burns",
        Specialty.Paediatric => "Paediatric",
        _ => "Psychiatric",
    };

    /// <summary>"Major trauma", "Burns" or "General" for a patient's need.</summary>
    public static string Need(Specialty? specialty) => specialty is { } s ? Name(s) : "General";

    public static string Name(CounsellingSetting setting) => setting switch
    {
        CounsellingSetting.Scene => "the scene",
        CounsellingSetting.RestCentre => "the rest centres",
        CounsellingSetting.FamilyAssistanceCentre => "the family assistance centre",
        _ => "a helpline",
    };

    /// <summary>What protects against a threat, as it would be dispensed.</summary>
    public static string Countermeasure(HealthThreatKind kind) => kind switch
    {
        HealthThreatKind.Biological => "antibiotics",
        HealthThreatKind.Radiological => "potassium iodide tablets",
        _ => "vaccine",
    };

    /// <summary>
    /// How long after exposure the countermeasure still protects. Compressed for an exercise: the real windows are
    /// days for antibiotics and vaccine, and hours for iodine.
    /// </summary>
    public static TimeSpan Window(HealthThreatKind kind) => kind switch
    {
        HealthThreatKind.Biological => TimeSpan.FromHours(6),
        HealthThreatKind.Radiological => TimeSpan.FromHours(4),
        _ => TimeSpan.FromHours(3),
    };

    /// <summary>Whether isolation and quarantine mean anything for it.</summary>
    public static bool Contagious(HealthThreatKind kind) => kind == HealthThreatKind.Infectious;

    /// <summary>A tag number as it is written on the triage card: "T-0007".</summary>
    public static string TagNumber(int sequence) => $"T-{sequence:D4}";
}

/// <summary>A patient as the tracking board has them, from their triage tag to where they ended up (Phase 16).</summary>
public sealed class PatientRecord
{
    public Guid Id { get; init; }
    public required string Tag { get; init; }
    public Guid? IncidentId { get; init; }
    public Triage Triage { get; set; }

    /// <summary>What they need beyond an ordinary emergency department, as the crew assessed it.</summary>
    public Specialty? Need { get; init; }

    public required string Injury { get; init; }
    public CasualtyState State { get; set; } = CasualtyState.TreatedOnScene;
    public DateTimeOffset TaggedAt { get; init; }
    public Guid? UnitId { get; set; }
    public Guid? HospitalId { get; set; }
    public DateTimeOffset? LeftSceneAt { get; set; }
    public DateTimeOffset? ArrivedAt { get; set; }

    /// <summary>The receiving hospital could give them what they need. False: a secondary transfer follows.</summary>
    public bool? RightPlace { get; set; }

    public int Transfers { get; set; }
    public DateTimeOffset? DiedAt { get; set; }
}

/// <summary>Command's rule for where the patients from an incident with one need go (Phase 16: transport destinations).</summary>
public sealed record DestinationRule(Guid IncidentId, Specialty? Need, Guid HospitalId);

/// <summary>A hospital's specialist beds, blood and mortuary, as it last reported them (Phase 16).</summary>
public sealed class HospitalResources
{
    public Guid HospitalId { get; init; }
    public List<SpecialtyBeds> Specialties { get; } = [];

    /// <summary>Takes children only (a children's hospital).</summary>
    public bool ChildrenOnly { get; set; }

    public int BloodUnits { get; set; }
    public int MorgueSpaces { get; set; }
    public int MorgueOccupied { get; set; }
    public DateTimeOffset? BloodLowSince { get; set; }
    public DateTimeOffset ReportedAt { get; set; }

    public SpecialtyBeds? Beds(Specialty specialty) => Specialties.FirstOrDefault(s => s.Specialty == specialty);
    public bool MorgueFull => MorgueOccupied >= MorgueSpaces;
}

/// <summary>The dead, as command knows of them (Phase 16: mass fatality).</summary>
public sealed class FatalitySituation
{
    /// <summary>Bodies reported at each incident's scene, still there.</summary>
    public Dictionary<Guid, int> AtScene { get; } = [];

    public int Recovered { get; set; }
    public int Identified { get; set; }
    public DateTimeOffset? CoronerNotifiedAt { get; set; }
    public Guid? MortuaryId { get; set; }
    public string? MortuaryName { get; set; }
    public GeoPoint? MortuaryLocation { get; set; }
    public DateTimeOffset? MortuaryOpenedAt { get; set; }
    public int InMortuary { get; set; }
}

/// <summary>A public-health threat, as HSE Public Health reports it.</summary>
public sealed class HealthThreat
{
    public Guid Id { get; init; }
    public HealthThreatKind Kind { get; init; }
    public required string Agent { get; init; }
    public required string Area { get; init; }
    public GeoPoint Location { get; init; }

    /// <summary>The incident it arose from: PODs draw their doses from its stock.</summary>
    public Guid? IncidentId { get; init; }

    public DateTimeOffset ReportedAt { get; init; }
    public int EstimatedExposed { get; set; }
    public int Ill { get; set; }
    public int Hospitalised { get; set; }
    public int Deaths { get; set; }
    public int Protected { get; set; }
    public required string Advice { get; init; }
    public DateTimeOffset? IsolationOrderedAt { get; set; }
    public DateTimeOffset? QuarantineOrderedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A point of dispensing: where the exposed come for the countermeasure.</summary>
public sealed class DispensingPoint
{
    public Guid Id { get; init; }
    public Guid ThreatId { get; init; }
    public required string Name { get; init; }
    public GeoPoint Location { get; init; }
    public DateTimeOffset OpenedAt { get; init; }
    public bool Ready { get; set; }
    public int Dispensed { get; set; }
    public int Queue { get; set; }
    public DateTimeOffset? OutOfStockSince { get; set; }
}

/// <summary>A crisis counselling team (or the helpline) and what its psychological triage found.</summary>
public sealed class CounsellingDeployment
{
    public Guid Id { get; init; }
    public Guid? IncidentId { get; init; }
    public CounsellingSetting Setting { get; init; }
    public DateTimeOffset DeployedAt { get; init; }
    public DateTimeOffset? WorkingSince { get; set; }
    public int Seen { get; set; }
    public int Low { get; set; }
    public int Moderate { get; set; }
    public int High { get; set; }

    /// <summary>High-risk people found a psychiatric bed; the rest wait for one.</summary>
    public int Admitted { get; set; }
}

/// <summary>Medical surge, the dead, public health and behavioural health (Phase 16), as the COP holds them.</summary>
public sealed class HealthSituation
{
    public List<PatientRecord> Patients { get; } = [];
    public List<DestinationRule> Destinations { get; } = [];
    public Dictionary<Guid, HospitalResources> Hospitals { get; } = [];
    public FatalitySituation Fatalities { get; } = new();
    public List<HealthThreat> Threats { get; } = [];
    public List<DispensingPoint> Dispensing { get; } = [];
    public List<CounsellingDeployment> Counselling { get; } = [];

    /// <summary>People in distress the scene, rest centres and family assistance centre report, by incident (an estimate).</summary>
    public Dictionary<Guid, int> Distressed { get; } = [];

    /// <summary>Walk-ins with no physical injury at each emergency department, as the hospitals report them.</summary>
    public Dictionary<Guid, int> WorriedWell { get; } = [];

    public Guid? DestinationFor(Guid incidentId, Specialty? need) =>
        Destinations.FirstOrDefault(d => d.IncidentId == incidentId && d.Need == need)?.HospitalId;

    public HealthSituation Copy()
    {
        var copy = new HealthSituation();
        copy.Patients.AddRange(Patients.Select(p => new PatientRecord
        {
            Id = p.Id, Tag = p.Tag, IncidentId = p.IncidentId, Triage = p.Triage, Need = p.Need, Injury = p.Injury, State = p.State,
            TaggedAt = p.TaggedAt, UnitId = p.UnitId, HospitalId = p.HospitalId, LeftSceneAt = p.LeftSceneAt, ArrivedAt = p.ArrivedAt,
            RightPlace = p.RightPlace, Transfers = p.Transfers, DiedAt = p.DiedAt,
        }));
        copy.Destinations.AddRange(Destinations);
        foreach (var (id, h) in Hospitals)
        {
            var resources = new HospitalResources
            {
                HospitalId = id, ChildrenOnly = h.ChildrenOnly, BloodUnits = h.BloodUnits, MorgueSpaces = h.MorgueSpaces, MorgueOccupied = h.MorgueOccupied,
                BloodLowSince = h.BloodLowSince, ReportedAt = h.ReportedAt,
            };
            resources.Specialties.AddRange(h.Specialties);
            copy.Hospitals[id] = resources;
        }
        var f = Fatalities;
        foreach (var (incident, bodies) in f.AtScene) copy.Fatalities.AtScene[incident] = bodies;
        (copy.Fatalities.Recovered, copy.Fatalities.Identified, copy.Fatalities.CoronerNotifiedAt, copy.Fatalities.MortuaryId) =
            (f.Recovered, f.Identified, f.CoronerNotifiedAt, f.MortuaryId);
        (copy.Fatalities.MortuaryName, copy.Fatalities.MortuaryLocation, copy.Fatalities.MortuaryOpenedAt, copy.Fatalities.InMortuary) =
            (f.MortuaryName, f.MortuaryLocation, f.MortuaryOpenedAt, f.InMortuary);
        copy.Threats.AddRange(Threats.Select(t => new HealthThreat
        {
            Id = t.Id, Kind = t.Kind, Agent = t.Agent, Area = t.Area, Location = t.Location, IncidentId = t.IncidentId, ReportedAt = t.ReportedAt,
            EstimatedExposed = t.EstimatedExposed, Ill = t.Ill, Hospitalised = t.Hospitalised, Deaths = t.Deaths, Protected = t.Protected,
            Advice = t.Advice, IsolationOrderedAt = t.IsolationOrderedAt, QuarantineOrderedAt = t.QuarantineOrderedAt, UpdatedAt = t.UpdatedAt,
        }));
        copy.Dispensing.AddRange(Dispensing.Select(d => new DispensingPoint
        {
            Id = d.Id, ThreatId = d.ThreatId, Name = d.Name, Location = d.Location, OpenedAt = d.OpenedAt, Ready = d.Ready, Dispensed = d.Dispensed,
            Queue = d.Queue, OutOfStockSince = d.OutOfStockSince,
        }));
        copy.Counselling.AddRange(Counselling.Select(c => new CounsellingDeployment
        {
            Id = c.Id, IncidentId = c.IncidentId, Setting = c.Setting, DeployedAt = c.DeployedAt, WorkingSince = c.WorkingSince, Seen = c.Seen,
            Low = c.Low, Moderate = c.Moderate, High = c.High, Admitted = c.Admitted,
        }));
        foreach (var (k, v) in Distressed) copy.Distressed[k] = v;
        foreach (var (k, v) in WorriedWell) copy.WorriedWell[k] = v;
        return copy;
    }
}
