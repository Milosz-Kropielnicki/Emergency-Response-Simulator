namespace Emergency_Response_Simulator.Simulation.Engine;

/// <summary>Identifies the running session; every event it produces carries this id.</summary>
public sealed record SimulationSession(Guid Id);

/// <summary>Bound from the "Simulation" configuration section.</summary>
public sealed class SimulationOptions
{
    public const string SectionName = "Simulation";

    /// <summary>Real time between engine ticks.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Simulation clock at session start. Defaults to the current time.</summary>
    public DateTimeOffset? StartTime { get; set; }

    public double InitialTimeScale { get; set; } = 1.0;

    public double MaxTimeScale { get; set; } = 60.0;

    /// <summary>Caps one tick's simulated time so a stalled machine does not make the world jump.</summary>
    public TimeSpan MaxTickDelta { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Scripted scenario to play: "barrow-street", or empty for none.</summary>
    public string? Scenario { get; set; }

    /// <summary>Start the clock as soon as the scenario is seeded (unattended demos, instructor setups).</summary>
    public bool AutoStart { get; set; }

    /// <summary>Presentation mode for scripted scenarios: the scenario makes the first dispatch decisions itself.</summary>
    public bool DemoAutoResponse { get; set; }

    /// <summary>
    /// Run the world simulation (hazards, civilians, casualties and hospitals, live traffic, power, weather drift and
    /// AI-run agencies). Off gives the scripted-only world of earlier phases.
    /// </summary>
    public bool WorldModels { get; set; } = true;

    /// <summary>
    /// Simulate communications (Phase 7): channel airtime, garbled and lost messages, coverage, batteries, the 999
    /// queue. Off, every message reaches command at once and intact.
    /// </summary>
    public bool CommsRealism { get; set; } = true;
}
