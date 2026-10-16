using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Family reunification and victim assistance (Phase 14).
/// <list type="bullet">
/// <item><b>Enquiries</b>: once people have been hurt or moved out, relatives who can't reach them ring the public
/// enquiry line: a daughter about her mother on the street that was evacuated, a husband whose wife isn't answering.
/// Each is about a real person, whose fate is ground truth.</item>
/// <item><b>Tracing</b>: nothing gets traced until command opens a family assistance centre, which registers evacuees,
/// checks with the hospitals and the scene, and calls the family back: safe and out, in hospital, dead, or — the one
/// that matters most — still at home inside the zone. Registered volunteers make it faster.</item>
/// <item><b>Victim assistance</b>: a family whose relative is in hospital or has died is given a family liaison officer
/// by the centre. Without a centre, families wait with nothing, and say so to the media.</item>
/// </list>
/// </summary>
public sealed class ReunificationSystem(IRoutingService? routing = null) : ISimulationSystem
{
    /// <summary>The first relatives ring this long after people are first affected.</summary>
    public static readonly TimeSpan FirstEnquiryAfter = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan EnquiryInterval = TimeSpan.FromMinutes(4);
    public const int MaxEnquiries = 12;

    /// <summary>How long the centre takes to trace someone, before volunteers speed it up.</summary>
    public static readonly TimeSpan TraceTime = TimeSpan.FromMinutes(12);

    private static readonly string[] FirstNames = ["Mary", "Patrick", "Bridget", "Seán", "Margaret", "Thomas", "Kathleen", "Michael", "Áine", "Declan", "Nuala", "Tomasz", "Ana", "Chidi"];
    private static readonly string[] Surnames = ["Byrne", "Murphy", "Kelly", "O'Brien", "Doyle", "Walsh", "Kavanagh", "Nolan", "Farrell", "Kowalski", "Silva", "Okafor"];
    private static readonly string[] Callers = ["Her daughter", "His son", "Her husband", "His wife", "A neighbour", "Her sister", "His brother", "A carer"];

    private readonly Dictionary<Guid, int> _subjects = [];
    private readonly Dictionary<Guid, DateTimeOffset> _traceAt = [];
    private DateTimeOffset? _affectedSince;
    private DateTimeOffset _nextEnquiry;

    public int Order => 17;

    public void Update(SimulationContext context)
    {
        Enquire(context);
        Trace(context);
    }

    private static bool Affected(Civilian person) => person.State != CivilianState.Normal || person.WaitingForHelp;

    private void Enquire(SimulationContext context)
    {
        var world = context.World;
        if (_subjects.Count >= MaxEnquiries) return;
        if (_affectedSince is null)
        {
            if (!world.Civilians.Any(p => p.State is CivilianState.Injured or CivilianState.Evacuating or CivilianState.Safe)) return;
            _affectedSince = context.SimTime;
            _nextEnquiry = context.SimTime + FirstEnquiryAfter;
        }
        if (context.SimTime < _nextEnquiry) return;
        _nextEnquiry = context.SimTime + EnquiryInterval;

        // Someone a relative would worry about: hurt, moved out, or living in a zone.
        var asked = _subjects.Values.ToHashSet();
        var candidates = world.Civilians.Where(p => !asked.Contains(p.Id) && !p.Is(Vulnerability.Homeless) && Affected(p)).ToList();
        if (candidates.Count == 0) return;
        var random = SimRandom.For(Guid.Empty, 80 + _subjects.Count);
        // The hurt and the vulnerable are asked about first: they are the ones whose families can't reach them.
        var person = candidates
            .OrderByDescending(p => p.State == CivilianState.Injured ? 2 : p.Is(Vulnerability.Elderly) ? 1 : 0)
            .ThenBy(_ => random.Next())
            .First();

        var enquiryId = Guid.NewGuid();
        _subjects[enquiryId] = person.Id;
        var name = $"{FirstNames[random.Next(FirstNames.Length)]} {Surnames[random.Next(Surnames.Length)]}";
        if (person.Is(Vulnerability.Elderly)) name += $" ({72 + random.Next(18)})";
        var road = routing?.NearestRoad(person.Home) is { DistanceMeters: < 120 } nearest ? nearest.Name : null;
        var incident = world.Interagency.Incidents.Where(i => !i.Value.Closed)
            .OrderBy(i => GeoMath.DistanceMeters(i.Value.Location, person.Home)).Select(i => (Guid?)i.Key).FirstOrDefault();
        var place = incident is { } id ? world.Interagency.Incidents[id].Place : "the incident";
        var lastKnown = road is not null ? $"at home on {road}" : $"at home near {place}";

        context.EmitPerceived(new MissingPersonReported(enquiryId, incident, Callers[random.Next(Callers.Length)], name, lastKnown), EventSources.Comms);
    }

    private void Trace(SimulationContext context)
    {
        var world = context.World;
        if (world.Public.AssistanceCentre is not { } centre) return;
        var speed = 1 + world.Public.RegisteredVolunteers / 20.0;

        foreach (var (enquiryId, (receivedAt, _)) in world.Public.Enquiries)
        {
            if (world.Public.Traced.ContainsKey(enquiryId) || !_subjects.TryGetValue(enquiryId, out var subject)) continue;
            if (!_traceAt.TryGetValue(enquiryId, out var due))
            {
                var start = receivedAt > centre.OpenedAt ? receivedAt : centre.OpenedAt;
                var jitter = SimRandom.For(enquiryId).NextDouble() * 0.6 + 0.7;
                // Registered at a rest centre (Phase 17): found on the list straight away.
                var registered = world.Civilians.FirstOrDefault(p => p.Id == subject) is { } listed
                                 && world.Protection.ShelterOf(listed) is { } shelter && shelter.Registered.Contains(listed.Id);
                _traceAt[enquiryId] = due = start + TraceTime * jitter / speed / (registered ? 3 : 1);
            }
            if (context.SimTime < due) continue;

            var person = world.Civilians.FirstOrDefault(p => p.Id == subject);
            // A death is confirmed to the family only once the body has been identified (Phase 16).
            if (person is not null && world.Health.FatalitiesActive
                && world.Casualties.Values.FirstOrDefault(c => c.CivilianId == person.Id) is { Alive: false } dead && !world.Health.Identified(dead.Id))
                continue;
            var (outcome, detail) = Outcome(world, person, context.SimTime);
            var supported = outcome is TraceOutcome.InHospital or TraceOutcome.Deceased;
            context.EmitPerceived(new MissingPersonTraced(enquiryId, outcome, detail, supported), EventSources.Comms);

            if (outcome == TraceOutcome.StillInArea && person is not null)
                context.EmitPerceived(new AlertRaised(Guid.NewGuid(), AlertCategory.Safety, AlertSeverity.Warning, "Person believed still in the zone",
                    $"The family assistance centre traced an enquiry: {detail}. Someone needs to go and get them.",
                    world.Public.Enquiries[enquiryId].Enquiry.IncidentId, null), EventSources.Comms);
        }
    }

    /// <summary>What really happened to them, as the centre can establish it.</summary>
    private static (TraceOutcome Outcome, string Detail) Outcome(WorldState world, Civilian? person, DateTimeOffset now)
    {
        if (person is null) return (TraceOutcome.Safe, "not in the area at the time");
        var casualty = world.Casualties.Values.FirstOrDefault(c => c.CivilianId == person.Id);
        if (casualty is not null)
        {
            if (!casualty.Alive)
                return (TraceOutcome.Deceased, world.Health.Identified(casualty.Id)
                    ? "died; formally identified, and the coroner has been informed"
                    : "died at the scene; the coroner has been informed");
            var hospital = casualty.HospitalId is { } h && world.Hospitals.TryGetValue(h, out var where) ? where.Name : null;
            return casualty.State switch
            {
                CasualtyState.AtHospital or CasualtyState.Transporting =>
                    (TraceOutcome.InHospital, hospital is null ? "taken to hospital" : $"in {hospital}"),
                _ => (TraceOutcome.InHospital, "injured, being treated by ambulance crews at the scene"),
            };
        }
        if (world.Protection.ShelterOf(person) is { } shelter && shelter.Registered.Contains(person.Id))
            return (TraceOutcome.Safe, $"evacuated, registered at {shelter.Opened.Name} rest centre");
        if (person.State is CivilianState.Safe or CivilianState.Evacuating || person.VehicleId is not null)
            return (TraceOutcome.Safe, "evacuated and registered as safe");

        var zone = world.ProtectiveActions.Values.FirstOrDefault(a => a.Type == ZoneType.EvacuationZone && a.Area.Contains(person.Location.ToPoint()));
        if (zone is not null)
            return (TraceOutcome.StillInArea, person.WaitingForHelp ? "at home in the evacuation zone, waiting for help to get out"
                : person.Is(Vulnerability.Elderly) ? "at home in the evacuation zone; would not leave"
                : "still at home in the evacuation zone");
        return (TraceOutcome.Safe, person.State == CivilianState.Sheltering ? "sheltering at home, safe" : "at home, safe");
    }
}
