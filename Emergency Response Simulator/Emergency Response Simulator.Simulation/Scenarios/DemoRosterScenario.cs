using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;

namespace Emergency_Response_Simulator.Simulation.Scenarios;

/// <summary>
/// A small fictional roster around Dublin so the shell has resources to show.
/// No incidents: those arrive with the scenario editor and simulation systems.
/// </summary>
public sealed class DemoRosterScenario : IScenario
{
    public string Name => "Demo roster";

    public async Task SeedAsync(IEventPublisher publisher, CancellationToken cancellationToken = default)
    {
        var fire = Guid.NewGuid();
        var ems = Guid.NewGuid();
        var police = Guid.NewGuid();

        await Register(new AgencyRegistered(fire, "City Fire Brigade", "Fire", AgencyType.Fire));
        await Register(new AgencyRegistered(ems, "Ambulance Service", "EMS", AgencyType.Ems));
        await Register(new AgencyRegistered(police, "Metropolitan Police", "Police", AgencyType.Police));

        var station3 = new GeoPoint(53.3498, -6.2603);
        var station8 = new GeoPoint(53.3331, -6.2489);
        var hospital = new GeoPoint(53.3389, -6.2333);

        await AddUnit("Engine 4", UnitType.Engine, fire, station3, "Station 3", 5, "Structural", "BreathingApparatus");
        await AddUnit("Engine 7", UnitType.Engine, fire, station8, "Station 8", 5, "Structural", "BreathingApparatus");
        await AddUnit("Engine 12", UnitType.Engine, fire, station3, "Station 3", 5, "Structural", "BreathingApparatus");
        await AddUnit("Engine 14", UnitType.Engine, fire, station8, "Station 8", 4, "Structural", "BreathingApparatus");
        await AddUnit("Ladder 3", UnitType.Ladder, fire, station8, "Station 8", 4, "AerialLadder", "Rescue");
        await AddUnit("Hazmat 2", UnitType.Hazmat, fire, station3, "Station 3", 6, "Hazmat", "Decontamination");
        await AddUnit("Ambulance 14", UnitType.AmbulanceAls, ems, hospital, "St. Mary's", 2, "ALS");
        await AddUnit("Ambulance 21", UnitType.AmbulanceAls, ems, hospital, "St. Mary's", 2, "ALS");
        await AddUnit("Ambulance 30", UnitType.AmbulanceBls, ems, station8, "Station 8", 2, "BLS");
        await AddUnit("Police 14", UnitType.Patrol, police, new GeoPoint(53.3440, -6.2672), "Central", 2);
        await AddUnit("Police 21", UnitType.Patrol, police, new GeoPoint(53.3520, -6.2440), "North", 2);
        await AddUnit("Traffic 5", UnitType.Traffic, police, new GeoPoint(53.3405, -6.2550), "Central", 1);

        await publisher.PublishAsync(
            new WeatherChanged(WindFromDegrees: 270, WindSpeedMps: 4.5, TemperatureC: 14, RelativeHumidity: 0.72),
            EventVisibility.Truth, EventSources.Scenario, cancellationToken);

        // What command is told: a met-service observation that happens to match the truth for now.
        await Register(new WeatherObserved("Met service", new GeoPoint(53.3498, -6.2603),
            WindFromDegrees: 270, WindSpeedMps: 4.5, TemperatureC: 14, RelativeHumidity: 0.72));

        Task Register(DomainEvent payload) =>
            publisher.PublishAsync(payload, EventVisibility.Perceived, EventSources.Scenario, cancellationToken);

        Task AddUnit(string callsign, UnitType type, Guid agency, GeoPoint at, string station, int crew, params string[] capabilities) =>
            Register(new UnitRegistered(Guid.NewGuid(), callsign, type, agency, at, station, crew, capabilities));
    }
}
