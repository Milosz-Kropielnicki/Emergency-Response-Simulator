using Microsoft.Extensions.Configuration;

namespace Emergency_Response_Simulator.Data;

/// <summary>
/// Finds machine-specific settings for tools and tests that do not run inside the app host.
/// Looks for <c>appsettings.Local.json</c> (git-ignored, written by database/Setup-Database.ps1)
/// in the current and base directories and their parents; environment variables override it.
/// </summary>
public static class ErsConfiguration
{
    public const string LocalSettingsFileName = "appsettings.Local.json";

    public static IConfiguration Load()
    {
        var builder = new ConfigurationBuilder();
        if (FindLocalSettings() is { } path)
            builder.AddJsonFile(path, optional: true);
        return builder.AddEnvironmentVariables().Build();
    }

    public static string? FindLocalSettings()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, LocalSettingsFileName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }
}
