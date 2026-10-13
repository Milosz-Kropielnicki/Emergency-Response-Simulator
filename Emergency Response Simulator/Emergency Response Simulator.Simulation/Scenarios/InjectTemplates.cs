using System.Globalization;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Comms;
using Emergency_Response_Simulator.Simulation.Engine;
using static Emergency_Response_Simulator.Simulation.Scenarios.Inject;

namespace Emergency_Response_Simulator.Simulation.Scenarios;

/// <summary>
/// The instructor's library of ad-hoc injects (Phase 11 M4): things they can make happen now, in any scenario. Each
/// works out where from the world ("near the incident" is the largest live incident), so it fits whatever is going on.
/// Ground-truth injects change the world; command learns of them the usual way (a call, a crew, a met report).
/// </summary>
public static class InjectTemplates
{
    private sealed record Template(InjectTemplate Info, Func<SimulationContext, IReadOnlyDictionary<string, string>, IEnumerable<(EventVisibility, DomainEvent)>> Produce,
        Func<IReadOnlyDictionary<string, string>, string> Describe);

    private static readonly string[] Compass = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    /// <summary>What can be released (Phase 13): one of each material class the dispersion model knows, and the common gases.</summary>
    private static readonly string[] Materials =
        ["chlorine", "ammonia", "sulphur dioxide", "hydrogen sulphide", "smoke", "toxic gas", "biological aerosol", "radioactive material"];

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static readonly List<Template> Library =
    [
        new(new("wind-shift", "Wind shift", "The wind really changes; the met service reports it if asked to.",
            [
                new("from", "Wind from", InjectParameterKind.Choice, "SW", Compass),
                new("speed", "Speed (m/s)", InjectParameterKind.Number, "8"),
                new("reported", "Met service reports it", InjectParameterKind.Choice, "Yes", ["Yes", "No"]),
            ]),
            (context, v) =>
            {
                var from = Array.IndexOf(Compass, v["from"]) * 45.0;
                var speed = Number(v, "speed", 8);
                var weather = context.World.Weather;
                List<(EventVisibility, DomainEvent)> events = [Truth(new WeatherChanged(from, speed, weather.TemperatureC, weather.RelativeHumidity))];
                if (v["reported"] == "Yes")
                    events.Add(Perceived(new WeatherObserved("Met service", Near(context), from, speed, weather.TemperatureC, weather.RelativeHumidity)));
                return events;
            },
            v => $"Wind shifts to {v["from"]} {Number(v, "speed", 8):0} m/s" + (v["reported"] == "Yes" ? ", reported by the met service" : " (unreported)")),

        new(new("fire-grows", "Incident escalates", "The largest live incident gets worse: more severe, more casualties.",
            [
                new("severity", "Severity (0–1)", InjectParameterKind.Number, "0.8"),
                new("casualties", "Extra casualties", InjectParameterKind.Number, "4"),
            ]),
            (context, v) => Largest(context) is { } incident
                ? [Truth(new WorldIncidentChanged(incident.Id, Math.Clamp(Number(v, "severity", 0.8), 0, 1), incident.ActualCasualties + (int)Number(v, "casualties", 4), false))]
                : [],
            v => $"Incident escalates to severity {Number(v, "severity", 0.8):0.0}, {Number(v, "casualties", 4):0} more casualties"),

        new(new("secondary-explosion", "Secondary incident", "Something new starts near the incident: an explosion, a collapse, a fire.",
            [
                new("type", "What", InjectParameterKind.Choice, nameof(IncidentType.Explosion),
                    [nameof(IncidentType.Explosion), nameof(IncidentType.StructuralCollapse), nameof(IncidentType.StructureFire), nameof(IncidentType.HazmatRelease)]),
                new("distance", "Metres from the incident", InjectParameterKind.Number, "150"),
                new("casualties", "Casualties", InjectParameterKind.Number, "3"),
            ]),
            (context, v) => [Truth(new WorldIncidentStarted(Guid.NewGuid(), Enum.Parse<IncidentType>(v["type"]),
                GeoMath.Destination(Near(context), 60, Number(v, "distance", 150)), 0.4, (int)Number(v, "casualties", 3)))],
            v => $"Secondary {EventDescriber.Humanize(Enum.Parse<IncidentType>(v["type"])).ToLowerInvariant()} {Number(v, "distance", 150):0} m from the incident"),

        new(new("call", "999 call", "A member of the public rings 999 from near the incident.",
            [
                new("caller", "Caller", InjectParameterKind.Text, "999 caller (mobile)"),
                new("summary", "What they say", InjectParameterKind.Text, "There's smoke coming into our flat and we can't get down the stairs"),
                new("distance", "Metres from the incident", InjectParameterKind.Number, "120"),
            ]),
            (context, v) =>
            {
                CommsNet.EmergencyCall(context, new PendingCall
                {
                    Caller = v["caller"], Summary = v["summary"], Location = GeoMath.Destination(Near(context), 30, Number(v, "distance", 120)),
                    AccuracyMeters = 80, Patience = TimeSpan.FromMinutes(3),
                });
                return [];
            },
            v => $"999 call: {v["summary"]}"),

        new(new("breakdown", "Unit breaks down", "A unit on the road really breaks down; command hears when the crew says so.",
            [new("callsign", "Unit", InjectParameterKind.Text, "Engine 12"), new("fault", "Fault", InjectParameterKind.Text, "flat tyre")]),
            (context, v) => Unit(context, v["callsign"]) is { } unit ? [Truth(new UnitBrokeDown(unit, v["fault"]))] : [],
            v => $"{v["callsign"]} breaks down: {v["fault"]}"),

        new(new("radio-failure", "Radio fails", "A unit's radio and data really fail; command only notices the silence.",
            [new("callsign", "Unit", InjectParameterKind.Text, "Engine 4")]),
            (context, v) => Unit(context, v["callsign"]) is { } unit ? [Truth(new UnitRadioFailed(unit, true))] : [],
            v => $"{v["callsign"]}'s radio fails"),

        new(new("road-blocked", "Road blocked", "A road near the incident is really blocked; crews find out when they reach it.",
            [new("description", "What", InjectParameterKind.Text, "burst water main, road flooded"), new("distance", "Metres from the incident", InjectParameterKind.Number, "300")]),
            (context, v) =>
            {
                var centre = GeoMath.Destination(Near(context), 270, Number(v, "distance", 300));
                return [Truth(new RoadObstructed(Guid.NewGuid(), [GeoMath.Destination(centre, 0, 50), GeoMath.Destination(centre, 180, 50)], v["description"]))];
            },
            v => $"Road blocked {Number(v, "distance", 300):0} m from the incident: {v["description"]}"),

        new(new("hospital-pressure", "Hospital under pressure", "The busiest hospital loses capacity and says so.",
            [new("places", "Places lost", InjectParameterKind.Number, "4"), new("reason", "Why", InjectParameterKind.Text, "a power failure in the emergency department")]),
            (context, v) =>
            {
                if (context.World.Hospitals.Values.OrderBy(h => GeoMath.DistanceMeters(h.Location, Near(context))).FirstOrDefault() is not { } hospital) return [];
                var lost = (int)Number(v, "places", 4);
                return
                [
                    Truth(new HospitalCapacityChanged(hospital.Id, Math.Max(0, hospital.Capacity - lost), v["reason"])),
                    Perceived(new AlertRaised(Guid.NewGuid(), AlertCategory.Critical, AlertSeverity.Warning, $"{hospital.Name}: reduced capacity",
                        $"{hospital.Name} reports {v["reason"]}: {lost} fewer emergency places.", null, null)),
                ];
            },
            v => $"Nearest hospital loses {Number(v, "places", 4):0} places: {v["reason"]}"),

        // Plume & hazard models (Phase 13): a release of any material the dispersion model knows, and revising one.
        new(new("release", "Hazardous release", "Something toxic, smoky, biological or radioactive really starts escaping near the incident.",
            [
                new("material", "Material", InjectParameterKind.Choice, "chlorine", Materials),
                new("rate", "Release rate (kg/s)", InjectParameterKind.Number, "0.5"),
                new("quantity", "Inventory (kg)", InjectParameterKind.Number, "1500"),
                new("distance", "Metres from the incident", InjectParameterKind.Number, "200"),
                new("casualties", "Casualties", InjectParameterKind.Number, "0"),
            ]),
            (context, v) =>
            {
                var incidentId = Guid.NewGuid();
                var at = GeoMath.Destination(Near(context), 120, Number(v, "distance", 200));
                var material = v["material"];
                return
                [
                    Truth(new WorldIncidentStarted(incidentId, IncidentType.HazmatRelease, at, 0.5, (int)Number(v, "casualties", 0))),
                    Truth(new HazardStarted(Guid.NewGuid(), incidentId, HazardKind.Plume, at, $"{Capitalise(material)} release",
                        Math.Max(0.001, Number(v, "rate", 0.5)), material, Math.Max(0, Number(v, "quantity", 1500)))),
                ];
            },
            v => $"{Capitalise(v["material"])} release, {Number(v, "rate", 0.5):0.###} kg/s, {Number(v, "distance", 200):0} m from the incident"),

        new(new("release-rate", "Release rate changes", "The latest live release gets worse, eases or stops: a valve fails, a crew plugs the leak, the store runs dry.",
            [
                new("rate", "New rate (kg/s, 0 stops it)", InjectParameterKind.Number, "2"),
                new("reason", "Why", InjectParameterKind.Text, "a second valve has failed"),
            ]),
            (context, v) => context.World.Hazards.Values.Where(h => h.Kind == HazardKind.Plume && !h.Ended).OrderByDescending(h => h.StartedAt).FirstOrDefault() is { } release
                ? [Truth(new HazardRateChanged(release.Id, Math.Max(0, Number(v, "rate", 2)), v["reason"]))]
                : [],
            v => Number(v, "rate", 2) <= 0 ? $"Release stops: {v["reason"]}" : $"Release rate now {Number(v, "rate", 2):0.###} kg/s: {v["reason"]}"),

        new(new("rumour", "Social media rumour", "A post spreading something wrong (or right) about the incident.",
            [new("text", "Post", InjectParameterKind.Text, "Hearing there's a second explosion and gas spreading towards the schools!!")]),
            (context, v) => [Perceived(new ReportReceived(Guid.NewGuid(), null, ReportSource.Media, "Social media post", v["text"], Confidence.Low,
                VerificationStatus.Suspected, GeoMath.Destination(Near(context), 45, 500), 800))],
            v => $"Social media rumour: {v["text"]}"),
    ];

    public static IReadOnlyList<InjectTemplate> All => [.. Library.Select(t => t.Info)];

    /// <summary>An inject from a template and the instructor's values (defaults for what they left out); null for an unknown template.</summary>
    public static Inject? Create(string key, IReadOnlyDictionary<string, string>? values)
    {
        if (Library.FirstOrDefault(t => t.Info.Key == key) is not { } template) return null;
        var filled = template.Info.Parameters.ToDictionary(p => p.Key,
            p => values?.GetValueOrDefault(p.Key) is { } given && !string.IsNullOrWhiteSpace(given) ? given.Trim() : p.Default);
        foreach (var parameter in template.Info.Parameters)
        {
            if (parameter.Kind == InjectParameterKind.Choice && parameter.Choices is { } choices && !choices.Contains(filled[parameter.Key]))
                throw new ArgumentException($"{parameter.Label}: choose one of {string.Join(", ", choices)}.");
            if (parameter.Kind == InjectParameterKind.Number && !double.TryParse(filled[parameter.Key], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                throw new ArgumentException($"{parameter.Label} must be a number.");
        }
        return new Inject(TimeSpan.Zero, template.Describe(filled), context => template.Produce(context, filled));
    }

    private static double Number(IReadOnlyDictionary<string, string> values, string key, double fallback) =>
        double.TryParse(values.GetValueOrDefault(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : fallback;

    private static State.WorldIncident? Largest(SimulationContext context) =>
        context.World.Incidents.Values.Where(i => !i.Extinguished).OrderByDescending(i => i.Severity).FirstOrDefault();

    /// <summary>The largest live incident, else the middle of the area's stations.</summary>
    private static GeoPoint Near(SimulationContext context) =>
        Largest(context)?.Location
        ?? (context.World.Units.Count > 0
            ? new GeoPoint(context.World.Units.Values.Average(u => u.Home.Latitude), context.World.Units.Values.Average(u => u.Home.Longitude))
            : BarrowStreetScenario.FireLocation);

    public static Guid? Unit(SimulationContext context, string callsign) =>
        context.World.Units.Values.FirstOrDefault(u => string.Equals(u.Callsign, callsign, StringComparison.OrdinalIgnoreCase))?.Id;
}
