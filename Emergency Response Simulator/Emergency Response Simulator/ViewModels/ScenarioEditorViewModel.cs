using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Microsoft.Win32;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>One setting of an inject in a step, with the template's label and choices.</summary>
public sealed partial class ParameterEntry(InjectParameter parameter, string value) : ObservableObject
{
    public InjectParameter Parameter { get; } = parameter;
    public string Label => Parameter.Label;
    public IReadOnlyList<string>? Choices => Parameter.Choices;
    public bool IsChoice => Parameter.Kind == InjectParameterKind.Choice;
    [ObservableProperty] private string _value = value;
}

/// <summary>One inject a step fires: a template and its settings.</summary>
public sealed partial class ActionEntry : ObservableObject
{
    private readonly ObservableCollection<ActionEntry> _parent;

    public ActionEntry(ObservableCollection<ActionEntry> parent, ScenarioAction action)
    {
        _parent = parent;
        _template = InjectTemplates.All.FirstOrDefault(t => t.Key == action.Template) ?? InjectTemplates.All[0];
        Fill(action.Values);
    }

    public IReadOnlyList<InjectTemplate> Templates => InjectTemplates.All;
    [ObservableProperty] private InjectTemplate _template;
    public ObservableCollection<ParameterEntry> Parameters { get; } = [];

    partial void OnTemplateChanged(InjectTemplate value) => Fill(null);

    private void Fill(IReadOnlyDictionary<string, string>? values)
    {
        Parameters.Clear();
        foreach (var p in Template.Parameters) Parameters.Add(new ParameterEntry(p, values?.GetValueOrDefault(p.Key) ?? p.Default));
    }

    [RelayCommand]
    private void Remove() => _parent.Remove(this);

    /// <summary>Only the settings changed from the template's defaults are saved.</summary>
    public ScenarioAction ToModel() => new(Template.Key,
        Parameters.Where(p => p.Value != p.Parameter.Default).ToDictionary(p => p.Parameter.Key, p => p.Value));
}

/// <summary>A step of the scenario being edited.</summary>
public sealed partial class StepEntry : ObservableObject
{
    public StepEntry(ScenarioStep step)
    {
        (_id, _name, _optional) = (step.Id, step.Name, step.Optional);
        var t = step.Trigger;
        (_kind, _minutes, _stepId, _outcome, _condition) = (t.Kind, F(t.Minutes), t.StepId ?? "", t.Outcome, t.Condition);
        (_value, _text, _window, _negate) = (F(t.Value), t.Text ?? "", t.WindowMinutes is { } w ? F(w) : "", t.Negate);
        foreach (var action in step.Actions) Actions.Add(new ActionEntry(Actions, action));
    }

    [ObservableProperty] private string _id;
    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _optional;
    [ObservableProperty] private TriggerKind _kind;
    [ObservableProperty] private string _minutes;
    [ObservableProperty] private string _stepId;
    [ObservableProperty] private StepOutcome _outcome;
    [ObservableProperty] private ConditionKind _condition;
    [ObservableProperty] private string _value;
    [ObservableProperty] private string _text;
    [ObservableProperty] private string _window;
    [ObservableProperty] private bool _negate;
    public ObservableCollection<ActionEntry> Actions { get; } = [];

    public bool IsAfterStep => Kind == TriggerKind.AfterStep;
    public bool IsCondition => Kind == TriggerKind.WhenCondition;
    partial void OnKindChanged(TriggerKind value)
    {
        OnPropertyChanged(nameof(IsAfterStep));
        OnPropertyChanged(nameof(IsCondition));
    }

    public string Summary => $"{Name} · {ScenarioDesign.Describe(Trigger())}" + (Optional ? " · optional" : "");

    public void Touch() => OnPropertyChanged(nameof(Summary));

    [RelayCommand]
    private void AddAction() => Actions.Add(new ActionEntry(Actions, new ScenarioAction("call")));

    private ScenarioTrigger Trigger() => new(Kind, D(Minutes), Kind == TriggerKind.AfterStep ? StepId : null, Outcome, Condition, D(Value, 1),
        string.IsNullOrWhiteSpace(Text) ? null : Text.Trim(), double.TryParse(Window, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : null, Negate);

    public ScenarioStep ToModel() => new(Id.Trim(), Name.Trim(), Trigger(), [.. Actions.Select(a => a.ToModel())], Optional);

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static double D(string text, double fallback = 0) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
}

/// <summary>
/// The scenario editor (Phase 19): design a scenario as steps — each a trigger (a time; after another step goes off or
/// expires; a condition within a window) and the injects it fires — with its place, weather and training settings; check
/// it, save it to the library (any session can then open on it) or to a file, import historical incidents as one, and load
/// it into the running session. Also the live training controls: fixed or adaptive difficulty, the level, random events.
/// </summary>
public sealed partial class ScenarioEditorViewModel(IInstructorService console) : ObservableObject
{
    public ObservableCollection<ScenarioDefinition> Library { get; } = [];
    [ObservableProperty] private ScenarioDefinition? _selectedLibrary;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _latitude = "53.344";
    [ObservableProperty] private string _longitude = "-6.26";

    public IReadOnlyList<WeatherSource> WeatherSources { get; } = Enum.GetValues<WeatherSource>();
    [ObservableProperty] private WeatherSource _weatherSource;
    [ObservableProperty] private string _windFrom = "270";
    [ObservableProperty] private string _windSpeed = "4";
    [ObservableProperty] private string _temperature = "12";
    [ObservableProperty] private string _humidity = "0.75";
    [ObservableProperty] private string _historicalAt = "";

    public IReadOnlyList<DifficultyMode> Modes { get; } = Enum.GetValues<DifficultyMode>();
    public IReadOnlyList<int> Levels { get; } = [1, 2, 3, 4, 5];
    [ObservableProperty] private DifficultyMode _mode;
    [ObservableProperty] private int _level = 3;
    [ObservableProperty] private bool _randomEvents;
    [ObservableProperty] private string _meanMinutes = "20";
    [ObservableProperty] private bool _randomWeather = true;
    [ObservableProperty] private bool _randomEquipment = true;
    [ObservableProperty] private bool _randomIncidents = true;

    public ObservableCollection<StepEntry> Steps { get; } = [];
    [ObservableProperty] private StepEntry? _selectedStep;
    public ObservableCollection<string> StepIds { get; } = [];

    public IReadOnlyList<TriggerKind> TriggerKinds { get; } = Enum.GetValues<TriggerKind>();
    public IReadOnlyList<StepOutcome> Outcomes { get; } = Enum.GetValues<StepOutcome>();
    public IReadOnlyList<ConditionKind> Conditions { get; } = Enum.GetValues<ConditionKind>();

    public ObservableCollection<string> Problems { get; } = [];
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _trainingStatus = "";

    partial void OnSelectedLibraryChanged(ScenarioDefinition? value)
    {
        if (value is not null) Load(value.Clone());
    }

    partial void OnSelectedStepChanged(StepEntry? value)
    {
        StepIds.Clear();
        foreach (var id in Steps.Where(s => s != value).Select(s => s.Id)) StepIds.Add(id);
    }

    public async Task OpenAsync()
    {
        RefreshLibrary();
        if (Steps.Count == 0) New();
        await RefreshTrainingAsync();
    }

    private void RefreshLibrary()
    {
        Library.Clear();
        foreach (var scenario in ScenarioLibrary.All()) Library.Add(scenario);
    }

    // ---- Editing ----

    [RelayCommand]
    private void New() => Load(new ScenarioDefinition
    {
        Name = "New scenario",
        Steps = [new ScenarioStep("s1", "First call", new ScenarioTrigger(TriggerKind.AtTime, 1), [new ScenarioAction("call")])],
    });

    [RelayCommand]
    private void AddStep()
    {
        var number = Steps.Count + 1;
        while (Steps.Any(s => s.Id == $"s{number}")) number++;
        var step = new StepEntry(new ScenarioStep($"s{number}", $"Step {number}", new ScenarioTrigger(TriggerKind.AtTime, 5), [new ScenarioAction("call")]));
        Steps.Add(step);
        SelectedStep = step;
    }

    [RelayCommand]
    private void RemoveStep()
    {
        if (SelectedStep is { } step) Steps.Remove(step);
        SelectedStep = Steps.FirstOrDefault();
    }

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(1);

    private void Move(int by)
    {
        if (SelectedStep is not { } step) return;
        var index = Steps.IndexOf(step);
        if (index + by < 0 || index + by >= Steps.Count) return;
        Steps.Move(index, index + by);
    }

    private void Load(ScenarioDefinition scenario)
    {
        (Name, Description, Author, Key) = (scenario.Name, scenario.Description, scenario.Author ?? "", scenario.Key);
        (Latitude, Longitude) = (F(scenario.Centre.Latitude), F(scenario.Centre.Longitude));
        var w = scenario.Weather;
        (WeatherSource, WindFrom, WindSpeed, Temperature, Humidity) = (w.Source, F(w.WindFromDegrees), F(w.WindSpeedMps), F(w.TemperatureC), F(w.RelativeHumidity));
        HistoricalAt = w.HistoricalAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";
        var t = scenario.Training;
        (Mode, Level, RandomEvents, MeanMinutes) = (t.Mode, t.Level, t.RandomEvents, F(t.MeanMinutes));
        (RandomWeather, RandomEquipment, RandomIncidents) = (t.RandomWeather, t.RandomEquipment, t.RandomIncidents);
        Steps.Clear();
        foreach (var step in scenario.Steps) Steps.Add(new StepEntry(step));
        SelectedStep = Steps.FirstOrDefault();
        Validate();
    }

    /// <summary>The scenario as edited.</summary>
    public ScenarioDefinition ToModel() => new()
    {
        Key = Key.Trim(), Name = Name.Trim(), Description = Description.Trim(), Author = string.IsNullOrWhiteSpace(Author) ? null : Author.Trim(),
        Centre = new GeoPoint(D(Latitude, 53.344), D(Longitude, -6.26)),
        Weather = new ScenarioWeather(D(WindFrom, 270), D(WindSpeed, 4), D(Temperature, 12), D(Humidity, 0.75), WeatherSource,
            DateTimeOffset.TryParse(HistoricalAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when : null),
        Training = Training(),
        Steps = [.. Steps.Select(s => s.ToModel())],
    };

    private TrainingSettings Training() =>
        new(Mode, Level, RandomEvents, D(MeanMinutes, 20), RandomWeather, RandomEquipment, RandomIncidents);

    [RelayCommand]
    private void Validate()
    {
        foreach (var step in Steps) step.Touch();
        Problems.Clear();
        foreach (var problem in ScenarioDesign.Validate(ToModel(), InjectTemplates.All)) Problems.Add(problem);
        Status = Problems.Count == 0 ? "No problems: ready to save or run." : $"{Problems.Count} problem(s) to fix.";
    }

    // ---- Library, files, import ----

    [RelayCommand]
    private void SaveToLibrary()
    {
        Validate();
        if (Problems.Count > 0) return;
        var scenario = ToModel();
        var path = ScenarioLibrary.Save(scenario);
        Key = scenario.Key;
        RefreshLibrary();
        Status = $"Saved to the library ({path}). New sessions can open on it.";
    }

    [RelayCommand]
    private void Export()
    {
        Validate();
        var dialog = new SaveFileDialog { Filter = "Scenario (*.json)|*.json", FileName = $"{ScenarioLibrary.KeyFor(Name)}.json", AddExtension = true };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, ToModel().ToJson());
        Status = $"Saved to {dialog.FileName}";
    }

    [RelayCommand]
    private void Import()
    {
        var dialog = new OpenFileDialog { Filter = "Scenario (*.json)|*.json|Historical incidents (*.csv)|*.csv" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (dialog.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                var (scenario, skipped) = HistoricalIncidentImporter.FromCsv(File.ReadAllText(dialog.FileName), Path.GetFileNameWithoutExtension(dialog.FileName));
                Load(scenario);
                Status = $"Imported {scenario.Steps.Count} historical incident(s)" + (skipped.Count > 0 ? $"; {skipped.Count} row(s) skipped: {skipped[0]}" : "");
            }
            else
            {
                Load(ScenarioDefinition.FromJson(File.ReadAllText(dialog.FileName)));
                Status = $"Opened {dialog.FileName}";
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException)
        {
            Status = $"Couldn't open it: {ex.Message}";
        }
    }

    // ---- The running session ----

    [RelayCommand]
    private async Task RunAsync()
    {
        Validate();
        if (Problems.Count > 0) return;
        var result = await console.LoadScenarioAsync(ToModel());
        Status = result.Succeeded ? result.Detail ?? "Loaded" : result.Error ?? "Refused";
        await RefreshTrainingAsync();
    }

    [RelayCommand]
    private async Task ApplyTrainingAsync()
    {
        var result = await console.SetTrainingAsync(Training());
        Status = result.Succeeded ? result.Detail ?? "Training set" : result.Error ?? "Refused";
        await RefreshTrainingAsync();
    }

    [RelayCommand]
    private async Task RefreshTrainingAsync()
    {
        try
        {
            var t = await console.GetTrainingAsync();
            TrainingStatus = $"Now: {t.Mode.ToString().ToLowerInvariant()} difficulty, level {t.Level}" +
                             (t.LastScore is { } score ? $", last coping score {score} at {t.LastAssessedAt?.ToLocalTime():HH:mm}" : "") +
                             (t.RandomEvents ? $"; random events about every {t.MeanMinutes:0} min" : "; no random events") +
                             (t.History.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, t.History.TakeLast(6)) : "");
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            TrainingStatus = "No session to read.";
        }
    }

    private static string F(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);

    private static double D(string text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
}
