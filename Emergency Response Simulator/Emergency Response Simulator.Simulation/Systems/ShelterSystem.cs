using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Rest centres (Phase 17: shelter management).
/// <list type="bullet">
/// <item><b>Who needs one</b>: of the people out of an evacuation zone, most go to family or friends. A quarter need a
/// rest centre; more of the elderly (60%) and of those who can't get about (80%), and every rough-sleeper. Those who
/// came by bus to a rest centre go in.</item>
/// <item><b>Getting there</b>: by car or on foot from where they are (at most an hour and a half), to the nearest open
/// rest centre for their incident with room. With none open, or all full, they have nowhere to go: the gardaí and the
/// council report how many every <see cref="ReportInterval"/>, and they go once one opens.</item>
/// <item><b>Capacity</b>: a centre takes up to a third over its capacity before it turns people away; overcrowded, it
/// has more trouble.</item>
/// <item><b>Registration</b>: council staff register <see cref="RegisteredPerHour"/> an hour once the centre is set up,
/// more with registered volunteers. The family assistance centre traces registered evacuees quickly.</item>
/// <item><b>Supplies</b>: each person eats three meals and drinks three litres a day, and needs a bed — drawn from the
/// incident's stock (Phase 15), so the Logistics section must keep it supplied. What runs out is reported.</item>
/// <item><b>Security</b>: in a centre of forty or more, there is trouble — a theft, a fight — every so often, more when it
/// is overcrowded or people go hungry. Security, in place 20 minutes after it is arranged, stops most of it.</item>
/// </list>
/// </summary>
public sealed class ShelterSystem : ISimulationSystem
{
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan TroubleInterval = TimeSpan.FromMinutes(30);
    public const int RegisteredPerHour = 60;
    public const double OverCapacity = 1.3;

    /// <summary>A meal and a litre of water every eight hours.</summary>
    public const double MealsPerHour = 3.0 / 24;
    public const double WaterPerHour = 3.0 / 24;

    private readonly Dictionary<Guid, double> _registration = [];
    private readonly Dictionary<Guid, DateTimeOffset> _reported = [];
    private readonly Dictionary<Guid, string> _lastReport = [];
    private readonly Dictionary<Guid, DateTimeOffset> _trouble = [];
    private readonly Dictionary<Guid, int> _unsheltered = [];
    private DateTimeOffset? _needReported;

    public int Order => 17; // after evacuees have moved (15) and been counted out (16)

    public void Update(SimulationContext context)
    {
        var world = context.World;
        Decide(context);
        Arrive(context);
        foreach (var shelter in world.Protection.Shelters.Values)
        {
            if (!shelter.Ready(context.SimTime)) continue;
            Register(context, shelter);
            Supply(context, shelter);
            Trouble(context, shelter);
        }
        Report(context);
        ReportNeed(context);
    }

    /// <summary>Whether someone out of a zone needs a rest centre: decided once, the same way every run.</summary>
    private static bool Needs(Civilian person)
    {
        var share = person.Is(Vulnerability.Homeless) ? 1.0
            : person.Is(Vulnerability.MobilityImpaired) ? 0.8
            : person.Is(Vulnerability.Elderly) ? 0.6
            : 0.25;
        return SimRandom.For(Guid.Empty, 1700 + person.Id).NextDouble() < share;
    }

    private static void Decide(SimulationContext context)
    {
        var world = context.World;
        foreach (var person in world.Civilians.Where(p => p.EvacuatedFrom is not null && p.State == CivilianState.Safe && p.VehicleId is null
                                                          && p.ShelterArrivalAt is null && p.ReturnAt is null))
        {
            person.NeedsShelter ??= Needs(person);
            if (person.NeedsShelter != true) continue;
            var incident = world.Protection.Evacuations.GetValueOrDefault(person.EvacuatedFrom!.Value)?.IncidentId;

            // Brought by bus to a rest centre, or making for the nearest with room (their own incident's first).
            var shelter = world.Protection.ShelterOf(person) is { } chosen && Room(chosen) ? chosen
                : world.Protection.Shelters.Values.Where(Room)
                    .OrderBy(s => s.Opened.IncidentId == incident ? 0 : 1)
                    .ThenBy(s => GeoMath.DistanceMeters(s.Opened.Location, person.Location))
                    .FirstOrDefault();
            if (shelter is null)
            {
                person.ShelterId = null;
                continue; // nowhere to go yet
            }

            var distance = GeoMath.DistanceMeters(person.Location, shelter.Opened.Location);
            var speed = person.HasCar ? 6.0 : person.Is(Vulnerability.Elderly) ? 0.8 : 1.2;
            person.ShelterId = shelter.Id;
            person.ShelterArrivalAt = context.SimTime + TimeSpan.FromSeconds(Math.Min(distance * 1.3 / speed, TimeSpan.FromMinutes(90).TotalSeconds));
            shelter.Arriving.Add(person.Id);
        }
    }

    private static bool Room(WorldProtection.ShelterState shelter) => shelter.Expected < shelter.Opened.Capacity * OverCapacity;

    private static void Arrive(SimulationContext context)
    {
        var world = context.World;
        foreach (var person in world.Civilians.Where(p => p.ShelterArrivalAt is { } at && context.SimTime >= at && p.ShelterId is not null && p.ReturnAt is null))
        {
            if (world.Protection.ShelterOf(person) is not { } shelter) continue;
            if (!shelter.Arriving.Remove(person.Id)) continue;
            person.Location = shelter.Opened.Location;
            shelter.Occupants.Add(person.Id);
            shelter.Arrivals.Add(person.Id);
        }
    }

    private void Register(SimulationContext context, WorldProtection.ShelterState shelter)
    {
        var waiting = shelter.Occupants.Where(id => !shelter.Registered.Contains(id)).ToList();
        if (waiting.Count == 0)
        {
            _registration[shelter.Id] = 0;
            return;
        }
        var perHour = Math.Min(200, RegisteredPerHour + 3 * context.World.Public.RegisteredVolunteers);
        var budget = _registration.GetValueOrDefault(shelter.Id) + perHour * context.Delta.TotalHours;
        var done = (int)Math.Min(Math.Floor(budget), waiting.Count);
        foreach (var id in waiting.Take(done)) shelter.Registered.Add(id);
        _registration[shelter.Id] = budget - done;
    }

    /// <summary>Meals, water and a bed for each, from the incident's stock (when logistics is being simulated).</summary>
    private static void Supply(SimulationContext context, WorldProtection.ShelterState shelter)
    {
        var logistics = context.World.Logistics;
        if (!logistics.Active || shelter.Opened.IncidentId is not { } incident || shelter.Occupants.Count == 0) return;
        var hours = context.Delta.TotalHours;
        void Draw(SupplyKind kind, double wanted)
        {
            if (wanted <= 0) return;
            if (logistics.Draw(incident, kind, wanted, context.SimTime) < wanted - 1e-9) shelter.Short.Add(kind);
            else shelter.Short.Remove(kind);
        }
        Draw(SupplyKind.Food, shelter.Occupants.Count * MealsPerHour * hours);
        Draw(SupplyKind.Water, shelter.Occupants.Count * WaterPerHour * hours);

        var unbedded = shelter.Occupants.Where(id => !shelter.Bedded.Contains(id)).ToList();
        if (unbedded.Count == 0)
        {
            shelter.Short.Remove(SupplyKind.Bedding);
            return;
        }
        var beds = (int)Math.Floor(logistics.Draw(incident, SupplyKind.Bedding, unbedded.Count, context.SimTime));
        foreach (var id in unbedded.Take(beds)) shelter.Bedded.Add(id);
        if (beds < unbedded.Count) shelter.Short.Add(SupplyKind.Bedding);
        else shelter.Short.Remove(SupplyKind.Bedding);
    }

    private static readonly string[] Troubles =
    [
        "a handbag and phones stolen from the sleeping area",
        "a fight in the queue for food",
        "a resident threatening staff over the wait for news",
        "an intoxicated man refusing to leave the women's area",
        "a row over the last camp beds",
    ];

    /// <summary>Every half hour in a centre of forty or more: a chance of trouble, which security mostly stops.</summary>
    private void Trouble(SimulationContext context, WorldProtection.ShelterState shelter)
    {
        var occupants = shelter.Occupants.Count;
        if (occupants < 40) return;
        if (_trouble.TryGetValue(shelter.Id, out var last) && context.SimTime - last < TroubleInterval) return;
        if (!_trouble.ContainsKey(shelter.Id))
        {
            _trouble[shelter.Id] = context.SimTime; // the first half hour is quiet
            return;
        }
        _trouble[shelter.Id] = context.SimTime;

        var random = SimRandom.For(shelter.Id, (int)(context.SimTime - shelter.OpenedAt).TotalMinutes);
        var chance = occupants / 300.0 * (occupants > shelter.Opened.Capacity ? 2 : 1) * (shelter.Short.Contains(SupplyKind.Food) ? 1.5 : 1);
        if (random.NextDouble() >= Math.Min(0.9, chance)) return;

        var what = Troubles[random.Next(Troubles.Length)];
        var prevented = shelter.Secured(context.SimTime) && random.NextDouble() < 0.85;
        context.EmitTruth(new ShelterIncident(shelter.Id, what, prevented));
        if (prevented) return;
        shelter.Incidents++;
        context.EmitPerceived(new ReportReceived(Guid.NewGuid(), shelter.Opened.IncidentId, ReportSource.Agency, $"{shelter.Opened.Name} manager",
            $"Trouble at the rest centre: {what}. We have no security here.", Confidence.High, VerificationStatus.Confirmed,
            shelter.Opened.Location, 30), EventSources.Comms);
    }

    /// <summary>Each centre's manager reports every 15 minutes, and straight away when it opens or something runs out.</summary>
    private void Report(SimulationContext context)
    {
        foreach (var shelter in context.World.Protection.Shelters.Values)
        {
            var ready = shelter.Ready(context.SimTime);
            var shortList = shelter.Short.Order().ToList();
            var key = $"{ready}|{string.Join(',', shortList)}|{shelter.Secured(context.SimTime)}";
            var due = !_reported.TryGetValue(shelter.Id, out var last) || context.SimTime - last >= ReportInterval;
            if (!due && _lastReport.GetValueOrDefault(shelter.Id) == key) continue;
            _reported[shelter.Id] = context.SimTime;
            _lastReport[shelter.Id] = key;

            var occupants = shelter.Occupants.Count;
            var text = !ready ? $"{shelter.Opened.Name}: still setting up, {occupants} waiting outside."
                : $"{shelter.Opened.Name}: {occupants} here (capacity {shelter.Opened.Capacity}), {shelter.Registered.Count} registered" +
                  (shortList.Count > 0 ? $". We have run out of {string.Join(" and ", shortList.Select(k => Supplies.Name(k).ToLowerInvariant()))}." : ".");
            CommsNet.Chat(context, $"{shelter.Opened.Name} manager", text,
                new ShelterStatusReported(shelter.Id, ready, occupants, shelter.Registered.Count, shelter.Opened.Capacity, shortList,
                    shelter.Secured(context.SimTime), shelter.Incidents), TimeSpan.Zero);
        }
    }

    /// <summary>Evacuees who need a rest centre and have none to go to, by incident, to the nearest five.</summary>
    private void ReportNeed(SimulationContext context)
    {
        if (_needReported is { } last && context.SimTime - last < ReportInterval) return;
        var world = context.World;
        var need = world.Civilians.Where(p => p is { NeedsShelter: true, ShelterArrivalAt: null, State: CivilianState.Safe, EvacuatedFrom: not null, ReturnAt: null })
            .GroupBy(p => world.Protection.Evacuations.GetValueOrDefault(p.EvacuatedFrom!.Value)?.IncidentId)
            .Where(g => g.Key is not null)
            .ToDictionary(g => g.Key!.Value, g => (int)Math.Round(g.Count() / 5.0) * 5);
        foreach (var incident in _unsheltered.Keys.Where(k => !need.ContainsKey(k)).ToList()) need[incident] = 0;
        var changed = need.Where(n => _unsheltered.GetValueOrDefault(n.Key, 0) != n.Value).ToList();
        if (changed.Count == 0) return;
        _needReported = context.SimTime;
        foreach (var (incident, people) in changed)
        {
            _unsheltered[incident] = people;
            context.EmitPerceived(new ShelterNeedReported(incident, people), EventSources.Comms);
        }
    }
}
