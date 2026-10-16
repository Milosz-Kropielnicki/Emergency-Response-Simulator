using System.Globalization;
using System.Net;
using System.Text;

namespace Emergency_Response_Simulator.Core.Model;

// ---- Phase 18: standard ICS forms and situation reports, generated from the COP and the decision record ----

/// <summary>The ICS forms (and the records alongside them) the documentation unit can produce (Phase 18).</summary>
public enum IcsFormKind
{
    /// <summary>Incident Briefing: the first hours on one form — map, situation, objectives, actions so far, organisation, resources.</summary>
    Ics201,

    /// <summary>Incident Objectives, for the operational period.</summary>
    Ics202,

    /// <summary>Organisation Assignment List.</summary>
    Ics203,

    /// <summary>Assignment List: one per division or group.</summary>
    Ics204,

    /// <summary>Incident Radio Communications Plan.</summary>
    Ics205,

    /// <summary>Communications List: phone and contact list for the agencies and facilities involved.</summary>
    Ics205A,

    /// <summary>Medical Plan.</summary>
    Ics206,

    /// <summary>Incident Organisation Chart.</summary>
    Ics207,

    /// <summary>Safety Message / Plan.</summary>
    Ics208,

    /// <summary>Incident Status Summary.</summary>
    Ics209,

    /// <summary>Resource Status Change.</summary>
    Ics210,

    /// <summary>Incident Check-in List.</summary>
    Ics211,

    /// <summary>General Message: every message sent to another organisation, and its reply.</summary>
    Ics213,

    /// <summary>Activity Log: what one person did, time-stamped (or everyone's, each under their name).</summary>
    Ics214,

    /// <summary>Operational Planning Worksheet.</summary>
    Ics215,

    /// <summary>Incident Action Plan Safety Analysis.</summary>
    Ics215A,

    /// <summary>Air Operations Summary.</summary>
    Ics220,

    /// <summary>Situation report to the EOC: those sent, and a draft of the next.</summary>
    Sitrep,

    /// <summary>The decision log: every order, request, approval and plan, time-stamped, with who gave it and why.</summary>
    DecisionLog,

    /// <summary>The IAP as issued: cover and version history, then 202, 203, 204, 205, 206, 207, 208, 215A and 220.</summary>
    IapPackage,
}

public sealed record FormField(string Label, string Value);

public sealed record FormTable(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public bool Empty => Rows.Count == 0;
}

/// <summary>A block of a form: labelled fields, a table, free text, or any of them together.</summary>
public sealed record FormSection(string Title, IReadOnlyList<FormField>? Fields = null, FormTable? Table = null, string? Text = null);

/// <summary>
/// One completed form (Phase 18). Generated, never edited: the source of truth is the COP and the event stream, so
/// a form regenerated later says the same about the same moment.
/// </summary>
/// <param name="Header">Incident name and number, operational period, date and time prepared.</param>
/// <param name="PreparedBy">Who the form is attributed to (the position that owns it, and the person in it).</param>
public sealed record IcsForm(IcsFormKind Kind, string Title, IReadOnlyList<FormField> Header, IReadOnlyList<FormSection> Sections,
    DateTimeOffset PreparedAt, string PreparedBy)
{
    public string Number => IcsForms.Number(Kind);
}

/// <summary>Names, owners and renderers for the ICS forms (Phase 18).</summary>
public static class IcsForms
{
    public static IReadOnlyList<IcsFormKind> All { get; } = Enum.GetValues<IcsFormKind>();

    public static string Number(IcsFormKind kind) => kind switch
    {
        IcsFormKind.Sitrep => "SITREP",
        IcsFormKind.DecisionLog => "LOG",
        IcsFormKind.IapPackage => "IAP",
        _ => "ICS " + kind.ToString()[3..],
    };

    public static string Title(IcsFormKind kind) => kind switch
    {
        IcsFormKind.Ics201 => "Incident Briefing",
        IcsFormKind.Ics202 => "Incident Objectives",
        IcsFormKind.Ics203 => "Organisation Assignment List",
        IcsFormKind.Ics204 => "Assignment List",
        IcsFormKind.Ics205 => "Incident Radio Communications Plan",
        IcsFormKind.Ics205A => "Communications List",
        IcsFormKind.Ics206 => "Medical Plan",
        IcsFormKind.Ics207 => "Incident Organisation Chart",
        IcsFormKind.Ics208 => "Safety Message / Plan",
        IcsFormKind.Ics209 => "Incident Status Summary",
        IcsFormKind.Ics210 => "Resource Status Change",
        IcsFormKind.Ics211 => "Incident Check-in List",
        IcsFormKind.Ics213 => "General Message",
        IcsFormKind.Ics214 => "Activity Log",
        IcsFormKind.Ics215 => "Operational Planning Worksheet",
        IcsFormKind.Ics215A => "Incident Action Plan Safety Analysis",
        IcsFormKind.Ics220 => "Air Operations Summary",
        IcsFormKind.Sitrep => "Situation Report",
        IcsFormKind.DecisionLog => "Decision Log",
        _ => "Incident Action Plan",
    };

    /// <summary>The position that prepares the form, as ICS assigns it.</summary>
    public static IcsRole Owner(IcsFormKind kind) => kind switch
    {
        IcsFormKind.Ics201 or IcsFormKind.Ics202 or IcsFormKind.Ics213 or IcsFormKind.DecisionLog => IcsRole.IncidentCommander,
        IcsFormKind.Ics205 or IcsFormKind.Ics205A or IcsFormKind.Ics206 => IcsRole.Logistics,
        IcsFormKind.Ics208 or IcsFormKind.Ics215A => IcsRole.Safety,
        IcsFormKind.Ics215 or IcsFormKind.Ics220 => IcsRole.Operations,
        _ => IcsRole.Planning,
    };

    /// <summary>The forms that make up an IAP, in the order they are bound.</summary>
    public static IReadOnlyList<IcsFormKind> IapForms { get; } =
    [
        IcsFormKind.Ics202, IcsFormKind.Ics203, IcsFormKind.Ics204, IcsFormKind.Ics205, IcsFormKind.Ics206, IcsFormKind.Ics207, IcsFormKind.Ics208,
        IcsFormKind.Ics215A, IcsFormKind.Ics220,
    ];

    /// <summary>Whether the form is drawn from an IAP (the plan in force, or a chosen version).</summary>
    public static bool FromPlan(IcsFormKind kind) => IapForms.Contains(kind) || kind == IcsFormKind.IapPackage;

    // ---- Rendering ----

    /// <summary>The form as plain text: for the preview, a radio read-out, or a printout.</summary>
    public static string ToText(IcsForm form)
    {
        var text = new StringBuilder();
        var title = $"{form.Number}  {form.Title.ToUpperInvariant()}";
        text.AppendLine(title).AppendLine(new string('═', title.Length));
        foreach (var field in form.Header) text.AppendLine($"{field.Label}: {field.Value}");
        foreach (var section in form.Sections)
        {
            text.AppendLine().AppendLine(section.Title.ToUpperInvariant()).AppendLine(new string('─', section.Title.Length));
            if (section.Fields is { } fields)
            {
                var width = fields.Count == 0 ? 0 : fields.Max(f => f.Label.Length);
                foreach (var field in fields) text.AppendLine($"{(field.Label + ":").PadRight(width + 1)} {field.Value}");
            }
            if (section.Text is { Length: > 0 } body) text.AppendLine(body.TrimEnd());
            if (section.Table is { } table)
            {
                if (table.Empty) text.AppendLine("None.");
                else AppendTable(text, table);
            }
        }
        text.AppendLine().Append($"Prepared by {form.PreparedBy}, {form.PreparedAt.ToLocalTime():dd/MM/yyyy HH:mm}");
        return text.ToString();
    }

    private static void AppendTable(StringBuilder text, FormTable table)
    {
        // Columns as wide as their widest cell, capped so a long remark wraps onto its own line instead.
        var widths = table.Columns.Select((c, i) => Math.Min(40, Math.Max(c.Length, table.Rows.Max(r => i < r.Count ? r[i].Length : 0)))).ToList();
        string Row(IReadOnlyList<string> cells) =>
            string.Join("  ", widths.Select((w, i) => (i < cells.Count ? cells[i] : "").PadRight(w))).TrimEnd();
        text.AppendLine(Row(table.Columns));
        text.AppendLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in table.Rows) text.AppendLine(Row(row));
    }

    /// <summary>The form as a printable HTML page (several forms one after another for a package).</summary>
    public static string ToHtml(IReadOnlyList<IcsForm> forms, string? documentTitle = null)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var html = new StringBuilder();
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>")
            .Append(E(documentTitle ?? (forms.Count == 1 ? $"{forms[0].Number} {forms[0].Title}" : "ICS forms")))
            .Append("</title><style>")
            .Append("body{font-family:Segoe UI,Arial,sans-serif;font-size:12px;margin:24px;color:#111}")
            .Append(".form{border:2px solid #111;padding:0;margin:0 0 24px;page-break-after:always}")
            .Append(".form h1{margin:0;padding:6px 10px;background:#111;color:#fff;font-size:15px}")
            .Append(".hdr{display:grid;grid-template-columns:repeat(3,1fr);border-bottom:1px solid #111}")
            .Append(".hdr div{padding:4px 10px;border-right:1px solid #999}.hdr b{display:block;font-size:10px;color:#555}")
            .Append("h2{font-size:12px;margin:0;padding:4px 10px;background:#e8e8e8;border-top:1px solid #111;border-bottom:1px solid #999}")
            .Append("table{border-collapse:collapse;width:100%}td,th{border:1px solid #bbb;padding:3px 6px;text-align:left;vertical-align:top}")
            .Append("th{background:#f4f4f4}.text{padding:6px 10px;white-space:pre-wrap}.foot{padding:4px 10px;border-top:1px solid #111;font-size:11px}")
            .Append("</style></head><body>");
        foreach (var form in forms)
        {
            html.Append("<div class=\"form\"><h1>").Append(E($"{form.Number} — {form.Title}")).Append("</h1><div class=\"hdr\">");
            foreach (var field in form.Header) html.Append("<div><b>").Append(E(field.Label)).Append("</b>").Append(E(field.Value)).Append("</div>");
            html.Append("</div>");
            foreach (var section in form.Sections)
            {
                html.Append("<h2>").Append(E(section.Title)).Append("</h2>");
                if (section.Fields is { Count: > 0 } fields)
                {
                    html.Append("<table>");
                    foreach (var field in fields) html.Append("<tr><th style=\"width:30%\">").Append(E(field.Label)).Append("</th><td>").Append(E(field.Value)).Append("</td></tr>");
                    html.Append("</table>");
                }
                if (section.Text is { Length: > 0 } body) html.Append("<div class=\"text\">").Append(E(body.TrimEnd())).Append("</div>");
                if (section.Table is { } table)
                {
                    if (table.Empty) html.Append("<div class=\"text\">None.</div>");
                    else
                    {
                        html.Append("<table><tr>");
                        foreach (var column in table.Columns) html.Append("<th>").Append(E(column)).Append("</th>");
                        html.Append("</tr>");
                        foreach (var row in table.Rows)
                        {
                            html.Append("<tr>");
                            foreach (var cell in row) html.Append("<td>").Append(E(cell)).Append("</td>");
                            html.Append("</tr>");
                        }
                        html.Append("</table>");
                    }
                }
            }
            html.Append("<div class=\"foot\">Prepared by ").Append(E(form.PreparedBy)).Append(", ")
                .Append(E(form.PreparedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture))).Append("</div></div>");
        }
        return html.Append("</body></html>").ToString();
    }
}

/// <summary>One line of the decision log (Phase 18): what was decided, when, by whom in which role, and why.</summary>
/// <param name="Kind">Order, Request, Approval, Plan, Decision, Message.</param>
/// <param name="Actor">The participant who gave it; for a single-seat session, the position it belongs to.</param>
/// <param name="Rationale">The reason recorded in the decision log for it, if anyone wrote one.</param>
public sealed record DecisionLogRow(long Sequence, DateTimeOffset At, string Kind, string Category, string Text, string Actor, string? Role,
    Guid? ActorId, Guid? IncidentId, string? Rationale);
