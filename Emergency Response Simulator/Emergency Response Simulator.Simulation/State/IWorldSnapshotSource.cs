namespace Emergency_Response_Simulator.Simulation.State;

/// <summary>
/// Where instructor views get ground truth from: the engine itself in a standalone session, or the server's
/// once-a-second push in a client (Phase 11). Never given to trainee views.
/// </summary>
public interface IWorldSnapshotSource
{
    WorldSnapshot Snapshot { get; }
}
