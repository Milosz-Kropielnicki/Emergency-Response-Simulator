using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Hazards;

namespace Emergency_Response_Simulator.Simulation.State;

/// <summary>
/// Ground truth about health (Phase 16): what each casualty really needs, the hospitals' specialist beds, blood and
/// mortuaries, the dead and where they are, public-health threats and who was exposed, points of dispensing, and the
/// counselling teams. Discrete changes come from the event stream; <c>PatientTrackingSystem</c>,
/// <c>MassFatalitySystem</c>, <c>PublicHealthSystem</c> and <c>BehaviouralHealthSystem</c> advance the rest.
/// </summary>
public sealed class WorldHealth
{
    /// <summary>Set by <c>MassFatalitySystem</c>: the dead are recovered and identified before families are told.</summary>
    public bool FatalitiesActive { get; set; }

    public sealed class HospitalCare
    {
        public Dictionary<Specialty, int> Beds { get; } = [];
        public Dictionary<Specialty, int> Occupied { get; } = [];
        public bool ChildrenOnly { get; set; }
        public double Blood { get; set; }

        /// <summary>The stock it normally holds.</summary>
        public double UsualBlood { get; set; }
        public int MorgueSpaces { get; set; }
        public int MorgueOccupied { get; set; }

        /// <summary>Walk-ins with no injury in the emergency department now, and when each lot leaves.</summary>
        public List<(DateTimeOffset Leaves, int Count)> WorriedWell { get; } = [];

        public int WorriedWellNow => WorriedWell.Sum(w => w.Count);
        public bool Has(Specialty specialty) => Beds.ContainsKey(specialty);
        public int Free(Specialty specialty) => Beds.GetValueOrDefault(specialty) - Occupied.GetValueOrDefault(specialty);

        public IReadOnlyList<SpecialtyBeds> Specialties() =>
            [.. Beds.Select(b => new SpecialtyBeds(b.Key, b.Value, Occupied.GetValueOrDefault(b.Key)))];
    }

    public enum BodyPlace
    {
        Scene,

        /// <summary>In a hospital's mortuary.</summary>
        HospitalMorgue,

        /// <summary>At a hospital with a full mortuary: held in the emergency department, taking a place.</summary>
        HeldInHospital,

        Mortuary,
    }

    public sealed class Body(Guid id, Guid? incidentId, GeoPoint location, DateTimeOffset diedAt)
    {
        public Guid Id { get; } = id;
        public Guid? IncidentId { get; } = incidentId;
        public GeoPoint Location { get; set; } = location;
        public DateTimeOffset DiedAt { get; } = diedAt;
        public BodyPlace Place { get; set; }
        public Guid? HospitalId { get; set; }
        public DateTimeOffset? InMortuarySince { get; set; }
        public DateTimeOffset? IdentifiedAt { get; set; }
    }

    public sealed class Threat(HealthThreatStarted started, DateTimeOffset at)
    {
        public HealthThreatStarted Started { get; } = started;
        public DateTimeOffset StartedAt { get; } = at;
        public HealthThreatKind Kind => Started.Kind;

        /// <summary>The perceived incident it belongs to (for the alert, and the doses at its points of dispensing).</summary>
        public Guid? IncidentId { get; set; }

        public bool Alerted { get; set; }

        /// <summary>Plume threats: the people exposed, when, and whether they will fall ill unless protected in time.</summary>
        public Dictionary<int, Exposure> Exposed { get; } = [];

        // Infectious threats: a group of contacts, by compartment.
        public double Susceptible { get; set; }
        public List<(DateTimeOffset At, double Count)> Incubating { get; } = [];
        public double Infectious { get; set; }
        public double Isolated { get; set; }
        public double Recovered { get; set; }

        public int Ill { get; set; }
        public int Hospitalised { get; set; }
        public int Deaths { get; set; }

        /// <summary>Protected by the countermeasure, and of those, protected in time.</summary>
        public int Protected { get; set; }
        public int ProtectedInTime { get; set; }

        public DateTimeOffset? IsolationAt { get; set; }
        public DateTimeOffset? QuarantineAt { get; set; }
        public int TotalExposed => Kind == HealthThreatKind.Infectious ? (int)Math.Round(Started.Contacts + (double)Started.IndexCases) : Exposed.Count;
    }

    public sealed class Exposure(int civilianId, DateTimeOffset at, DateTimeOffset onset, bool fallsIll)
    {
        public int CivilianId { get; } = civilianId;
        public DateTimeOffset At { get; } = at;
        public DateTimeOffset Onset { get; } = onset;
        public bool FallsIll { get; } = fallsIll;
        public bool Protected { get; set; }
        public bool Ill { get; set; }
    }

    public sealed class Pod(DispensingPointOpened opened, DateTimeOffset at)
    {
        public DispensingPointOpened Opened { get; } = opened;
        public DateTimeOffset OpenedAt { get; } = at;
        public double Queue { get; set; }
        public double Arrivals { get; set; }
        public int Dispensed { get; set; }
        public bool OutOfStock { get; set; }
    }

    public sealed class Counselling(CrisisCounsellingDeployed deployed, DateTimeOffset at)
    {
        public CrisisCounsellingDeployed Deployed { get; } = deployed;
        public DateTimeOffset DeployedAt { get; } = at;
        public int Seen { get; set; }
        public int Low { get; set; }
        public int Moderate { get; set; }
        public int High { get; set; }
        public int Admitted { get; set; }
    }

    public Dictionary<Guid, HospitalCare> Hospitals { get; } = [];

    /// <summary>What each casualty really needs beyond an ordinary emergency department; null for general care.</summary>
    public Dictionary<Guid, Specialty?> Needs { get; } = [];

    /// <summary>Command's transport destinations, by perceived incident and need.</summary>
    public Dictionary<(Guid Incident, Specialty? Need), Guid> Destinations { get; } = [];

    public Dictionary<Guid, Body> Bodies { get; } = [];

    /// <summary>Patients who died after they were handed over (they had a place in the emergency department).</summary>
    public HashSet<Guid> DiedInHospital { get; } = [];
    public (Guid Id, string Name, GeoPoint Location, Guid? IncidentId, DateTimeOffset OpenedAt)? Mortuary { get; private set; }
    public Dictionary<Guid, Threat> Threats { get; } = [];
    public Dictionary<Guid, Pod> Pods { get; } = [];
    public List<Counselling> Counsellors { get; } = [];

    /// <summary>Blood on its way from the IBTS: to which hospital, how much, when.</summary>
    public List<(Guid HospitalId, int Units, DateTimeOffset Due)> BloodDue { get; } = [];

    /// <summary>When command last told the public something, and what (people come to a POD they have heard about).</summary>
    public List<(DateTimeOffset At, string Text)> Statements { get; } = [];

    /// <summary>People in distress, by perceived incident (the behavioural health system's estimate of the truth).</summary>
    public Dictionary<Guid, int> Distress { get; } = [];

    public bool Accepts(WorldHospital hospital, Specialty? need) =>
        !(Hospitals.GetValueOrDefault(hospital.Id)?.ChildrenOnly == true && need != Specialty.Paediatric);

    /// <summary>Whether the hospital can give this patient what they need now.</summary>
    public bool CanTreat(Guid hospitalId, Specialty? need) =>
        need is not { } specialty || Hospitals.GetValueOrDefault(hospitalId) is { } care && care.Has(specialty) && care.Free(specialty) > 0;

    public bool Identified(Guid casualtyId) => Bodies.TryGetValue(casualtyId, out var body) && body.IdentifiedAt is not null;

    /// <summary>
    /// What a casualty needs, from how they were hurt (seeded from their id, so every run agrees): children go to the
    /// children's hospital; serious burns to the burns unit; serious blunt or blast injuries to the trauma centre.
    /// </summary>
    public static Specialty? NeedOf(CasualtyInjured injured)
    {
        var cause = injured.Cause.ToLowerInvariant();
        var random = SimRandom.For(injured.CasualtyId, 161);
        var civilian = injured.Who is null && !cause.Contains("firefighter");
        if (civilian && random.NextDouble() < 0.12) return Specialty.Paediatric;
        if (injured.Triage is not (Triage.Immediate or Triage.Urgent)) return null;
        if (cause.Contains("burn") || cause.Contains("fire") && random.NextDouble() < 0.6) return Specialty.Burns;
        if (cause.Contains("explosion") || cause.Contains("collapse") || cause.Contains("crush") || cause.Contains("fall")
            || cause.Contains("accident") || cause.Contains("caught") || cause.Contains("struck"))
            return cause.Contains("explosion") && random.NextDouble() < 0.3 ? Specialty.Burns : Specialty.Trauma;
        return injured.Triage == Triage.Immediate && random.NextDouble() < 0.4 ? Specialty.Trauma : null;
    }

    public void Apply(DomainEvent payload, DateTimeOffset at)
    {
        switch (payload)
        {
            case HospitalServicesRegistered e:
                var care = new HospitalCare { ChildrenOnly = e.ChildrenOnly, Blood = e.BloodUnits, UsualBlood = e.BloodUnits, MorgueSpaces = e.MorgueSpaces };
                foreach (var beds in e.Specialties)
                {
                    care.Beds[beds.Specialty] = beds.Beds;
                    care.Occupied[beds.Specialty] = beds.Occupied;
                }
                Hospitals[e.HospitalId] = care;
                break;
            case CasualtyInjured e:
                Needs.TryAdd(e.CasualtyId, NeedOf(e));
                break;
            case TransportDestinationSet e:
                if (e.HospitalId is { } to) Destinations[(e.IncidentId, e.Need)] = to;
                else Destinations.Remove((e.IncidentId, e.Need));
                break;
            case BloodRequested e:
                BloodDue.Add((e.HospitalId, e.Units, at + Systems.PatientTrackingSystem.BloodLeadTime));
                break;
            case TemporaryMortuaryOpened e:
                Mortuary ??= (e.MortuaryId, e.Name, e.Location, e.IncidentId, at);
                break;
            case HealthThreatStarted e:
                Threats.TryAdd(e.ThreatId, new Threat(e, at)
                {
                    Susceptible = e.Contacts, Infectious = e.IndexCases, Ill = e.IndexCases,
                });
                break;
            case IsolationOrdered e when Threats.TryGetValue(e.ThreatId, out var isolating):
                isolating.IsolationAt ??= at;
                break;
            case QuarantineOrdered e when Threats.TryGetValue(e.ThreatId, out var quarantining):
                quarantining.QuarantineAt ??= at;
                break;
            case DispensingPointOpened e:
                Pods.TryAdd(e.PodId, new Pod(e, at));
                break;
            case CrisisCounsellingDeployed e:
                Counsellors.Add(new Counselling(e, at));
                break;
            case PublicStatementIssued e:
                Statements.Add((at, e.Text));
                break;
        }
    }

    /// <summary>Words that tell people to come for the countermeasure.</summary>
    public static bool TellsPeopleToCome(string text) =>
        new[] { "dispens", "tablet", "antibiotic", "vaccin", "prophyla", "iodine", "collect" }.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
}
