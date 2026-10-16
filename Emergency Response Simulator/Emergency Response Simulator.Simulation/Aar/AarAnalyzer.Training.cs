using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Aar;

/// <summary>
/// The session as training (Phase 19): the scenario that ran, how difficulty moved and why, the random events, and the
/// incidents within the incident — whether the secondary device was made safe or caught responders, how long the shooter
/// went on, how many responders were hurt. The difficulty a team reached is context for every other number in the review.
/// </summary>
public sealed partial class AarAnalyzer
{
    private string? _scenarioName;
    private DifficultyMode _difficultyMode = DifficultyMode.Fixed;
    private int? _startLevel;
    private int _level = 3;
    private readonly List<string> _adjustments = [];
    private readonly List<int> _scores = [];
    private readonly List<string> _randomEvents = [];
    private readonly Dictionary<Guid, (DateTimeOffset At, string What)> _devices = [];
    private readonly Dictionary<Guid, DateTimeOffset> _shooters = [];
    private readonly List<string> _complications = [];
    private readonly List<AarFinding> _trainingFindings = [];
    private int _respondersHurt;

    private void TrackTraining(SimEvent simEvent)
    {
        var at = simEvent.SimTime;
        switch (simEvent.Payload)
        {
            case ScenarioLoaded e:
                _scenarioName = e.Name;
                (_difficultyMode, _level) = (e.Training.Mode, e.Training.Level);
                _startLevel ??= e.Training.Level;
                break;
            case TrainingSettingsChanged e:
                (_difficultyMode, _level) = (e.Settings.Mode, e.Settings.Level);
                _startLevel ??= e.Settings.Level;
                _adjustments.Add($"{Clock(at)} set by the instructor: {e.Settings.Mode.ToString().ToLowerInvariant()}, level {e.Settings.Level}");
                break;
            case DifficultyAssessed e:
                _scores.Add(e.Score);
                break;
            case DifficultyAdjusted e:
                _level = e.To;
                _adjustments.Add($"{Clock(at)} {(e.To > e.From ? "raised" : "lowered")} to {e.To} (score {e.Score}): {e.Reason}");
                break;
            case RandomEventOccurred e:
                _randomEvents.Add($"{Clock(at)} {e.Kind.ToLowerInvariant()}: {e.Description}");
                break;

            case SecondaryDevicePlaced e:
                _devices[e.DeviceId] = (at, e.Description);
                break;
            case SecondaryDeviceRenderedSafe e when _devices.TryGetValue(e.DeviceId, out var safe):
                _complications.Add($"Secondary device ({safe.What}) made safe {Duration(at - safe.At)} after it was left");
                _trainingFindings.Add(new AarFinding(true, $"The secondary device was found and made safe before it went off ({Duration(at - safe.At)})."));
                _devices.Remove(e.DeviceId);
                break;
            case SecondaryDeviceDetonated e when _devices.TryGetValue(e.DeviceId, out var bomb):
                _complications.Add($"Secondary device ({bomb.What}) went off {Duration(at - bomb.At)} after it was left: {e.Hurt} hurt, {e.Responders} responders");
                _trainingFindings.Add(new AarFinding(false, e.Responders > 0
                    ? $"The secondary device went off with {e.Responders} responder(s) still within range: crews weren't pulled back."
                    : $"The secondary device went off and nobody made it safe; {e.Hurt} hurt."));
                _liabilities.Add(new AarLiability(at, "Responders exposed to a secondary device",
                    $"A suspicious device was left at the scene and nobody had it made safe; it went off and hurt {e.Hurt} ({e.Responders} responders)."));
                _devices.Remove(e.DeviceId);
                break;
            case ActiveShooterStarted e:
                _shooters[e.ThreatId] = at;
                break;
            case ActiveShooterNeutralised e when _shooters.Remove(e.ThreatId, out var started):
                _complications.Add($"Active shooter stopped by armed gardaí {Duration(at - started)} after the first shots; {e.Victims} shot");
                _trainingFindings.Add(new AarFinding(at - started <= TimeSpan.FromMinutes(15),
                    $"Armed gardaí stopped the shooter {Duration(at - started)} after the first shots; {e.Victims} people were shot."));
                break;
            case ResponderDown:
                _respondersHurt++;
                break;
            case FirefighterInDistress:
                _respondersHurt++;
                break;
        }
    }

    private AarTraining TrainingSummary()
    {
        var complications = _complications.ToList();
        complications.AddRange(_devices.Values.Select(d => $"Secondary device ({d.What}) still there at the end"));
        complications.AddRange(_shooters.Values.Select(s => $"Active shooter from {Clock(s)}: never stopped by armed gardaí"));
        return new AarTraining(_scenarioName, _difficultyMode, _startLevel ?? 3, _level, [.. _adjustments], [.. _scores], [.. _randomEvents], complications, _respondersHurt);
    }

    private IEnumerable<AarFinding> TrainingFindings()
    {
        foreach (var finding in _trainingFindings) yield return finding;
        foreach (var started in _shooters.Values)
            yield return new AarFinding(false, $"The active shooter from {Clock(started)} was never stopped: no armed response reached the scene.");
        if (_scores.Count > 0)
            yield return new AarFinding(_scores.Average() >= 60,
                $"Coping score averaged {_scores.Average():0} over {_scores.Count} assessment(s); difficulty ended at level {_level}" +
                (_startLevel is { } start && start != _level ? $" (from {start})." : "."));
    }
}
