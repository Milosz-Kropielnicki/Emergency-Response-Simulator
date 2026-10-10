using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Simulation;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.EventStore;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Emergency_Response_Simulator.Simulation.Systems;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Emergency_Response_Simulator.Tests;

/// <summary>The services as the app wires them: every system resolves and the Barrow Street world runs.</summary>
public class CompositionTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Simulation:Scenario"] = BarrowStreetScenario.Key,
                ["Simulation:StartTime"] = TestHarness.Start.ToString("O"),
            }.Concat(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEventStore, InMemoryEventStore>();
        services.AddSimulation(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_app_wires_every_world_system_and_the_scenario_runs()
    {
        await using var provider = Build();
        var systems = provider.GetServices<ISimulationSystem>().Select(s => s.GetType()).ToList();
        Assert.Contains(typeof(HazardSystem), systems);
        Assert.Contains(typeof(CivilianSystem), systems);
        Assert.Contains(typeof(MedicalSystem), systems);
        Assert.Contains(typeof(TrafficSystem), systems);
        Assert.Contains(typeof(InfrastructureSystem), systems);
        Assert.Contains(typeof(WeatherSystem), systems);
        Assert.Contains(typeof(AgencyAiSystem), systems);
        Assert.Contains(typeof(HazardReportingSystem), systems);

        // Routing plans with the lagged feed; vehicles drive by live traffic.
        Assert.IsType<TrafficFeed>(provider.GetRequiredService<ITrafficModel>());
        Assert.NotSame(provider.GetRequiredService<LiveTraffic>(), provider.GetRequiredService<ITrafficModel>());

        var engine = provider.GetRequiredService<SimulationEngine>();
        _ = provider.GetRequiredService<ICopService>(); // the live COP follows the stream from here
        await new DemoRosterScenario().SeedAsync(engine);
        for (var i = 0; i < 15 * 12; i++)
            await engine.StepAsync(TimeSpan.FromSeconds(5));

        Assert.NotEmpty(engine.World.Hazards);
        Assert.NotEmpty(engine.World.Civilians);
        Assert.NotEmpty(provider.GetRequiredService<ICopService>().Hospitals);
    }

    [Fact]
    public async Task The_world_models_can_be_switched_off_for_a_scripted_only_session()
    {
        await using var provider = Build(("Simulation:WorldModels", "false"));
        var systems = provider.GetServices<ISimulationSystem>().Select(s => s.GetType()).ToList();

        Assert.DoesNotContain(typeof(HazardSystem), systems);
        Assert.DoesNotContain(typeof(CivilianSystem), systems);
        Assert.Contains(typeof(UnitResponseSystem), systems);
        Assert.Contains(typeof(ScriptedInjectSystem), systems);
    }
}
