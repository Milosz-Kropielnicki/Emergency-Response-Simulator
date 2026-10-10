using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
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
                world.Civilians.Add(new Civilian
                {
                    Id = _nextId++,
                    Location = PlacePerson(incident.Location),
                    HasCar = _random.NextDouble() < _options.CarOwnership,
                });
        }
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
            var order = ZoneAt(world, person.Location);

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
                     && person.IgnoredOrder != zone.ZoneId)
            {
                // Already aware, now told to leave or shelter by the authorities.
                Decide(context, person, exposures, zone);
            }

            // Real danger overrides whatever they had decided: run.
            if (worst >= HazardLevel.Moderate && person.State is not (CivilianState.Evacuating or CivilianState.Sheltering)
                || worst == HazardLevel.High && person.State == CivilianState.Sheltering && exposures.Any(e => e.Hazard.Kind == HazardKind.Fire))
            {
                person.Panicking = true;
                StartEvacuating(context, person, exposures, order, allowCar: false);
            }

            Move(world, person, seconds);
        }

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
            if (random.NextDouble() >= _options.EvacuationCompliance)
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
        if (person.State == CivilianState.Converging)
        {
            // Onlookers stop once they have a view: about 60 m from the fire, or 80 m from anything else.
            var stopAt = world.Hazards.Values.Where(h => !h.Ended && h.Model is FireSpreadModel)
                .Select(h => ((FireSpreadModel)h.Model!).DistanceToFire(person.Location))
                .DefaultIfEmpty(double.PositiveInfinity).Min();
            if (stopAt <= 60 || distance <= 80) return;
        }

        var speed = person.Panicking ? RunMps : person.State == CivilianState.Converging ? 1.2 : WalkMps;
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

    /// <summary>People inside a newly declared zone hear about it over the next 2–10 minutes (door-knocking, alerts).</summary>
    private void WarnZones(SimulationContext context)
    {
        var world = context.World;
        foreach (var action in world.ProtectiveActions.Values)
        {
            foreach (var person in world.Civilians)
            {
                if (person.WarnedAt is not null || person.State is CivilianState.Injured or CivilianState.Safe) continue;
                if (!action.Area.Contains(person.Location.ToPoint())) continue;
                person.WarnedAt = action.DeclaredAt + TimeSpan.FromMinutes(2 + _random!.NextDouble() * 8);
            }
        }
    }

    private static ProtectiveAction? ZoneAt(WorldState world, GeoPoint point)
    {
        if (world.ProtectiveActions.Count == 0) return null;
        var p = point.ToPoint();
        return world.ProtectiveActions.Values.FirstOrDefault(a => a.Area.Contains(p));
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
                if (person.State == CivilianState.Sheltering) rate *= 0.15; // indoors, windows shut
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
            var injured = new CasualtyInjured(Guid.NewGuid(), incidentId, person.Location, triage, cause);
            context.World.Casualties.TryAdd(injured.CasualtyId, new WorldCasualty
            {
                Id = injured.CasualtyId, IncidentId = incidentId, Location = person.Location, Triage = triage,
                State = CasualtyState.AwaitingTreatment, Cause = cause, InjuredAt = context.SimTime,
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
        context.EmitPerceived(new CallReceived(Guid.NewGuid(), "999 caller (mobile)", summary, reported, Math.Round(error * 1.3 / 10) * 10),
            EventSources.Comms);
    }
}
