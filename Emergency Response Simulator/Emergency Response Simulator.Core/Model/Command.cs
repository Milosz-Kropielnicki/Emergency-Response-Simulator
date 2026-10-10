namespace Emergency_Response_Simulator.Core.Model;

// Command and control objects (Design Document §8.1, §9). They are live COP state rebuilt from the
// event stream; Phase 5 persists the formal plan (IAP) separately.

/// <summary>ICS positions: the Incident Commander, command staff and general staff (§8.1).</summary>
public enum IcsRole
{
    IncidentCommander,
    Safety,
    Liaison,
    PublicInformation,
    Operations,
    Planning,
    Logistics,
    FinanceAdmin,
}

public enum IcsGroupKind
{
    /// <summary>A functional group, e.g. "Fire Group", "Medical Group".</summary>
    Group,

    /// <summary>A geographic division, e.g. "Division A (north side)".</summary>
    Division,
}

/// <summary>A group or division under Operations, with its supervisor and assigned units.</summary>
public sealed class IcsGroup
{
    public Guid Id { get; init; }
    public required string Name { get; set; }
    public IcsGroupKind Kind { get; set; }
    public string? Supervisor { get; set; }
    public List<Guid> UnitIds { get; } = [];
}

/// <summary>The command structure of one incident.</summary>
public sealed class IncidentCommand
{
    public Dictionary<IcsRole, string> Positions { get; } = [];
    public List<IcsGroup> Groups { get; } = [];

    /// <summary>
    /// Applies an ICS event to this structure. Shared by the COP and ground truth so that both see the same
    /// organisation (the structure is a fact of how command set itself up, not something to be discovered).
    /// </summary>
    public void Apply(Events.DomainEvent payload)
    {
        switch (payload)
        {
            case Events.IcsPositionAssigned e:
                Positions[e.Role] = e.Name;
                break;
            case Events.IncidentCommanderAssigned e:
                Positions[IcsRole.IncidentCommander] = e.Name;
                break;
            case Events.IcsGroupFormed e:
                Groups.Add(new IcsGroup { Id = e.GroupId, Name = e.Name, Kind = e.Kind, Supervisor = e.Supervisor });
                break;
            case Events.IcsGroupDisbanded e:
                Groups.RemoveAll(g => g.Id == e.GroupId); // its units fall back to Operations / the IC
                break;
            case Events.UnitAssignedToGroup e:
                foreach (var group in Groups)
                    group.UnitIds.Remove(e.UnitId);
                if (e.GroupId is { } target && Groups.FirstOrDefault(g => g.Id == target) is { } joined)
                    joined.UnitIds.Add(e.UnitId);
                break;
            case Events.UnitDispatchCancelled e:
                foreach (var group in Groups)
                    group.UnitIds.Remove(e.UnitId);
                break;
        }
    }

    public IcsGroup? GroupOf(Guid unitId) => Groups.FirstOrDefault(g => g.UnitIds.Contains(unitId));

    public bool IsStaffed(IcsRole role) => Positions.ContainsKey(role);
}

public enum OrderTargetKind
{
    Unit,
    Group,
    Position,
}

public enum OrderStatus
{
    Issued,
    Acknowledged,
    Completed,
    Cancelled,

    /// <summary>The recipient said it can't do it (e.g. nobody qualified for the task).</summary>
    Declined,
}

/// <summary>A directive to a unit, group or position, closed by a read-back (§9, §13).</summary>
public sealed class Order
{
    public Guid Id { get; init; }
    public Guid? IncidentId { get; init; }
    public OrderTargetKind TargetKind { get; init; }
    public Guid? TargetId { get; init; }
    public required string TargetName { get; init; }
    public required string Text { get; init; }
    public DateTimeOffset IssuedAt { get; init; }
    public OrderStatus Status { get; set; } = OrderStatus.Issued;
    public DateTimeOffset? AcknowledgedAt { get; set; }

    /// <summary>What the recipient read back. May differ from what was said (§13).</summary>
    public string? ReadBack { get; set; }

    public bool ReadBackGarbled { get; set; }

    /// <summary>Command confirmed the read-back was right, closing the loop (§13).</summary>
    public DateTimeOffset? ReadBackConfirmedAt { get; set; }

    /// <summary>Why the recipient declined the order (§12: not qualified for the task).</summary>
    public string? DeclineReason { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }
}

public enum ResourceRequestKind
{
    /// <summary>More of our own resources from outside the operational area; no approval needed.</summary>
    AdditionalResources,

    /// <summary>Resources from a neighbouring service under a mutual-aid agreement; needs regional approval.</summary>
    MutualAid,

    /// <summary>National or specialist teams (USAR, bomb disposal, hazmat specialists); needs national approval.</summary>
    SpecialistTeam,
}

public enum ResourceRequestStatus
{
    Requested,
    Approved,
    Denied,
    Fulfilled,
    Cancelled,
}

public sealed class ResourceRequest
{
    public Guid Id { get; init; }
    public Guid IncidentId { get; init; }
    public ResourceRequestKind Kind { get; init; }
    public UnitType? UnitType { get; init; }
    public int Quantity { get; init; }
    public required string Description { get; init; }
    public string? Justification { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public ResourceRequestStatus Status { get; set; } = ResourceRequestStatus.Requested;
    public string? DecidedBy { get; set; }
    public string? DecisionReason { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>When the resources are expected on scene, as promised by the provider.</summary>
    public DateTimeOffset? ExpectedAt { get; set; }

    public List<Guid> FulfilledBy { get; } = [];
}

public enum ApprovalStatus
{
    Pending,
    Approved,
    Denied,
}

/// <summary>A decision someone is asking command to make, e.g. authority to evacuate (§9).</summary>
public sealed class ApprovalRequest
{
    public Guid Id { get; init; }
    public Guid? IncidentId { get; init; }
    public required string Subject { get; init; }
    public required string Details { get; init; }
    public required string RequestedBy { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public string? Note { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>A message to an outside organisation: hospital, utility, agency, government (§9).</summary>
public sealed class Notification
{
    public Guid Id { get; init; }
    public Guid? IncidentId { get; init; }
    public required string Recipient { get; init; }
    public required string Message { get; init; }
    public DateTimeOffset SentAt { get; init; }
    public string? Reply { get; set; }
    public DateTimeOffset? RepliedAt { get; set; }
}
