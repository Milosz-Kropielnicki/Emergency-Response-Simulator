using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Emergency_Response_Simulator.Simulation.Services;
using Emergency_Response_Simulator.Simulation.State;
using Emergency_Response_Simulator.Simulation.Systems;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emergency_Response_Simulator.Simulation;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the engine and its systems, the live COP (and the switchable <see cref="CopView"/> for
    /// display), C2, the IAP builder, AAR and the plume client. The world simulation (Phase 6: hazards, civilians,
    /// medical, traffic, infrastructure, weather, AI agencies) runs unless "Simulation:WorldModels" is false.
    /// An <see cref="IEventStore"/> must be registered separately (in-memory or PostgreSQL).
    /// </summary>
    public static IServiceCollection AddSimulation(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SimulationOptions>(configuration.GetSection(SimulationOptions.SectionName));
        services.Configure<HazardModelOptions>(configuration.GetSection(HazardModelOptions.SectionName));
        var attention = configuration.GetSection(AttentionOptions.SectionName).Get<AttentionOptions>() ?? new AttentionOptions();
        services.AddSingleton(attention);
        var civilians = configuration.GetSection(CivilianOptions.SectionName).Get<CivilianOptions>() ?? new CivilianOptions();
        services.AddSingleton(civilians);
        var comms = configuration.GetSection(CommsOptions.SectionName).Get<CommsOptions>() ?? new CommsOptions();
        services.AddSingleton(comms);

        services.AddSingleton(new SimulationSession(Guid.NewGuid()));
        services.AddSingleton<WorldState>();
        services.AddSingleton(sp => new SimulationEngine(
            sp.GetRequiredService<IEventStore>(),
            sp.GetRequiredService<WorldState>(),
            sp.GetServices<ISimulationSystem>(),
            sp.GetRequiredService<IOptions<SimulationOptions>>().Value,
            sp.GetService<ILogger<SimulationEngine>>(),
            sp.GetRequiredService<SimulationSession>().Id));
        services.AddSingleton<ISimulationControl>(sp => sp.GetRequiredService<SimulationEngine>());
        services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<SimulationEngine>());

        services.AddSingleton<ICopService>(sp =>
        {
            var cop = new PerceivedState();
            cop.Follow(sp.GetRequiredService<IEventStore>(), sp.GetRequiredService<SimulationSession>().Id);
            return cop;
        });
        services.AddSingleton(sp => new CopView(sp.GetRequiredService<ICopService>()));

        // Vehicles drive at live (true) speeds; command's estimates and crews' navigation plan with the lagged feed.
        services.AddSingleton(new LiveTraffic(new TimeOfDayTraffic()));
        services.AddSingleton(new TrafficFeed(new TimeOfDayTraffic()));
        services.AddSingleton<ITrafficModel>(sp => sp.GetRequiredService<TrafficFeed>());
        services.AddSingleton<RoutingService>();
        services.AddSingleton<HazardTerrain>();
        services.AddSingleton<IHazardTerrain>(sp => sp.GetRequiredService<HazardTerrain>());
        services.AddSingleton<IRoutingService>(sp => sp.GetRequiredService<RoutingService>());
        services.AddSingleton(sp => new AvlService(
            sp.GetRequiredService<ICopService>(), sp.GetRequiredService<IEventStore>(), sp.GetRequiredService<SimulationSession>().Id));
        services.AddSingleton<IAvlService>(sp => sp.GetRequiredService<AvlService>());

        var worldModels = configuration.GetValue($"{SimulationOptions.SectionName}:{nameof(SimulationOptions.WorldModels)}", true);
        services.AddSingleton<ISimulationSystem>(sp => new UnitResponseSystem(sp.GetRequiredService<RoutingService>(),
            worldModels ? sp.GetRequiredService<LiveTraffic>() : null));
        services.AddSingleton<ISimulationSystem, CommandResponseSystem>();
        if (worldModels)
        {
            services.AddSingleton<ISimulationSystem>(_ => new WeatherSystem());
            services.AddSingleton<ISimulationSystem>(sp => new HazardSystem(sp.GetRequiredService<IHazardTerrain>()));
            services.AddSingleton<ISimulationSystem>(sp => new InfrastructureSystem(sp.GetRequiredService<IRoutingService>()));
            services.AddSingleton<ISimulationSystem>(sp => new MedicalSystem(sp.GetRequiredService<IRoutingService>()));
            services.AddSingleton<ISimulationSystem>(sp => new CivilianSystem(sp.GetRequiredService<IHazardTerrain>(),
                sp.GetRequiredService<CivilianOptions>(), sp.GetRequiredService<IRoutingService>()));
            services.AddSingleton<ISimulationSystem>(sp => new AgencyAiSystem(sp.GetRequiredService<RoutingService>()));
            services.AddSingleton<ISimulationSystem>(sp => new TrafficSystem(sp.GetRequiredService<RoutingService>(),
                sp.GetRequiredService<LiveTraffic>(), sp.GetRequiredService<TrafficFeed>()));
            services.AddSingleton<ISimulationSystem>(sp => new HazardReportingSystem(sp.GetRequiredService<IRoutingService>()));
        }
        if (configuration.GetValue($"{SimulationOptions.SectionName}:{nameof(SimulationOptions.CommsRealism)}", true))
            services.AddSingleton<ISimulationSystem>(sp => new CommsSystem(sp.GetRequiredService<CommsOptions>(), sp.GetRequiredService<IRoutingService>()));
        services.AddSingleton<ISimulationSystem>(sp => new AttentionMonitor(
            sp.GetRequiredService<ICopService>(), sp.GetRequiredService<AttentionOptions>(), sp.GetRequiredService<IRoutingService>()));
        if (configuration[$"{SimulationOptions.SectionName}:Scenario"] == BarrowStreetScenario.Key)
            services.AddSingleton<ISimulationSystem>(_ => BarrowStreetScenario.Create(
                configuration.GetValue<bool>($"{SimulationOptions.SectionName}:{nameof(SimulationOptions.DemoAutoResponse)}")));

        services.AddSingleton<IC2Service, C2Service>();
        services.AddSingleton<ICommsService>(sp => new CommsService(sp.GetRequiredService<ICopService>(), sp.GetRequiredService<IC2Service>(),
            sp.GetRequiredService<IEventStore>(), sp.GetRequiredService<SimulationSession>().Id));
        services.AddSingleton<IAarService, AarService>();
        services.AddSingleton<IIapService>(sp => new IapService(
            sp.GetRequiredService<ICopService>(), sp.GetRequiredService<IEventPublisher>(), sp.GetRequiredService<IC2Service>(),
            sp.GetRequiredService<ISimulationControl>(), sp.GetRequiredService<IRoutingService>(), sp.GetService<IGisService>()));

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
