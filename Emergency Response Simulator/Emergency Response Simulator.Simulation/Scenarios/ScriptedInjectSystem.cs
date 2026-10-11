using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;

namespace Emergency_Response_Simulator.Simulation.Scenarios;

/// <summary>
/// Plays a scenario's timed injects (Design Document §20): at each offset from the start of the session
/// it emits the inject's events. Injects may be ground truth (a fire starts) or information reaching
/// command (a call, a report), and are written as functions of the world so they can refer to real units.
/// The instructor can run the script live (Phase 11 M4): fire an inject now, hold it, release it, skip it, or fire an
/// ad-hoc one. Every inject that goes off is recorded (<see cref="InjectFired"/>, ground truth) for the review.
/// </summary>
public sealed class ScriptedInjectSystem : ISimulationSystem
{
    private readonly List<Inject> _injects;
    private readonly Dictionary<int, (InjectStatus Status, DateTimeOffset? FiredAt)> _state = [];
    private DateTimeOffset? _start;

    public ScriptedInjectSystem(string scenarioName, IReadOnlyList<Inject> injects)
    {
        ScenarioName = scenarioName;
        _injects = [.. injects];
        for (var number = 1; number <= _injects.Count; number++)
            _state[number] = (InjectStatus.Pending, null);
    }

    public string ScenarioName { get; }

    public int Order => -10; // before systems that react to what the injects start

    public void Update(SimulationContext context)
    {
        _start ??= context.SimTime - context.Delta;
        var elapsed = context.SimTime - _start.Value;

        for (var number = 1; number <= _injects.Count; number++)
        {
            var inject = _injects[number - 1];
            if (_state[number].Status != InjectStatus.Pending || elapsed < inject.At) continue;
            if (inject.Until is { } until && elapsed > until)
            {
                _state[number] = (InjectStatus.Expired, null); // window passed without the condition being met
                continue;
            }
            if (inject.Condition is { } condition && !condition(context)) continue;
            Fire(context, number, early: false);
        }
    }

    private void Fire(SimulationContext context, int number, bool early)
    {
        var inject = _injects[number - 1];
        _state[number] = (InjectStatus.Fired, context.SimTime);
        context.EmitTruth(new InjectFired(number, inject.Description, early), early ? EventSources.Instructor : EventSources.Scenario);
        foreach (var (visibility, payload) in inject.Produce(context))
            context.Emit(visibility, payload, EventSources.Scenario);
    }

    // ---- The instructor's controls (Phase 11 M4); run through SimulationEngine.ApplyAsync ----

    /// <summary>The script as the console lists it, in time order.</summary>
    public IReadOnlyList<InjectInfo> Script() =>
        [.. _injects.Select((inject, i) => new InjectInfo(i + 1, inject.At, inject.Description, _state[i + 1].Status, inject.Condition is not null, _state[i + 1].FiredAt))
            .OrderBy(i => i.At).ThenBy(i => i.Number)];

    /// <summary>Fires an inject now, whatever its time or condition. Null when done, else why not.</summary>
    public string? FireNow(SimulationContext context, int number)
    {
        if ((Refuse(number, InjectStatus.Fired, "has already gone off") ?? Refuse(number, InjectStatus.Skipped, "was skipped")) is { } refused) return refused;
        _start ??= context.SimTime;
        Fire(context, number, early: context.SimTime - _start.Value < _injects[number - 1].At);
        return null;
    }

    public string? Hold(SimulationContext context, int number)
    {
        if (Unknown(number) is { } unknown) return unknown;
        if (_state[number].Status != InjectStatus.Pending) return $"Inject {number} is {_state[number].Status.ToString().ToLowerInvariant()}: only a pending inject can be held.";
        _state[number] = (InjectStatus.Held, null);
        context.EmitTruth(new InjectHeld(number, _injects[number - 1].Description), EventSources.Instructor);
        return null;
    }

    /// <summary>Back on the script: if its time has passed it goes off at the next tick (its condition permitting).</summary>
    public string? Release(SimulationContext context, int number)
    {
        if (Unknown(number) is { } unknown) return unknown;
        if (_state[number].Status != InjectStatus.Held) return $"Inject {number} isn't held.";
        _state[number] = (InjectStatus.Pending, null);
        context.EmitTruth(new InjectReleased(number, _injects[number - 1].Description), EventSources.Instructor);
        return null;
    }

    public string? Skip(SimulationContext context, int number)
    {
        if ((Refuse(number, InjectStatus.Fired, "has already gone off") ?? Refuse(number, InjectStatus.Skipped, "is already skipped")) is { } refused) return refused;
        _state[number] = (InjectStatus.Skipped, null);
        context.EmitTruth(new InjectSkipped(number, _injects[number - 1].Description), EventSources.Instructor);
        return null;
    }

    /// <summary>An inject from the template library, fired now.</summary>
    public static void FireAdHoc(SimulationContext context, string template, Inject inject)
    {
        context.EmitTruth(new InjectFired(0, inject.Description, Template: template), EventSources.Instructor);
        foreach (var (visibility, payload) in inject.Produce(context))
            context.Emit(visibility, payload, EventSources.Instructor);
    }

    private string? Unknown(int number) => _state.ContainsKey(number) ? null : $"There is no inject {number} in {ScenarioName}.";

    private string? Refuse(int number, InjectStatus status, string why) =>
        Unknown(number) ?? (_state[number].Status == status ? $"Inject {number} {why}." : null);
}

/// <param name="At">Time after the session starts.</param>
/// <param name="Description">What the instructor sees in the scenario script.</param>
/// <param name="Condition">If set, the inject waits after <paramref name="At"/> until this holds.</param>
/// <param name="Until">If set with a condition, the inject is dropped when the condition hasn't held by then.</param>
public sealed record Inject(
    TimeSpan At,
    string Description,
    Func<SimulationContext, IEnumerable<(EventVisibility Visibility, DomainEvent Payload)>> Produce,
    Func<SimulationContext, bool>? Condition = null,
    TimeSpan? Until = null)
{
    public static (EventVisibility, DomainEvent) Truth(DomainEvent payload) => (EventVisibility.Truth, payload);

    public static (EventVisibility, DomainEvent) Perceived(DomainEvent payload) => (EventVisibility.Perceived, payload);
}
