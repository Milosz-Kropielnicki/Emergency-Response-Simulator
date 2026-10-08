using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emergency_Response_Simulator.Simulation.Engine;

/// <summary>Seeds the scenario, then runs the engine's tick loop for the lifetime of the host.</summary>
public sealed class SimulationHostedService(
    SimulationEngine engine,
    IEnumerable<IScenario> scenarios,
    // Taken only so the live COP exists and is following the stream before the first event is published.
    ICopService cop,
    Microsoft.Extensions.Options.IOptions<SimulationOptions> options,
    ILogger<SimulationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = cop;

        foreach (var scenario in scenarios)
        {
            logger.LogInformation("Seeding scenario {Scenario} for session {SessionId}", scenario.Name, engine.SessionId);
            await scenario.SeedAsync(engine, stoppingToken);
        }

        if (options.Value.AutoStart)
            await engine.StartAsync(stoppingToken);

        await engine.RunAsync(stoppingToken);
    }
}
