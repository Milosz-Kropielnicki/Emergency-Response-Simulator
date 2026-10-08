namespace Emergency_Response_Simulator.Simulation.Engine;

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
}
