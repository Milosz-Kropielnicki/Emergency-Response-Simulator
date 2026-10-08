using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Engine;

/// <summary>
/// One piece of world behaviour advanced every tick: unit movement, fire spread, caller behaviour,
/// radio degradation. Systems read and advance <see cref="WorldState"/> and report what happened
/// through <see cref="SimulationContext.Emit"/>.
/// </summary>
public interface ISimulationSystem
{
    /// <summary>Systems run in ascending order each tick.</summary>
    int Order => 0;

    void Update(SimulationContext context);
}

public sealed class SimulationContext(WorldState world, DateTimeOffset simTime, TimeSpan delta)
{
    private readonly List<(EventVisibility Visibility, string Source, DomainEvent Payload)> _emitted = [];

    public WorldState World { get; } = world;

    /// <summary>Simulation time at the end of this tick.</summary>
    public DateTimeOffset SimTime { get; } = simTime;

    /// <summary>Simulation time covered by this tick.</summary>
    public TimeSpan Delta { get; } = delta;

    internal IReadOnlyList<(EventVisibility Visibility, string Source, DomainEvent Payload)> Emitted => _emitted;

    /// <summary>
    /// Records a change in ground truth. The trainee does not see it unless some system later
    /// emits a perceived event about it (a call, a field report, a sensor reading).
    /// </summary>
    public void EmitTruth(DomainEvent payload, string source = EventSources.Engine) =>
        _emitted.Add((EventVisibility.Truth, source, payload));

    /// <summary>Records information reaching the organisation; it will appear on the COP.</summary>
    public void EmitPerceived(DomainEvent payload, string source = EventSources.Engine) =>
        _emitted.Add((EventVisibility.Perceived, source, payload));

    public void Emit(EventVisibility visibility, DomainEvent payload, string source = EventSources.Engine) =>
        _emitted.Add((visibility, source, payload));
}
