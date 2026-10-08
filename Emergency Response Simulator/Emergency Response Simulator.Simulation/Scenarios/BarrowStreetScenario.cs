using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using static Emergency_Response_Simulator.Simulation.Scenarios.Inject;

namespace Emergency_Response_Simulator.Simulation.Scenarios;

/// <summary>
/// Training scenario: a fire and explosion in a warehouse unit near Barrow Street, with a suspected
/// chemical store next door. Fictional, set on real streets.
/// Information arrives late, imprecise and partly wrong; the trainee must open the incident, decide
/// what to believe, commit resources and react to the wind shift and a unit falling silent.
/// </summary>
public static class BarrowStreetScenario
{
    public const string Key = "barrow-street";
    public const string Name = "Barrow Street warehouse fire";

    /// <summary>Where the fire really is. Callers place it slightly wrong.</summary>
    public static readonly GeoPoint FireLocation = new(53.34045, -6.23710);

    public static ScriptedInjectSystem Create()
    {
        var fire = Guid.NewGuid();

        return new ScriptedInjectSystem(Name,
        [
            new(TimeSpan.FromSeconds(20), "Fire and explosion start in the warehouse (truth)",
                _ => [Truth(new WorldIncidentStarted(fire, IncidentType.Explosion, FireLocation, Severity: 0.4, ActualCasualties: 6))]),

            new(TimeSpan.FromSeconds(60), "First 999 call: vague location",
                _ => [Perceived(new CallReceived(Guid.NewGuid(), "999 caller (mobile)",
                    "Loud bang and smoke from a warehouse somewhere near Barrow Street",
                    GeoMath.Destination(FireLocation, 300, 120), 150))]),

            new(TimeSpan.FromSeconds(105), "Second call: better location, people affected",
                _ => [Perceived(new CallReceived(Guid.NewGuid(), "999 caller (landline)",
                    "Fire in the warehouse unit on Barrow Street, people coming out coughing",
                    GeoMath.Destination(FireLocation, 90, 25), 40))]),

            new(TimeSpan.FromSeconds(150), "Social media rumour (wrong)",
                _ => [Perceived(new ReportReceived(Guid.NewGuid(), null, ReportSource.Media, "Social media post",
                    "Huge explosion at the tech offices on Grand Canal Dock, dozens hurt!!", Confidence.Low,
                    VerificationStatus.Suspected, new GeoPoint(53.3395, -6.2365), 500))]),

            new(TimeSpan.FromMinutes(4), "Fire grows (truth)",
                _ => [Truth(new WorldIncidentChanged(fire, Severity: 0.6, ActualCasualties: 9, Extinguished: false))]),

            new(TimeSpan.FromMinutes(5), "Passing police patrol confirms a large fire",
                context => [Perceived(new ReportReceived(Guid.NewGuid(), null, ReportSource.FieldUnit, "Police 14",
                    "Large fire visible from Grand Canal Street, black smoke drifting east", Confidence.High,
                    VerificationStatus.Reported, FireLocation, 30, UnitIdByCallsign(context, "Police 14")))]),

            new(TimeSpan.FromMinutes(5.5), "Wind really backs to the south-west (truth)",
                _ => [Truth(new WeatherChanged(WindFromDegrees: 210, WindSpeedMps: 7, TemperatureC: 13, RelativeHumidity: 0.68))]),

            new(TimeSpan.FromMinutes(6.5), "Met service reports the wind shift",
                _ => [Perceived(new WeatherObserved("Met service", new GeoPoint(53.3498, -6.2603),
                    WindFromDegrees: 210, WindSpeedMps: 7, TemperatureC: 13, RelativeHumidity: 0.68))]),

            new(TimeSpan.FromMinutes(7), "Neighbour warns of chemical storage",
                _ => [Perceived(new ReportReceived(Guid.NewGuid(), null, ReportSource.Public, "Facility manager, unit 4",
                    "The unit next door stores chlorine-based cleaning chemicals, maybe 40 drums", Confidence.Medium,
                    VerificationStatus.Suspected, GeoMath.Destination(FireLocation, 45, 40), 20))]),

            new(TimeSpan.FromMinutes(8), "Engine 7's radio fails (truth: command only notices the silence)",
                context => UnitIdByCallsign(context, "Engine 7") is { } engine7
                    ? [Truth(new UnitRadioFailed(engine7, Failed: true))]
                    : []),

            new(TimeSpan.FromMinutes(10), "Hospital reports pressure on the emergency department",
                _ => [Perceived(new AlertRaised(Guid.NewGuid(), AlertCategory.Critical, AlertSeverity.Critical,
                    "St. James's Hospital: ED near capacity",
                    "Emergency department at 95% capacity; can accept 3 more P1 casualties.", null, null))]),

            new(TimeSpan.FromMinutes(18), "Engine 7's radio recovers (truth)",
                context => UnitIdByCallsign(context, "Engine 7") is { } engine7
                    ? [Truth(new UnitRadioFailed(engine7, Failed: false))]
                    : []),
        ]);
    }

    private static Guid? UnitIdByCallsign(SimulationContext context, string callsign) =>
        context.World.Units.Values.FirstOrDefault(u => u.Callsign == callsign)?.Id;
}
