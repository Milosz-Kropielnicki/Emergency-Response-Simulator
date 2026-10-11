using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Simulation.State;

/// <summary>
/// The multi-agency side of the world as it really is (Design Document §14): what each agency knows and has not told
/// command, how it feels about command, what it is waiting on, the EOC's staffing, and how the public is being told
/// things. Advanced by <see cref="Systems.InteragencySystem"/>; the COP sees only what reaches command.
/// </summary>
public sealed class WorldInteragency
{
    /// <summary>What every agency hears about command's incidents (type, threats, whether closed), by incident id.</summary>
    public Dictionary<Guid, IncidentFacts> Incidents { get; } = [];

    /// <summary>Zones declared for each incident.</summary>
    public Dictionary<Guid, List<Geometry>> IncidentZones { get; } = [];

    public List<WorldJurisdiction> Jurisdictions { get; } = [];
    public Dictionary<Guid, MutualAidAgreementRegistered> Agreements { get; } = [];

    /// <summary>Units sent under each agreement so far.</summary>
    public Dictionary<Guid, int> Provided { get; } = [];

    /// <summary>Providers whose crews were overruled on the agreement's terms: they are slower to help again.</summary>
    public HashSet<string> Reluctant { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<Guid, AgencyStanding> Standing { get; } = [];

    /// <summary>Other agencies' units released to command.</summary>
    public HashSet<Guid> Attached { get; } = [];

    public List<PendingInvitation> Invitations { get; } = [];
    public List<PendingAssistance> Assistance { get; } = [];
    public List<(Guid AgencyId, DateTimeOffset At)> Briefings { get; } = [];
    public Dictionary<Guid, HeldItem> Held { get; } = [];
    public Dictionary<Guid, WorldDispute> Disputes { get; } = [];

    /// <summary>Zones declared since the friction model last looked (for jurisdiction checks).</summary>
    public List<(ZoneDeclared Zone, DateTimeOffset At)> NewZones { get; } = [];

    /// <summary>Parts of zones not enforced while an authority disputes them.</summary>
    public Dictionary<Guid, List<ZoneExclusion>> Exclusions { get; } = [];

    /// <summary>Incidents where an authority has already agreed joint measures with command.</summary>
    public HashSet<(Guid IncidentId, Guid AgencyId)> Agreed { get; } = [];

    // EOC
    public EocLevel EocLevel { get; set; }
    public EocLevel? EocRequested { get; set; }
    public DateTimeOffset? EocRequestedAt { get; set; }
    public DateTimeOffset? EocLevelSince { get; set; }
    public HashSet<string> StaffedSeats { get; } = [];
    public DateTimeOffset? SitrepDue { get; set; }
    public DateTimeOffset? LastSitrepAt { get; set; }

    // Public information
    public List<(PublicStatementIssued Statement, DateTimeOffset At)> Statements { get; } = [];

    /// <summary>When command last told the public what to do (evacuate, shelter, keep windows shut).</summary>
    public DateTimeOffset? LastWarningAt { get; set; }

    /// <summary>Another agency contradicted the official line and no joint statement has put it right since.</summary>
    public DateTimeOffset? ConflictingSince { get; set; }

    public Dictionary<Guid, (MediaEnquiryReceived Enquiry, DateTimeOffset At)> Enquiries { get; } = [];

    public AgencyStanding StandingOf(Guid agencyId) => Standing.GetValueOrDefault(agencyId, AgencyStanding.Good);

    /// <summary>The EOC can decide disputes and coordinate agencies: partial activation reached, or above.</summary>
    public bool MajorEmergency => EocLevel >= EocLevel.Partial;

    /// <summary>Whether a zone's measures apply at a point now (not cut out by an open jurisdiction dispute).</summary>
    public bool Enforced(Guid zoneId, Point point, DateTimeOffset now) =>
        !Exclusions.TryGetValue(zoneId, out var cut) || !cut.Any(x => (x.Until is null || now < x.Until) && x.Area.Covers(point));

    public void Apply(DomainEvent payload, DateTimeOffset at)
    {
        switch (payload)
        {
            case JurisdictionDeclared e when e.Boundary.Count >= 3:
                Jurisdictions.RemoveAll(j => j.Info.JurisdictionId == e.JurisdictionId);
                Jurisdictions.Add(new WorldJurisdiction(e, Wgs84.CreatePolygon(e.Boundary)));
                break;
            case MutualAidAgreementRegistered e:
                Agreements[e.AgreementId] = e;
                break;
            case AgencyRelationChanged e:
                Standing[e.AgencyId] = e.Standing;
                break;
            case UnifiedCommandInvited e:
                Invitations.Add(new PendingInvitation(e.IncidentId, e.AgencyId, at));
                break;
            case AgencyAssistanceRequested e:
                Assistance.Add(new PendingAssistance(e, at));
                break;
            case AgencyAssistanceDecided e:
                Attached.UnionWith(e.UnitIds);
                break;
            case UnitDispatchCancelled e:
                Attached.Remove(e.UnitId);
                break;
            case UnitTasked e:
                Attached.Remove(e.UnitId);
                break;
            case AgencyBriefingRequested e:
                Briefings.Add((e.AgencyId, at));
                break;
            case InformationHeld e:
                Held[e.ItemId] = new HeldItem(e, at);
                break;
            case InformationShared e when Held.TryGetValue(e.ItemId, out var item):
                item.SharedAt = at;
                item.Via = e.Via;
                break;
            case DisputeRaised e:
                Disputes[e.DisputeId] = new WorldDispute(e, at);
                break;
            case DisputeResolutionChosen e when Disputes.TryGetValue(e.DisputeId, out var dispute):
                dispute.Chosen = e.Resolution;
                dispute.ChosenAt = at;
                break;
            case DisputeResolved e when Disputes.TryGetValue(e.DisputeId, out var settled):
                settled.Resolved = e.Resolution;
                settled.ResolvedAt = at;
                break;
            case IncidentCreated e:
                Incidents[e.IncidentId] = new IncidentFacts(e.Type, e.Location, e.Name, e.Address, at) { Priority = e.Priority };
                break;
            case IncidentUpdated e when Incidents.TryGetValue(e.IncidentId, out var facts):
                facts.Priority = e.Priority ?? facts.Priority;
                if (e.Status == IncidentStatus.Closed) facts.Closed = true;
                if (e.Threats is not null) facts.Threats = [.. e.Threats];
                if (e.CasualtiesReported is { } reported) facts.Casualties = Math.Max(facts.Casualties, reported);
                if (e.CasualtiesConfirmed is { } confirmed) facts.Casualties = Math.Max(facts.Casualties, confirmed);
                break;
            case ZoneDeclared e:
                NewZones.Add((e, at));
                if (e.IncidentId is { } zoned && e.Boundary.Count >= 3)
                {
                    if (!IncidentZones.TryGetValue(zoned, out var list)) IncidentZones[zoned] = list = [];
                    list.Add(Wgs84.CreatePolygon(e.Boundary));
                }
                break;
            case ZoneLifted e:
                Exclusions.Remove(e.ZoneId);
                break;
            case EocActivationRequested e:
                EocRequested = e.Level == EocLevel ? null : e.Level;
                EocRequestedAt = at;
                break;
            case EocSeatStaffed e:
                StaffedSeats.Add(e.Function);
                break;
            case EocLevelReached e:
                EocLevel = e.Level;
                EocLevelSince = at;
                if (EocRequested == e.Level) EocRequested = null;
                StaffedSeats.RemoveWhere(s => EocLevels.Seats.FirstOrDefault(x => x.Function == s)?.From > e.Level);
                if (EocLevels.SitrepInterval(e.Level) is null) SitrepDue = null;
                break;
            case SitrepRequested e:
                SitrepDue = e.DueAt;
                break;
            case SitrepSubmitted:
                LastSitrepAt = at;
                break;
            case PublicStatementIssued e:
                Statements.Add((e, at));
                if (e.Conflicting)
                    ConflictingSince ??= at;
                else
                {
                    if (e.Joint) ConflictingSince = null;
                    if (Interagency.IsWarning(e.Text)) LastWarningAt = at;
                    foreach (var (id, enquiry) in Enquiries.ToList())
                        if (e.IncidentId is null || enquiry.Enquiry.IncidentId == e.IncidentId)
                            Enquiries.Remove(id);
                }
                break;
            case MediaEnquiryReceived e:
                Enquiries[e.EnquiryId] = (e, at);
                break;
        }
    }
}

public sealed record WorldJurisdiction(JurisdictionDeclared Info, Geometry Area);

public sealed record PendingInvitation(Guid IncidentId, Guid AgencyId, DateTimeOffset At)
{
    /// <summary>Stable for the incident and agency, so the instructor console can name it (Phase 11 M4).</summary>
    public Guid Id { get; } = Mix(IncidentId, AgencyId);

    private static Guid Mix(Guid a, Guid b)
    {
        var (x, y) = (a.ToByteArray(), b.ToByteArray());
        for (var i = 0; i < 16; i++) x[i] ^= (byte)(y[(i + 5) % 16] + i);
        return new Guid(x);
    }
}

public sealed record PendingAssistance(AgencyAssistanceRequested Request, DateTimeOffset At);

/// <summary>Something an agency knows that command doesn't (yet).</summary>
public sealed class HeldItem(InformationHeld info, DateTimeOffset knownAt)
{
    public InformationHeld Info { get; } = info;
    public DateTimeOffset KnownAt { get; } = knownAt;
    public DateTimeOffset? SharedAt { get; set; }
    public string? Via { get; set; }
}

public sealed class WorldDispute(DisputeRaised raised, DateTimeOffset at)
{
    public DisputeRaised Raised { get; } = raised;
    public DateTimeOffset RaisedAt { get; } = at;
    public DisputeResolution? Chosen { get; set; }
    public DateTimeOffset? ChosenAt { get; set; }

    /// <summary>When a negotiation or EOC decision will be settled.</summary>
    public DateTimeOffset? SettleAt { get; set; }

    public DisputeResolution? Resolved { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public bool Open => Resolved is null;
}

/// <summary>Part of a zone an authority hasn't agreed to; not enforced until <see cref="Until"/> (null: still disputed).</summary>
public sealed class ZoneExclusion(Geometry area, Guid disputeId)
{
    public Geometry Area { get; } = area;
    public Guid DisputeId { get; } = disputeId;
    public DateTimeOffset? Until { get; set; }
}

/// <summary>An incident as the agencies hear of it from command (they have their own sources for the rest).</summary>
public sealed class IncidentFacts(IncidentType type, GeoPoint location, string? name, string? address, DateTimeOffset createdAt)
{
    public IncidentType Type { get; } = type;
    public GeoPoint Location { get; } = location;
    public string? Name { get; } = name;
    public string? Address { get; } = address;

    /// <summary>Where it is, as people would say it: the address, else the incident's name.</summary>
    public string Place => Address ?? Name ?? "the scene";
    public DateTimeOffset CreatedAt { get; } = createdAt;
    public IncidentPriority Priority { get; set; }
    public List<string> Threats { get; set; } = [];
    public int Casualties { get; set; }
    public bool Closed { get; set; }
}
