using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.State;

/// <summary>
/// Ground truth about the public side of the response (Phase 14): which rumours are true, what command has done about
/// volunteers, vulnerable people, families and the press, and which community leaders are on side. Kept from the event
/// stream, so a replay rebuilds it; the systems that act on it (<c>CivilianSystem</c>, <c>PublicOrderSystem</c>,
/// <c>ReunificationSystem</c>, <c>RumourSystem</c>) hold only their timers.
/// </summary>
public sealed class WorldPublic
{
    public sealed class RumourState(RumourSpreading first, DateTimeOffset at)
    {
        public Guid Id { get; } = first.RumourId;
        public Guid? IncidentId { get; } = first.IncidentId;
        public string Claim { get; } = first.Claim;
        public string Topic { get; } = first.Topic;
        public DateTimeOffset StartedAt { get; } = at;
        public int ReachThousands { get; set; } = first.ReachThousands;
        public bool? IsTrue { get; set; }
        /// <summary>When it stopped spreading: command rebutted it, or a press conference dealt with it.</summary>
        public DateTimeOffset? RebuttedAt { get; set; }

        /// <summary>Command explicitly said it isn't true. A press conference speaking to it is not a denial.</summary>
        public bool Denied { get; set; }

        public bool DenialExposed { get; set; }
        public bool Spreading => RebuttedAt is null;
    }

    public sealed class VolunteerState(Guid incidentId, GeoPoint at, DateTimeOffset arrived)
    {
        public Guid IncidentId { get; } = incidentId;
        public GeoPoint Location { get; } = at;
        public DateTimeOffset ArrivedAt { get; } = arrived;
        public int Count { get; set; }
        public VolunteerDecision? Decision { get; set; }
        public int SelfDeployed { get; set; }
    }

    public Dictionary<Guid, RumourState> Rumours { get; } = [];
    public Dictionary<Guid, VolunteerState> Volunteers { get; } = [];

    /// <summary>Zones command asked to have the vulnerable got out of, and when.</summary>
    public Dictionary<Guid, DateTimeOffset> AssistedEvacuations { get; } = [];

    /// <summary>Zones whose community leader command has engaged (Phase 14): people there listen.</summary>
    public HashSet<Guid> EngagedZones { get; } = [];

    /// <summary>The family assistance centre, once open.</summary>
    public (Guid Id, GeoPoint Location, DateTimeOffset OpenedAt)? AssistanceCentre { get; set; }

    /// <summary>Volunteers registered: they staff the centre and rest centres.</summary>
    public int RegisteredVolunteers => Volunteers.Values.Where(v => v.Decision == VolunteerDecision.Register).Sum(v => v.Count);

    /// <summary>Break-ins in evacuated streets, and whether gardaí were there to stop them.</summary>
    public List<(Guid ZoneId, DateTimeOffset At, bool Policed)> Looting { get; } = [];

    public Dictionary<Guid, (DateTimeOffset At, MissingPersonReported Enquiry)> Enquiries { get; } = [];
    public Dictionary<Guid, TraceOutcome> Traced { get; } = [];
    public List<DateTimeOffset> PressConferences { get; } = [];

    /// <summary>
    /// A false rumour that has gone a long way and nobody has answered confuses people: some take it over the
    /// official line (Phase 14).
    /// </summary>
    public bool Confused => Rumours.Values.Any(r => r.Spreading && r.IsTrue == false && r.ReachThousands >= 20);

    public void Apply(DomainEvent payload, DateTimeOffset at, Func<Guid, Guid?> zoneOfCall)
    {
        switch (payload)
        {
            case RumourSpreading e:
                if (Rumours.TryGetValue(e.RumourId, out var rumour)) rumour.ReachThousands = e.ReachThousands;
                else Rumours[e.RumourId] = new RumourState(e, at);
                break;
            case RumourTruth e when Rumours.TryGetValue(e.RumourId, out var known):
                known.IsTrue = e.IsTrue;
                break;
            case RumourRebutted e when Rumours.TryGetValue(e.RumourId, out var rebutted):
                rebutted.RebuttedAt ??= at;
                rebutted.Denied = true;
                break;
            case DenialExposed e when Rumours.TryGetValue(e.RumourId, out var exposed):
                exposed.DenialExposed = true;
                break;
            case PressConferenceHeld e:
                PressConferences.Add(at);
                foreach (var addressed in Rumours.Values.Where(r => r.Spreading && e.Statement.Contains(r.Topic, StringComparison.OrdinalIgnoreCase)))
                    addressed.RebuttedAt = at;
                break;

            case VolunteersArrived e:
                if (!Volunteers.TryGetValue(e.IncidentId, out var group)) Volunteers[e.IncidentId] = group = new VolunteerState(e.IncidentId, e.Location, at);
                group.Count += e.Count;
                break;
            case VolunteersManaged e when Volunteers.TryGetValue(e.IncidentId, out var managed):
                managed.Decision = e.Decision;
                break;
            case VolunteersSelfDeployed e when Volunteers.TryGetValue(e.IncidentId, out var loose):
                loose.SelfDeployed += e.Count;
                break;

            case LootingOccurred e:
                Looting.Add((e.ZoneId, at, e.Policed));
                break;
            case AssistedEvacuationRequested e:
                AssistedEvacuations.TryAdd(e.ZoneId, at);
                break;
            case PoliticalCallAnswered e when zoneOfCall(e.CallId) is { } zone:
                EngagedZones.Add(zone);
                break;

            case FamilyAssistanceCentreOpened e:
                AssistanceCentre ??= (e.CentreId, e.Location, at);
                break;
            case MissingPersonReported e:
                Enquiries[e.EnquiryId] = (at, e);
                break;
            case MissingPersonTraced e:
                Traced[e.EnquiryId] = e.Outcome;
                break;
        }
    }
}
