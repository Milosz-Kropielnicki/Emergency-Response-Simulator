namespace Emergency_Response_Simulator.Core.Contracts;

/// <summary>Start, pause and speed control for the running session.</summary>
public interface ISimulationControl
{
    Guid SessionId { get; }
    DateTimeOffset SimTime { get; }
    bool IsRunning { get; }

    /// <summary>Simulation seconds per real second.</summary>
    double TimeScale { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task SetTimeScaleAsync(double timeScale, CancellationToken cancellationToken = default);
}
