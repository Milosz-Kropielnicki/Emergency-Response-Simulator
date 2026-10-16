using System.Text.Json;
using System.Text.Json.Serialization;
using Emergency_Response_Simulator.Core.Geo;

namespace Emergency_Response_Simulator.Core.Model;

// ---- Phase 19: scenarios as data — designed in the editor, saved to the library, run in any session ----

public enum TriggerKind
{
    /// <summary>At a time after the session starts.</summary>
    AtTime,

    /// <summary>A while after another step went off, or expired without its condition being met (a branch).</summary>
    AfterStep,

    /// <summary>When something is true in the world, from a time, within a window (or it expires, and its branch runs).</summary>
    WhenCondition,
}

public enum StepOutcome
{
    Fired,
    Expired,
}

/// <summary>What a conditional step waits for (Phase 19). Most count something, compared with the trigger's value.</summary>
public enum ConditionKind
{
    /// <summary>Command has opened at least <c>Value</c> incidents.</summary>
    IncidentsOpened,

    /// <summary>At least <c>Value</c> units are on scene or working.</summary>
    UnitsOnScene,

    /// <summary>At least <c>Value</c> people are really hurt.</summary>
    CasualtiesAtLeast,

    EvacuationDeclared,
    ShelterInPlaceDeclared,

    /// <summary>A road closure, hot zone or fire exclusion zone is declared.</summary>
    ClosureDeclared,

    /// <summary>A major emergency is declared (the EOC at partial activation or above).</summary>
    MajorEmergency,

    /// <summary>Command has notified someone whose name contains the trigger's text.</summary>
    Notified,

    MaydayDeclared,

    /// <summary>At least <c>Value</c> 999 calls are waiting to be answered.</summary>
    CallsWaiting,
}

/// <summary>How difficulty is set (Phase 19).</summary>
public enum DifficultyMode
{
    /// <summary>The instructor's level, unchanged.</summary>
    Fixed,

    /// <summary>The level follows how the trainees are coping, measured every few minutes.</summary>
    Adaptive,
}

/// <summary>When a step goes off.</summary>
/// <param name="Minutes">AtTime: after the start. AfterStep: after the other step. WhenCondition: the earliest the condition counts.</param>
/// <param name="WindowMinutes">WhenCondition: how long after <paramref name="Minutes"/> to wait before giving up (null: for ever).</param>
/// <param name="Negate">WhenCondition: wait for the condition to be false instead.</param>
public sealed record ScenarioTrigger(
    TriggerKind Kind,
    double Minutes = 0,
    string? StepId = null,
    StepOutcome Outcome = StepOutcome.Fired,
    ConditionKind Condition = ConditionKind.IncidentsOpened,
    double Value = 1,
    string? Text = null,
    double? WindowMinutes = null,
    bool Negate = false);

/// <summary>One thing a step makes happen: an inject from the template library with its values (defaults for the rest).</summary>
public sealed record ScenarioAction(string Template, IReadOnlyDictionary<string, string>? Values = null);

/// <param name="Optional">
/// Pressure the scenario can do without: held back while adaptive difficulty finds the trainees struggling (level 1–2).
/// </param>
public sealed record ScenarioStep(string Id, string Name, ScenarioTrigger Trigger, IReadOnlyList<ScenarioAction> Actions, bool Optional = false);

/// <summary>Where the starting weather comes from.</summary>
public enum WeatherSource
{
    /// <summary>As the scenario sets it.</summary>
    Fixed,

    /// <summary>What it is outside now, from a weather service (falling back to the scenario's).</summary>
    Live,

    /// <summary>What it was on a date and hour, from a weather archive (falling back to the scenario's).</summary>
    Historical,
}

public sealed record ScenarioWeather(double WindFromDegrees = 270, double WindSpeedMps = 4, double TemperatureC = 12, double RelativeHumidity = 0.75,
    WeatherSource Source = WeatherSource.Fixed, DateTimeOffset? HistoricalAt = null);

/// <summary>How a scenario wants difficulty and random events run (the instructor can change both live).</summary>
/// <param name="Level">1 (gentle) to 5 (hard).</param>
/// <param name="MeanMinutes">Average time between random events at level 3.</param>
public sealed record TrainingSettings(DifficultyMode Mode = DifficultyMode.Fixed, int Level = 3, bool RandomEvents = false, double MeanMinutes = 20,
    bool RandomWeather = true, bool RandomEquipment = true, bool RandomIncidents = true);

/// <summary>A scenario as data (Phase 19): what the editor saves and any session can load.</summary>
public sealed class ScenarioDefinition
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Author { get; set; }
    public int Version { get; set; } = 1;

    /// <summary>Where it happens: the place injects work out "near the incident" from before there is one.</summary>
    public GeoPoint Centre { get; set; } = new(53.344, -6.26);

    public ScenarioWeather Weather { get; set; } = new();
    public TrainingSettings Training { get; set; } = new();
    public List<ScenarioStep> Steps { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <exception cref="JsonException">Not a scenario.</exception>
    public static ScenarioDefinition FromJson(string json) =>
        JsonSerializer.Deserialize<ScenarioDefinition>(json, Json) ?? throw new JsonException("Empty scenario file.");

    public ScenarioDefinition Clone() => FromJson(ToJson());
}

/// <summary>A training adjustment as the instructor and the review see it.</summary>
public sealed record TrainingStatus(DifficultyMode Mode, int Level, bool RandomEvents, double MeanMinutes, int? LastScore, DateTimeOffset? LastAssessedAt,
    IReadOnlyList<string> History);

/// <summary>Checks a definition before it is saved or run, with the inject templates it may use.</summary>
public static class ScenarioDesign
{
    public static IReadOnlyList<string> Validate(ScenarioDefinition definition, IReadOnlyList<InjectTemplate> templates)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(definition.Name)) problems.Add("The scenario has no name.");
        if (definition.Steps.Count == 0) problems.Add("The scenario has no steps.");
        if (definition.Training.Level is < 1 or > 5) problems.Add("Difficulty runs from 1 to 5.");
        if (definition.Training.RandomEvents && definition.Training.MeanMinutes < 2) problems.Add("Random events can't come more often than every 2 minutes on average.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in definition.Steps)
        {
            var label = $"Step \"{(string.IsNullOrWhiteSpace(step.Name) ? step.Id : step.Name)}\"";
            if (string.IsNullOrWhiteSpace(step.Id)) problems.Add($"{label} has no id.");
            else if (!ids.Add(step.Id)) problems.Add($"Two steps are called {step.Id}.");
            if (step.Actions.Count == 0) problems.Add($"{label} does nothing: add an inject.");
            if (step.Trigger.Minutes < 0) problems.Add($"{label} is timed before the start.");
            if (step.Trigger is { Kind: TriggerKind.WhenCondition, WindowMinutes: <= 0 }) problems.Add($"{label} has an empty window.");
            if (step.Trigger is { Kind: TriggerKind.WhenCondition, Condition: ConditionKind.Notified } && string.IsNullOrWhiteSpace(step.Trigger.Text))
                problems.Add($"{label} waits for a notification but doesn't say to whom.");
            if (step.Trigger is { Kind: TriggerKind.AfterStep, Outcome: StepOutcome.Expired } && definition.Steps.FirstOrDefault(s => s.Id == step.Trigger.StepId) is { } parent
                && parent.Trigger is not { Kind: TriggerKind.WhenCondition, WindowMinutes: not null })
                problems.Add($"{label} follows {parent.Id} expiring, but {parent.Id} has no window to expire in.");
            foreach (var action in step.Actions)
            {
                if (templates.FirstOrDefault(t => t.Key == action.Template) is not { } template)
                {
                    problems.Add($"{label}: there is no inject \"{action.Template}\".");
                    continue;
                }
                foreach (var (key, value) in action.Values ?? new Dictionary<string, string>())
                {
                    var parameter = template.Parameters.FirstOrDefault(p => p.Key == key);
                    if (parameter is null) problems.Add($"{label}: {template.Name} has no setting \"{key}\".");
                    else if (parameter is { Kind: InjectParameterKind.Choice, Choices: { } choices } && !choices.Contains(value))
                        problems.Add($"{label}: {parameter.Label} must be one of {string.Join(", ", choices)}.");
                    else if (parameter.Kind == InjectParameterKind.Number && !double.TryParse(value, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out _))
                        problems.Add($"{label}: {parameter.Label} must be a number.");
                }
            }
        }

        // Every step a step follows exists, and following never goes round in a circle.
        foreach (var step in definition.Steps.Where(s => s.Trigger.Kind == TriggerKind.AfterStep))
        {
            if (!ids.Contains(step.Trigger.StepId ?? ""))
            {
                problems.Add($"Step \"{step.Name}\" follows a step that doesn't exist ({step.Trigger.StepId}).");
                continue;
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { step.Id };
            var current = step;
            while (current?.Trigger.Kind == TriggerKind.AfterStep)
            {
                current = definition.Steps.FirstOrDefault(s => string.Equals(s.Id, current.Trigger.StepId, StringComparison.OrdinalIgnoreCase));
                if (current is null) break;
                if (!seen.Add(current.Id))
                {
                    problems.Add($"Step \"{step.Name}\" waits on itself through {string.Join(" → ", seen)}.");
                    break;
                }
            }
        }
        return problems;
    }

    /// <summary>"at 12 min", "5 min after A fired", "when 3+ units on scene from 10 min, within 15 min".</summary>
    public static string Describe(ScenarioTrigger trigger) => trigger.Kind switch
    {
        TriggerKind.AtTime => $"at {trigger.Minutes:0.#} min",
        TriggerKind.AfterStep => $"{trigger.Minutes:0.#} min after {trigger.StepId} {(trigger.Outcome == StepOutcome.Fired ? "goes off" : "expires")}",
        _ => $"{(trigger.Negate ? "unless" : "when")} {Describe(trigger.Condition, trigger.Value, trigger.Text)} from {trigger.Minutes:0.#} min" +
             (trigger.WindowMinutes is { } window ? $", within {window:0.#} min" : ""),
    };

    public static string Describe(ConditionKind condition, double value, string? text) => condition switch
    {
        ConditionKind.IncidentsOpened => $"{value:0}+ incident(s) opened",
        ConditionKind.UnitsOnScene => $"{value:0}+ unit(s) on scene",
        ConditionKind.CasualtiesAtLeast => $"{value:0}+ casualties",
        ConditionKind.EvacuationDeclared => "an evacuation zone is declared",
        ConditionKind.ShelterInPlaceDeclared => "a shelter-in-place zone is declared",
        ConditionKind.ClosureDeclared => "a closure or hot zone is declared",
        ConditionKind.MajorEmergency => "a major emergency is declared",
        ConditionKind.Notified => $"{text} is notified",
        ConditionKind.MaydayDeclared => "a Mayday is declared",
        _ => $"{value:0}+ 999 calls waiting",
    };
}
