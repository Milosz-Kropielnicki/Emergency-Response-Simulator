using System.Diagnostics;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Simulation.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Emergency_Response_Simulator.Simulation.Engine;

/// <summary>
/// Owns the simulation clock and the ground truth, runs the systems every tick, and is the single
/// writer to the event stream for a session so that sequence order equals causal order.
/// </summary>
public sealed class SimulationEngine : ISimulationControl, IEventPublisher
{
    private readonly IEventStore _store;
    private readonly IReadOnlyList<ISimulationSystem> _systems;
    private readonly SimulationOptions _options;
    private readonly ILogger<SimulationEngine> _logger;

    // Serialises ticks and publishes; WorldState is only touched while this is held.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SimulationEngine(
        IEventStore store,
        WorldState world,
        IEnumerable<ISimulationSystem> systems,
        SimulationOptions options,
        ILogger<SimulationEngine>? logger = null,
        Guid? sessionId = null)
    {
        SessionId = sessionId ?? Guid.NewGuid();
        _store = store;
        World = world;
        _systems = systems.OrderBy(s => s.Order).ToList();
        _options = options;
        _logger = logger ?? NullLogger<SimulationEngine>.Instance;

        // Kept in UTC; the UI converts to local time for display.
        var start = (options.StartTime ?? DateTimeOffset.UtcNow).ToUniversalTime();
        SimTime = start.AddTicks(-(start.Ticks % TimeSpan.TicksPerSecond));
        TimeScale = options.InitialTimeScale;
    }

    public Guid SessionId { get; }
    public DateTimeOffset SimTime { get; private set; }
    public bool IsRunning { get; private set; }
    public double TimeScale { get; private set; }

    /// <summary>Ground truth. Read it only from systems, tests or instructor tools.</summary>
    public WorldState World { get; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;
        IsRunning = true;
        await PublishAsync(new SimulationStarted(TimeScale), EventVisibility.Perceived, EventSources.Engine, cancellationToken);
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning) return;
        IsRunning = false;
        await PublishAsync(new SimulationPaused(), EventVisibility.Perceived, EventSources.Engine, cancellationToken);
    }

    public async Task SetTimeScaleAsync(double timeScale, CancellationToken cancellationToken = default)
    {
        var clamped = Math.Clamp(timeScale, 0.1, _options.MaxTimeScale);
        if (clamped.Equals(TimeScale)) return;
        TimeScale = clamped;
        await PublishAsync(new TimeScaleChanged(clamped), EventVisibility.Perceived, EventSources.Engine, cancellationToken);
    }

    public async Task<SimEvent> PublishAsync(
        DomainEvent payload, EventVisibility visibility, string source, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await AppendLockedAsync(visibility, source, payload, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Advances the world if running, converting real elapsed time to simulation time.</summary>
    public Task TickAsync(TimeSpan realElapsed, CancellationToken cancellationToken = default)
    {
        if (!IsRunning) return Task.CompletedTask;

        var simDelta = realElapsed * TimeScale;
        if (simDelta > _options.MaxTickDelta)
            simDelta = _options.MaxTickDelta;

        return StepAsync(simDelta, cancellationToken);
    }

    /// <summary>
    /// Advances the world by exactly <paramref name="simDelta"/>, running or not.
    /// Deterministic: used by tests, instructor "step" and fast-forward.
    /// </summary>
    public async Task StepAsync(TimeSpan simDelta, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            SimTime += simDelta;
            var context = new SimulationContext(World, SimTime, simDelta);

            foreach (var system in _systems)
                system.Update(context);

            foreach (var (visibility, source, payload) in context.Emitted)
                await AppendLockedAsync(visibility, source, payload, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ticks in real time until cancelled. The engine starts paused.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.TickInterval);
        var stopwatch = Stopwatch.StartNew();
        var last = stopwatch.Elapsed;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var now = stopwatch.Elapsed;
                try
                {
                    await TickAsync(now - last, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A faulty system must not kill the session; log and keep the clock running.
                    _logger.LogError(ex, "Simulation tick failed at {SimTime}", SimTime);
                }
                last = now;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<SimEvent> AppendLockedAsync(
        EventVisibility visibility, string source, DomainEvent payload, CancellationToken cancellationToken)
    {
        var appended = await _store.AppendAsync(
            new SimEventDraft(SessionId, SimTime, visibility, source, payload), cancellationToken);
        World.Apply(appended);
        return appended;
    }
}

/// <summary>Adds events to the current session's stream, stamped with the current simulation time.</summary>
public interface IEventPublisher
{
    Guid SessionId { get; }

    Task<SimEvent> PublishAsync(
        DomainEvent payload, EventVisibility visibility, string source, CancellationToken cancellationToken = default);
}
