using System.Globalization;
using System.Net;
using System.Text;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Aar;

/// <summary>Who the report is for, and the assessor's view (Design Document §22: reports for certification and debriefing).</summary>
public sealed record AarSignOff(string Trainee, string Role, string Assessor, string? Comments, Competence? AssessorVerdict = null);

/// <summary>
/// Exports the after-action review (Design Document §22): a self-contained HTML report for debriefing and certification
/// (prints to PDF from any browser), and a CSV of every metric, decision, branch point, delay and key event for analysis.
/// </summary>
public static class AarExport
{
    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>"2 competent, 1 developing", or "none" (Phase 11 M6).</summary>
    public static string ObserverRatings(AarParticipant person) =>
        string.Join(", ", new[] { (person.Competent, "competent"), (person.Developing, "developing"), (person.NotYetCompetent, "not yet competent") }
            .Where(r => r.Item1 > 0).Select(r => $"{r.Item1} {r.Item2}")) is { Length: > 0 } text ? text : "none";

    public static string Label(Rating rating) => rating switch
    {
        Rating.Good => "Good",
        Rating.Fair => "Fair",
        Rating.Poor => "Poor",
        _ => "—",
    };

    public static string Label(Competence competence) => competence switch
    {
        Competence.Competent => "Competent",
        Competence.Developing => "Developing",
        _ => "Not yet competent",
    };

    // ---- HTML ----

    public static string Html(AarReport report, AarSignOff signOff)
    {
        var html = new StringBuilder();
        html.Append($$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>After-action review: {{E(report.Title)}}</title>
            <style>
            :root { --ink:#1b2430; --muted:#5b6876; --line:#d8dee5; --good:#1f7a3a; --fair:#9a6a00; --poor:#b42318; --accent:#1764b0; }
            body { font: 14px/1.45 "Segoe UI", system-ui, sans-serif; color: var(--ink); margin: 32px auto; max-width: 1000px; padding: 0 16px; }
            h1 { font-size: 24px; margin: 0 0 4px; } h2 { font-size: 17px; margin: 28px 0 8px; border-bottom: 2px solid var(--ink); padding-bottom: 4px; }
            .muted { color: var(--muted); } table { border-collapse: collapse; width: 100%; margin: 6px 0; }
            th, td { border-bottom: 1px solid var(--line); padding: 5px 6px; text-align: left; vertical-align: top; }
            th { background: #f3f5f8; font-weight: 600; } .num { text-align: right; white-space: nowrap; }
            .pill { display:inline-block; padding: 1px 8px; border-radius: 10px; font-size: 12px; font-weight: 600; color: #fff; }
            .Good { background: var(--good); } .Fair { background: var(--fair); } .Poor { background: var(--poor); } .NotApplicable { background: #8a96a3; }
            .verdict { font-size: 20px; font-weight: 700; } .grid { display:grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 12px; }
            .card { border: 1px solid var(--line); border-radius: 6px; padding: 10px 12px; }
            .strength::before { content: "✔ "; color: var(--good); } .improve::before { content: "▲ "; color: var(--poor); }
            .sign { display:grid; grid-template-columns: 1fr 1fr; gap: 32px; margin-top: 24px; } .line { border-bottom: 1px solid var(--ink); height: 32px; }
            svg { max-width: 100%; height: auto; } @media print { body { margin: 0; } h2 { break-after: avoid; } tr { break-inside: avoid; } }
            </style></head><body>
            <h1>After-action review: {{E(report.Title)}}</h1>
            <div class="muted">{{report.From.ToLocalTime():dddd d MMMM yyyy, HH:mm}}–{{report.To.ToLocalTime():HH:mm}} · {{E(AarAnalyzer.Duration(report.Duration))}} of simulated time · session {{report.SessionId.ToString()[..8]}}</div>

            <h2>Summary</h2>
            <div class="grid">
              <div class="card"><div class="muted">Trainee</div><div><b>{{E(signOff.Trainee)}}</b></div><div class="muted">{{E(signOff.Role)}}</div></div>
              <div class="card"><div class="muted">Assessed against the standards</div><div class="verdict">{{Label(report.Overall)}}</div><div class="muted">Score {{report.Score}} / 100</div></div>
              <div class="card"><div class="muted">Assessor's verdict</div><div class="verdict">{{(signOff.AssessorVerdict is { } verdict ? Label(verdict) : "—")}}</div><div class="muted">{{E(signOff.Assessor)}}</div></div>
            </div>
            """);

        if (!string.IsNullOrWhiteSpace(signOff.Comments))
            html.Append($"<p><b>Assessor's comments:</b> {E(signOff.Comments)}</p>");

        // Findings.
        html.Append("<h2>Debrief points</h2><ul>");
        foreach (var finding in report.Findings)
            html.Append($"<li class=\"{(finding.Strength ? "strength" : "improve")}\">{E(finding.Text)}</li>");
        if (report.Findings.Count == 0) html.Append("<li class=\"muted\">Nothing stood out either way.</li>");
        html.Append("</ul>");

        // Outcomes.
        html.Append("<h2>Outcome</h2><table><tr><th>Run</th><th class=\"num\">Hurt</th><th class=\"num\">Dead</th><th class=\"num\">Firefighters in distress</th><th class=\"num\">Largest fire (m²)</th><th class=\"num\">First on scene</th><th>Notes</th></tr>");
        foreach (var outcome in report.Outcomes)
        {
            html.Append($"<tr><td>{E(outcome.Label)}</td><td class=\"num\">{outcome.Injured:0.#}</td><td class=\"num\">{outcome.Deaths:0.#}</td>" +
                        $"<td class=\"num\">{outcome.FirefightersInDistress:0.#}</td><td class=\"num\">{outcome.PeakFireSquareMeters:N0}</td>" +
                        $"<td class=\"num\">{(outcome.FirstOnSceneMinutes is { } m ? $"{m:0.0} min" : "—")}</td><td>{E(outcome.Notes)}</td></tr>");
        }
        html.Append("</table>");
        if (report.Outcomes.Count == 1)
            html.Append("<p class=\"muted\">Run the branch comparison to set this against the recommended response and no response in the same scenario.</p>");

        // Branch points.
        html.Append("<h2>Key decisions against the recommended response</h2><table><tr><th>Decision point</th><th>What you did</th><th>Recommended</th><th>Likely difference</th><th></th></tr>");
        foreach (var branch in report.Branches)
            html.Append($"<tr><td><b>{E(branch.Topic)}</b>{(branch.At is { } at ? $"<div class=\"muted\">{Clock(at)}</div>" : "")}</td><td>{E(branch.Yours)}</td>" +
                        $"<td>{E(branch.Recommended)}<div class=\"muted\">Alternative: {E(branch.Alternative)}</div></td><td>{E(branch.Effect)}</td><td>{Pill(branch.Rating)}</td></tr>");
        html.Append("</table>");

        // Participants (networked sessions).
        if (report.Participants.Count > 0)
        {
            html.Append("<h2>Participants</h2><table><tr><th>Name</th><th>Roles</th><th class=\"num\">Time in session</th><th class=\"num\">Commands</th><th class=\"num\">Good / fair / poor</th><th class=\"num\">Mean response</th><th class=\"num\">Score</th><th>Observers</th><th class=\"num\">Combined</th><th>Mostly</th></tr>");
            foreach (var person in report.Participants)
                html.Append($"<tr><td><b>{E(person.Name)}</b></td><td>{E(person.Roles)}</td><td class=\"num\">{E(AarAnalyzer.Duration(person.Present))}</td>" +
                            $"<td class=\"num\">{person.Commands}</td><td class=\"num\">{person.Good} / {person.Fair} / {person.Poor}</td>" +
                            $"<td class=\"num\">{(person.MeanResponse is { } mean ? E(AarAnalyzer.Duration(mean)) : "—")}</td>" +
                            $"<td class=\"num\">{(person.Score is { } score ? score.ToString(CultureInfo.InvariantCulture) : "—")}</td>" +
                            $"<td>{E(ObserverRatings(person))}</td><td class=\"num\">{(person.Combined is { } combined ? $"{combined} ({E(Rubric.Name(person.Level!.Value))})" : "—")}</td>" +
                            $"<td class=\"muted\">{E(person.Focus)}</td></tr>");
            html.Append("</table>");
        }

        // Observers' notes (Phase 11 M6).
        if (report.Notes.Count > 0)
        {
            html.Append("<h2>Observer notes</h2><table><tr><th>Time</th><th>Participant</th><th>Area</th><th>Rating</th><th>Note</th><th>About</th><th>By</th></tr>");
            foreach (var note in report.Notes)
                html.Append($"<tr><td>{Clock(note.At)}</td><td><b>{E(note.Participant)}</b></td><td>{(note.Area is { } area ? E(Rubric.Name(area)) : "—")}</td>" +
                            $"<td>{(note.Rating is { } rating ? E(Rubric.Name(rating)) : "—")}</td>" +
                            $"<td>{E(note.Note)}</td><td class=\"muted\">{(note.About is { } about ? E(about) : "")}</td><td class=\"muted\">{E(note.By ?? "")}</td></tr>");
            html.Append("</table>");
        }

        // Legal and liability (Phase 14).
        if (report.Liabilities.Count > 0 || report.Documentation.Significant > 0)
        {
            html.Append("<h2>Legal &amp; liability</h2>");
            var log = report.Documentation;
            if (log.Significant > 0)
                html.Append($"<p>Decision log: <b>{log.Documented} of {log.Significant}</b> significant decisions documented with their reasons ({log.Percent}%).</p>" +
                            (log.Undocumented.Count > 0 ? $"<p class=\"muted\">Not logged: {E(string.Join("; ", log.Undocumented))}</p>" : ""));
            if (report.Liabilities.Count > 0)
            {
                html.Append("<table><tr><th>Time</th><th>Exposure</th><th>What happened</th><th></th></tr>");
                foreach (var item in report.Liabilities)
                    html.Append($"<tr><td>{Clock(item.At)}</td><td><b>{E(item.Kind)}</b></td><td>{E(item.Text)}</td>" +
                                $"<td>{(item.Mitigated ? "mitigated" : "")}</td></tr>");
                html.Append("</table>");
            }
        }

        // Ratings by competency area (Phase 12).
        if (report.Competencies.Count > 0)
        {
            html.Append("<h2>Competencies</h2><table><tr><th>Participant</th><th>Area</th><th class=\"num\">Ratings</th><th class=\"num\">Score</th><th>Level</th></tr>");
            foreach (var competency in report.Competencies)
                html.Append($"<tr><td><b>{E(competency.Participant)}</b></td><td>{E(Rubric.Name(competency.Area))}</td><td class=\"num\">{competency.Ratings}</td>" +
                            $"<td class=\"num\">{competency.Score}</td><td>{E(Rubric.Name(competency.Level))}</td></tr>");
            html.Append("</table>");
        }

        // How the instructor ran it (Phase 11 M4); scripted injects that went off on time are counted, not listed.
        var steered = report.Instructor.Where(a => a.Kind != "Inject (on time)").ToList();
        if (steered.Count > 0)
        {
            html.Append("<h2>Instructor</h2>");
            html.Append($"<p class=\"muted\">{report.Instructor.Count - steered.Count} scripted inject(s) went off on time. Timeline entries marked as role-play were the instructor speaking or deciding as someone in the world.</p>");
            html.Append("<table><tr><th>Time</th><th>What</th><th>Detail</th></tr>");
            foreach (var action in steered)
                html.Append($"<tr><td>{Clock(action.At)}</td><td><b>{E(action.Kind)}</b></td><td>{E(action.Text)}</td></tr>");
            html.Append("</table>");
        }

        // Metrics.
        html.Append("<h2>Performance metrics</h2>");
        foreach (var group in report.Metrics.GroupBy(m => m.Category))
        {
            html.Append($"<h3>{E(group.Key)}</h3><table><tr><th>Measure</th><th class=\"num\">Result</th><th>Standard</th><th></th><th>Notes</th></tr>");
            foreach (var metric in group)
                html.Append($"<tr><td>{E(metric.Name)}</td><td class=\"num\">{E(metric.Display)}</td><td>{E(metric.Standard)}</td><td>{Pill(metric.Rating)}</td><td class=\"muted\">{E(metric.Explanation)}</td></tr>");
            html.Append("</table>");
        }

        // Resources.
        if (report.Units.Count > 0)
        {
            html.Append("<h2>Resource commitment</h2>");
            html.Append(Gantt(report));
            html.Append("<p class=\"muted\">Grey available · blue dispatched or en route · orange on scene or operating · purple transporting · red out of service.</p>");
        }
        if (report.Heatmaps.Count > 0)
        {
            html.Append("<div class=\"grid\">");
            foreach (var map in report.Heatmaps)
            {
                html.Append($"<div class=\"card\"><b>{E(map.Title)}</b><div class=\"muted\">{map.Columns * map.CellMeters / 1000:0.#} km square, centred on the incident; max {map.Max:0} {E(map.Unit)} per cell</div>{Heatmap(map)}<ul>");
                foreach (var hotspot in map.Hotspots) html.Append($"<li>{E(hotspot)}</li>");
                html.Append("</ul></div>");
            }
            html.Append("</div>");
        }
        if (report.Delays.Count > 0)
        {
            html.Append("<h3>Delays en route</h3><table><tr><th>Time</th><th>Unit</th><th class=\"num\">Lost</th><th>Where</th><th>Why</th></tr>");
            foreach (var delay in report.Delays.Take(20))
                html.Append($"<tr><td>{Clock(delay.At)}</td><td>{E(delay.Callsign)}</td><td class=\"num\">{E(AarAnalyzer.Duration(delay.Lost))}</td><td>{E(delay.Where)}</td><td>{E(delay.Cause)}</td></tr>");
            html.Append("</table>");
        }

        // Decisions.
        html.Append("<h2>Decision timeline</h2><table><tr><th>Time</th><th>Decision</th><th>Prompted by</th><th class=\"num\">Took</th><th>What you knew</th><th>What was really happening</th><th></th></tr>");
        foreach (var decision in report.Decisions)
        {
            html.Append($"<tr><td>{Clock(decision.At)}</td><td><b>{E(decision.Category)}</b> {E(decision.Decision)}{(decision.Actor is { } actor ? $"<div class=\"muted\">By {E(actor)}</div>" : "")}{(decision.Note is { } note ? $"<div class=\"muted\">{E(note)}</div>" : "")}</td>" +
                        $"<td>{E(decision.Trigger ?? "—")}</td><td class=\"num\">{(decision.ResponseTime is { } t ? E(AarAnalyzer.Duration(t)) : "—")}</td>" +
                        $"<td class=\"muted\">{E(decision.Known)}</td><td class=\"muted\">{E(decision.Truth)}</td><td>{Pill(decision.Rating)}</td></tr>");
        }
        if (report.Decisions.Count == 0) html.Append("<tr><td colspan=\"7\" class=\"muted\">No commands were given.</td></tr>");
        html.Append("</table>");

        // Key events.
        html.Append("<h2>Key events</h2><table><tr><th>Time</th><th>What</th><th>Detail</th></tr>");
        foreach (var entry in report.Timeline.Where(t => t.Important))
            html.Append($"<tr><td>{Clock(entry.At)}</td><td>{E(entry.Category)}{(entry.IsTruth ? " <span class=\"muted\">(truth)</span>" : "")}</td><td>{E(entry.Text)}</td></tr>");
        html.Append("</table>");

        html.Append("""
            <h2>Certification</h2>
            <p>The trainee's performance in this exercise has been reviewed against the standards above.</p>
            <div class="sign"><div><div class="line"></div>Trainee signature and date</div><div><div class="line"></div>Assessor signature and date</div></div>
            </body></html>
            """);
        return html.ToString();
    }

    private static string Pill(Rating rating) => rating == Rating.NotApplicable ? "" : $"<span class=\"pill {rating}\">{Label(rating)}</span>";

    /// <summary>Each unit's status over the session as a strip of colour.</summary>
    private static string Gantt(AarReport report)
    {
        const int labelWidth = 120, width = 840, row = 18;
        var total = Math.Max(1, report.Duration.TotalSeconds);
        var units = report.Units.Take(30).ToList();
        var svg = new StringBuilder($"<svg viewBox=\"0 0 {labelWidth + width} {units.Count * row + 20}\" xmlns=\"http://www.w3.org/2000/svg\" font-size=\"11\" font-family=\"Segoe UI, sans-serif\">");
        for (var i = 0; i < units.Count; i++)
        {
            var y = i * row;
            svg.Append($"<text x=\"0\" y=\"{y + 13}\">{E(units[i].Callsign)}</text>");
            foreach (var span in units[i].Spans)
            {
                var x = labelWidth + (span.From - report.From).TotalSeconds / total * width;
                var w = Math.Max(0.5, (span.To - span.From).TotalSeconds / total * width);
                svg.Append($"<rect x=\"{x:0.#}\" y=\"{y + 3}\" width=\"{w:0.#}\" height=\"{row - 6}\" fill=\"{StatusColour(span.Status)}\"><title>{span.Status} {Clock(span.From)}–{Clock(span.To)}</title></rect>");
            }
        }
        for (var minute = 0; minute <= report.Duration.TotalMinutes; minute += 10)
        {
            var x = labelWidth + minute * 60 / total * width;
            svg.Append($"<text x=\"{x:0.#}\" y=\"{units.Count * row + 14}\" fill=\"#5b6876\">+{minute}m</text>");
        }
        return svg.Append("</svg>").ToString();
    }

    public static string StatusColour(UnitStatus status) => status switch
    {
        UnitStatus.Dispatched or UnitStatus.EnRoute => "#2f7fd1",
        UnitStatus.OnScene or UnitStatus.Operating => "#e08a1e",
        UnitStatus.Transporting => "#8257c4",
        UnitStatus.OutOfService => "#c0392b",
        _ => "#d5dbe1",
    };

    private static string Heatmap(AarHeatmap map)
    {
        const int cell = 12;
        var max = Math.Max(map.Max, 0.001);
        var svg = new StringBuilder($"<svg viewBox=\"0 0 {map.Columns * cell} {map.Rows * cell}\" xmlns=\"http://www.w3.org/2000/svg\">");
        svg.Append($"<rect width=\"{map.Columns * cell}\" height=\"{map.Rows * cell}\" fill=\"#f3f5f8\"/>");
        for (var row = 0; row < map.Rows; row++)
        {
            for (var column = 0; column < map.Columns; column++)
            {
                var value = map.Values[row * map.Columns + column];
                if (value <= 0) continue;
                var intensity = Math.Sqrt(value / max); // so faint activity still shows
                svg.Append($"<rect x=\"{column * cell}\" y=\"{(map.Rows - 1 - row) * cell}\" width=\"{cell}\" height=\"{cell}\" fill=\"#d62d20\" fill-opacity=\"{0.15 + 0.85 * intensity:0.##}\"/>");
            }
        }
        var centre = map.Columns * cell / 2;
        svg.Append($"<circle cx=\"{centre}\" cy=\"{centre}\" r=\"4\" fill=\"none\" stroke=\"#1b2430\" stroke-width=\"2\"/><text x=\"4\" y=\"12\" font-size=\"10\">N ↑</text>");
        return svg.Append("</svg>").ToString();
    }

    // ---- CSV ----

    /// <summary>Everything in one sheet: Section, Time, Category, Item, Result, Standard, Rating, Detail.</summary>
    public static string Csv(AarReport report)
    {
        var csv = new StringBuilder("Section,Time,Category,Item,Result,Standard,Rating,Detail\r\n");
        void Row(string section, DateTimeOffset? at, string category, string item, string result, string standard, string rating, string detail) =>
            csv.Append(string.Join(',', new[] { section, at is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "", category, item, result, standard, rating, detail }
                .Select(Quote))).Append("\r\n");

        Row("Summary", report.From, "", report.Title, Label(report.Overall), "", report.Score.ToString(CultureInfo.InvariantCulture),
            $"{AarAnalyzer.Duration(report.Duration)} of simulated time");
        foreach (var metric in report.Metrics)
            Row("Metric", null, metric.Category, metric.Name, metric.Display, metric.Standard, Label(metric.Rating), metric.Explanation);
        foreach (var branch in report.Branches)
            Row("Branch", branch.At, "", branch.Topic, branch.Yours, branch.Recommended, Label(branch.Rating), branch.Effect);
        foreach (var outcome in report.Outcomes)
            Row("Outcome", null, outcome.Label, $"{outcome.Runs} run(s)",
                string.Create(CultureInfo.InvariantCulture, $"{outcome.Injured:0.#} hurt; {outcome.Deaths:0.#} dead; {outcome.FirefightersInDistress:0.#} firefighters in distress; peak fire {outcome.PeakFireSquareMeters:0} m2"),
                "", "", outcome.Notes);
        foreach (var decision in report.Decisions)
            Row("Decision", decision.At, decision.Category, decision.Decision,
                decision.ResponseTime is { } taken ? AarAnalyzer.Duration(taken) : "", decision.Trigger ?? "", Label(decision.Rating),
                $"Knew: {decision.Known}. Truth: {decision.Truth}." + (decision.Note is { } note ? $" {note}" : "") +
                (decision.Actor is { } actor ? $" By {actor}." : ""));
        foreach (var person in report.Participants)
            Row("Participant", person.JoinedAt, person.Roles, person.Name, $"{person.Commands} commands", AarAnalyzer.Duration(person.Present),
                person.Combined?.ToString(CultureInfo.InvariantCulture) ?? "",
                $"Good {person.Good}, fair {person.Fair}, poor {person.Poor}. {person.Focus}. Decisions {person.Score?.ToString(CultureInfo.InvariantCulture) ?? "—"}; observers: {ObserverRatings(person)}" +
                (person.Level is { } level ? $"; overall {Rubric.Name(level)}." : "."));
        foreach (var note in report.Notes)
            Row("Observer note", note.At, note.Area is { } area ? Rubric.Name(area) : "", note.Participant, note.Note, "",
                note.Rating is { } rating ? Rubric.Name(rating) : "",
                $"{(note.About is { } about ? $"About: {about}. " : "")}By {note.By}");
        foreach (var item in report.Liabilities)
            Row("Liability", item.At, item.Kind, item.Text, item.Mitigated ? "mitigated" : "", "", "", "");
        if (report.Documentation.Significant > 0)
            Row("Decision log", null, "Documentation", $"{report.Documentation.Documented} of {report.Documentation.Significant} significant decisions logged",
                report.Documentation.Percent.ToString(CultureInfo.InvariantCulture) + "%", "", "", string.Join("; ", report.Documentation.Undocumented));
        foreach (var competency in report.Competencies)
            Row("Competency", null, Rubric.Name(competency.Area), competency.Participant, $"{competency.Ratings} rating(s)", "",
                competency.Score.ToString(CultureInfo.InvariantCulture), Rubric.Name(competency.Level));
        foreach (var unit in report.Units)
            Row("Unit", null, unit.Type, unit.Callsign, unit.Committed.ToString("P0", CultureInfo.InvariantCulture), "", "",
                unit.DelayedEnRoute is { } delayed ? $"Delayed en route {AarAnalyzer.Duration(delayed)}" : "");
        foreach (var delay in report.Delays)
            Row("Delay", delay.At, delay.Callsign, delay.Where, AarAnalyzer.Duration(delay.Lost), "", "", delay.Cause);
        foreach (var entry in report.Timeline)
            Row(entry.IsTruth ? "Truth" : entry.IsDecision ? "Command" : entry.RolePlay ? "Role-play" : "Event", entry.At, entry.Category, entry.Text, "", "", "", entry.Actor ?? "");
        foreach (var action in report.Instructor)
            Row("Instructor", action.At, action.Kind, action.Text, "", "", "", action.By ?? "");
        return csv.ToString();
    }

    private static string Quote(string field) =>
        field.IndexOfAny([',', '"', '\r', '\n']) < 0 ? field : $"\"{field.Replace("\"", "\"\"")}\"";
}
