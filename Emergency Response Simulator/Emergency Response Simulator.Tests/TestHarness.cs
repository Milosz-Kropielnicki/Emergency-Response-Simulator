using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.EventStore;
using Emergency_Response_Simulator.Simulation.Services;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Tests;

/// <summary>An engine wired to an in-memory store and a live COP, starting at a fixed time.</summary>
internal sealed class TestHarness
{
    public static readonly DateTimeOffset Start = new(2026, 10, 8, 14, 0, 0, TimeSpan.Zero);
    public static readonly GeoPoint Dublin = new(53.344, -6.26);

    public TestHarness(params ISimulationSystem[] systems) : this(_ => systems)
    {
    }

    /// <summary>For systems that need the live COP, such as the attention monitor.</summary>
    public TestHarness(Func<PerceivedState, ISimulationSystem[]> systems)
    {
        Engine = new SimulationEngine(Store, World, systems(Cop), new SimulationOptions { StartTime = Start });
        Cop.Follow(Store, Engine.SessionId);
        C2 = new C2Service(Cop, Engine);
        Iap = new IapService(Cop, Engine, C2, Engine);
        Aar = new AarService(Store);
    }

    public InMemoryEventStore Store { get; } = new();
    public WorldState World { get; } = new();
    public PerceivedState Cop { get; } = new();
    public SimulationEngine Engine { get; }
    public C2Service C2 { get; }
    public IapService Iap { get; }
    public AarService Aar { get; }

    public Task<SimEvent> Perceived(DomainEvent payload) =>
        Engine.PublishAsync(payload, EventVisibility.Perceived, EventSources.Scenario);

    public Task<SimEvent> Truth(DomainEvent payload) =>
        Engine.PublishAsync(payload, EventVisibility.Truth, EventSources.Scenario);

    public async Task<Guid> RegisterUnitAsync(string callsign = "Engine 12", UnitType type = UnitType.Engine)
    {
        var id = Guid.NewGuid();
        await Perceived(new UnitRegistered(id, callsign, type, null, Dublin, "Station 3", 5, ["Structural"]));
        return id;
    }

    public async Task<Guid> CreateIncidentAsync(string number = "INC-00241")
    {
        var id = Guid.NewGuid();
        await Perceived(new IncidentCreated(id, number, IncidentType.StructureFire, IncidentPriority.High, Dublin, "14 Industrial Road", null));
        return id;
    }
}
