using System.Text.Json;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Scenarios;

/// <summary>
/// The scenario library (Phase 19): the designed scenarios shipped with the simulator, and those saved from the editor in
/// the user's library folder (<c>ERS_SCENARIOS</c>, or <c>%LOCALAPPDATA%\EmergencyResponseSimulator\scenarios</c>). A
/// saved scenario with a shipped one's key replaces it. Each appears in the scenario catalogue as <c>library:&lt;key&gt;</c>.
/// </summary>
public static class ScenarioLibrary
{
    public const string Prefix = "library:";

    public static string Folder =>
        Environment.GetEnvironmentVariable("ERS_SCENARIOS") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmergencyResponseSimulator", "scenarios");

    private static readonly Lazy<IReadOnlyList<ScenarioDefinition>> Shipped = new(() =>
    {
        var assembly = typeof(ScenarioLibrary).Assembly;
        return [.. assembly.GetManifestResourceNames().Where(n => n.EndsWith(".scenario.json", StringComparison.Ordinal)).Order()
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return ScenarioDefinition.FromJson(reader.ReadToEnd());
            })];
    });

    /// <summary>The scenarios shipped with the simulator.</summary>
    public static IReadOnlyList<ScenarioDefinition> BuiltIn => Shipped.Value;

    /// <summary>Everything in the library: shipped, then saved (a saved one replacing a shipped one with its key). Unreadable files are skipped.</summary>
    public static IReadOnlyList<ScenarioDefinition> All()
    {
        var byKey = BuiltIn.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(Folder))
            foreach (var file in Directory.EnumerateFiles(Folder, "*.json").Order())
            {
                try
                {
                    var scenario = ScenarioDefinition.FromJson(File.ReadAllText(file));
                    if (!string.IsNullOrWhiteSpace(scenario.Key)) byKey[scenario.Key] = scenario;
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    // A broken file stays out of the catalogue rather than taking it down.
                }
            }
        return [.. byKey.Values.OrderBy(s => s.Name)];
    }

    /// <summary>A library scenario by its key, with or without the catalogue prefix.</summary>
    public static ScenarioDefinition? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var bare = key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? key[Prefix.Length..] : key;
        return All().FirstOrDefault(s => string.Equals(s.Key, bare, StringComparison.OrdinalIgnoreCase))?.Clone();
    }

    /// <summary>Saves a scenario to the user's library (its key from its name if it has none); returns the file written.</summary>
    public static string Save(ScenarioDefinition scenario)
    {
        if (string.IsNullOrWhiteSpace(scenario.Key)) scenario.Key = KeyFor(scenario.Name);
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, scenario.Key + ".json");
        File.WriteAllText(path, scenario.ToJson());
        return path;
    }

    /// <summary>"Night shift: chlorine at the docks" → "night-shift-chlorine-at-the-docks".</summary>
    public static string KeyFor(string name)
    {
        var key = new string([.. name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')]);
        while (key.Contains("--", StringComparison.Ordinal)) key = key.Replace("--", "-", StringComparison.Ordinal);
        key = key.Trim('-');
        return key.Length == 0 ? "scenario" : key;
    }
}
