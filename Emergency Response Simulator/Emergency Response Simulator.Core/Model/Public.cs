using Emergency_Response_Simulator.Core.Geo;

namespace Emergency_Response_Simulator.Core.Model;

// ---- Phase 14: public, media, political and legal pressures, as the COP holds them ----

/// <summary>Why a person needs more than a warning to get out (Phase 14). Ground truth: command learns of it from registers and reports.</summary>
[Flags]
public enum Vulnerability
{
    None = 0,

    /// <summary>Slow on foot, and many won't leave without someone they trust telling them in person.</summary>
    Elderly = 1,

    /// <summary>Can't leave at all without help: a wheelchair user, someone bed-bound or on oxygen.</summary>
    MobilityImpaired = 2,

    /// <summary>Outdoors, at no address: door-knocking and shelter-in-place don't reach them.</summary>
    Homeless = 4,

    /// <summary>Won't leave without the dog or cat, unless somewhere takes animals; may go back in for them.</summary>
    PetOwner = 8,
}

/// <summary>What command does with members of the public who turn up wanting to help.</summary>
public enum VolunteerDecision
{
    /// <summary>Sign them in, brief them and give them work away from the hazard (with the family assistance centre, rest centres).</summary>
    Register,

    /// <summary>Thank them and send them home.</summary>
    TurnAway,
}

/// <summary>What a missing-person enquiry turned out to be, once traced.</summary>
public enum TraceOutcome
{
    /// <summary>Out of the area and safe: evacuated, or never there.</summary>
    Safe,

    InHospital,
    Deceased,

    /// <summary>Still inside the affected area: a search, or a welfare check, is needed.</summary>
    StillInArea,
}

/// <summary>A rumour going round (Phase 14), as social-media monitoring picks it up. Whether it is true, command does not know.</summary>
public sealed class Rumour
{
    public Guid Id { get; init; }
    public Guid? IncidentId { get; init; }
    public required string Claim { get; init; }

    /// <summary>Short subject, used to tell whether a statement or press conference addressed it ("explosion", "schools").</summary>
    public required string Topic { get; init; }

    public DateTimeOffset FirstSeenAt { get; init; }

    /// <summary>Roughly how many people have seen it, as monitoring estimates it (shares and views), in thousands.</summary>
    public int ReachThousands { get; set; }

    public DateTimeOffset? RebuttedAt { get; set; }
    public string? Rebuttal { get; set; }

    /// <summary>Set when it came out that command had denied something true (Phase 14: misinformation from the authorities).</summary>
    public DateTimeOffset? DenialExposedAt { get; set; }

    public bool Spreading => RebuttedAt is null;
}

/// <summary>Members of the public at the cordon offering to help.</summary>
public sealed class VolunteerGroup
{
    public Guid IncidentId { get; init; }
    public int Count { get; set; }
    public GeoPoint Location { get; set; }
    public DateTimeOffset ArrivedAt { get; init; }
    public VolunteerDecision? Decision { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>Some have gone past the cordon on their own: reported, and a hazard to them and the crews.</summary>
    public int SelfDeployed { get; set; }

    public bool Waiting => Decision is null;
}

/// <summary>What the council's vulnerable-persons register (or a community leader) says about a zone.</summary>
public sealed class VulnerableReport
{
    public Guid ZoneId { get; init; }
    public required string Source { get; init; }
    public int Elderly { get; set; }
    public int MobilityImpaired { get; set; }
    public int PetOwners { get; set; }

    /// <summary>Rough-sleepers the source knows of: they are on no register.</summary>
    public int Homeless { get; set; }

    public DateTimeOffset ReportedAt { get; set; }

    /// <summary>Command asked for an assisted evacuation of the zone: ambulance transport, care staff, a bus.</summary>
    public DateTimeOffset? AssistanceRequestedAt { get; set; }

    public int Helped { get; set; }
    public int Total => Elderly + MobilityImpaired + Homeless;
}

/// <summary>Someone a relative can't reach, asked about on the public enquiry line.</summary>
public sealed class MissingPersonEnquiry
{
    public Guid Id { get; init; }
    public Guid? IncidentId { get; init; }
    public required string Caller { get; init; }
    public required string Missing { get; init; }
    public required string LastKnown { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
    public DateTimeOffset? TracedAt { get; set; }
    public TraceOutcome? Outcome { get; set; }
    public string? Detail { get; set; }

    /// <summary>The family was given support: a family liaison officer, for the bereaved and those with someone in hospital.</summary>
    public bool Supported { get; set; }

    public bool Waiting => TracedAt is null;
}

/// <summary>Where relatives come for news, evacuees register, and families get support.</summary>
public sealed class FamilyAssistanceCentre
{
    public Guid Id { get; init; }
    public Guid? IncidentId { get; init; }
    public required string Name { get; init; }
    public GeoPoint Location { get; init; }
    public DateTimeOffset OpenedAt { get; init; }

    /// <summary>Volunteers registered and sent here to help (Phase 14).</summary>
    public int Volunteers { get; set; }
}

/// <summary>One entry in command's decision log: what was decided and why, written at the time (Phase 14: legal).</summary>
public sealed class DecisionLogEntry
{
    public Guid Id { get; init; }
    public DateTimeOffset At { get; init; }
    public required string Decision { get; init; }
    public required string Rationale { get; init; }

    /// <summary>The recorded command it explains, when the author tied it to one.</summary>
    public long? AboutSequence { get; init; }

    public string? By { get; init; }
}

/// <summary>Everything from outside the response that command has to manage (Phase 14), as the COP holds it.</summary>
public sealed class PublicSituation
{
    public List<Rumour> Rumours { get; } = [];
    public List<VolunteerGroup> Volunteers { get; } = [];
    public List<VulnerableReport> Vulnerable { get; } = [];
    public List<MissingPersonEnquiry> MissingPersons { get; } = [];
    public List<FamilyAssistanceCentre> AssistanceCentres { get; } = [];
    public List<DecisionLogEntry> DecisionLog { get; } = [];

    /// <summary>Press conferences held, newest last.</summary>
    public List<DateTimeOffset> PressConferences { get; } = [];

    /// <summary>Zones whose assisted evacuation command has asked for, and when: before any register has come back, too.</summary>
    public Dictionary<Guid, DateTimeOffset> AssistedEvacuations { get; } = [];

    /// <summary>A copy, so a reader never sees it change underneath them.</summary>
    public PublicSituation Copy()
    {
        var copy = new PublicSituation();
        copy.Rumours.AddRange(Rumours.Select(r => new Rumour
        {
            Id = r.Id, IncidentId = r.IncidentId, Claim = r.Claim, Topic = r.Topic, FirstSeenAt = r.FirstSeenAt, ReachThousands = r.ReachThousands,
            RebuttedAt = r.RebuttedAt, Rebuttal = r.Rebuttal, DenialExposedAt = r.DenialExposedAt,
        }));
        copy.Volunteers.AddRange(Volunteers.Select(v => new VolunteerGroup
        {
            IncidentId = v.IncidentId, Count = v.Count, Location = v.Location, ArrivedAt = v.ArrivedAt, Decision = v.Decision,
            DecidedAt = v.DecidedAt, SelfDeployed = v.SelfDeployed,
        }));
        copy.Vulnerable.AddRange(Vulnerable.Select(v => new VulnerableReport
        {
            ZoneId = v.ZoneId, Source = v.Source, Elderly = v.Elderly, MobilityImpaired = v.MobilityImpaired, PetOwners = v.PetOwners,
            Homeless = v.Homeless, ReportedAt = v.ReportedAt, AssistanceRequestedAt = v.AssistanceRequestedAt, Helped = v.Helped,
        }));
        copy.MissingPersons.AddRange(MissingPersons.Select(m => new MissingPersonEnquiry
        {
            Id = m.Id, IncidentId = m.IncidentId, Caller = m.Caller, Missing = m.Missing, LastKnown = m.LastKnown, ReceivedAt = m.ReceivedAt,
            TracedAt = m.TracedAt, Outcome = m.Outcome, Detail = m.Detail, Supported = m.Supported,
        }));
        copy.AssistanceCentres.AddRange(AssistanceCentres.Select(c => new FamilyAssistanceCentre
        {
            Id = c.Id, IncidentId = c.IncidentId, Name = c.Name, Location = c.Location, OpenedAt = c.OpenedAt, Volunteers = c.Volunteers,
        }));
        copy.DecisionLog.AddRange(DecisionLog);
        copy.PressConferences.AddRange(PressConferences);
        foreach (var (zone, at) in AssistedEvacuations) copy.AssistedEvacuations[zone] = at;
        return copy;
    }
}
