using Emergency_Response_Simulator.Simulation.Engine;

namespace Emergency_Response_Simulator.Simulation.Scenarios;

/// <summary>Sets up a session's starting world: agencies, units, weather, scheduled injects.</summary>
public interface IScenario
{
    string Name { get; }

    Task SeedAsync(IEventPublisher publisher, CancellationToken cancellationToken = default);
}
