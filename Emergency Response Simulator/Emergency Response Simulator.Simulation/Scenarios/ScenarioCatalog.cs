using Emergency_Response_Simulator.Simulation.Remote;

namespace Emergency_Response_Simulator.Simulation.Scenarios;

/// <summary>
/// The scenarios a session can be opened on, and the <c>Simulation:Scenario</c> setting each needs: the scripted ones, free
/// play, and (Phase 19) every designed scenario in the library.
/// </summary>
public static class ScenarioCatalog
{
    public const string FreePlay = "free-play";
    public const string FreePlayName = "Free play";

    private static readonly IReadOnlyList<ScenarioInfo> Scripted =
    [
        new(BarrowStreetScenario.Key, BarrowStreetScenario.Name,
            "Fire and explosion in a warehouse beside a chlorine store, near Grand Canal Dock. Scripted calls, a wind shift, a collapse."),
        new(FreePlay, FreePlayName, "The roster, hospitals and agencies with no scripted incident: the instructor or the world brings the events."),
    ];

    /// <summary>The catalogue now: the scripted scenarios, then the library's (read afresh, so a newly saved one shows).</summary>
    public static IReadOnlyList<ScenarioInfo> All =>
        [.. Scripted, .. ScenarioLibrary.All().Select(s => new ScenarioInfo(ScenarioLibrary.Prefix + s.Key, s.Name, s.Description))];

    public static ScenarioInfo? Find(string? key) => All.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The engine setting for a catalogue entry (free play runs no script).</summary>
    public static string SettingFor(ScenarioInfo scenario) => scenario.Key == FreePlay ? "" : scenario.Key;
}
