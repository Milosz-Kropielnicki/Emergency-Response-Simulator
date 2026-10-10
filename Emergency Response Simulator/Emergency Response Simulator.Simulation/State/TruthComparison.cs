using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Hazards;

namespace Emergency_Response_Simulator.Simulation.State;

public enum DivergenceSeverity
{
    /// <summary>The COP matches the truth well enough.</summary>
    Aligned,

    /// <summary>The COP is behind or off, as it normally will be.</summary>
    Lagging,

    /// <summary>Something important is happening that command does not know about.</summary>
    Unknown,
}

/// <param name="Topic">What is being compared, e.g. "Engine 7" or "Fire".</param>
public sealed record Divergence(string Topic, string Truth, string Cop, DivergenceSeverity Severity);

/// <summary>
/// Truth against perception (Design Document §10.1, §11, §21): for the instructor, where the COP and the world
/// disagree, and by how much. Command never sees this; the AAR uses the same comparison to ask "what did they know?".
/// </summary>
public static class TruthComparison
{
    /// <summary>A COP incident within this distance of a real one is taken to be the same incident.</summary>
    public const double MatchMeters = 400;

    public static IReadOnlyList<Divergence> Compare(WorldSnapshot truth, ICopService cop)
    {
        var lines = new List<Divergence>();
        var copIncidents = cop.Incidents.Where(i => i.Status != IncidentStatus.Closed).ToList();
        var reports = cop.Reports;

        // Incidents and their casualties.
        foreach (var incident in truth.Incidents.Where(i => !i.Extinguished))
        {
            var topic = EventDescriber.Humanize(incident.Type);
            var match = copIncidents.MinBy(c => GeoMath.DistanceMeters(GeoPoint.FromPoint(c.Location), incident.Location));
            var off = match is null ? double.PositiveInfinity : GeoMath.DistanceMeters(GeoPoint.FromPoint(match.Location), incident.Location);
            var age = truth.At - incident.StartedAt;

            if (match is null || off > MatchMeters)
            {
                lines.Add(new Divergence(topic, $"Burning since {incident.StartedAt.ToLocalTime():HH:mm}, severity {incident.Severity:P0}",
                    "Not on the COP", age > TimeSpan.FromMinutes(3) ? DivergenceSeverity.Unknown : DivergenceSeverity.Lagging));
            }
            else
            {
                lines.Add(new Divergence(topic, $"Severity {incident.Severity:P0}",
                    $"{match.Number}, {match.Priority} priority, plotted {off:F0} m from the real location",
                    off > 100 ? DivergenceSeverity.Lagging : DivergenceSeverity.Aligned));
            }

            var casualties = truth.Casualties.Where(c => c.IncidentId == incident.Id).ToList();
            var alive = casualties.Where(c => c.Triage != Triage.Deceased).ToList();
            var dead = casualties.Count - alive.Count;
            var byTriage = string.Join(", ", alive.GroupBy(c => c.Triage).OrderBy(g => g.Key)
                .Select(g => $"{g.Count()} {EventDescriber.TriageLabel(g.Key)}"));
            var waiting = alive.Count(c => c.State == CasualtyState.AwaitingTreatment);
            var reported = match is null || off > MatchMeters ? 0 : Math.Max(match.CasualtiesReported, match.CasualtiesConfirmed);
            lines.Add(new Divergence($"{topic}: casualties",
                $"{alive.Count} hurt ({(byTriage.Length > 0 ? byTriage : "none")}), {dead} dead, {waiting} untreated",
                match is null || off > MatchMeters ? "—" : $"{match.CasualtiesReported} reported, {match.CasualtiesConfirmed} confirmed",
                casualties.Count == 0 || reported >= casualties.Count ? DivergenceSeverity.Aligned
                    : reported >= casualties.Count / 2 ? DivergenceSeverity.Lagging : DivergenceSeverity.Unknown));
        }

        // Hazards: has anyone told command about them?
        foreach (var hazard in truth.Hazards)
        {
            var (topic, size, words) = hazard.Kind switch
            {
                HazardKind.Fire => ("Fire", $"{hazard.Intensity:N0} m² burning, {hazard.AreaSquareMeters:N0} m² affected", new[] { "fire", "smoke", "burning" }),
                HazardKind.Flood => ("Flood", $"Up to {hazard.Intensity * 100:F0} cm deep, {hazard.AreaSquareMeters / 10_000:0.#} ha", new[] { "flood", "water" }),
                _ => ($"{Capitalise(hazard.Substance ?? "Toxic")} release",
                    $"{hazard.Rate:0.0#} kg/s, cloud over {hazard.AreaSquareMeters / 10_000:0.#} ha",
                    new[] { "smell", "chlorine", "chemical", "fumes", "gas", hazard.Substance ?? "toxic" }),
            };
            var heard = reports.Where(r => r.ReceivedAt >= hazard.StartedAt && words.Any(w => r.Claim.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .MaxBy(r => r.ReceivedAt);
            var zones = hazard.Kind == HazardKind.Plume && cop.Zones.Any(z => z.Type is ZoneType.PlumeLow or ZoneType.PlumeModerate or ZoneType.PlumeHigh);
            lines.Add(new Divergence(topic, size,
                heard is null ? "No reports" : $"Last report {heard.ReceivedAt.ToLocalTime():HH:mm} from {heard.SourceName}" + (zones ? "; plume zones drawn" : ""),
                heard is null ? DivergenceSeverity.Unknown : DivergenceSeverity.Lagging));
        }

        // Power.
        foreach (var outage in truth.Outages)
        {
            var heard = reports.Any(r => r.ReceivedAt >= outage.StartedAt
                                         && (r.Claim.Contains("power", StringComparison.OrdinalIgnoreCase) || r.SourceName.Contains("ESB")));
            lines.Add(new Divergence("Power outage", $"{outage.Cause}; restoring {outage.RestoreAt.ToLocalTime():HH:mm}",
                heard ? "Reported" : "Not known to command", heard ? DivergenceSeverity.Lagging : DivergenceSeverity.Unknown));
        }

        // Units: positions command believes, failures it hasn't heard about.
        foreach (var unit in truth.Units)
        {
            if (cop.FindUnit(unit.Id) is not { } seen) continue;
            if (unit.BrokenDown && seen.Status != UnitStatus.OutOfService)
            {
                lines.Add(new Divergence(unit.Callsign, "Broken down", $"COP: {EventDescriber.Humanize(seen.Status)}", DivergenceSeverity.Unknown));
                continue;
            }
            var off = seen.Location is { } p ? GeoMath.DistanceMeters(GeoPoint.FromPoint(p), unit.Location) : double.PositiveInfinity;
            if (unit.RadioFailed)
            {
                lines.Add(new Divergence(unit.Callsign, "Radio dead, still working",
                    seen.CommsConnected ? $"COP shows it in contact, position {off:F0} m off" : "Flagged out of contact",
                    seen.CommsConnected ? DivergenceSeverity.Unknown : DivergenceSeverity.Lagging));
            }
            else if (off > 150)
            {
                lines.Add(new Divergence(unit.Callsign, "Position", $"COP position {off:F0} m off", DivergenceSeverity.Lagging));
            }
        }

        // Hospitals: what they last said against how full they are.
        foreach (var hospital in truth.Hospitals)
        {
            var told = cop.Hospitals.FirstOrDefault(h => h.Id == hospital.Id);
            var truthText = $"ED {hospital.Occupied}/{hospital.Capacity}" + (hospital.OnDiversion ? ", on diversion" : "");
            var copText = told is null ? "Unknown" : $"ED {told.Occupied}/{told.Capacity} at {told.ReportedAt.ToLocalTime():HH:mm}" + (told.OnDiversion ? ", on diversion" : "");
            var aligned = told is not null && told.OnDiversion == hospital.OnDiversion && Math.Abs(told.Occupied - hospital.Occupied) <= 2;
            if (!aligned)
                lines.Add(new Divergence(hospital.Name, truthText, copText,
                    hospital.OnDiversion && told is { OnDiversion: false } ? DivergenceSeverity.Unknown : DivergenceSeverity.Lagging));
        }

        // Roads really blocked.
        if (truth.Obstructions.Count > 0)
        {
            var closures = cop.Zones.Count(z => z.Type == ZoneType.RoadClosure);
            lines.Add(new Divergence("Roads", $"{truth.Obstructions.Count} stretch(es) really blocked (crashes, debris, floodwater)",
                $"{closures} closure(s) declared", DivergenceSeverity.Lagging));
        }

        // Communications: what never reached command, and crews it can't hear.
        var comms = truth.Comms;
        var lost = comms.RecentLost.Where(l => truth.At - l.At <= TimeSpan.FromMinutes(10)).ToList();
        if (lost.Count > 0)
        {
            var reasons = string.Join(", ", lost.GroupBy(l => l.Reason).OrderByDescending(g => g.Count()).Take(3)
                .Select(g => $"{g.Count()} {g.Key}"));
            lines.Add(new Divergence("Radio", $"{lost.Count} transmission(s) unheard in the last 10 min ({reasons})",
                "Never received", DivergenceSeverity.Unknown));
        }
        foreach (var (callsign, problem) in comms.Unreachable)
            lines.Add(new Divergence($"{callsign}: comms", problem, "COP can't tell", DivergenceSeverity.Lagging));
        foreach (var mast in comms.Masts.Where(m => m.Down || m.OnBattery))
        {
            var heard = reports.Any(r => r.Claim.Contains("mobile", StringComparison.OrdinalIgnoreCase));
            lines.Add(new Divergence(mast.Name, mast.Down ? "Down: no mobile calls or data nearby" : "On batteries (power cut)",
                heard ? "Reported" : "Not known to command", mast.Down && !heard ? DivergenceSeverity.Unknown : DivergenceSeverity.Lagging));
        }

        // Crews: people in trouble command hasn't heard about, real tiredness against what crews admit, what handovers lost.
        foreach (var distress in truth.Distress)
        {
            var known = cop.Maydays.Any(m => m.Active && m.Callsign == distress.Callsign);
            var air = (distress.AirRunsOutAt - truth.At).TotalMinutes;
            lines.Add(new Divergence($"{distress.Member} ({distress.Callsign})",
                $"{Capitalise(distress.Cause)}; {(air > 0 ? $"{air:F0} min of air left" : "out of air")}" +
                (distress.RescueCallsign is { } rescuer ? $"; {rescuer} {distress.RescueProgress:P0} of the way to them" : "; nobody coming"),
                known ? "Mayday known" : "No Mayday heard", known ? DivergenceSeverity.Lagging : DivergenceSeverity.Unknown));
        }
        foreach (var crew in truth.Crews)
        {
            if (cop.FindUnit(crew.UnitId) is not { Crew: { } seen } unit || unit.Agency?.AiControlled == true) continue;
            var band = CrewFactors.Band(crew.Fatigue);
            var told = seen.Condition switch
            {
                CrewCondition.Exhausted => "exhausted",
                CrewCondition.Tired => "tired",
                _ => "fresh",
            };
            if (band != "fresh" && band != told)
            {
                lines.Add(new Divergence($"{crew.Callsign} crew", $"Really {band} (fatigue {crew.Fatigue:P0}, stress {crew.Stress:P0}), {crew.Activity}",
                    seen.ConditionReportedAt is null ? "Hasn't said" : $"Last said: \"{seen.ConditionNote}\"",
                    band == "exhausted" ? DivergenceSeverity.Unknown : DivergenceSeverity.Lagging));
            }
            if (crew.ForgottenTasks.Count > 0 || crew.UnawareOfEvacuation)
            {
                var gaps = crew.ForgottenTasks.Select(t => $"\"{t}\"").ToList();
                if (crew.UnawareOfEvacuation) gaps.Add("the evacuation (may go back inside)");
                lines.Add(new Divergence($"{crew.Callsign} relief crew", $"Never told: {string.Join(", ", gaps)}", "COP assumes it was handed over",
                    crew.UnawareOfEvacuation ? DivergenceSeverity.Unknown : DivergenceSeverity.Lagging));
            }
            if (unit.Channel is { } believed && believed != crew.Channel)
                lines.Add(new Divergence($"{crew.Callsign}: channel", $"Working on {crew.Channel}", $"COP thinks {believed}", DivergenceSeverity.Unknown));
        }

        // Weather.
        if (cop.Weather is { } observed)
        {
            var shift = Math.Abs(((truth.Weather.WindFromDegrees - observed.WindFromDegrees + 540) % 360) - 180);
            lines.Add(new Divergence("Wind", $"From {truth.Weather.WindFromDegrees:F0}° at {truth.Weather.WindSpeedMps:F1} m/s",
                $"From {observed.WindFromDegrees:F0}° at {observed.WindSpeedMps:F1} m/s ({observed.ObservedAt.ToLocalTime():HH:mm})",
                shift >= 30 ? DivergenceSeverity.Unknown : shift >= 10 ? DivergenceSeverity.Lagging : DivergenceSeverity.Aligned));
        }

        return lines.OrderByDescending(l => l.Severity).ToList();
    }

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
