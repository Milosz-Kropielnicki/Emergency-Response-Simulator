using System.Text.Json;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.State;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Emergency_Response_Simulator.Server;

/// <summary>
/// The one hub clients talk to (Phase 11 M1). A connection says who it is when it connects (role, and the instructor
/// PIN); the server decides what it may see and puts it in groups accordingly. Clients then ask for the event stream
/// from their last applied sequence and send commands through <see cref="Call"/>.
/// </summary>
public sealed class SimulationHub(EventFeed feed, CommandDispatcher dispatcher, ISimulationControl clock, IWorldSnapshotSource world,
    LiveTraffic live, TrafficFeed traffic, IAarService aar, IOptions<ServerOptions> options) : Hub<ISimulationClient>
{
    private const string TruthKey = "truth";

    private bool Truth => Context.Items.TryGetValue(TruthKey, out var value) && value is true;

    public override async Task OnConnectedAsync()
    {
        var query = Context.GetHttpContext()?.Request.Query;
        var instructor = string.Equals(query?[SimulationHubProtocol.RoleQuery], SimulationHubProtocol.InstructorRole, StringComparison.OrdinalIgnoreCase);
        if (instructor)
        {
            var pin = options.Value.InstructorPin;
            if (string.IsNullOrEmpty(pin) || query?[SimulationHubProtocol.PinQuery].ToString() != pin)
                throw new HubException("Wrong instructor PIN.");
        }

        Context.Items[TruthKey] = instructor;
        await Groups.AddToGroupAsync(Context.ConnectionId, SimulationHubProtocol.PerceivedGroup);
        if (instructor)
            await Groups.AddToGroupAsync(Context.ConnectionId, SimulationHubProtocol.TruthGroup);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        feed.Unsubscribe(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>The session this server runs, whether this connection sees truth, and the clock.</summary>
    public SessionInfo Hello() => new(clock.SessionId, Truth, Broadcaster.ClockOf(clock), aar.CanCompareBranches);

    /// <summary>
    /// Starts (or restarts, after a reconnect) this connection's event stream after the last sequence it applied, and
    /// sends the current traffic picture (and, for the instructor, the world) so it doesn't wait for the next push.
    /// </summary>
    public async Task Subscribe(long afterSequence)
    {
        feed.Subscribe(Context.ConnectionId, afterSequence, Truth);
        await Clients.Caller.TrafficFeed(TrafficState.From(traffic.Snapshot));
        if (!Truth) return;
        await Clients.Caller.WorldSnapshot(world.Snapshot);
        await Clients.Caller.LiveTraffic(TrafficState.From(live.Snapshot));
    }

    /// <summary>Calls a method of one of the service contracts on the server.</summary>
    public Task<JsonElement?> Call(string service, string method, JsonElement[] args) =>
        dispatcher.InvokeAsync(service, method, args, Truth, Context.ConnectionAborted);
}
