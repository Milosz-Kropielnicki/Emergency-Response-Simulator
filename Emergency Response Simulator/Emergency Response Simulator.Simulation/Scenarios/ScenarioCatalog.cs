using Emergency_Response_Simulator.Simulation.Remote;

namespace Emergency_Response_Simulator.Simulation.Scenarios;

/// <summary>The scenarios a session can be opened on, and the <c>Simulation:Scenario</c> setting each needs.</summary>
public static class ScenarioCatalog
{
    public const string FreePlay = "free-play";

    public static readonly IReadOnlyList<ScenarioInfo> All =
    [
        new(BarrowStreetScenario.Key, BarrowStreetScenario.Name,
            "Fire and explosion in a warehouse beside a chlorine store, near Grand Canal Dock. Scripted calls, a wind shift, a collapse."),
        new(FreePlay, "Free play", "The roster, hospitals and agencies with no scripted incident: the instructor or the world brings the events."),
    ];

    public static ScenarioInfo? Find(string? key) => All.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The engine setting for a catalogue entry (free play runs no script).</summary>
    public static string SettingFor(ScenarioInfo scenario) => scenario.Key == FreePlay ? "" : scenario.Key;
}
