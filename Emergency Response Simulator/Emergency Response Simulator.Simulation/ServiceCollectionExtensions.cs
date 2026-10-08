using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Services;
using Emergency_Response_Simulator.Simulation.State;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emergency_Response_Simulator.Simulation;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the engine, the live COP, C2, AAR and the plume client.
    /// An <see cref="IEventStore"/> must be registered separately (in-memory or PostgreSQL).
    /// </summary>
    public static IServiceCollection AddSimulation(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SimulationOptions>(configuration.GetSection(SimulationOptions.SectionName));
        services.Configure<HazardModelOptions>(configuration.GetSection(HazardModelOptions.SectionName));

        services.AddSingleton<WorldState>();
        services.AddSingleton(sp => new SimulationEngine(
            sp.GetRequiredService<IEventStore>(),
            sp.GetRequiredService<WorldState>(),
            sp.GetServices<ISimulationSystem>(),
            sp.GetRequiredService<IOptions<SimulationOptions>>().Value,
            sp.GetService<ILogger<SimulationEngine>>()));
        services.AddSingleton<ISimulationControl>(sp => sp.GetRequiredService<SimulationEngine>());
        services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<SimulationEngine>());

        services.AddSingleton<ICopService>(sp =>
        {
            var cop = new PerceivedState();
            cop.Follow(sp.GetRequiredService<IEventStore>(), sp.GetRequiredService<SimulationEngine>().SessionId);
            return cop;
        });

        services.AddSingleton<IC2Service, C2Service>();
        services.AddSingleton<IAarService, AarService>();

        services.AddHttpClient<IPlumeService, HttpPlumeService>((sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<HazardModelOptions>>().Value;
            http.BaseAddress = options.BaseUrl;
            http.Timeout = options.Timeout;
        });

        services.AddHostedService<SimulationHostedService>();
        return services;
    }
}
