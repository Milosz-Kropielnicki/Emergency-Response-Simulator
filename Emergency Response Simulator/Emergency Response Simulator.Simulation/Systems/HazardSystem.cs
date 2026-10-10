using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Advances the physical hazards (Design Document §10.4) and what they set off (§10.5):
/// <list type="bullet">
/// <item>Real fires, floods and toxic releases get a model as soon as they start (a structure fire or explosion gets a
/// fire, a flood a flood, a hazmat release a plume).</item>
/// <item>Fire crews working at the scene put water on burning cells within reach.</item>
/// <item>A fire that reaches a chemical store starts a toxic release; fire or floodwater reaching a substation cuts
/// the power it supplies.</item>
/// <item>Streets under more than 25 cm of water become real obstructions: crews find them by driving into them.</item>
/// <item>The incident's true severity follows the fire, and footprints are recorded every minute for the instructor
/// and the AAR.</item>
/// </list>
/// Everything here is ground truth; nothing reaches the COP until someone reports it.
/// </summary>
public sealed class HazardSystem(IHazardTerrain? terrain = null) : ISimulationSystem
{
    public static readonly TimeSpan FootprintInterval = TimeSpan.FromMinutes(1);

    /// <summary>A crew's hoses reach this far from its appliance.</summary>
    public const double HoseReachMeters = 90;

    /// <summary>Default inflow for a flood incident with no better information (m³/s).</summary>
    public const double DefaultFloodInflow = 3;

    private readonly IHazardTerrain _terrain = terrain ?? new UniformTerrain();
    private readonly HashSet<Guid> _seededIncidents = [];
    private readonly Dictionary<Guid, Dictionary<int, Guid>> _floodedRoads = [];

    public int Order => 5; // after injects and weather, before units and people react

    public void Update(SimulationContext context)
    {
        var world = context.World;
        StartHazardsForIncidents(context);

        foreach (var hazard in world.Hazards.Values.Where(h => !h.Ended).ToList())
        {
            hazard.Model ??= CreateModel(hazard, world);
            var model = hazard.Model;

            if (hazard.Kind == HazardKind.Plume)
                DrawDownInventory(context, hazard);

            model.Step(new HazardStepInput(context.SimTime, world.Weather, hazard.Rate,
                hazard.Kind == HazardKind.Fire ? Suppression(world, context.SimTime) : null), context.Delta);

            switch (model)
            {
                case FireSpreadModel fire:
                    TriggerSites(context, hazard, site => fire.DistanceToFire(site.Location) <= FireSpreadModel.CellMeters / 2, "Fire");
                    TrackSeverity(context, hazard, fire);
                    break;
                case FloodModel flood:
                    TriggerSites(context, hazard, site => site.Kind is HazardSiteKind.Substation or HazardSiteKind.CellTower
                                                          && flood.DepthAt(site.Location) >= 0.4, "Floodwater");
                    CloseFloodedRoads(world, hazard, flood);
                    break;
            }

            RecordFootprint(context, hazard, model);

            if (!model.IsActive)
                End(context, hazard);
        }
    }

    /// <summary>Real incidents of a hazardous type get a hazard of the matching kind (once).</summary>
    private void StartHazardsForIncidents(SimulationContext context)
    {
        var world = context.World;
        foreach (var incident in world.Incidents.Values)
        {
            if (incident.Extinguished || !_seededIncidents.Add(incident.Id)) continue;
            if (world.Hazards.Values.Any(h => h.IncidentId == incident.Id)) continue;

            HazardStarted? started = incident.Type switch
            {
                IncidentType.StructureFire or IncidentType.Explosion or IncidentType.WildlandFire =>
                    new HazardStarted(Guid.NewGuid(), incident.Id, HazardKind.Fire, incident.Location,
                        $"{EventDescriber.Humanize(incident.Type)}: fire takes hold"),
                IncidentType.Flood =>
                    new HazardStarted(Guid.NewGuid(), incident.Id, HazardKind.Flood, incident.Location,
                        "Surface flooding", DefaultFloodInflow),
                IncidentType.HazmatRelease =>
                    new HazardStarted(Guid.NewGuid(), incident.Id, HazardKind.Plume, incident.Location,
                        "Toxic gas release", Rate: 0.4, Substance: "chlorine", Inventory: 1500),
                _ => null,
            };
            if (started is not null)
                Start(context, started);
        }
    }

    /// <summary>Adds a hazard to the world now (so it is modelled this tick) and records it.</summary>
    private static WorldHazard Start(SimulationContext context, HazardStarted started)
    {
        var hazard = new WorldHazard
        {
            Id = started.HazardId, Kind = started.Kind, IncidentId = started.WorldIncidentId, Origin = started.Origin,
            Description = started.Description, Rate = started.Rate, Substance = started.Substance, Inventory = started.Inventory,
            StartedAt = context.SimTime,
        };
        context.World.Hazards[hazard.Id] = hazard;
        context.EmitTruth(started);
        return hazard;
    }

    private IHazardModel CreateModel(WorldHazard hazard, WorldState world)
    {
        var severity = hazard.IncidentId is { } id && world.Incidents.TryGetValue(id, out var incident) ? incident.Severity : 0.4;
        return hazard.Kind switch
        {
            HazardKind.Fire => new FireSpreadModel(hazard.Origin, severity, _terrain, SimRandom.For(hazard.Id).Next()),
            HazardKind.Flood => new FloodModel(hazard.Origin, _terrain),
            _ => new PlumeModel(hazard.Origin, hazard.Substance),
        };
    }

    private static void DrawDownInventory(SimulationContext context, WorldHazard hazard)
    {
        if (hazard.Rate <= 0 || hazard.Inventory <= 0) return;
        hazard.Inventory -= hazard.Rate * context.Delta.TotalSeconds;
        if (hazard.Inventory > 0) return;

        hazard.Inventory = 0;
        hazard.Rate = 0;
        context.EmitTruth(new HazardRateChanged(hazard.Id, 0, "Store empty: the release has stopped"));
    }

    /// <summary>
    /// Fire crews working at a scene, with how much fire each can knock down: less for a tired or short-handed crew, one
    /// working from outside after an evacuation, or one handing over (Phase 8).
    /// </summary>
    private static List<Suppression> Suppression(WorldState world, DateTimeOffset now) =>
        world.Units.Values
            .Where(u => u.Phase == ResponsePhase.Operating && !u.BrokenDown)
            .Select(u => (Unit: u, Rate: SuppressionRate(u.Type) * CrewFactors.Effectiveness(u, now)))
            .Where(x => x.Rate > 0)
            .Select(x => new Suppression(x.Unit.Location, HoseReachMeters, x.Rate))
            .ToList();

    /// <summary>Burning 15 m cells a crew can put out per minute.</summary>
    public static double SuppressionRate(UnitType type) => type switch
    {
        UnitType.Engine or UnitType.Wildland => 1.5,
        UnitType.Ladder => 1.0,
        UnitType.Tanker or UnitType.WaterTender => 0.8,
        UnitType.Rescue or UnitType.Hazmat => 0.4,
        _ => 0,
    };

    private void TriggerSites(SimulationContext context, WorldHazard cause, Func<HazardSite, bool> reached, string what)
    {
        foreach (var site in context.World.Sites.Values.Where(s => !s.Triggered))
        {
            if (GeoMath.DistanceMeters(site.Location, cause.Origin) > 2500 || !reached(site)) continue;
            site.Triggered = true;

            if (site.Kind == HazardSiteKind.ChemicalStore)
            {
                // The store empties over about 40 minutes once its containers fail in the heat.
                var rate = Math.Max(0.1, site.Quantity / 2400);
                var release = Start(context, new HazardStarted(Guid.NewGuid(), cause.IncidentId, HazardKind.Plume, site.Location,
                    $"{site.Substance ?? "Toxic gas"} release from {site.Name}", rate, site.Substance, site.Quantity));
                context.EmitTruth(new CascadeOccurred($"{what} reached {site.Name}",
                    $"{Capitalise(site.Substance ?? "toxic gas")} release begins ({rate:0.0} kg/s)", HazardId: release.Id));
            }
            else if (site.Kind == HazardSiteKind.CellTower)
            {
                context.EmitTruth(new CellTowerFailed(site.Id, $"{site.Name} damaged by {what.ToLowerInvariant()}"));
                context.EmitTruth(new CascadeOccurred($"{what} reached {site.Name}",
                    $"Mobile mast down: 999 calls from mobiles and mobile data lost within {site.ServiceRadiusMeters:F0} m", HazardId: cause.Id));
            }
            else
            {
                var outage = new PowerOutage
                {
                    Id = Guid.NewGuid(), Centre = site.Location, RadiusMeters = site.ServiceRadiusMeters,
                    Cause = $"{site.Name} damaged by {what.ToLowerInvariant()}", StartedAt = context.SimTime,
                    RestoreAt = context.SimTime + PowerOutage.DefaultRepairTime, SiteId = site.Id,
                };
                context.World.Outages[outage.Id] = outage;
                context.EmitTruth(new PowerOutageStarted(outage.Id, outage.Centre, outage.RadiusMeters, outage.Cause));
                context.EmitTruth(new CascadeOccurred($"{what} reached {site.Name}",
                    $"Power out within {site.ServiceRadiusMeters:F0} m", HazardId: cause.Id));
            }
        }
    }

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>The incident's true severity follows how much is burning.</summary>
    private static void TrackSeverity(SimulationContext context, WorldHazard hazard, FireSpreadModel fire)
    {
        if (hazard.IncidentId is not { } id || !context.World.Incidents.TryGetValue(id, out var incident) || incident.Extinguished)
            return;

        var severity = Math.Round(Math.Clamp(0.25 + fire.BurningAreaSquareMeters / 10_000, 0.05, 1) * 20) / 20;
        if (!fire.IsActive || Math.Abs(severity - incident.Severity) < 0.1) return;

        incident.Severity = severity;
        context.EmitTruth(new WorldIncidentChanged(id, severity, incident.ActualCasualties, Extinguished: false));
    }

    /// <summary>Streets under water are closed in reality; drivers find out when they reach them.</summary>
    private void CloseFloodedRoads(WorldState world, WorldHazard hazard, FloodModel flood)
    {
        if (!_floodedRoads.TryGetValue(hazard.Id, out var closed))
            _floodedRoads[hazard.Id] = closed = [];

        foreach (var (cell, depth) in flood.CellsDeeperThan(FloodModel.ImpassableDepth))
        {
            if (closed.ContainsKey(cell)) continue;
            var id = SimRandom.Derive(hazard.Id, cell);
            closed[cell] = id;
            world.Obstructions[id] = (flood.Grid.CellPolygon(cell), $"flooded, water about {depth * 100:F0} cm deep");
        }

        // Re-opens once the water drops well below the closing depth.
        foreach (var (cell, id) in closed.ToList())
        {
            if (flood.DepthOf(cell) >= 0.15) continue;
            closed.Remove(cell);
            world.Obstructions.Remove(id);
        }
    }

    private static void RecordFootprint(SimulationContext context, WorldHazard hazard, IHazardModel model)
    {
        if (hazard.FootprintAt is { } last && context.SimTime - last < FootprintInterval) return;

        var area = model.AreaSquareMeters;
        var changed = hazard.FootprintAt is null || Math.Abs(area - hazard.FootprintArea) > Math.Max(1, hazard.FootprintArea * 0.03);
        hazard.FootprintAt = context.SimTime;
        if (!changed) return;

        var polygons = model.Footprint();
        hazard.Footprint = polygons.Count switch
        {
            0 => null,
            1 => polygons[0],
            _ => Wgs84.Factory.CreateMultiPolygon(polygons.ToArray()),
        };
        hazard.FootprintArea = area;
        context.EmitTruth(new HazardFootprintChanged(hazard.Id, polygons.Select(LocalGrid.RingOf).ToList(), area, model.Intensity));
    }

    private void End(SimulationContext context, WorldHazard hazard)
    {
        hazard.Ended = true;
        hazard.Footprint = null;
        var reason = hazard.Kind switch
        {
            HazardKind.Fire => "Fire out",
            HazardKind.Flood => "Water has receded",
            _ => "Release stopped and the cloud has cleared",
        };
        context.EmitTruth(new HazardEnded(hazard.Id, reason));

        if (_floodedRoads.Remove(hazard.Id, out var closed))
        {
            foreach (var id in closed.Values)
                context.World.Obstructions.Remove(id);
        }

        if (hazard.Kind == HazardKind.Fire && hazard.IncidentId is { } id2
            && context.World.Incidents.TryGetValue(id2, out var incident) && !incident.Extinguished)
        {
            incident.Extinguished = true;
            incident.Severity = 0;
            context.EmitTruth(new WorldIncidentChanged(id2, 0, incident.ActualCasualties, Extinguished: true));
        }
    }

    /// <summary>Exposure at a point from every active hazard: the worst level, and each hazard's own reading.</summary>
    public static IEnumerable<(WorldHazard Hazard, HazardExposure Exposure)> ExposuresAt(WorldState world, GeoPoint point)
    {
        foreach (var hazard in world.Hazards.Values)
        {
            if (hazard.Ended || hazard.Model is not { } model) continue;
            var exposure = model.ExposureAt(point);
            if (exposure.Level != HazardLevel.None)
                yield return (hazard, exposure);
        }
    }
}
