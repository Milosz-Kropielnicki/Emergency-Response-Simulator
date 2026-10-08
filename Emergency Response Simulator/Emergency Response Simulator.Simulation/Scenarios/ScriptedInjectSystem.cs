using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Simulation.Engine;

namespace Emergency_Response_Simulator.Simulation.Scenarios;

/// <summary>
/// Plays a scenario's timed injects (Design Document §20): at each offset from the start of the session
/// it emits the inject's events. Injects may be ground truth (a fire starts) or information reaching
/// command (a call, a report), and are written as functions of the world so they can refer to real units.
/// </summary>
public sealed class ScriptedInjectSystem(string scenarioName, IReadOnlyList<Inject> injects) : ISimulationSystem
{
    private readonly HashSet<Inject> _fired = [];
    private DateTimeOffset? _start;

    public string ScenarioName { get; } = scenarioName;

    public int Order => -10; // before systems that react to what the injects start

    public void Update(SimulationContext context)
    {
        _start ??= context.SimTime - context.Delta;
        var elapsed = context.SimTime - _start.Value;

        foreach (var inject in injects)
        {
            if (elapsed < inject.At || !_fired.Add(inject)) continue;

            foreach (var (visibility, payload) in inject.Produce(context))
                context.Emit(visibility, payload, EventSources.Scenario);
        }
    }
}

/// <param name="At">Time after the session starts.</param>
/// <param name="Description">What the instructor sees in the scenario script.</param>
public sealed record Inject(
    TimeSpan At,
    string Description,
    Func<SimulationContext, IEnumerable<(EventVisibility Visibility, DomainEvent Payload)>> Produce)
{
    public static (EventVisibility, DomainEvent) Truth(DomainEvent payload) => (EventVisibility.Truth, payload);

    public static (EventVisibility, DomainEvent) Perceived(DomainEvent payload) => (EventVisibility.Perceived, payload);
}
