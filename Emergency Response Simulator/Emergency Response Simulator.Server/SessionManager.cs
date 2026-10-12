using System.Collections.Concurrent;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Remote;
using Emergency_Response_Simulator.Simulation.Scenarios;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Emergency_Response_Simulator.Server;

/// <summary>
/// The server's sessions (Phase 11 M2): an instructor opens one on a scenario and gets a join code; others join with the
/// code, a display name and a role; a dropped participant comes back with their reconnect token. Several sessions can
/// run at once, each with its own engine.
/// </summary>
public sealed class SessionManager(IServiceProvider root, IConfiguration configuration, IOptions<ServerOptions> options, ILogger<SessionManager> logger)
    : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, SessionRuntime> _sessions = new();
    private readonly SemaphoreSlim _creating = new(1, 1);

    public IReadOnlyCollection<SessionRuntime> All => [.. _sessions.Values];

    /// <summary>Sessions whose clocks and pushes still matter.</summary>
    public IEnumerable<SessionRuntime> Live => _sessions.Values.Where(s => s.State != SessionState.Ended);

    public SessionRuntime? Find(Guid sessionId) => _sessions.GetValueOrDefault(sessionId);

    public async Task<(SessionRuntime Session, JoinTicket Ticket)> CreateAsync(CreateSessionRequest request, string connectionId)
    {
        CheckPin(request.Pin);
        var scenario = ScenarioCatalog.Find(request.Scenario) ?? throw new HubException($"Unknown scenario '{request.Scenario}'.");
        var title = string.IsNullOrWhiteSpace(request.Title) ? scenario.Name : request.Title.Trim();

        await _creating.WaitAsync();
        SessionRuntime session;
        try
        {
            string code;
            do code = SessionRuntime.NewCode();
            while (_sessions.Values.Any(s => s.Code == code && s.Joinable(DateTimeOffset.UtcNow)));
            session = await SessionRuntime.CreateAsync(scenario, title, root, configuration, code, options.Value.JoinCodeLifetime);
            _sessions[session.Id] = session;
        }
        finally
        {
            _creating.Release();
        }

        var ticket = await session.OpenAsync(request.DisplayName, connectionId, request.VoiceConsent);
        logger.LogInformation("Session {Session} '{Title}' opened on {Scenario}; join code {Code}", session.Id, title, scenario.Key, session.Code);
        return (session, ticket);
    }

    public async Task<(SessionRuntime Session, JoinTicket Ticket)> JoinAsync(JoinSessionRequest request, string connectionId)
    {
        var code = request.Code?.Trim().ToUpperInvariant() ?? "";
        var session = _sessions.Values.FirstOrDefault(s => s.Code == code && s.Joinable(DateTimeOffset.UtcNow))
                      ?? throw new HubException("No open session has that code. Check it with your instructor; codes expire.");
        if (request.Role == UserRole.Instructor) CheckPin(request.Pin);
        return (session, await session.JoinAsync(request.DisplayName, request.Role, connectionId, request.VoiceConsent));
    }

    public async Task<(SessionRuntime Session, JoinTicket Ticket)> ResumeAsync(string token, string connectionId)
    {
        foreach (var session in _sessions.Values)
            if (session.FindByToken(token) is { } participant)
                return (session, await session.ResumeAsync(participant, connectionId));
        throw new HubException("That place in the session is no longer held. Join again with the code.");
    }

    private void CheckPin(string? pin)
    {
        var expected = options.Value.InstructorPin;
        if (string.IsNullOrEmpty(expected) || pin != expected)
            throw new HubException("Wrong instructor PIN.");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
            await session.DisposeAsync();
        _sessions.Clear();
    }
}
