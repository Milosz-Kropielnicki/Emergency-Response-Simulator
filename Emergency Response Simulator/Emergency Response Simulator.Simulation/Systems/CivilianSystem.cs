using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>Tuning for the civilian population (bound from the "Civilians" settings section).</summary>
public sealed class CivilianOptions
{
    public const string SectionName = "Civilians";

    /// <summary>People placed around each real incident when it starts.</summary>
    public int PopulationPerIncident { get; set; } = 300;

    public double PopulationRadiusMeters { get; set; } = 450;
    public int MaxPopulation { get; set; } = 900;

    /// <summary>Chance that someone who notices the hazard rings 999.</summary>
    public double CallProbability { get; set; } = 0.12;

    /// <summary>Calls about the same hazard are at least this far apart (the call-taker is busy).</summary>
    public TimeSpan MinCallInterval { get; set; } = TimeSpan.FromSeconds(45);

    public int MaxCallsPerHazard { get; set; } = 10;

    /// <summary>Someone who decides to call takes between these long to react and get through.</summary>
    public TimeSpan MinCallDelay { get; set; } = TimeSpan.FromSeconds(40);
    public TimeSpan MaxCallDelay { get; set; } = TimeSpan.FromSeconds(150);

    /// <summary>Share of people inside a declared evacuation zone who leave once warned.</summary>
    public double EvacuationCompliance { get; set; } = 0.85;

    /// <summary>Share of people with a car to leave in.</summary>
    public double CarOwnership { get; set; } = 0.35;

    /// <summary>Share of people with little English (Design Document §13).</summary>
    public double LimitedEnglish { get; set; } = 0.08;

    /// <summary>
    /// Compliance of people with little English when the warning reaches them only in English. Command restores it by
    /// notifying a translation or community liaison service (multilingual warnings).
    /// </summary>
    public double LimitedEnglishCompliance { get; set; } = 0.5;

    /// <summary>Extra compliance once command has told the public what to do through the media (Phase 10).</summary>
    public double PublicWarningBoost { get; set; } = 0.08;

    /// <summary>Compliance is multiplied by this while another agency's statement contradicts the official line.</summary>
    public double ConflictingMessageFactor { get; set; } = 0.7;

    // ---- Phase 14: vulnerable people ----

    public double ElderlyShare { get; set; } = 0.14;

    /// <summary>People who can't leave without help: wheelchair users, the bed-bound, people on oxygen.</summary>
    public double MobilityImpairedShare { get; set; } = 0.04;

    public double HomelessShare { get; set; } = 0.015;
    public double PetOwnerShare { get; set; } = 0.25;

    /// <summary>Compliance is multiplied by this for elderly people, many of whom won't leave home on an official's say-so.</summary>
    public double ElderlyComplianceFactor { get; set; } = 0.7;

    /// <summary>And by this for pet owners, unless somewhere takes animals (command has notified an animal welfare service).</summary>
    public double PetOwnerComplianceFactor { get; set; } = 0.6;

    /// <summary>Share of evacuated pet owners who go back in for the animal when nobody has arranged for pets.</summary>
    public double PetReturnShare { get; set; } = 0.3;

    /// <summary>Added to compliance where command has engaged the area's community leader.</summary>
    public double CommunityEngagementBoost { get; set; } = 0.15;

    /// <summary>Compliance is multiplied by this while a false rumour runs unanswered (Phase 14).</summary>
    public double RumourConfusionFactor { get; set; } = 0.85;

    /// <summary>How long an assisted evacuation takes to get teams and vehicles to a zone.</summary>
    public TimeSpan AssistanceLeadTime { get; set; } = TimeSpan.FromMinutes(12);

    /// <summary>People the assisted-evacuation teams get out per minute, per zone.</summary>
    public double AssistedPerMinute { get; set; } = 2;
}

/// <summary>
/// The people around an incident (Design Document §10.3): each is an agent who may notice the hazard (sight, smell,
/// water at the door), then evacuates, comes to look (convergence), shelters indoors, or panics and runs. People in
/// the fire, the toxic cloud or deep water are hurt, and become casualties in the world. Some of those who notice
/// ring 999, describing what they see, with a vague location. When command declares an evacuation or
/// shelter-in-place zone, the people inside are warned over the next few minutes and most of them comply;
/// evacuees with cars drive out and add to the traffic.
/// </summary>
public sealed class CivilianSystem(IHazardTerrain? terrain = null, CivilianOptions? options = null, IRoutingService? routing = null)
    : ISimulationSystem
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);
    private const double WalkMps = 1.4;
    private const double RunMps = 3.0;

    private readonly IHazardTerrain _terrain = terrain ?? new UniformTerrain();
    private readonly CivilianOptions _options = options ?? new CivilianOptions();
    private readonly HashSet<Guid> _populated = [];
    private readonly Dictionary<Guid, (DateTimeOffset At, int Count)> _calls = [];
    private Random? _random;
    private TimeSpan _pending;
    private int _nextId;

    public int Order => 15; // after hazards move, before medical care and traffic

    public void Update(SimulationContext context)
    {
        Populate(context);
        if (context.World.Civilians.Count == 0) return;

        _pending += context.Delta;
        while (_pending >= Step)
        {
            _pending -= Step;
            Advance(context, Step.TotalSeconds);
        }
    }

    // ---- Population ----

    private void Populate(SimulationContext context)
    {
        var world = context.World;
        foreach (var incident in world.Incidents.Values)
        {
            if (!_populated.Add(incident.Id)) continue;
            _random ??= SimRandom.For(incident.Id, 3);

            // Somewhere already populated (a second incident next door) needs no more people.
            if (world.Civilians.Any(c => GeoMath.DistanceMeters(c.Location, incident.Location) < _options.PopulationRadiusMeters / 2))
                continue;

            var count = Math.Min(_options.PopulationPerIncident, _options.MaxPopulation - world.Civilians.Count);
            for (var i = 0; i < count; i++)
            {
                // Phase 14: who needs more than a warning. Rough-sleepers are outdoors and have no car.
                var vulnerability = Vulnerability.None;
                var roll = _random.NextDouble();
                if (roll < _options.HomelessShare) vulnerability |= Vulnerability.Homeless;
                else if (roll < _options.HomelessShare + _options.MobilityImpairedShare) vulnerability |= Vulnerability.MobilityImpaired | Vulnerability.Elderly;
                else if (roll < _options.HomelessShare + _options.MobilityImpairedShare + _options.ElderlyShare) vulnerability |= Vulnerability.Elderly;
                if (_random.NextDouble() < _options.PetOwnerShare) vulnerability |= Vulnerability.PetOwner;
                var homeless = (vulnerability & Vulnerability.Homeless) != 0;

                var home = homeless ? PlaceOutdoors(incident.Location) : PlacePerson(incident.Location);
                world.Civilians.Add(new Civilian
                {
                    Id = _nextId++,
                    Location = home,
                    Home = home,
                    Vulnerability = vulnerability,
                    HasCar = !homeless && (vulnerability & Vulnerability.MobilityImpaired) == 0 && _random.NextDouble() < _options.CarOwnership,
                    Language = _random.NextDouble() < _options.LimitedEnglish
                        ? CallerLanguages.Other[_random.Next(CallerLanguages.Other.Count)]
                        : null,
                });
            }
        }
    }

    /// <summary>Somewhere outside: a doorway, a park, under a bridge (Phase 14: rough-sleepers).</summary>
    private GeoPoint PlaceOutdoors(GeoPoint centre)
    {
        GeoPoint point = centre;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            point = GeoMath.Destination(centre, _random!.NextDouble() * 360, Math.Sqrt(_random.NextDouble()) * _options.PopulationRadiusMeters);
            if (!_terrain.IsBuilding(point)) break;
        }
        return point;
    }

    /// <summary>A random spot in the area, preferring buildings (most people are indoors).</summary>
    private GeoPoint PlacePerson(GeoPoint centre)
    {
        GeoPoint point = centre;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            // Uniform over the disc: radius ∝ √u.
            point = GeoMath.Destination(centre, _random!.NextDouble() * 360,
                Math.Sqrt(_random.NextDouble()) * _options.PopulationRadiusMeters);
            if (_terrain.IsBuilding(point) || _terrain.Fuel(point) >= 0.9) break;
        }
        return point;
    }

    // ---- Behaviour ----

    private void Advance(SimulationContext context, double seconds)
    {
        var world = context.World;
        var random = _random!;
        WarnZones(context);

        foreach (var person in world.Civilians)
        {
            if (person.State is CivilianState.Injured or CivilianState.Safe) continue;
            if (person.VehicleId is { } carId)
            {
                FollowCar(world, person, carId);
                continue;
            }

            var exposures = HazardSystem.ExposuresAt(world, person.Location).ToList();
            if (Hurt(context, person, exposures, seconds)) continue;

            if (person.CallAt is { } callAt && context.SimTime >= callAt)
            {
                person.CallAt = null;
                Call(context, person, exposures);
            }

            var worst = exposures.Count == 0 ? HazardLevel.None : exposures.Max(e => e.Exposure.Level);
            var order = ZoneAt(world, person.Location, context.SimTime);

            if (person.State == CivilianState.Normal)
            {
                var warned = order is not null && person.WarnedAt is { } warnedAt && context.SimTime >= warnedAt;
                var seesSmoke = SeesDistantFire(world, person, seconds, random);
                if (worst >= HazardLevel.Low || warned || seesSmoke)
                {
                    person.State = CivilianState.Aware;
                    person.AwareSince = context.SimTime;
                    if (random.NextDouble() < _options.CallProbability)
                        person.CallAt = context.SimTime + _options.MinCallDelay
                                        + (_options.MaxCallDelay - _options.MinCallDelay) * random.NextDouble();
                    Decide(context, person, exposures, order);
                }
            }
            else if (person.State == CivilianState.Aware && order is { } zone && person.WarnedAt is { } at && context.SimTime >= at
                     && person.IgnoredOrder != zone.ZoneId && !person.WaitingForHelp)
            {
                // Already aware, now told to leave or shelter by the authorities.
                Decide(context, person, exposures, zone);
            }

            // Real danger overrides whatever they had decided: run. Someone who can't walk can't run either (Phase 14).
            if (!person.Is(Vulnerability.MobilityImpaired)
                && (worst >= HazardLevel.Moderate && person.State is not (CivilianState.Evacuating or CivilianState.Sheltering)
                    || worst == HazardLevel.High && person.State == CivilianState.Sheltering && exposures.Any(e => e.Hazard.Kind == HazardKind.Fire)))
            {
                person.Panicking = true;
                StartEvacuating(context, person, exposures, order, allowCar: false);
            }

            Move(world, person, seconds);
        }

        // Phase 14: the people who need more than a warning.
        AssistEvacuations(context, seconds);
        ReturnForPets(context);
        Stranded(context);

        // Cars that have arrived let their occupants out.
        foreach (var car in world.Vehicles.Values.Where(v => v.Arrived).ToList())
        {
            foreach (var occupant in car.Occupants)
            {
                if (world.Civilians.FirstOrDefault(c => c.Id == occupant) is { } person)
                {
                    person.Location = car.Destination;
                    person.VehicleId = null;
                    person.State = CivilianState.Safe;
                }
            }
            world.Vehicles.Remove(car.Id);
        }
    }

    /// <summary>Someone who can see a big fire's smoke from a few hundred metres away may notice it.</summary>
    private static bool SeesDistantFire(WorldState world, Civilian person, double seconds, Random random)
    {
        foreach (var hazard in world.Hazards.Values)
        {
            if (hazard.Ended || hazard.Model is not FireSpreadModel fire || !fire.IsActive) continue;
            var distance = fire.DistanceToFire(person.Location);
            if (distance > 400) continue;
            // Roughly a 2-minute notice time nearby, rising with distance.
            if (random.NextDouble() < seconds / (120 + distance)) return true;
        }
        return false;
    }

    /// <summary>What a person does once they know something is wrong.</summary>
    private void Decide(SimulationContext context, Civilian person, List<(WorldHazard Hazard, HazardExposure Exposure)> exposures,
        ProtectiveAction? order)
    {
        var random = _random!;
        if (order is not null && person.WarnedAt is { } warned && context.SimTime >= warned)
        {
            // A warning in English only reaches people with little English poorly, unless command has arranged
            // multilingual warnings through a translation or community liaison service.
            var compliance = person.Language is not null && !MultilingualWarnings(context.World)
                ? _options.LimitedEnglishCompliance
                : _options.EvacuationCompliance;
            // Phase 10: a clear public warning from the authorities persuades more people; another agency saying the
            // opposite (no joint information centre) persuades fewer.
            var publicInfo = context.World.Interagency;
            if (publicInfo.LastWarningAt is { } said && said >= order.DeclaredAt - TimeSpan.FromMinutes(15))
                compliance = Math.Min(0.97, compliance + _options.PublicWarningBoost);
            if (publicInfo.ConflictingSince is not null)
                compliance *= _options.ConflictingMessageFactor;

            // Phase 14: who they are, who told them, and what else they have heard.
            var world = context.World;
            if (person.Is(Vulnerability.Elderly)) compliance *= _options.ElderlyComplianceFactor;
            var petsCatered = AnimalWelfareArranged(world);
            if (person.Is(Vulnerability.PetOwner) && !petsCatered) compliance *= _options.PetOwnerComplianceFactor;
            if (world.Public.EngagedZones.Contains(order.ZoneId)) compliance = Math.Min(0.97, compliance + _options.CommunityEngagementBoost);
            if (world.Public.Confused) compliance *= _options.RumourConfusionFactor;

            if (random.NextDouble() >= compliance)
            {
                person.IgnoredOrder = order.ZoneId; // "it's not that bad", stays put
                return;
            }
            if (order.Type == ZoneType.ShelterInPlace)
            {
                person.State = CivilianState.Sheltering;
                person.Target = null;
                return;
            }
            // Willing, but unable: they wait for someone to come and get them.
            if (person.Is(Vulnerability.MobilityImpaired))
            {
                person.WaitingForHelp = true;
                return;
            }
            if (person.Is(Vulnerability.PetOwner) && !petsCatered && random.NextDouble() < _options.PetReturnShare)
                person.ReturnForPetAt = context.SimTime + TimeSpan.FromMinutes(15 + random.NextDouble() * 15);
            StartEvacuating(context, person, exposures, order, allowCar: true);
            return;
        }

        // Self-directed: most leave, some come to look, some go inside and shut the windows.
        var roll = random.NextDouble();
        var smell = exposures.Any(e => e.Hazard.Kind == HazardKind.Plume);
        if (roll < 0.6)
            StartEvacuating(context, person, exposures, null, allowCar: true);
        else if (roll < 0.75 && !smell)
        {
            person.State = CivilianState.Converging;
            person.Target = NearestHazard(context.World, person.Location)?.Origin;
        }
        else if (smell || roll < 0.9)
        {
            person.State = CivilianState.Sheltering;
            person.Target = null;
        }
        // Otherwise: aware, staying put to see what happens.
    }

    private void StartEvacuating(SimulationContext context, Civilian person, List<(WorldHazard Hazard, HazardExposure Exposure)> exposures,
        ProtectiveAction? order, bool allowCar)
    {
        person.State = CivilianState.Evacuating;
        person.Target = EscapePoint(context.World, person.Location, exposures, order);

        if (allowCar && person.HasCar && !person.Panicking)
        {
            var car = new WorldVehicle { Id = Guid.NewGuid(), Location = person.Location, Destination = person.Target.Value, Occupants = [person.Id] };
            context.World.Vehicles[car.Id] = car;
            person.VehicleId = car.Id;
        }
    }

    /// <summary>Away from the danger: out of the declared zone, or 450 m directly away from the hazard.</summary>
    private static GeoPoint EscapePoint(WorldState world, GeoPoint from, List<(WorldHazard Hazard, HazardExposure Exposure)> exposures,
        ProtectiveAction? order)
    {
        // Phase 17: told which way to go, they go that way.
        if (order is not null && world.Protection.Evacuations.TryGetValue(order.ZoneId, out var evacuation) && evacuation.Destination is { } destination)
            return destination;
        if (order is not null)
        {
            var centre = GeoPoint.FromPoint(order.Area.Centroid);
            var radius = order.Area.Coordinates.Max(c => GeoMath.DistanceMeters(centre, new GeoPoint(c.Y, c.X)));
            var bearing = GeoMath.DistanceMeters(centre, from) < 1 ? 0 : GeoMath.BearingDegrees(centre, from);
            return GeoMath.Destination(centre, bearing, radius + 150);
        }

        var source = exposures.Count > 0 ? exposures.MaxBy(e => e.Exposure.Level).Hazard : NearestHazard(world, from);
        if (source is null) return GeoMath.Destination(from, 0, 450);

        // Out of a plume, go across the wind, not downwind with it.
        var away = GeoMath.DistanceMeters(source.Origin, from) < 1 ? 0 : GeoMath.BearingDegrees(source.Origin, from);
        if (source.Kind == HazardKind.Plume)
        {
            var downwind = world.Weather.WindFromDegrees + 180;
            var side = ((away - downwind + 540) % 360) - 180 >= 0 ? 90 : -90;
            away = downwind + side;
        }
        return GeoMath.Destination(from, away, 450);
    }

    private static WorldHazard? NearestHazard(WorldState world, GeoPoint point) =>
        world.Hazards.Values.Where(h => !h.Ended).MinBy(h => GeoMath.DistanceMeters(h.Origin, point));

    private static void Move(WorldState world, Civilian person, double seconds)
    {
        if (person.Target is not { } target) return;

        var distance = GeoMath.DistanceMeters(person.Location, target);
        // Onlookers stop to watch; a pet owner going back in walks all the way home (Phase 14).
        if (person.State == CivilianState.Converging && person.ReturnForPetAt != DateTimeOffset.MaxValue)
        {
            // Onlookers stop once they have a view: about 60 m from the fire, or 80 m from anything else.
            var stopAt = world.Hazards.Values.Where(h => !h.Ended && h.Model is FireSpreadModel)
                .Select(h => ((FireSpreadModel)h.Model!).DistanceToFire(person.Location))
                .DefaultIfEmpty(double.PositiveInfinity).Min();
            if (stopAt <= 60 || distance <= 80) return;
        }

        var speed = person.Panicking ? RunMps : person.State == CivilianState.Converging ? 1.2 : WalkMps;
        if (person.Is(Vulnerability.Elderly)) speed = Math.Min(speed, 0.8); // Phase 14: slower on foot, and can't run
        var step = speed * seconds;
        if (step >= distance)
        {
            person.Location = target;
            person.Target = null;
            if (person.State == CivilianState.Evacuating)
            {
                person.State = CivilianState.Safe;
                person.Panicking = false;
            }
            return;
        }
        person.Location = GeoMath.Destination(person.Location, GeoMath.BearingDegrees(person.Location, target), step);
    }

    private static void FollowCar(WorldState world, Civilian person, Guid carId)
    {
        if (world.Vehicles.TryGetValue(carId, out var car))
            person.Location = car.Location;
        else
        {
            person.VehicleId = null; // the car is gone (shouldn't happen): carry on on foot
        }
    }

    // ---- Evacuation and shelter orders ----

    /// <summary>
    /// People inside a newly declared zone hear about it over the next 2–10 minutes (door-knocking, alerts). A public
    /// warning through the media (Phase 10: the PIO) reaches them within 1–3 minutes of going out. Where an authority
    /// disputes the zone, nobody there is warned until the dispute lets the measure apply.
    /// </summary>
    private void WarnZones(SimulationContext context)
    {
        var world = context.World;
        var broadcast = world.Interagency.LastWarningAt;
        foreach (var action in world.ProtectiveActions.Values)
        {
            foreach (var person in world.Civilians)
            {
                if (person.State is CivilianState.Injured or CivilianState.Safe) continue;
                if (person.WarnedAt is { } due && (due <= context.SimTime || broadcast is null)) continue;
                var point = person.Location.ToPoint();
                if (!action.Area.Contains(point) || !world.Interagency.Enforced(action.ZoneId, point, context.SimTime)) continue;

                // Phase 14: there's no door to knock for a rough-sleeper, and no radio. Only people who know where they
                // sleep reach them: the assisted-evacuation outreach teams, or a community leader command has engaged.
                if (person.Is(Vulnerability.Homeless))
                {
                    DateTimeOffset? reached = world.Public.AssistedEvacuations.TryGetValue(action.ZoneId, out var asked) ? asked + _options.AssistanceLeadTime
                        : world.Public.EngagedZones.Contains(action.ZoneId) ? context.SimTime + TimeSpan.FromMinutes(5) : null;
                    if (reached is { } outreach) person.WarnedAt = person.WarnedAt is { } earlier && earlier < outreach ? earlier : outreach;
                    continue;
                }
                var start = action.DeclaredAt > context.SimTime - Step ? action.DeclaredAt : context.SimTime;
                var warned = person.WarnedAt ?? start + TimeSpan.FromMinutes(2 + _random!.NextDouble() * 8);
                if (broadcast is { } said && said >= action.DeclaredAt - TimeSpan.FromMinutes(15))
                {
                    // Fixed per person, so re-checking every tick doesn't keep drawing earlier times.
                    var heard = (said > start ? said : start) + TimeSpan.FromMinutes(1 + person.Id % 13 / 6.0);
                    if (heard < warned) warned = heard;
                }
                person.WarnedAt = warned;
            }
        }
    }

    // ---- Phase 14: assisted evacuation, pets, and the people left behind ----

    private readonly Dictionary<Guid, double> _assistBudget = [];
    private readonly Dictionary<Guid, int> _helped = [];
    private readonly Dictionary<Guid, DateTimeOffset> _progressReported = [];
    private readonly Dictionary<Guid, DateTimeOffset> _strandedReported = [];
    private readonly Dictionary<Guid, DateTimeOffset> _returnsReported = [];

    /// <summary>Someone the assisted-evacuation teams go for: can't leave alone, wasn't reached, or wouldn't go for an official.</summary>
    private static bool NeedsAssistance(Civilian person, Guid zoneId) =>
        person.State is not (CivilianState.Injured or CivilianState.Safe or CivilianState.Evacuating) && person.VehicleId is null
        && (person.WaitingForHelp
            || person.Is(Vulnerability.MobilityImpaired)
            || person.Is(Vulnerability.Homeless)
            || person.Is(Vulnerability.Elderly) && person.IgnoredOrder == zoneId);

    /// <summary>
    /// Once command has asked for it and the teams have arrived, the people who can't get out of an evacuation zone on
    /// their own are taken out by vehicle, a couple a minute. Their progress is reported back every few minutes.
    /// </summary>
    private void AssistEvacuations(SimulationContext context, double seconds)
    {
        var world = context.World;
        foreach (var (zoneId, requestedAt) in world.Public.AssistedEvacuations)
        {
            if (!world.ProtectiveActions.TryGetValue(zoneId, out var zone) || zone.Type != ZoneType.EvacuationZone) continue;
            if (context.SimTime < requestedAt + _options.AssistanceLeadTime) continue;

            var budget = _assistBudget.GetValueOrDefault(zoneId) + _options.AssistedPerMinute * seconds / 60;
            var inside = world.Civilians.Where(p => zone.Area.Contains(p.Location.ToPoint()) && NeedsAssistance(p, zoneId)).ToList();
            var batch = inside.Take((int)budget).ToList();
            _assistBudget[zoneId] = budget - batch.Count;
            if (batch.Count > 0)
            {
                // A minibus or ambulance takes them out together.
                var target = EscapePoint(world, batch[0].Location, [], zone);
                var vehicle = new WorldVehicle { Id = Guid.NewGuid(), Location = batch[0].Location, Destination = target, Occupants = [.. batch.Select(p => p.Id)] };
                world.Vehicles[vehicle.Id] = vehicle;
                foreach (var person in batch)
                {
                    person.State = CivilianState.Evacuating;
                    person.WaitingForHelp = false;
                    person.Panicking = false;
                    person.Target = target;
                    person.VehicleId = vehicle.Id;
                }
                _helped[zoneId] = _helped.GetValueOrDefault(zoneId) + batch.Count;
            }

            if (_progressReported.GetValueOrDefault(zoneId) is var last && last != default && context.SimTime - last < TimeSpan.FromMinutes(5)) continue;
            if (_helped.GetValueOrDefault(zoneId) == 0 && inside.Count == 0) continue;
            _progressReported[zoneId] = context.SimTime;
            context.EmitPerceived(new AssistedEvacuationProgress(zoneId, _helped.GetValueOrDefault(zoneId), inside.Count - batch.Count), EventSources.Comms);
        }
    }

    /// <summary>
    /// Evacuated pet owners who couldn't take the animal go back for it, if nobody has arranged somewhere for pets. A
    /// garda cordon turns them back; otherwise they walk back into the zone, and the gardaí at the edge report it.
    /// </summary>
    private void ReturnForPets(SimulationContext context)
    {
        var world = context.World;
        var returned = new Dictionary<Guid, int>();
        foreach (var person in world.Civilians.Where(p => p.ReturnForPetAt is { } due && context.SimTime >= due))
        {
            person.ReturnForPetAt = DateTimeOffset.MaxValue; // once
            if (person.State is CivilianState.Injured || person.VehicleId is not null) continue;
            if (ZoneAt(world, person.Home, context.SimTime) is not { Type: ZoneType.EvacuationZone } zone) continue;

            var home = person.Home.ToPoint();
            var cordoned = world.PoliceCordons.Values.Any(c => c.Contains(home));
            if (!cordoned)
            {
                person.State = CivilianState.Converging; // walking back in, towards home
                person.Target = person.Home;
                person.Panicking = false;
            }
            returned[zone.ZoneId] = returned.GetValueOrDefault(zone.ZoneId) + 1;
            if (cordoned) continue;
        }

        foreach (var (zoneId, count) in returned)
        {
            context.EmitTruth(new PetOwnersReturned(zoneId, count));
            if (_returnsReported.GetValueOrDefault(zoneId) is var last && last != default && context.SimTime - last < TimeSpan.FromMinutes(10)) continue;
            _returnsReported[zoneId] = context.SimTime;
            var at = world.ProtectiveActions.TryGetValue(zoneId, out var area) ? GeoPoint.FromPoint(area.Area.Centroid) : (GeoPoint?)null;
            context.EmitPerceived(new ReportReceived(Guid.NewGuid(), null, ReportSource.FieldUnit, "Gardaí at the evacuation cordon",
                "Residents are trying to get back into the evacuation zone for their pets. Some have got past us.",
                Confidence.High, VerificationStatus.Reported, at, 300), EventSources.Comms);
        }

        // Back home with the animal: out again, on foot.
        foreach (var person in world.Civilians.Where(p => p.State == CivilianState.Converging && p.ReturnForPetAt == DateTimeOffset.MaxValue
                                                          && p.Target is null && GeoMath.DistanceMeters(p.Location, p.Home) < 5))
            StartEvacuating(context, person, [], ZoneAt(world, person.Location, context.SimTime), allowCar: false);
    }

    /// <summary>
    /// People who can't leave on their own still inside an evacuation zone half an hour after the order: ground truth
    /// for the review (Phase 14: negligence), recorded once, and again every half hour while it lasts.
    /// </summary>
    private void Stranded(SimulationContext context)
    {
        var world = context.World;
        foreach (var zone in world.ProtectiveActions.Values.Where(a => a.Type == ZoneType.EvacuationZone))
        {
            if (context.SimTime - zone.DeclaredAt < TimeSpan.FromMinutes(30)) continue;
            if (_strandedReported.GetValueOrDefault(zone.ZoneId) is var last && last != default && context.SimTime - last < TimeSpan.FromMinutes(30)) continue;

            var left = world.Civilians.Where(p => zone.Area.Contains(p.Location.ToPoint())
                                                  && p.State is not (CivilianState.Injured or CivilianState.Safe or CivilianState.Evacuating)
                                                  && (p.WaitingForHelp || p.Is(Vulnerability.MobilityImpaired) || p.Is(Vulnerability.Homeless))).ToList();
            if (left.Count == 0) continue;
            _strandedReported[zone.ZoneId] = context.SimTime;
            var who = string.Join(", ", new[]
            {
                (left.Count(p => p.Is(Vulnerability.MobilityImpaired)), "who can't leave unaided"),
                (left.Count(p => p.Is(Vulnerability.Homeless)), "rough-sleeping"),
            }.Where(g => g.Item1 > 0).Select(g => $"{g.Item1} {g.Item2}"));
            context.EmitTruth(new VulnerableStranded(zone.ZoneId, left.Count, who));
        }
    }

    /// <summary>Somewhere takes pets (Phase 14): command has notified an animal welfare or veterinary service.</summary>
    public static bool AnimalWelfareArranged(WorldState world) =>
        world.Notified.Keys.Any(k => new[] { "animal", "dspca", "ispca", "veterinar", "pets" }.Any(w => k.Contains(w, StringComparison.OrdinalIgnoreCase)));

    private static bool MultilingualWarnings(WorldState world) =>
        world.Notified.Keys.Any(k => k.Contains("translat", StringComparison.OrdinalIgnoreCase)
                                     || k.Contains("interpret", StringComparison.OrdinalIgnoreCase)
                                     || k.Contains("community", StringComparison.OrdinalIgnoreCase));

    private static ProtectiveAction? ZoneAt(WorldState world, GeoPoint point, DateTimeOffset now)
    {
        if (world.ProtectiveActions.Count == 0) return null;
        var p = point.ToPoint();
        return world.ProtectiveActions.Values.FirstOrDefault(a => a.Area.Contains(p) && world.Interagency.Enforced(a.ZoneId, p, now));
    }

    // ---- Harm ----

    /// <summary>Chance per second of being hurt at each level, by hazard.</summary>
    private static double InjuryRate(HazardKind kind, HazardLevel level) => (kind, level) switch
    {
        (HazardKind.Fire, HazardLevel.High) => 0.02,
        (HazardKind.Fire, HazardLevel.Moderate) => 0.002,
        (HazardKind.Plume, HazardLevel.High) => 0.01,
        (HazardKind.Plume, HazardLevel.Moderate) => 0.0015,
        (HazardKind.Plume, HazardLevel.Low) => 0.0002,
        (HazardKind.Flood, HazardLevel.High) => 0.003,
        (HazardKind.Flood, HazardLevel.Moderate) => 0.0005,
        _ => 0,
    };

    private bool Hurt(SimulationContext context, Civilian person, List<(WorldHazard Hazard, HazardExposure Exposure)> exposures, double seconds)
    {
        var random = _random!;
        foreach (var (hazard, exposure) in exposures)
        {
            var rate = InjuryRate(hazard.Kind, exposure.Level);
            if (hazard.Kind == HazardKind.Plume)
            {
                person.Dose += exposure.Value * seconds / 60;
                // Indoors, windows shut; a rough-sleeper has nowhere to shelter (Phase 14).
                if (person.State == CivilianState.Sheltering && !person.Is(Vulnerability.Homeless)) rate *= 0.15;
            }
            if (rate <= 0 || random.NextDouble() >= 1 - Math.Exp(-rate * seconds)) continue;

            var roll = random.NextDouble();
            var triage = exposure.Level switch
            {
                HazardLevel.High => roll < 0.45 ? Triage.Immediate : roll < 0.85 ? Triage.Urgent : Triage.Delayed,
                HazardLevel.Moderate => roll < 0.1 ? Triage.Immediate : roll < 0.45 ? Triage.Urgent : Triage.Delayed,
                _ => roll < 0.2 ? Triage.Urgent : Triage.Delayed,
            };
            var cause = hazard.Kind switch
            {
                HazardKind.Fire => exposure.Level == HazardLevel.High ? "burns: caught in the fire" : "burns and smoke inhalation near the fire",
                HazardKind.Plume => $"{hazard.Substance ?? "toxic gas"} inhalation",
                _ => "injured in floodwater",
            };

            person.State = CivilianState.Injured;
            person.Target = null;
            var incidentId = hazard.IncidentId ?? NearestIncident(context.World, person.Location);
            var injured = new CasualtyInjured(Guid.NewGuid(), incidentId, person.Location, triage, cause, person.Who);
            context.World.Casualties.TryAdd(injured.CasualtyId, new WorldCasualty
            {
                Id = injured.CasualtyId, IncidentId = incidentId, Location = person.Location, Triage = triage,
                State = CasualtyState.AwaitingTreatment, Cause = cause, InjuredAt = context.SimTime, CivilianId = person.Id,
            });
            context.EmitTruth(injured);
            return true;
        }
        return false;
    }

    private static Guid? NearestIncident(WorldState world, GeoPoint point) =>
        world.Incidents.Values.MinBy(i => GeoMath.DistanceMeters(i.Location, point))?.Id;

    // ---- 999 calls ----

    /// <summary>
    /// Someone rings 999 about what they can see. Calls about one hazard are spaced out and capped (the lines are
    /// busy), and the caller's idea of where it is can be a couple of hundred metres out.
    /// </summary>
    private void Call(SimulationContext context, Civilian person, List<(WorldHazard Hazard, HazardExposure Exposure)> exposures)
    {
        var random = _random!;
        if (person.HasCalled) return;

        var hazard = exposures.Count > 0 ? exposures.MaxBy(e => e.Exposure.Level).Hazard : NearestHazard(context.World, person.Location);
        if (hazard is null) return;

        var previous = _calls.GetValueOrDefault(hazard.Id);
        if (previous.Count >= _options.MaxCallsPerHazard || context.SimTime - previous.At < _options.MinCallInterval) return;
        _calls[hazard.Id] = (context.SimTime, previous.Count + 1);
        person.HasCalled = true;

        var road = routing?.NearestRoad(person.Location) is { } nearest && nearest.DistanceMeters < 80 ? $" on {nearest.Name}" : "";
        var hurt = context.World.Casualties.Values.Any(c => c.Alive && GeoMath.DistanceMeters(c.Location, person.Location) < 60);
        var what = hazard.Kind switch
        {
            HazardKind.Fire => exposures.Any(e => e.Hazard.Kind == HazardKind.Fire && e.Exposure.Level >= HazardLevel.Moderate)
                ? "There's a building on fire right beside me" : "Big fire and thick black smoke",
            HazardKind.Plume => "Strong smell like bleach, my eyes and throat are burning",
            _ => "Water rising fast in the street, cars are stuck",
        };
        var summary = what + road + (hurt ? ". Someone here is hurt" : "");

        // A caller's location: a phone fix or a guess, 30–250 m out.
        var error = 30 + random.NextDouble() * 220;
        var reported = GeoMath.Destination(person.Location, random.NextDouble() * 360, error);
        // Phase 17: the phone's own location comes from GPS. Jammed, the network's cell estimate is all there is; spoofed, it is
        // confidently wrong.
        switch (context.World.Protection.GnssAt(person.Location))
        {
            case { Kind: GnssInterferenceKind.Jamming }:
                error += 400;
                reported = GeoMath.Destination(person.Location, random.NextDouble() * 360, error);
                break;
            case { Kind: GnssInterferenceKind.Spoofing } spoofed:
                reported = GeoMath.Destination(reported, spoofed.BearingDegrees, spoofed.OffsetMeters);
                break;
        }
        CommsNet.EmergencyCall(context, new PendingCall
        {
            Caller = "999 caller (mobile)", Summary = summary, Location = reported, AccuracyMeters = Math.Round(error * 1.3 / 10) * 10,
            Language = person.Language, Mobile = true,
            // In real danger people hold on longer.
            Patience = TimeSpan.FromSeconds(45 + random.NextDouble() * 105),
        });
    }
}
