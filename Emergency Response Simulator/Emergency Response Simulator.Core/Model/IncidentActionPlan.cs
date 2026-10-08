using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emergency_Response_Simulator.Core.Model;

/// <summary>
/// The formal plan for one operational period (Design Document §8.2–8.4, §19).
/// Each revision is a new version; when a version is approved, earlier approved versions for the same
/// period become <see cref="IapStatus.Superseded"/>. Rebuilt from the event stream like the rest of the COP.
/// </summary>
public class IncidentActionPlan
{
    public Guid Id { get; set; }
    public int Version { get; set; } = 1;
    public IapStatus Status { get; set; } = IapStatus.Draft;

    public Guid IncidentId { get; set; }
    public Incident? Incident { get; set; }

    public Guid OperationalPeriodId { get; set; }
    public OperationalPeriod? OperationalPeriod { get; set; }

    /// <summary>The version this one was copied from, if any.</summary>
    public Guid? BasedOnId { get; set; }

    /// <summary>Everything the plan says: objectives, organisation, assignments and the support plans.</summary>
    public IapContent Content { get; set; } = new();

    public string? PreparedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSavedAt { get; set; }

    public string? SubmittedBy { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }

    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }

    /// <summary>Why the approver sent the plan back for revision.</summary>
    public string? ReturnedBy { get; set; }
    public string? ReturnComments { get; set; }

    /// <summary>When the plan's assignments were last pushed to the units as orders (the briefing).</summary>
    public DateTimeOffset? BriefedAt { get; set; }

    public bool IsEditable => Status == IapStatus.Draft;

    public string Label => $"Period {OperationalPeriod?.Number.ToString() ?? "?"} · v{Version}";
}

/// <summary>
/// The body of an IAP. Plain data so that each saved version can travel in the event stream and be
/// compared with others; the builder edits a copy (<see cref="Clone"/>), never the recorded version.
/// </summary>
public sealed class IapContent
{
    /// <summary>Commander's intent: the one-paragraph "why" behind the objectives (§8.2).</summary>
    public string? CommandersIntent { get; set; }

    /// <summary>Situation summary at the time of writing (ICS 201/209), usually pulled from the COP.</summary>
    public string? SituationSummary { get; set; }

    public List<StrategicObjective> Objectives { get; set; } = [];

    // ICS 203: organisation assignment list
    public List<IapPosition> Organization { get; set; } = [];
    public List<IapGroup> Groups { get; set; } = [];

    // ICS 204: operational assignments
    public List<IapAssignment> Assignments { get; set; } = [];

    public CommunicationsPlan Communications { get; set; } = new();
    public MedicalPlan Medical { get; set; } = new();
    public SafetyPlan Safety { get; set; } = new();

    public string? NameFor(IcsRole role) =>
        Organization.FirstOrDefault(p => p.Role == role && !string.IsNullOrWhiteSpace(p.Name))?.Name;

    [JsonIgnore]
    public IEnumerable<OperationalObjective> OperationalObjectives => Objectives.SelectMany(o => o.Operational);

    /// <summary>"1.2" for the second operational objective under the first strategic objective.</summary>
    public string? NumberOf(Guid operationalObjectiveId)
    {
        for (var s = 0; s < Objectives.Count; s++)
        {
            var index = Objectives[s].Operational.FindIndex(o => o.Id == operationalObjectiveId);
            if (index >= 0) return $"{s + 1}.{index + 1}";
        }
        return null;
    }

    private static readonly JsonSerializerOptions CopyOptions = new();

    /// <summary>A deep copy, so editing a draft never touches a recorded version.</summary>
    public IapContent Clone() =>
        JsonSerializer.Deserialize<IapContent>(JsonSerializer.Serialize(this, CopyOptions), CopyOptions)!;
}

/// <summary>Strategic level: what we must achieve overall, e.g. "Protect life" (§8.2).</summary>
public sealed class StrategicObjective
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Statement { get; set; } = "";
    public List<OperationalObjective> Operational { get; set; } = [];
}

/// <summary>
/// Operational level: a measurable outcome for this period, e.g. "Evacuate all civilians within the
/// projected hazard area", with the resources it needs and a performance target (§8.2).
/// </summary>
public sealed class OperationalObjective
{
    /// <summary>Kept when a plan is revised, so progress is tracked across versions and periods.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Statement { get; set; } = "";

    /// <summary>Section or group responsible, e.g. "Operations" or "Evacuation Group".</summary>
    public string? Responsible { get; set; }

    public string? ResourcesSummary { get; set; }

    /// <summary>What "done" looks like, e.g. "Initial evacuation completed".</summary>
    public string? PerformanceTarget { get; set; }

    /// <summary>When the target should be met.</summary>
    public DateTimeOffset? TargetTime { get; set; }

    public List<TacticalTask> Tactics { get; set; } = [];
}

/// <summary>Tactical level: how the objective is achieved, e.g. "Establish traffic-control points".</summary>
public sealed class TacticalTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Description { get; set; } = "";
}

public enum ObjectiveStatus
{
    Open,
    InProgress,
    Achieved,

    /// <summary>Will not be achieved this period; carried forward or dropped at reassessment.</summary>
    NotAchieved,
}

/// <summary>Who fills which ICS position for this period (ICS 203).</summary>
public sealed class IapPosition
{
    public IcsRole Role { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>A group or division in the plan (ICS 203/204).</summary>
public sealed class IapGroup
{
    public string Name { get; set; } = "";
    public IcsGroupKind Kind { get; set; }
    public string? Supervisor { get; set; }
}

/// <summary>Unit → assignment, e.g. "Engine 12 → Building A" (§8.4, ICS 204).</summary>
public sealed class IapAssignment
{
    public Guid UnitId { get; set; }

    /// <summary>Recorded with the plan so the plan still reads correctly if the unit leaves the roster.</summary>
    public string Callsign { get; set; } = "";

    public string Assignment { get; set; } = "";

    /// <summary>Group or division the unit works under; null reports directly to Operations / the IC.</summary>
    public string? Group { get; set; }

    /// <summary>The operational objective the work serves.</summary>
    public Guid? ObjectiveId { get; set; }
}

/// <summary>Radio plan (ICS 205): who talks on which channel.</summary>
public sealed class CommunicationsPlan
{
    public List<RadioChannel> Channels { get; set; } = [];
    public string? Notes { get; set; }
}

public sealed class RadioChannel
{
    /// <summary>Command, Tactical, Medical, Inter-agency, Air-to-ground…</summary>
    public string Function { get; set; } = "";

    /// <summary>Talkgroup or channel name, e.g. "FIRE CMD 1".</summary>
    public string Channel { get; set; } = "";

    /// <summary>Who uses it, e.g. "IC, section chiefs, group supervisors".</summary>
    public string AssignedTo { get; set; } = "";

    public string? Remarks { get; set; }
}

/// <summary>Medical plan (ICS 206): care for responders and the receiving hospitals.</summary>
public sealed class MedicalPlan
{
    /// <summary>Where casualties are triaged and treated, e.g. "Grand Canal Dock car park".</summary>
    public string? CasualtyClearingStation { get; set; }

    public string? AmbulanceLoadingPoint { get; set; }

    /// <summary>Who runs the medical side, e.g. "Medical Group Supervisor (AP Walsh)".</summary>
    public string? MedicalLead { get; set; }

    public List<ReceivingHospital> Hospitals { get; set; } = [];

    /// <summary>What to do if a responder is injured.</summary>
    public string? EmergencyProcedures { get; set; }
}

public sealed class ReceivingHospital
{
    public string Name { get; set; } = "";
    public double? DistanceKm { get; set; }

    /// <summary>Blue-light road travel time from the incident.</summary>
    public double? TravelMinutes { get; set; }

    public string? Capabilities { get; set; }
}

/// <summary>Safety plan (ICS 208): known hazards, mitigations and the safety message.</summary>
public sealed class SafetyPlan
{
    public List<SafetyHazard> Hazards { get; set; } = [];

    /// <summary>Required protective equipment, e.g. "BA in hot zone", "Hi-vis on all roads".</summary>
    public List<string> Ppe { get; set; } = [];

    /// <summary>The message every responder hears at briefing.</summary>
    public string? Message { get; set; }

    /// <summary>Personnel accountability, e.g. "BA entry control at Bridge Street".</summary>
    public string? Accountability { get; set; }
}

public sealed class SafetyHazard
{
    public string Hazard { get; set; } = "";
    public string? Mitigation { get; set; }
}
