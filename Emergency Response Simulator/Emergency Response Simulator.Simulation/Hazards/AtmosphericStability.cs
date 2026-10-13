namespace Emergency_Response_Simulator.Simulation.Hazards;

/// <summary>
/// Which Pasquill–Gifford stability class the air is in (Phase 13), which decides how fast a plume mixes out: stable
/// air (E, F) keeps a cloud narrow and dangerous for kilometres, unstable air (A, B) breaks it up within a few hundred
/// metres. It matters more to the answer than the release rate does.
/// <para>
/// The real classification uses wind speed with incoming solar radiation by day and cloud cover by night. The weather
/// model carries no cloud cover, so this reads the strength of the sun from the time of day and the season and the
/// humidity (damp air means cloud), which gets the class right most of the time and is never more than one class out.
/// </para>
/// </summary>
public static class AtmosphericStability
{
    /// <summary>
    /// The class for a wind speed (m/s at 10 m), the local time, and the humidity that stands in for cloud cover.
    /// </summary>
    public static string Classify(double windSpeedMps, DateTimeOffset localTime, double relativeHumidity = 0.7)
    {
        var wind = Math.Max(0, windSpeedMps);
        var hour = localTime.Hour + localTime.Minute / 60.0;
        var sun = Insolation(hour, localTime.Month, relativeHumidity);

        // Night: clear skies and light wind let the ground cool and the air settle (F); wind mixes it back to neutral.
        if (sun == Sun.Night)
            return wind switch
            {
                < 2 => relativeHumidity >= 0.9 ? "E" : "F", // overcast nights do not go fully stable
                < 3 => "F",
                < 5 => "E",
                _ => "D",
            };

        if (sun == Sun.Strong)
            return wind switch { < 2 => "A", < 3 => "B", < 5 => "B", < 6 => "C", _ => "C" };
        if (sun == Sun.Moderate)
            return wind switch { < 2 => "B", < 3 => "B", < 5 => "C", < 6 => "C", _ => "D" };
        return wind switch { < 2 => "B", < 3 => "C", < 5 => "C", _ => "D" }; // slight sun
    }

    /// <summary>The class in the weather as the engine holds it, at the time given.</summary>
    public static string Classify(State.WorldWeather weather, DateTimeOffset localTime) =>
        Classify(weather.WindSpeedMps, localTime, weather.RelativeHumidity);

    private enum Sun
    {
        Night,
        Slight,
        Moderate,
        Strong,
    }

    private static Sun Insolation(double hour, int month, double humidity)
    {
        // Dublin: first light around 05:00 midsummer and 08:00 midwinter.
        var summer = month is >= 4 and <= 9;
        var (sunrise, sunset) = summer ? (5.5, 21.0) : (8.0, 16.5);
        if (hour < sunrise || hour > sunset) return Sun.Night;

        // Overcast and damp: little heating whatever the hour.
        if (humidity >= 0.9) return Sun.Slight;

        var noon = (sunrise + sunset) / 2;
        var fromNoon = Math.Abs(hour - noon);
        if (!summer) return fromNoon <= 2 ? Sun.Moderate : Sun.Slight; // a low winter sun never heats much
        if (fromNoon <= 2) return humidity >= 0.75 ? Sun.Moderate : Sun.Strong;
        return fromNoon <= 4 ? Sun.Moderate : Sun.Slight;
    }
}
