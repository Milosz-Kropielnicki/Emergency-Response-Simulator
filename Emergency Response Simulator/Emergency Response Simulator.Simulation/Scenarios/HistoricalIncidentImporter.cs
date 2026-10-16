using System.Globalization;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Scenarios;

/// <summary>
/// Historical incidents as a scenario (Phase 19: real-world data). Reads a CSV of past incidents — an export from a
/// fire service's call records, an open-data set, a hand-made list — and turns each row into a step that starts that
/// incident, with its 999 call, at its time, with the gaps between them compressed (by default an hour of real time
/// becomes ten minutes). The weather is the archive's for the first incident's date and hour.
/// </summary>
/// <remarks>
/// Columns, by header name in any order (case-insensitive): <c>time</c> (or <c>date</c>, or <c>date</c> + <c>time</c>),
/// <c>type</c>, <c>latitude</c>, <c>longitude</c>; optionally <c>severity</c> (0–1), <c>casualties</c>, <c>description</c>.
/// Types are the simulator's incident types or a near word (fire, crash, collision, flood, explosion, collapse, chemical, gas).
/// Lines starting with <c>#</c> are comments. Rows that can't be read are reported, not imported.
/// </remarks>
public static class HistoricalIncidentImporter
{
    public sealed record Result(ScenarioDefinition Scenario, IReadOnlyList<string> Skipped);

    public static Result FromCsv(string csv, string name, double minutesPerHour = 10)
    {
        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')).ToList();
        if (lines.Count < 2) throw new FormatException("The file needs a header row and at least one incident.");
        var header = Split(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Column(params string[] names) => header.FindIndex(h => names.Contains(h));
        var (time, date, type, lat, lon) = (Column("time", "datetime", "timestamp"), Column("date"), Column("type", "incident type", "category"),
            Column("latitude", "lat"), Column("longitude", "lon", "lng"));
        var (severity, casualties, description) = (Column("severity"), Column("casualties", "injured"), Column("description", "summary", "details"));
        if ((time < 0 && date < 0) || type < 0 || lat < 0 || lon < 0)
            throw new FormatException("The header needs time (or date), type, latitude and longitude columns.");

        var rows = new List<(DateTimeOffset At, IncidentType Type, GeoPoint Where, double Severity, int Casualties, string Description)>();
        var skipped = new List<string>();
        for (var i = 1; i < lines.Count; i++)
        {
            var cells = Split(lines[i]);
            string Cell(int column) => column >= 0 && column < cells.Count ? cells[column].Trim() : "";
            var stamp = date >= 0 && time >= 0 && date != time ? $"{Cell(date)} {Cell(time)}" : Cell(time >= 0 ? time : date);
            if (!DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            {
                skipped.Add($"Line {i + 1}: can't read the time \"{stamp}\".");
                continue;
            }
            if (!double.TryParse(Cell(lat), NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) || latitude is < -90 or > 90
                || !double.TryParse(Cell(lon), NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude) || longitude is < -180 or > 180)
            {
                skipped.Add($"Line {i + 1}: no usable latitude and longitude.");
                continue;
            }
            if (TypeOf(Cell(type)) is not { } kind)
            {
                skipped.Add($"Line {i + 1}: unknown incident type \"{Cell(type)}\".");
                continue;
            }
            var sev = double.TryParse(Cell(severity), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? Math.Clamp(s, 0.05, 1) : 0.4;
            var hurt = int.TryParse(Cell(casualties), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? Math.Max(0, c) : 0;
            rows.Add((at, kind, new GeoPoint(latitude, longitude), sev, hurt, Cell(description)));
        }
        if (rows.Count == 0) throw new FormatException("No incident in the file could be read. " + string.Join(" ", skipped.Take(3)));

        rows.Sort((a, b) => a.At.CompareTo(b.At));
        var first = rows[0].At;
        var scenario = new ScenarioDefinition
        {
            Name = name,
            Key = ScenarioLibrary.KeyFor(name),
            Description = $"{rows.Count} historical incident(s) from {first:dd MMM yyyy HH:mm} to {rows[^1].At:dd MMM yyyy HH:mm} (UTC), " +
                          $"an hour of real time to {minutesPerHour:0} minutes.",
            Centre = new GeoPoint(rows.Average(r => r.Where.Latitude), rows.Average(r => r.Where.Longitude)),
            Weather = new ScenarioWeather(Source: WeatherSource.Historical, HistoricalAt: first),
        };
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var minutes = Math.Round((row.At - first).TotalHours * minutesPerHour + 1, 1);
            var call = row.Description.Length > 0 ? row.Description : $"There's a {EventDescriber(row.Type)} here, please hurry";
            scenario.Steps.Add(new ScenarioStep($"h{i + 1}", $"{row.At:HH:mm} {EventDescriber(row.Type)}", new ScenarioTrigger(TriggerKind.AtTime, minutes),
            [
                new ScenarioAction("new-incident", new Dictionary<string, string>
                {
                    ["type"] = row.Type.ToString(),
                    ["latitude"] = row.Where.Latitude.ToString("0.######", CultureInfo.InvariantCulture),
                    ["longitude"] = row.Where.Longitude.ToString("0.######", CultureInfo.InvariantCulture),
                    ["severity"] = row.Severity.ToString("0.##", CultureInfo.InvariantCulture),
                    ["casualties"] = row.Casualties.ToString(CultureInfo.InvariantCulture),
                    ["call"] = call,
                }),
            ]));
        }
        return new Result(scenario, skipped);
    }

    private static string EventDescriber(IncidentType type) => Core.Events.EventDescriber.Humanize(type).ToLowerInvariant();

    /// <summary>The simulator's type for a record's type: its own names, or a near word.</summary>
    public static IncidentType? TypeOf(string text)
    {
        if (Enum.TryParse<IncidentType>(text.Replace(" ", ""), ignoreCase: true, out var exact) && Enum.IsDefined(exact)) return exact;
        var t = text.ToLowerInvariant();
        return t.Contains("chemical") || t.Contains("hazmat") || t.Contains("gas leak") ? IncidentType.HazmatRelease
            : t.Contains("explosion") || t.Contains("blast") ? IncidentType.Explosion
            : t.Contains("gorse") || t.Contains("grass") || t.Contains("wildland") || t.Contains("forest") ? IncidentType.WildlandFire
            : t.Contains("fire") ? IncidentType.StructureFire
            : t.Contains("crash") || t.Contains("collision") || t.Contains("rta") || t.Contains("road") ? IncidentType.RoadAccident
            : t.Contains("flood") ? IncidentType.Flood
            : t.Contains("collapse") ? IncidentType.StructuralCollapse
            : null;
    }

    /// <summary>A CSV line's cells, honouring double quotes ("a, b" is one cell; "" is a quote).</summary>
    private static List<string> Split(string line)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(ch);
        }
        cells.Add(cell.ToString());
        return cells;
    }
}
