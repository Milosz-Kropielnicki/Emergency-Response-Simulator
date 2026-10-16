using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.State;

/// <summary>
/// How the session is being run as training (Phase 19): the scenario loaded, where it is, its weather source, and the
/// difficulty and random-event settings with the level adaptive difficulty has reached. Kept from the event stream, so a
/// replay rebuilds it; <c>AdaptiveDifficultySystem</c>, <c>RandomEventSystem</c> and <c>RealWeatherSystem</c> act on it.
/// </summary>
public sealed class WorldTraining
{
    public string? Scenario { get; private set; }

    /// <summary>Where the scenario happens: "near the incident" before there is one.</summary>
    public GeoPoint? Centre { get; private set; }

    public ScenarioWeather? Weather { get; private set; }
    public TrainingSettings Settings { get; private set; } = new();

    /// <summary>The level now: the instructor's, or where adaptive difficulty has moved it.</summary>
    public int Level { get; private set; } = 3;

    // Incidents within the incident (Phase 19), as they really are.

    public sealed class Device(SecondaryDevicePlaced placed, DateTimeOffset at)
    {
        public SecondaryDevicePlaced Placed { get; } = placed;
        public DateTimeOffset PlacedAt { get; } = at;
        public DateTimeOffset GoesOffAt => PlacedAt + TimeSpan.FromMinutes(Placed.FuseMinutes);
        public bool Found { get; set; }

        /// <summary>When a bomb disposal team started work on it.</summary>
        public DateTimeOffset? WorkStartedAt { get; set; }

        public bool Safe { get; set; }
        public bool Detonated { get; set; }
        public bool Live => !Safe && !Detonated;
    }

    public sealed class Shooter(ActiveShooterStarted started, DateTimeOffset at)
    {
        public ActiveShooterStarted Started { get; } = started;
        public DateTimeOffset StartedAt { get; } = at;
        public int Victims { get; set; }
        public DateTimeOffset? ArmedSince { get; set; }
        public DateTimeOffset? LastShotAt { get; set; }
        public bool Stopped { get; set; }
    }

    public Dictionary<Guid, Device> Devices { get; } = [];
    public Dictionary<Guid, Shooter> Shooters { get; } = [];

    public int? LastScore { get; private set; }
    public DateTimeOffset? LastAssessedAt { get; private set; }
    public List<string> History { get; } = [];

    /// <summary>Optional steps wait while adaptive difficulty finds the trainees struggling.</summary>
    public bool HoldOptional => Settings.Mode == DifficultyMode.Adaptive && Level <= 2;

    /// <summary>How much more (or less) often random events come at this level than at level 3.</summary>
    public double RateFactor => Level switch
    {
        1 => 0.5,
        2 => 0.7,
        4 => 1.4,
        5 => 2.0,
        _ => 1.0,
    };

    public void Apply(DomainEvent payload, DateTimeOffset at)
    {
        switch (payload)
        {
            case ScenarioLoaded e:
                (Scenario, Centre, Weather, Settings, Level) = (e.Name, e.Centre, e.Weather, e.Training, Math.Clamp(e.Training.Level, 1, 5));
                break;
            case TrainingSettingsChanged e:
                (Settings, Level) = (e.Settings, Math.Clamp(e.Settings.Level, 1, 5));
                History.Add($"{at.ToLocalTime():HH:mm} set to {e.Settings.Mode.ToString().ToLowerInvariant()}, level {Level}");
                break;
            case DifficultyAssessed e:
                (LastScore, LastAssessedAt) = (e.Score, at);
                break;
            case SecondaryDevicePlaced e:
                Devices.TryAdd(e.DeviceId, new Device(e, at));
                break;
            case ActiveShooterStarted e:
                Shooters.TryAdd(e.ThreatId, new Shooter(e, at));
                break;
            case DifficultyAdjusted e:
                Level = Math.Clamp(e.To, 1, 5);
                History.Add($"{at.ToLocalTime():HH:mm} {(e.To > e.From ? "raised" : "lowered")} to {e.To} (score {e.Score}): {e.Reason}");
                break;
        }
    }
}
