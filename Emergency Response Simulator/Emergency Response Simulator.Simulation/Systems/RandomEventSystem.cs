using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Randomised events (Phase 19), when the training settings switch them on. They come at random — on average every
/// <see cref="TrainingSettings.MeanMinutes"/> at level 3, more often at higher levels (<see cref="WorldTraining.RateFactor"/>),
/// never closer than two minutes — and each is one of the kinds allowed:
/// <list type="bullet">
/// <item><b>Weather</b>: the wind veers or backs 30–90° and changes speed; the met service reports it.</item>
/// <item><b>Equipment</b>: a committed unit breaks down (two times in three) or its radio fails.</item>
/// <item><b>A new incident</b> 0.6–2.5 km away — a fire, a crash, a flood — bigger at higher levels, and someone rings 999.</item>
/// </list>
/// The sequence is seeded, so the same settings give the same events: sessions can be compared.
/// </summary>
public sealed class RandomEventSystem : ISimulationSystem
{
    public static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(2);
    private static readonly Guid Seed = new("a1f0c3d2-5b6e-4f71-9a8b-0c1d2e3f4a5b");

    private static readonly string[] Faults = ["flat tyre", "pump seal has failed", "engine overheating", "air brake fault", "electrical failure"];

    private static readonly (IncidentType Type, string Call)[] Incidents =
    [
        (IncidentType.StructureFire, "There's smoke pouring out of the house next door, I think someone's still inside"),
        (IncidentType.RoadAccident, "A car's hit a cyclist at the junction, he's not moving"),
        (IncidentType.StructureFire, "The shop under our flat is on fire"),
        (IncidentType.Flood, "Water's coming up through the drains and into the houses on our road"),
    ];

    private DateTimeOffset? _next;
    private int _count;

    public int Order => -9; // with the script, before anything reacts

    public void Update(SimulationContext context)
    {
        var training = context.World.Training;
        if (!training.Settings.RandomEvents)
        {
            _next = null;
            return;
        }
        _next ??= context.SimTime + Gap(training);
        if (context.SimTime < _next) return;
        _next = context.SimTime + Gap(training);

        var random = SimRandom.For(Seed, 7919 + _count++);
        var kinds = new List<Action<SimulationContext, Random>>();
        if (training.Settings.RandomWeather) kinds.Add(Weather);
        if (training.Settings.RandomEquipment && Committed(context.World).Count > 0) kinds.Add(Equipment);
        if (training.Settings.RandomIncidents) kinds.Add(NewIncident);
        if (kinds.Count > 0) kinds[random.Next(kinds.Count)](context, random);
    }

    /// <summary>An exponential wait: mean <see cref="TrainingSettings.MeanMinutes"/> divided by the level's rate, at least two minutes.</summary>
    private TimeSpan Gap(WorldTraining training)
    {
        var random = SimRandom.For(Seed, 104729 + _count);
        var mean = Math.Max(MinimumGap.TotalMinutes, training.Settings.MeanMinutes / training.RateFactor);
        var minutes = -Math.Log(1 - random.NextDouble()) * mean;
        return TimeSpan.FromMinutes(Math.Max(MinimumGap.TotalMinutes, minutes));
    }

    private static List<WorldUnit> Committed(WorldState world) =>
        [.. world.Units.Values.Where(u => u.OrderedIncidentId is not null && !u.BrokenDown && !u.RadioFailed).OrderBy(u => u.Callsign, StringComparer.Ordinal)];

    private static void Weather(SimulationContext context, Random random)
    {
        var weather = context.World.Weather;
        var turn = (30 + random.NextDouble() * 60) * (random.Next(2) == 0 ? -1 : 1);
        var from = ((weather.WindFromDegrees + turn) % 360 + 360) % 360;
        var speed = Math.Clamp(weather.WindSpeedMps + (random.NextDouble() * 6 - 3), 1, 15);
        context.EmitTruth(new WeatherChanged(from, speed, weather.TemperatureC, weather.RelativeHumidity));
        context.EmitPerceived(new WeatherObserved("Met Éireann", Near(context.World), from, speed, weather.TemperatureC, weather.RelativeHumidity), EventSources.Comms);
        context.EmitTruth(new RandomEventOccurred("Weather", $"wind {(turn > 0 ? "veers" : "backs")} to {GeoMath.CompassPoint(from)}, {speed:F0} m/s"));
    }

    private static void Equipment(SimulationContext context, Random random)
    {
        var units = Committed(context.World);
        var unit = units[random.Next(units.Count)];
        if (random.Next(3) < 2)
        {
            var fault = Faults[random.Next(Faults.Length)];
            context.EmitTruth(new UnitBrokeDown(unit.Id, fault));
            context.EmitTruth(new RandomEventOccurred("Equipment", $"{unit.Callsign} breaks down: {fault}"));
        }
        else
        {
            context.EmitTruth(new UnitRadioFailed(unit.Id, true));
            context.EmitTruth(new RandomEventOccurred("Equipment", $"{unit.Callsign}'s radio fails"));
        }
    }

    private static void NewIncident(SimulationContext context, Random random)
    {
        var level = context.World.Training.Level;
        var (type, call) = Incidents[random.Next(Incidents.Length)];
        var at = GeoMath.Destination(Near(context.World), random.NextDouble() * 360, 600 + random.NextDouble() * 1900);
        var casualties = random.Next(level + 1);
        context.EmitTruth(new WorldIncidentStarted(Guid.NewGuid(), type, at, Math.Clamp(0.15 + 0.1 * level, 0.1, 0.8), casualties));
        CommsNet.EmergencyCall(context, new PendingCall
        {
            Caller = "999 caller (mobile)", Summary = call, Location = GeoMath.Destination(at, random.NextDouble() * 360, 40), AccuracyMeters = 80,
            Patience = TimeSpan.FromMinutes(2),
        });
        context.EmitTruth(new RandomEventOccurred("Incident", $"{EventDescriber.Humanize(type).ToLowerInvariant()} {GeoMath.DistanceMeters(Near(context.World), at):N0} m away, {casualties} hurt"));
    }

    /// <summary>The largest live incident, else where the scenario happens, else the middle of the stations.</summary>
    public static GeoPoint Near(WorldState world) =>
        world.Incidents.Values.Where(i => !i.Extinguished).OrderByDescending(i => i.Severity).FirstOrDefault()?.Location
        ?? world.Training.Centre
        ?? (world.Units.Count > 0
            ? new GeoPoint(world.Units.Values.Average(u => u.Home.Latitude), world.Units.Values.Average(u => u.Home.Longitude))
            : new GeoPoint(53.344, -6.26));
}
