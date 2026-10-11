using System.Text.Json;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Services;

namespace Emergency_Response_Simulator.Client;

/// <summary>The server's engine clock: state from its pushes, start/pause/speed sent to it.</summary>
public sealed class RemoteSimulationControl(SimulationConnection connection) : ISimulationControl
{
    public Guid SessionId => connection.Session?.SessionId ?? Guid.Empty;
    public DateTimeOffset SimTime => connection.Clock.SimTime;
    public bool IsRunning => connection.Clock.IsRunning;
    public double TimeScale => connection.Clock.TimeScale;

    public Task StartAsync(CancellationToken cancellationToken = default) => Call(nameof(StartAsync), [], cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken = default) => Call(nameof(PauseAsync), [], cancellationToken);

    public Task SetTimeScaleAsync(double timeScale, CancellationToken cancellationToken = default) =>
        Call(nameof(SetTimeScaleAsync), [JsonSerializer.SerializeToElement(timeScale, WireJson.Options)], cancellationToken);

    private Task Call(string method, JsonElement[] args, CancellationToken cancellationToken) =>
        connection.CallAsync(SimulationHubProtocol.Services.Control, method, args, cancellationToken);
}

/// <summary>
/// The after-action review from a client. Replay and the timeline run on the client's own copy of the stream (so a
/// trainee's copy has no truth in it); the full review and branch re-runs need the whole stream and run on the server.
/// </summary>
public sealed class ClientAarService(SimulationConnection connection) : IAarService
{
    private readonly AarService _local = new(connection.Mirror);
    private readonly IAarService _remote = RemoteProxy.Create<IAarService>(connection, SimulationHubProtocol.Services.Aar);

    public bool CanCompareBranches => connection.Session?.CanCompareBranches == true;

    public Task<IReadOnlyList<SimEvent>> GetTimelineAsync(Guid sessionId, bool includeTruth, CancellationToken cancellationToken = default) =>
        _local.GetTimelineAsync(sessionId, includeTruth, cancellationToken);

    public Task<ICopService> ReplayToAsync(Guid sessionId, DateTimeOffset simTime, CancellationToken cancellationToken = default) =>
        _local.ReplayToAsync(sessionId, simTime, cancellationToken);

    public Task<AarReport> BuildReportAsync(Guid sessionId, string? title = null, CancellationToken cancellationToken = default) =>
        _remote.BuildReportAsync(sessionId, title, cancellationToken);

    public Task<IReadOnlyList<AarOutcome>> CompareBranchesAsync(Guid sessionId, int runs = 2, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("Re-running on the server…");
        return _remote.CompareBranchesAsync(sessionId, runs, null, cancellationToken);
    }
}
