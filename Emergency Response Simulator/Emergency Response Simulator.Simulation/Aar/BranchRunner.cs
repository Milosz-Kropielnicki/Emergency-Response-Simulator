using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.EventStore;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Emergency_Response_Simulator.Simulation.State;
using Emergency_Response_Simulator.Simulation.Systems;
using Microsoft.Extensions.DependencyInjection;

namespace Emergency_Response_Simulator.Simulation.Aar;

/// <summary>A command policy to re-run the scenario under.</summary>
public enum BranchPolicy
{
    /// <summary>The recommended response (<see cref="ExpertPolicy"/>).</summary>
    Recommended,

    /// <summary>Nobody in command: the world runs on its own.</summary>
    NoResponse,
}

/// <summary>
/// Re-runs the session's scenario headless under other command policies (Design Document §22: branching AAR), so the
/// trainee's outcome can be set beside what the recommended response, and no response at all, achieved in the same
/// world. Each branch is a fresh, private engine with its own event store, traffic and COP; it shares only read-only
/// data (the road network and terrain) with the live session.
/// </summary>
/// <remarks>
/// The world is stochastic (the fire, casualties, the collapse are seeded from ids that differ every run), so each
/// policy is run several times and averaged; the outcome says how many runs it covers.
/// </remarks>
public sealed class BranchRunner(Func<RoadNetwork?>? network = null, IHazardTerrain? terrain = null)
{
    /// <summary>Simulation time per step: coarse enough to be quick, fine enough for the models.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(6);

    /// <summary>Only Barrow Street can be re-run: it is the one scripted scenario.</summary>
    public static bool CanRun(string? scenario) => scenario == BarrowStreetScenario.Key;

    /// <summary>Runs a policy <paramref name="runs"/> times and averages the outcomes.</summary>
    /// <param name="start">When the session started: traffic follows the time of day.</param>
    public async Task<AarOutcome> RunAsync(BranchPolicy policy, DateTimeOffset start, TimeSpan duration, int runs = 2, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var outcomes = new List<AarOutcome>();
        for (var run = 1; run <= runs; run++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"{Label(policy)}: run {run} of {runs}…");
            outcomes.Add((await RunOnceAsync(policy, start, duration, cancellationToken)).Outcomes[0]);
        }

        double Mean(Func<AarOutcome, double> value) => outcomes.Average(value);
        double? MeanOf(Func<AarOutcome, double?> value) =>
            outcomes.Select(value).OfType<double>().ToList() is { Count: > 0 } values ? values.Average() : null;
        return new AarOutcome(Label(policy), runs, Mean(o => o.Injured), Mean(o => o.Deaths), Mean(o => o.FirefightersInDistress),
            Mean(o => o.PeakFireSquareMeters), MeanOf(o => o.FirstOnSceneMinutes), MeanOf(o => o.FirstDispatchMinutes),
            $"Average of {runs} run(s); deaths ranged {outcomes.Min(o => o.Deaths):F0}–{outcomes.Max(o => o.Deaths):F0}.",
            outcomes[0].KeyActions);
    }

    public static string Label(BranchPolicy policy) => policy switch
    {
        BranchPolicy.Recommended => "Recommended response",
        _ => "No response",
    };

    /// <summary>One run of the scenario under a policy, fully analysed (its decisions are the policy's).</summary>
    public async Task<AarReport> RunOnceAsync(BranchPolicy policy, DateTimeOffset start, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var store = new InMemoryEventStore();
        var world = new WorldState();
        var cop = new PerceivedState();

        // Its own traffic and routing (the live session's traffic feed must not be touched), on the shared road network.
        var services = new ServiceCollection().BuildServiceProvider();
        var feed = new TrafficFeed(new TimeOfDayTraffic());
        var live = new LiveTraffic(new TimeOfDayTraffic());
        var routing = new RoutingService(feed, services);
        if (network?.Invoke() is { } roads) routing.Use(roads);
        var ground = terrain ?? new UniformTerrain();

        var systems = new List<ISimulationSystem>
        {
            BarrowStreetScenario.Create(), new WeatherSystem(), new HazardSystem(ground), new InfrastructureSystem(routing),
            new UnitResponseSystem(routing, live), new MedicalSystem(routing), new CivilianSystem(ground, routing: routing),
            new AgencyAiSystem(routing), new CommandResponseSystem(), new TrafficSystem(routing, live, feed),
            new HazardReportingSystem(routing), new CrewSystem(), new CommsSystem(routing: routing),
            new AttentionMonitor(cop, new AttentionOptions(), routing),
        };
        if (policy == BranchPolicy.Recommended) systems.Add(new ExpertPolicy(cop));

        var engine = new SimulationEngine(store, world, systems, new SimulationOptions { StartTime = start });
        cop.Follow(store, engine.SessionId);
        await new DemoRosterScenario().SeedAsync(engine, cancellationToken);

        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += Step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await engine.StepAsync(Step, cancellationToken);
        }

        var events = new List<Core.Events.SimEvent>();
        await foreach (var simEvent in store.ReadAsync(engine.SessionId, cancellationToken: cancellationToken))
            events.Add(simEvent);
        return AarAnalyzer.Analyze(engine.SessionId, events, Label(policy));
    }
}
