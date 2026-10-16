using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// The environment (Phase 17): air, water, soil and wildlife.
/// <list type="bullet">
/// <item><b>Water</b>: fire crews' water runs off contaminated (40% of what they put on: an engine about 400 L a minute).
/// Uncontained, 70% of it goes down the drains to the nearest river or canal and the rest soaks into the ground. Once
/// command has ordered it contained (<see cref="WorldProtection.ContainmentLeadTime"/> to put drain covers, booms and bunds
/// in place), <see cref="ContainedShare"/> is held back for collection.</item>
/// <item><b>Wildlife</b>: past <see cref="FishKillLitres"/> in a watercourse the fish start dying, and Inland Fisheries
/// Ireland reports it. A toxic cloud (not smoke) over more than 5 hectares kills birds, and the council's parks staff
/// find them.</item>
/// <item><b>Soil</b>: soaked run-off contaminates the ground round the fire; a chemical, biological or radioactive cloud
/// leaves contamination over half its footprint. Residents going home to it is recorded (<see cref="EvacuationSystem"/>).</item>
/// <item><b>Monitoring</b>: a team sent to sample the air, water or soil starts <see cref="MonitorLeadTime"/> later and
/// reports every <see cref="ReadingInterval"/>, graded against guideline values: the evidence for lifting a shelter or
/// letting people home.</item>
/// </list>
/// </summary>
public sealed class EnvironmentSystem : ISimulationSystem
{
    public static readonly TimeSpan MonitorLeadTime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ReadingInterval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan OutcomeInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan FishKillReportDelay = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan BirdsReportDelay = TimeSpan.FromMinutes(30);

    public const double RunoffShare = 0.4;
    public const double ContainedShare = 0.85;
    public const double ToWaterShare = 0.7;
    public const double FishKillLitres = 20_000;
    public const double LitresPerFish = 200;

    /// <summary>Dublin's rivers and canals, where the drains come out.</summary>
    private static readonly (string Name, GeoPoint At)[] Watercourses =
    [
        ("Grand Canal Dock", new GeoPoint(53.3405, -6.2380)),
        ("River Liffey", new GeoPoint(53.3470, -6.2600)),
        ("River Dodder", new GeoPoint(53.3300, -6.2330)),
        ("Grand Canal", new GeoPoint(53.3320, -6.2600)),
        ("Royal Canal", new GeoPoint(53.3560, -6.2550)),
        ("River Tolka", new GeoPoint(53.3640, -6.2400)),
    ];

    private readonly Dictionary<Guid, int> _waterSteps = [];
    private readonly Dictionary<Guid, (int Fish, int Birds)> _wildlifeRecorded = [];
    private readonly Dictionary<(Guid, EnvironmentMedium), DateTimeOffset> _impactDue = [];
    private readonly HashSet<(Guid, EnvironmentMedium)> _impactReported = [];
    private readonly Dictionary<Guid, DateTimeOffset> _outcomeAt = [];

    public int Order => 24; // after crews have worked and the clouds have moved

    public static bool IsEpa(string recipient) =>
        recipient.Contains("EPA", StringComparison.Ordinal) || recipient.Contains("Environmental Protection", StringComparison.OrdinalIgnoreCase);

    public void Update(SimulationContext context)
    {
        Runoff(context);
        Deposition(context);
        Wildlife(context);
        Readings(context);
        Outcomes(context);
    }

    /// <summary>The river or canal the drains near a place empty into.</summary>
    public static (string Name, GeoPoint At) NearestWatercourse(GeoPoint near) => Watercourses.MinBy(w => GeoMath.DistanceMeters(w.At, near));

    private static WorldProtection.Pollution PollutionOf(WorldState world, Guid key, GeoPoint near)
    {
        if (world.Protection.Pollutions.TryGetValue(key, out var pollution)) return pollution;
        var (name, at) = NearestWatercourse(near);
        return world.Protection.Pollutions[key] = new WorldProtection.Pollution { Watercourse = name, WatercoursePoint = at };
    }

    /// <summary>Litres a minute a crew puts on a fire.</summary>
    private static double Flow(UnitType type) => type switch
    {
        UnitType.Engine or UnitType.Wildland => 1000,
        UnitType.Ladder => 800,
        UnitType.Tanker or UnitType.WaterTender => 600,
        _ => 0,
    };

    // ---- Run-off ----

    private void Runoff(SimulationContext context)
    {
        var world = context.World;
        var fires = world.Hazards.Values.Where(h => !h.Ended && h.Model is FireSpreadModel { IsActive: true }).ToList();
        if (fires.Count == 0) return;
        var minutes = context.Delta.TotalMinutes;

        foreach (var unit in world.Units.Values.Where(u => u.Phase == ResponsePhase.Operating && !u.Disabled && Flow(u.Type) > 0))
        {
            var fire = fires.Where(h => ((FireSpreadModel)h.Model!).DistanceToFire(unit.Location) <= 120).MinBy(h => GeoMath.DistanceMeters(h.Origin, unit.Location));
            if (fire is null) continue;
            var runoff = Flow(unit.Type) * minutes * RunoffShare;
            var key = fire.IncidentId ?? Guid.Empty;
            var pollution = PollutionOf(world, key, fire.Origin);
            pollution.Runoff += runoff;

            var contained = unit.OrderedIncidentId is { } incident && world.Protection.Containment.TryGetValue(incident, out var ordered)
                            && context.SimTime >= ordered + WorldProtection.ContainmentLeadTime
                ? runoff * ContainedShare : 0;
            pollution.Contained += contained;
            pollution.ToWater += (runoff - contained) * ToWaterShare;
            pollution.Soaked += (runoff - contained) * (1 - ToWaterShare);

            // The ground round the fire, growing with what has soaked in (60 m for the first 2,000 L, up to 150 m).
            if (pollution.Soaked > 2000)
            {
                var radius = Math.Min(150, 60 * Math.Sqrt(pollution.Soaked / 2000));
                var index = pollution.Ground.FindIndex(g => GeoMath.DistanceMeters(g.Centre, fire.Origin) < 5);
                if (index < 0) pollution.Ground.Add((fire.Origin, radius));
                else pollution.Ground[index] = (fire.Origin, Math.Max(radius, pollution.Ground[index].RadiusMeters));
            }

            var step = (int)(pollution.ToWater / 5000);
            if (step > _waterSteps.GetValueOrDefault(key))
            {
                _waterSteps[key] = step;
                context.EmitTruth(new PollutionReachedWater(key == Guid.Empty ? null : key, pollution.Watercourse, Math.Round(pollution.ToWater)));
            }
        }
    }

    /// <summary>A cloud of anything but smoke leaves its mark on the ground under the worst of it, and kills birds over a large area.</summary>
    private static void Deposition(SimulationContext context)
    {
        foreach (var hazard in context.World.Hazards.Values.Where(h => h.Kind == HazardKind.Plume && h.FootprintArea > 0))
        {
            if (hazard.Substance?.Contains("smoke", StringComparison.OrdinalIgnoreCase) == true) continue;
            var pollution = PollutionOf(context.World, hazard.IncidentId ?? Guid.Empty, hazard.Origin);
            var radius = Math.Min(600, Math.Sqrt(hazard.FootprintArea / Math.PI) * 0.5);
            var index = pollution.Ground.FindIndex(g => GeoMath.DistanceMeters(g.Centre, hazard.Origin) < 5);
            if (index < 0) pollution.Ground.Add((hazard.Origin, radius));
            else if (radius > pollution.Ground[index].RadiusMeters) pollution.Ground[index] = (hazard.Origin, radius);
            if (hazard.FootprintArea > 50_000)
                pollution.Birds = Math.Max(pollution.Birds, (int)Math.Min(300, hazard.FootprintArea / 4000));
        }
    }

    // ---- Wildlife ----

    private void Wildlife(SimulationContext context)
    {
        var world = context.World;
        foreach (var (key, pollution) in world.Protection.Pollutions)
        {
            if (pollution.ToWater > FishKillLitres)
                pollution.Fish = Math.Max(pollution.Fish, (int)Math.Min(3000, (pollution.ToWater - FishKillLitres) / LitresPerFish));
            var incident = key == Guid.Empty ? (Guid?)null : key;
            var (fish, birds) = _wildlifeRecorded.GetValueOrDefault(key);
            if (pollution.Fish >= fish + 25 || pollution.Fish > 0 && fish == 0)
            {
                _wildlifeRecorded[key] = (pollution.Fish, birds);
                context.EmitTruth(new WildlifeHarmed(incident, "fish", pollution.Fish, $"fire-water run-off in the {pollution.Watercourse}"));
                _impactDue.TryAdd((key, EnvironmentMedium.Water), context.SimTime + FishKillReportDelay);
            }
            (fish, birds) = _wildlifeRecorded.GetValueOrDefault(key);
            if (pollution.Birds >= birds + 10 || pollution.Birds > 0 && birds == 0)
            {
                _wildlifeRecorded[key] = (fish, pollution.Birds);
                context.EmitTruth(new WildlifeHarmed(incident, "birds", pollution.Birds, "the toxic cloud"));
                _impactDue.TryAdd((key, EnvironmentMedium.Air), context.SimTime + BirdsReportDelay);
            }

            // Somebody finds them and rings it in.
            foreach (var medium in new[] { EnvironmentMedium.Water, EnvironmentMedium.Air })
            {
                if (!_impactDue.TryGetValue((key, medium), out var due) || context.SimTime < due || !_impactReported.Add((key, medium))) continue;
                var (from, what, where) = medium == EnvironmentMedium.Water
                    ? ("Inland Fisheries Ireland", $"Fish kill in the {pollution.Watercourse}: dead fish downstream of the drain outfalls, the water discoloured and foaming", pollution.WatercoursePoint)
                    : ("Dublin City Council parks", "Dead birds in the parks and on the quays under where the cloud went", world.Hazards.Values.FirstOrDefault(h => (h.IncidentId ?? Guid.Empty) == key)?.Origin ?? pollution.WatercoursePoint);
                CommsNet.Chat(context, from, $"{from}: {what}.",
                    new EnvironmentalImpactReported(Guid.NewGuid(), WorldToPerceived(world, incident, where), medium, what, where), TimeSpan.Zero);
            }
        }
    }

    /// <summary>The perceived incident nearest a place, for a report about it (command knows only its own incidents).</summary>
    private static Guid? WorldToPerceived(WorldState world, Guid? _, GeoPoint near) =>
        world.Logistics.IncidentLocations.Count == 0 ? null
            : world.Logistics.IncidentLocations.MinBy(i => GeoMath.DistanceMeters(i.Value, near)).Key;

    // ---- Monitoring ----

    private static void Readings(SimulationContext context)
    {
        var world = context.World;
        foreach (var monitor in world.Protection.Monitors.Values)
        {
            if (context.SimTime < monitor.DeployedAt + MonitorLeadTime) continue;
            if (monitor.LastReadingAt is { } last && context.SimTime - last < ReadingInterval) continue;
            monitor.LastReadingAt = context.SimTime;
            var at = monitor.Deployed.Location;
            var (level, detail) = monitor.Deployed.Medium switch
            {
                EnvironmentMedium.Air => Air(world, at),
                EnvironmentMedium.Water => Water(world, at),
                _ => Soil(world, at),
            };
            var team = monitor.Deployed.Medium == EnvironmentMedium.Air ? "EPA air monitoring" : "EPA sampling team";
            CommsNet.Chat(context, team, $"{team}: {Protection.Name(monitor.Deployed.Medium).ToLowerInvariant()} {Protection.Name(level)}. {detail}",
                new EnvironmentalReading(monitor.Deployed.MonitorId, monitor.Deployed.Medium, level, detail), TimeSpan.Zero);
        }
    }

    private static PollutionLevel Grade(HazardLevel level) => level switch
    {
        HazardLevel.High => PollutionLevel.High,
        HazardLevel.Moderate => PollutionLevel.Moderate,
        HazardLevel.Low => PollutionLevel.Low,
        _ => PollutionLevel.Clear,
    };

    private static (PollutionLevel, string) Air(WorldState world, GeoPoint at)
    {
        var exposures = HazardSystem.ExposuresAt(world, at).Where(e => e.Hazard.Kind != HazardKind.Flood).ToList();
        if (exposures.Count == 0) return (PollutionLevel.Clear, "Within guideline values.");
        var worst = exposures.MaxBy(e => e.Exposure.Level);
        var level = Grade(worst.Exposure.Level);
        return worst.Hazard.Kind == HazardKind.Fire
            ? (level, $"Smoke: fine particles (PM2.5) {Protection.Name(level)}.")
            : (level, $"{Capitalise(worst.Hazard.Substance ?? "an unidentified gas")} detected, {Protection.Name(level)}.");
    }

    private static (PollutionLevel, string) Water(WorldState world, GeoPoint at)
    {
        var pollution = world.Protection.Pollutions.Values.Where(p => GeoMath.DistanceMeters(p.WatercoursePoint, at) <= 2000).MaxBy(p => p.ToWater);
        var tap = world.Protection.UnsafeWater.Any(w => GeoMath.DistanceMeters(w.Centre, at) <= w.RadiusMeters);
        var level = pollution is null ? PollutionLevel.Clear
            : pollution.ToWater < 1000 ? PollutionLevel.Clear
            : pollution.ToWater < 10_000 ? PollutionLevel.Low
            : pollution.ToWater < FishKillLitres ? PollutionLevel.Moderate
            : PollutionLevel.High;
        var detail = level == PollutionLevel.Clear ? "No contamination detected in the watercourse."
            : $"{pollution!.Watercourse}: fire-water run-off — foam, ash and combustion products; dissolved oxygen falling.";
        if (tap)
        {
            level = level < PollutionLevel.Moderate ? PollutionLevel.Moderate : level;
            detail += " Tap water: chlorine residual below the safe minimum.";
        }
        return (level, detail);
    }

    private static (PollutionLevel, string) Soil(WorldState world, GeoPoint at)
    {
        var hit = world.Protection.Pollutions.Values.SelectMany(p => p.Ground)
            .Where(g => GeoMath.DistanceMeters(g.Centre, at) <= g.RadiusMeters)
            .Select(g => GeoMath.DistanceMeters(g.Centre, at) / g.RadiusMeters)
            .DefaultIfEmpty(double.NaN).Min();
        return double.IsNaN(hit)
            ? (PollutionLevel.Clear, "No contamination found in the samples.")
            : (hit < 0.5 ? PollutionLevel.High : PollutionLevel.Moderate, "Deposited contamination: clean up or decontaminate before people move back in.");
    }

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // ---- For the review ----

    private void Outcomes(SimulationContext context)
    {
        foreach (var (key, pollution) in context.World.Protection.Pollutions)
        {
            if (_outcomeAt.TryGetValue(key, out var last) && context.SimTime - last < OutcomeInterval) continue;
            _outcomeAt[key] = context.SimTime;
            context.EmitTruth(new EnvironmentOutcome(key == Guid.Empty ? null : key, Math.Round(pollution.Runoff), Math.Round(pollution.ToWater),
                Math.Round(pollution.Contained), Math.Round(pollution.GroundSquareMeters), pollution.Fish + pollution.Birds));
        }
    }
}
