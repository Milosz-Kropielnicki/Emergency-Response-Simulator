using System.Globalization;
using System.Xml.Linq;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Simulation.Standards;

// ---- OASIS Common Alerting Protocol 1.2 ----

/// <summary>A CAP 1.2 alert: who sent it, when, and one or more info blocks.</summary>
/// <param name="Status">Actual, Exercise, System, Test or Draft.</param>
/// <param name="MsgType">Alert, Update, Cancel, Ack or Error.</param>
public sealed record CapAlert(string Identifier, string Sender, DateTimeOffset Sent, string Status, string MsgType, string Scope,
    IReadOnlyList<CapInfo> Info, string? Note = null, string? References = null);

/// <param name="Category">Geo, Met, Safety, Security, Rescue, Fire, Health, Env, Transport, Infra, CBRNE or Other.</param>
/// <param name="ResponseType">Shelter, Evacuate, Prepare, Execute, Avoid, Monitor, Assess, AllClear or None.</param>
public sealed record CapInfo(string Category, string Event, string Urgency, string Severity, string Certainty, string? Headline,
    string? Description, string? Instruction, string? SenderName, IReadOnlyList<CapArea> Areas, string? ResponseType = null,
    DateTimeOffset? Expires = null);

/// <summary>Where it applies.</summary>
/// <param name="Polygons">"lat,lon lat,lon …", first and last point the same.</param>
/// <param name="Circles">"lat,lon radius-km".</param>
public sealed record CapArea(string AreaDesc, IReadOnlyList<string> Polygons, IReadOnlyList<string> Circles)
{
    /// <summary>The middle of the first shape, for placing the alert on the map.</summary>
    public GeoPoint? Centre()
    {
        if (Polygons.FirstOrDefault() is { } polygon && Cap.ParsePoints(polygon) is { Count: > 0 } points)
            return new GeoPoint(points.Average(p => p.Latitude), points.Average(p => p.Longitude));
        if (Circles.FirstOrDefault() is { } circle && Cap.ParsePoints(circle.Split(' ')[0]) is [var centre])
            return centre;
        return null;
    }
}

public static class Cap
{
    public static readonly XNamespace Ns = "urn:oasis:names:tc:emergency:cap:1.2";

    /// <summary>The CAP status for an exercise: what a training system's warnings must say, so no one acts on them.</summary>
    public const string ExerciseStatus = "Exercise";

    public static string Write(CapAlert alert) => ToElement(alert).ToString();

    public static XElement ToElement(CapAlert alert) =>
        new(Ns + "alert",
            new XElement(Ns + "identifier", alert.Identifier),
            new XElement(Ns + "sender", alert.Sender),
            new XElement(Ns + "sent", Time(alert.Sent)),
            new XElement(Ns + "status", alert.Status),
            new XElement(Ns + "msgType", alert.MsgType),
            new XElement(Ns + "scope", alert.Scope),
            alert.Note is null ? null : new XElement(Ns + "note", alert.Note),
            alert.References is null ? null : new XElement(Ns + "references", alert.References),
            alert.Info.Select(info => new XElement(Ns + "info",
                new XElement(Ns + "language", "en-IE"),
                new XElement(Ns + "category", info.Category),
                new XElement(Ns + "event", info.Event),
                info.ResponseType is null ? null : new XElement(Ns + "responseType", info.ResponseType),
                new XElement(Ns + "urgency", info.Urgency),
                new XElement(Ns + "severity", info.Severity),
                new XElement(Ns + "certainty", info.Certainty),
                info.Expires is { } expires ? new XElement(Ns + "expires", Time(expires)) : null,
                info.SenderName is null ? null : new XElement(Ns + "senderName", info.SenderName),
                info.Headline is null ? null : new XElement(Ns + "headline", info.Headline),
                info.Description is null ? null : new XElement(Ns + "description", info.Description),
                info.Instruction is null ? null : new XElement(Ns + "instruction", info.Instruction),
                info.Areas.Select(area => new XElement(Ns + "area",
                    new XElement(Ns + "areaDesc", area.AreaDesc),
                    area.Polygons.Select(p => new XElement(Ns + "polygon", p)),
                    area.Circles.Select(c => new XElement(Ns + "circle", c)))))));

    /// <summary>Reads a CAP 1.2 alert; throws <see cref="FormatException"/> when it isn't one.</summary>
    public static CapAlert Read(string xml) => Read(XElement.Parse(xml));

    public static CapAlert Read(XElement root)
    {
        if (root.Name != Ns + "alert") throw new FormatException($"Not a CAP 1.2 alert: the root is <{root.Name.LocalName}> in \"{root.Name.NamespaceName}\".");
        string Required(XElement parent, string name) =>
            parent.Element(Ns + name)?.Value.Trim() is { Length: > 0 } value ? value : throw new FormatException($"CAP alert has no <{name}>.");
        string? Optional(XElement parent, string name) => parent.Element(Ns + name)?.Value.Trim() is { Length: > 0 } value ? value : null;

        return new CapAlert(
            Required(root, "identifier"), Required(root, "sender"), DateTimeOffset.Parse(Required(root, "sent"), CultureInfo.InvariantCulture),
            Required(root, "status"), Required(root, "msgType"), Optional(root, "scope") ?? "Public",
            root.Elements(Ns + "info").Select(info => new CapInfo(
                Optional(info, "category") ?? "Other", Required(info, "event"), Optional(info, "urgency") ?? "Unknown",
                Optional(info, "severity") ?? "Unknown", Optional(info, "certainty") ?? "Unknown", Optional(info, "headline"),
                Optional(info, "description"), Optional(info, "instruction"), Optional(info, "senderName"),
                info.Elements(Ns + "area").Select(area => new CapArea(
                    Optional(area, "areaDesc") ?? "",
                    area.Elements(Ns + "polygon").Select(p => p.Value.Trim()).ToList(),
                    area.Elements(Ns + "circle").Select(c => c.Value.Trim()).ToList())).ToList(),
                Optional(info, "responseType"),
                Optional(info, "expires") is { } expires ? DateTimeOffset.Parse(expires, CultureInfo.InvariantCulture) : null)).ToList(),
            Optional(root, "note"), Optional(root, "references"));
    }

    /// <summary>CAP's date-time: to the second, with the offset, no fractions ("2026-10-08T14:00:00+00:00").</summary>
    public static string Time(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    public static string Polygon(Geometry area)
    {
        var ring = area is Polygon polygon ? polygon.ExteriorRing.Coordinates : area.Envelope.Coordinates;
        var points = ring.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Y:0.#####},{c.X:0.#####}")).ToList();
        if (points.Count > 0 && points[0] != points[^1]) points.Add(points[0]);
        return string.Join(' ', points);
    }

    public static string Circle(GeoPoint centre, double radiusKm) =>
        string.Create(CultureInfo.InvariantCulture, $"{centre.Latitude:0.#####},{centre.Longitude:0.#####} {radiusKm:0.###}");

    public static IReadOnlyList<GeoPoint> ParsePoints(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split(','))
            .Where(parts => parts.Length == 2)
            .Select(parts => new GeoPoint(double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture)))
            .ToList();

    /// <summary>
    /// The warnings command has given the public, as CAP: each evacuation and shelter-in-place zone in force, each
    /// public-health alert and each public statement. Status Exercise, always: these come from a simulation.
    /// </summary>
    public static IReadOnlyList<CapAlert> FromCop(ICopService cop, string sender, Guid? incidentId = null)
    {
        var alerts = new List<CapAlert>();
        string Number(Guid? id) => id is { } i && cop.FindIncident(i) is { } incident ? $" Incident {incident.Number}." : "";
        bool Wanted(Guid? id) => incidentId is null || id == incidentId;

        foreach (var zone in cop.Zones.Where(z => z.Type is ZoneType.EvacuationZone or ZoneType.ShelterInPlace && Wanted(z.IncidentId)))
        {
            var evacuate = zone.Type == ZoneType.EvacuationZone;
            alerts.Add(new CapAlert($"ERS-{zone.Id:N}", sender, zone.EffectiveFrom, ExerciseStatus, "Alert", "Public",
            [
                new CapInfo("Safety", evacuate ? "Evacuation order" : "Shelter-in-place order", "Immediate", "Severe", "Observed",
                    $"{(evacuate ? "Evacuate" : "Shelter in place")}: {zone.Name}",
                    $"Emergency services have ordered the people in {zone.Name} to {(evacuate ? "leave the area" : "stay indoors")}.{Number(zone.IncidentId)}",
                    evacuate ? "Leave now by the routes the Gardaí direct. Take medicines and pets. Do not return until told it is safe."
                             : "Go indoors and stay there. Close windows and doors, turn off ventilation, and listen to local radio.",
                    "Emergency services (exercise)", [new CapArea(zone.Name, [Polygon(zone.Area)], [])], evacuate ? "Evacuate" : "Shelter",
                    zone.EffectiveUntil),
            ], Note: "EXERCISE. Generated by the Emergency Response Simulator."));
        }

        foreach (var threat in cop.Health.Threats.Where(t => Wanted(t.IncidentId)))
            alerts.Add(new CapAlert($"ERS-{threat.Id:N}", sender, threat.ReportedAt, ExerciseStatus, threat.UpdatedAt > threat.ReportedAt ? "Update" : "Alert", "Public",
            [
                new CapInfo(threat.Kind == HealthThreatKind.Radiological ? "CBRNE" : "Health", "Public health alert", "Expected", "Severe", "Likely",
                    $"{threat.Agent}: {threat.Area}", $"HSE Public Health reports {threat.Agent} in {threat.Area}. About {threat.EstimatedExposed} people may have been exposed.",
                    threat.Advice, "HSE Public Health (exercise)", [new CapArea(threat.Area, [], [Circle(threat.Location, 1.0)])], "Prepare"),
            ], Note: "EXERCISE. Generated by the Emergency Response Simulator."));

        foreach (var statement in cop.PublicStatements.Where(s => Wanted(s.IncidentId)))
        {
            var area = statement.IncidentId is { } id && cop.FindIncident(id) is { } incident
                ? [new CapArea(incident.Address ?? incident.Number, [], [Circle(GeoPoint.FromPoint(incident.Location), 2.0)])]
                : Array.Empty<CapArea>();
            alerts.Add(new CapAlert($"ERS-{statement.Id:N}", sender, statement.IssuedAt, ExerciseStatus, "Alert", "Public",
            [
                new CapInfo("Other", "Public information", "Expected", "Minor", "Observed", $"Statement from {statement.Issuer}", statement.Text, null,
                    statement.Issuer, area, "Monitor"),
            ], Note: "EXERCISE. Generated by the Emergency Response Simulator."));
        }
        return alerts.OrderBy(a => a.Sent).ToList();
    }

    /// <summary>CAP severity to the simulator's alert severity.</summary>
    public static AlertSeverity SeverityOf(string severity) => severity switch
    {
        "Extreme" or "Severe" => AlertSeverity.Critical,
        "Moderate" => AlertSeverity.Warning,
        _ => AlertSeverity.Info,
    };
}
