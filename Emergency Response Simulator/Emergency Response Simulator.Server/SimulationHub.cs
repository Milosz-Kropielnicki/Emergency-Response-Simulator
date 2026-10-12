using System.Text.Json;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Microsoft.AspNetCore.SignalR;

namespace Emergency_Response_Simulator.Server;

/// <summary>
/// The one hub clients talk to. A connection first opens a session (instructor, with the PIN), joins one (code, name,
/// role) or resumes its place (reconnect token). From then on the server knows who it is: what it may see, and whom
/// to stamp on the events its commands produce (Phase 11 M2). Nothing a client sends says who it is after that.
/// </summary>
public sealed class SimulationHub(SessionManager sessions, CommandDispatcher dispatcher) : Hub<ISimulationClient>
{
    private const string SessionKey = "session";
    private const string ParticipantKey = "participant";

    private SessionRuntime Session => Context.Items.TryGetValue(SessionKey, out var value) && value is SessionRuntime session
        ? session
        : throw new HubException("Join a session first.");

    private ParticipantRecord Participant => Session.Find((Guid)Context.Items[ParticipantKey]!) ?? throw new HubException("Join a session first.");

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(SessionKey, out var value) && value is SessionRuntime session && Context.Items[ParticipantKey] is Guid participant)
        {
            session.Feed.Unsubscribe(Context.ConnectionId);
            session.Disconnected(participant, Context.ConnectionId);
        }
        return base.OnDisconnectedAsync(exception);
    }

    // ---- Getting in ----

    public IReadOnlyList<ScenarioInfo> ListScenarios() => ScenarioCatalog.All;

    public async Task<JoinTicket> CreateSession(CreateSessionRequest request)
    {
        var (session, ticket) = await sessions.CreateAsync(request, Context.ConnectionId);
        await EnterAsync(session, ticket);
        return ticket;
    }

    public async Task<JoinTicket> JoinSession(JoinSessionRequest request)
    {
        var (session, ticket) = await sessions.JoinAsync(request, Context.ConnectionId);
        await EnterAsync(session, ticket);
        return ticket;
    }

    /// <summary>Back after the link dropped, as the same participant.</summary>
    public async Task<JoinTicket> Resume(string reconnectToken)
    {
        var (session, ticket) = await sessions.ResumeAsync(reconnectToken, Context.ConnectionId);
        await EnterAsync(session, ticket);
        return ticket;
    }

    private async Task EnterAsync(SessionRuntime session, JoinTicket ticket)
    {
        Context.Items[SessionKey] = session;
        Context.Items[ParticipantKey] = ticket.ParticipantId;
        await Groups.AddToGroupAsync(Context.ConnectionId, SimulationHubProtocol.PerceivedGroup(session.Id));
        if (ticket.Truth)
            await Groups.AddToGroupAsync(Context.ConnectionId, SimulationHubProtocol.TruthGroup(session.Id));
    }

    public async Task Leave()
    {
        var session = Session;
        await session.LeaveAsync(Participant);
        session.Feed.Unsubscribe(Context.ConnectionId);
        Context.Items.Remove(SessionKey);
    }

    // ---- The session ----

    public SessionInfo Hello() => Session.InfoFor(Participant);

    /// <summary>
    /// Starts (or restarts, after a reconnect) this connection's event stream after the last sequence it applied, and
    /// sends the current traffic picture (and, for the instructor, the world) so it doesn't wait for the next push.
    /// </summary>
    public async Task Subscribe(long afterSequence)
    {
        var session = Session;
        var truth = Participant.Truth;
        session.Feed.Subscribe(Context.ConnectionId, afterSequence, truth, Participant.Assessor);
        await Clients.Caller.TrafficFeed(TrafficState.From(session.Services.GetRequiredService<TrafficFeed>().Snapshot));
        if (!truth) return;
        await Clients.Caller.WorldSnapshot(session.Engine.Snapshot);
        await Clients.Caller.LiveTraffic(TrafficState.From(session.Services.GetRequiredService<LiveTraffic>().Snapshot));
    }

    /// <summary>Calls a method of one of the service contracts, on behalf of this participant.</summary>
    public Task<JsonElement?> Call(string service, string method, JsonElement[] args) =>
        dispatcher.InvokeAsync(Session, Participant, service, method, args, Context.ConnectionAborted);

    // ---- The instructor's lifecycle controls and roles ----

    public Task StartSession() => Session.BeginAsync(Instructor());

    public Task EndSession(string reason) => Session.EndAsync(Instructor(), reason);

    public Task AssignRole(Guid participantId, UserRole role) => Session.AssignRoleAsync(Instructor(), participantId, role);

    /// <summary>Lets an observer see ground truth, or stops them (Phase 11 M6). Instructor only.</summary>
    public Task SetObserverTruth(Guid participantId, bool granted) => Session.SetObserverTruthAsync(Instructor(), participantId, granted);

    /// <summary>
    /// Hands this participant's role to another, who takes it; this participant takes <paramref name="newRole"/>. Once the
    /// exercise runs, a full briefing takes longer than a quick changeover but passes the role's open business on (M3).
    /// </summary>
    public Task HandOverRole(Guid toParticipantId, UserRole newRole, bool fullBriefing) =>
        Session.HandOverAsync(Participant, toParticipantId, newRole, fullBriefing);

    private ParticipantRecord Instructor() =>
        Participant is { Role: UserRole.Instructor } instructor ? instructor : throw new HubException("Only the instructor can do that.");
}
