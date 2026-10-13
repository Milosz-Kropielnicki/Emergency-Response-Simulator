using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Simulation.Aar;
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
        var crews = configuration.GetSection(CrewOptions.SectionName).Get<CrewOptions>() ?? new CrewOptions();
        services.AddSingleton(crews);

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
        services.AddSingleton<IWorldSnapshotSource>(sp => sp.GetRequiredService<SimulationEngine>());

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

            // Phase 14: the public around the response, families looking for their own, and rumour.
            if (configuration.GetValue($"{SimulationOptions.SectionName}:{nameof(SimulationOptions.PublicPressures)}", true))
            {
                services.AddSingleton<ISimulationSystem>(_ => new PublicOrderSystem());
                services.AddSingleton<ISimulationSystem>(sp => new ReunificationSystem(sp.GetRequiredService<IRoutingService>()));
                services.AddSingleton<ISimulationSystem>(_ => new RumourSystem());
            }

            // Phase 13: the Python dispersion model drives real releases, and keeps command's projections current.
            // Without the service (or with this off) the in-engine Gaussian plume carries the session.
            if (configuration.GetValue($"{SimulationOptions.SectionName}:{nameof(SimulationOptions.DispersionService)}", true))
                services.AddSingleton<ISimulationSystem>(sp => new PlumeDispersionSystem(
                    sp.GetService<IPlumeService>(), sp.GetRequiredService<ICopService>(), sp.GetService<IGisService>()));
        }
        if (configuration.GetValue($"{SimulationOptions.SectionName}:{nameof(SimulationOptions.HumanFactors)}", true))
            services.AddSingleton<ISimulationSystem>(sp => new CrewSystem(sp.GetRequiredService<CrewOptions>()));
        if (configuration.GetValue($"{SimulationOptions.SectionName}:{nameof(SimulationOptions.CommsRealism)}", true))
            services.AddSingleton<ISimulationSystem>(sp => new CommsSystem(sp.GetRequiredService<CommsOptions>(), sp.GetRequiredService<IRoutingService>()));
        if (configuration.GetValue($"{SimulationOptions.SectionName}:{nameof(SimulationOptions.MultiAgency)}", true))
            services.AddSingleton<ISimulationSystem>(_ => new InteragencySystem());
        if (configuration.GetValue($"{SimulationOptions.SectionName}:{nameof(SimulationOptions.PoliticalPressure)}", true))
            services.AddSingleton<ISimulationSystem>(_ => new PoliticalPressureSystem());
        if (configuration.GetValue($"{SimulationOptions.SectionName}:{nameof(SimulationOptions.MediaCoverage)}", true))
            services.AddSingleton<ISimulationSystem>(_ => new MediaCoverageSystem());
        services.AddSingleton<ISimulationSystem>(sp => new AttentionMonitor(
            sp.GetRequiredService<ICopService>(), sp.GetRequiredService<AttentionOptions>(), sp.GetRequiredService<IRoutingService>()));
        if (configuration[$"{SimulationOptions.SectionName}:Scenario"] == BarrowStreetScenario.Key)
            services.AddSingleton<ISimulationSystem>(_ => BarrowStreetScenario.Create(
                configuration.GetValue<bool>($"{SimulationOptions.SectionName}:{nameof(SimulationOptions.DemoAutoResponse)}")));

        // The plume service and the GIS database are both optional: C2 only needs them to run a projection (Phase 13).
        services.AddSingleton<IC2Service>(sp => new C2Service(sp.GetRequiredService<ICopService>(), sp.GetRequiredService<IEventPublisher>(),
            sp.GetService<IPlumeService>(), sp.GetService<IGisService>()));
        services.AddSingleton<IAssessmentService>(sp => new Instructor.AssessmentService(sp.GetRequiredService<ICopService>(), sp.GetRequiredService<IEventPublisher>()));
        services.AddSingleton<IInstructorService>(sp => new Instructor.InstructorService(sp.GetRequiredService<SimulationEngine>(), sp.GetServices<ISimulationSystem>(), sp.GetRequiredService<ICopService>()));
        services.AddSingleton<ICommsService>(sp => new CommsService(sp.GetRequiredService<ICopService>(), sp.GetRequiredService<IC2Service>(),
            sp.GetRequiredService<IEventStore>(), sp.GetRequiredService<SimulationSession>().Id));
        // Branch re-runs need a scripted scenario; they share the (read-only) road network and terrain once loaded.
        var scenario = configuration[$"{SimulationOptions.SectionName}:Scenario"];
        services.AddSingleton<IAarService>(sp => new AarService(sp.GetRequiredService<IEventStore>(), sp.GetRequiredService<IRoutingService>(),
            BranchRunner.CanRun(scenario)
                ? new BranchRunner(() => sp.GetRequiredService<RoutingService>().Network, sp.GetRequiredService<HazardTerrain>())
                : null));
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
