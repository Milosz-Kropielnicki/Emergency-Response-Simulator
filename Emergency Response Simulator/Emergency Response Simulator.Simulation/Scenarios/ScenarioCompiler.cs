using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Scenarios;

/// <summary>
/// Turns a designed scenario (Phase 19) into injects the script runs. Each step becomes one inject: its actions are inject
/// templates; a timed step goes off at its time; a step that follows another waits for that one to go off or expire, then
/// for its delay; a conditional step waits from its time for the condition, and expires at the end of its window — which is
/// what the steps branching off its expiry wait for. The first inject, at the start, announces the scenario and sets its
/// weather and training.
/// </summary>
public static class ScenarioCompiler
{
    /// <param name="firstNumber">The number the script will give the first inject (steps refer to each other by it).</param>
    public static IReadOnlyList<Inject> Compile(ScenarioDefinition definition, int firstNumber)
    {
        var numbers = definition.Steps.Select((step, i) => (step.Id, Number: firstNumber + 1 + i))
            .ToDictionary(s => s.Id, s => s.Number, StringComparer.OrdinalIgnoreCase);
        var injects = new List<Inject> { Setup(definition) };
        foreach (var step in definition.Steps)
        {
            var trigger = step.Trigger;
            var actions = step.Actions.Select(a => InjectTemplates.Create(a.Template, a.Values)
                                                   ?? throw new ArgumentException($"There is no inject \"{a.Template}\".")).ToList();
            var description = $"{step.Name}: {string.Join("; ", actions.Select(a => a.Description))}";
            IEnumerable<(EventVisibility, DomainEvent)> Produce(SimulationContext context) => actions.SelectMany(a => a.Produce(context)).ToList();

            injects.Add(trigger.Kind switch
            {
                TriggerKind.AfterStep => new Inject(TimeSpan.Zero, description, Produce,
                    After: new InjectDependency(numbers[trigger.StepId!], trigger.Outcome == StepOutcome.Fired ? InjectStatus.Fired : InjectStatus.Expired,
                        TimeSpan.FromMinutes(trigger.Minutes)),
                    Optional: step.Optional),
                TriggerKind.WhenCondition => new Inject(TimeSpan.FromMinutes(trigger.Minutes), description, Produce,
                    Condition: context => Holds(trigger, context.World) != trigger.Negate,
                    Until: trigger.WindowMinutes is { } window ? TimeSpan.FromMinutes(trigger.Minutes + window) : null,
                    Optional: step.Optional),
                _ => new Inject(TimeSpan.FromMinutes(trigger.Minutes), description, Produce, Optional: step.Optional),
            });
        }
        return injects;
    }

    /// <summary>The scenario announces itself and its settings, and sets its weather (the met service reports it).</summary>
    private static Inject Setup(ScenarioDefinition definition) =>
        new(TimeSpan.Zero, $"Scenario: {definition.Name}", context =>
        {
            List<(EventVisibility, DomainEvent)> events =
            [
                Inject.Truth(new ScenarioLoaded(definition.Key, definition.Name, definition.Centre, definition.Weather, definition.Training, definition.Steps.Count)),
            ];
            // The scenario's weather; live or historical weather replaces it when (and if) the service answers.
            var w = definition.Weather;
            events.Add(Inject.Truth(new WeatherChanged(w.WindFromDegrees, w.WindSpeedMps, w.TemperatureC, w.RelativeHumidity)));
            events.Add(Inject.Perceived(new WeatherObserved("Met Éireann", definition.Centre, w.WindFromDegrees, w.WindSpeedMps, w.TemperatureC, w.RelativeHumidity)));
            _ = context;
            return events;
        });

    /// <summary>Whether a step's condition holds in the world now (perceived facts where command's actions are what counts).</summary>
    public static bool Holds(ScenarioTrigger trigger, WorldState world) => trigger.Condition switch
    {
        ConditionKind.IncidentsOpened => world.ReportedIncidentLocations.Count >= trigger.Value,
        ConditionKind.UnitsOnScene => world.Units.Values.Count(u => u.OrderedIncidentId is not null
                                                                    && u.Phase is ResponsePhase.OnScene or ResponsePhase.Operating) >= trigger.Value,
        ConditionKind.CasualtiesAtLeast => world.Casualties.Count >= trigger.Value,
        ConditionKind.EvacuationDeclared => world.ProtectiveActions.Values.Any(a => a.Type == ZoneType.EvacuationZone),
        ConditionKind.ShelterInPlaceDeclared => world.ProtectiveActions.Values.Any(a => a.Type == ZoneType.ShelterInPlace),
        ConditionKind.ClosureDeclared => world.DeclaredNoGoAreas.Count > 0,
        ConditionKind.MajorEmergency => world.Interagency.MajorEmergency,
        ConditionKind.Notified => world.Notified.Keys.Any(k => k.Contains(trigger.Text ?? "\0", StringComparison.OrdinalIgnoreCase)),
        ConditionKind.MaydayDeclared => world.MaydayUnits.Count > 0,
        _ => world.Radio.CallQueue.Count >= trigger.Value,
    };
}
