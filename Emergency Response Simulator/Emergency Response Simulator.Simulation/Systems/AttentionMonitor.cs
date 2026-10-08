using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// "Something requires your attention" (Design Document §6.9). Watches the COP — what command
/// knows, not ground truth — and raises an alert once when a condition starts, re-arming when it clears:
/// critical incidents, resource shortages, situation changes (wind shifts) and communication failures.
/// </summary>
public sealed class AttentionMonitor(ICopService cop, AttentionOptions options) : ISimulationSystem
{
    private readonly HashSet<string> _active = [];
    private PerceivedWeather? _lastWeather;

    public int Order => 100; // after systems that change the world

    public void Update(SimulationContext context)
    {
        CheckCriticalIncidents(context);
        CheckResourceShortages(context);
        CheckWeather(context);
        CheckCommunications(context);
    }

    private void CheckCriticalIncidents(SimulationContext context)
    {
        foreach (var incident in cop.Incidents.Where(i => i.Status != IncidentStatus.Closed))
        {
            Track(context, $"critical:{incident.Id}", incident.Priority == IncidentPriority.Critical,
                AlertCategory.Critical, AlertSeverity.Critical,
                $"{incident.Number} upgraded to CRITICAL",
                $"{incident.Name ?? EventDescriber.Humanize(incident.Type)} at {incident.Address ?? "reported location"}.",
                incident.Id);

            Track(context, $"mci:{incident.Id}", incident.CasualtiesReported >= options.MassCasualtyThreshold,
                AlertCategory.Critical, AlertSeverity.Critical,
                $"Possible mass-casualty incident at {incident.Number}",
                $"{incident.CasualtiesReported} casualties reported ({incident.CasualtiesConfirmed} confirmed). Consider MCI procedures.",
                incident.Id);
        }
    }

    private void CheckResourceShortages(SimulationContext context)
    {
        foreach (var group in cop.Units.GroupBy(u => ResourceGroups.For(u.Type)))
        {
            var total = group.Count();
            if (total < options.ShortageMinimumFleet) continue;

            var available = group.Count(u => u.Status == UnitStatus.Available);
            var key = $"shortage:{group.Key}";

            if (available <= options.ShortageThreshold)
            {
                Track(context, key, true, AlertCategory.ResourceShortage, AlertSeverity.Warning,
                    $"{group.Key}: {available} of {total} available",
                    available == 0
                        ? $"No {group.Key.ToLowerInvariant()} remain available in the operational area. Consider mutual aid."
                        : $"Only {available} {group.Key.ToLowerInvariant()} remain available in the operational area.");
            }
            else if (available > options.ShortageThreshold + 1)
            {
                // Hysteresis: re-arm only once there is real slack again, so the alert doesn't flap.
                _active.Remove(key);
            }
        }
    }

    private void CheckWeather(SimulationContext context)
    {
        if (cop.Weather is not { } weather) return;
        if (_lastWeather is null || _lastWeather.ObservedAt == weather.ObservedAt)
        {
            _lastWeather ??= weather;
            return;
        }

        var previous = _lastWeather;
        _lastWeather = weather;

        var shift = Math.Abs(((weather.WindFromDegrees - previous.WindFromDegrees + 540) % 360) - 180);
        var speedChange = Math.Abs(weather.WindSpeedMps - previous.WindSpeedMps);
        if (shift < options.WindShiftDegrees && speedChange < options.WindSpeedChangeMps) return;

        string Direction(PerceivedWeather w) =>
            $"{GeoMath.CompassPoint(w.WindFromDegrees)} → {GeoMath.CompassPoint(w.WindFromDegrees + 180)}";

        var affected = cop.Zones.Count(z => z.Type is ZoneType.EvacuationZone or ZoneType.ShelterInPlace
            or ZoneType.HotZone or ZoneType.WarmZone or ZoneType.ColdZone);
        Raise(context, AlertCategory.SituationChange, AlertSeverity.Warning,
            "Wind direction changed",
            $"Previous: {Direction(previous)} {previous.WindSpeedMps:F0} m/s. Current: {Direction(weather)} {weather.WindSpeedMps:F0} m/s." +
            (affected > 0 ? $" {affected} hazard/evacuation zone(s) potentially affected." : ""));
    }

    private void CheckCommunications(SimulationContext context)
    {
        foreach (var unit in cop.Units)
        {
            if (!UnitStatusRules.IsCommitted(unit.Status) || !unit.CommsConnected || unit.LastContactAt is not { } last)
                continue;

            var silence = context.SimTime - last;
            if (silence < options.CommunicationTimeout) continue;

            var position = unit.Location is { } p
                ? $"{p.Y:F4}, {p.X:F4}" + (unit.AssignedIncident is { } i ? $" ({i.Number})" : "")
                : "unknown";
            // The COP marks the unit as out of contact when this alert is applied, which stops repeats.
            Raise(context, AlertCategory.CommunicationFailure, AlertSeverity.Warning,
                $"{unit.Callsign} not heard from",
                $"{unit.Callsign} has not transmitted for {silence.TotalMinutes:F0} minutes. Last known position: {position}.",
                unit.AssignedIncidentId, unit.Id);
        }
    }

    private void Track(SimulationContext context, string key, bool condition, AlertCategory category,
        AlertSeverity severity, string title, string message, Guid? incidentId = null, Guid? unitId = null)
    {
        if (!condition)
        {
            _active.Remove(key);
            return;
        }

        if (_active.Add(key))
            Raise(context, category, severity, title, message, incidentId, unitId);
    }

    private static void Raise(SimulationContext context, AlertCategory category, AlertSeverity severity,
        string title, string message, Guid? incidentId = null, Guid? unitId = null) =>
        context.EmitPerceived(new AlertRaised(Guid.NewGuid(), category, severity, title, message, incidentId, unitId),
            EventSources.Cop);
}

/// <summary>Bound from the "Attention" configuration section.</summary>
public sealed class AttentionOptions
{
    public const string SectionName = "Attention";

    public TimeSpan CommunicationTimeout { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>Raise a shortage alert when this many or fewer units of a type remain available.</summary>
    public int ShortageThreshold { get; set; } = 1;

    /// <summary>Only watch resource types with at least this many units.</summary>
    public int ShortageMinimumFleet { get; set; } = 2;

    public int MassCasualtyThreshold { get; set; } = 10;
    public double WindShiftDegrees { get; set; } = 45;
    public double WindSpeedChangeMps { get; set; } = 5;
}
