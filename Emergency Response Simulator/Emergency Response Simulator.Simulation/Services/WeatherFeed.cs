using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Emergency_Response_Simulator.Core.Geo;

namespace Emergency_Response_Simulator.Simulation.Services;

/// <summary>Weather from outside the simulation (Phase 19: real-world data).</summary>
public sealed record WeatherReading(double WindFromDegrees, double WindSpeedMps, double TemperatureC, double RelativeHumidity, DateTimeOffset At, string Source);

public interface IWeatherFeed
{
    /// <summary>The weather at a place now; null if the service can't be reached.</summary>
    Task<WeatherReading?> CurrentAsync(GeoPoint at, CancellationToken cancellationToken = default);

    /// <summary>The weather at a place at a past hour; null if the archive can't be reached or has nothing.</summary>
    Task<WeatherReading?> HistoricalAsync(GeoPoint at, DateTimeOffset when, CancellationToken cancellationToken = default);
}

/// <summary>
/// Open-Meteo (open-meteo.com): free, no key, global, with an archive back decades (ERA5). Current conditions from the
/// forecast API, past hours from the archive API. Wind in m/s, humidity as a fraction. Any failure is null: the
/// scenario's own weather stands.
/// </summary>
public sealed class OpenMeteoWeatherFeed(HttpClient http) : IWeatherFeed
{
    public const string ForecastUrl = "https://api.open-meteo.com/v1/forecast";
    public const string ArchiveUrl = "https://archive-api.open-meteo.com/v1/archive";
    private const string Fields = "temperature_2m,relative_humidity_2m,wind_speed_10m,wind_direction_10m";

    public async Task<WeatherReading?> CurrentAsync(GeoPoint at, CancellationToken cancellationToken = default)
    {
        var url = $"{ForecastUrl}?latitude={F(at.Latitude)}&longitude={F(at.Longitude)}&current={Fields}&wind_speed_unit=ms&timezone=UTC";
        try
        {
            using var document = await http.GetFromJsonAsync<JsonDocument>(url, cancellationToken);
            if (document?.RootElement.TryGetProperty("current", out var current) != true) return null;
            return Reading(current.GetProperty("wind_direction_10m").GetDouble(), current.GetProperty("wind_speed_10m").GetDouble(),
                current.GetProperty("temperature_2m").GetDouble(), current.GetProperty("relative_humidity_2m").GetDouble(),
                DateTimeOffset.Parse(current.GetProperty("time").GetString()! + "Z", CultureInfo.InvariantCulture), "Open-Meteo (live)");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public async Task<WeatherReading?> HistoricalAsync(GeoPoint at, DateTimeOffset when, CancellationToken cancellationToken = default)
    {
        var day = when.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var url = $"{ArchiveUrl}?latitude={F(at.Latitude)}&longitude={F(at.Longitude)}&start_date={day}&end_date={day}&hourly={Fields}&wind_speed_unit=ms&timezone=UTC";
        try
        {
            using var document = await http.GetFromJsonAsync<JsonDocument>(url, cancellationToken);
            if (document?.RootElement.TryGetProperty("hourly", out var hourly) != true) return null;
            var hour = Math.Clamp(when.UtcDateTime.Hour, 0, hourly.GetProperty("time").GetArrayLength() - 1);
            double Value(string field) => hourly.GetProperty(field)[hour].GetDouble();
            return Reading(Value("wind_direction_10m"), Value("wind_speed_10m"), Value("temperature_2m"), Value("relative_humidity_2m"),
                new DateTimeOffset(when.UtcDateTime.Date.AddHours(hour), TimeSpan.Zero), $"Open-Meteo archive ({day} {hour:D2}:00 UTC)");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException
                                       or IndexOutOfRangeException or FormatException)
        {
            return null;
        }
    }

    private static WeatherReading Reading(double direction, double speed, double temperature, double humidity, DateTimeOffset at, string source) =>
        new(direction, speed, temperature, Math.Clamp(humidity / 100, 0, 1), at, source);

    private static string F(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
