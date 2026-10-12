using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Remote;
using Microsoft.AspNetCore.SignalR;

namespace Emergency_Response_Simulator.Server;

/// <summary>
/// Decides whether a participant may make a call (Phase 11 M3), before it reaches a service. Nothing a client sends
/// counts: the role is the one the server holds for the connection, and the IC's share of unfilled functions comes
/// from who the server knows is present. Refusals throw, so the caller sees the reason as a failed command.
/// <list type="bullet">
/// <item>The session's stage: commands only while the exercise runs; the clock is the instructor's; the review, which
/// reveals ground truth, is the instructor's while the session is live (Phase 11 M2).</item>
/// <item>The permission matrix (<see cref="PermissionMatrix"/>) for every C2 and IAP command.</item>
/// </list>
/// </summary>
public static class CommandAuthorizer
{
    public static void Authorise(SessionRuntime session, ParticipantRecord participant, string service, string method)
    {
        var instructor = participant.Role == UserRole.Instructor;
        switch (service)
        {
            // Observers (Phase 11 M6): read-only, whatever the client sends. They may note how players are doing.
            case SimulationHubProtocol.Services.C2 or SimulationHubProtocol.Services.Iap or SimulationHubProtocol.Services.Control
                or SimulationHubProtocol.Services.Instructor when participant.Role == UserRole.Observer:
                throw new HubException("Observers watch: they don't give commands.");
            case SimulationHubProtocol.Services.Assessment when !participant.Assessor:
                throw new HubException("Only observers and the instructor record assessments.");
            case SimulationHubProtocol.Services.Assessment when session.State == SessionState.Ended:
                throw new HubException("The session has ended.");
            case SimulationHubProtocol.Services.Control when !instructor:
                throw new HubException("Only the instructor controls the clock.");
            case SimulationHubProtocol.Services.Control when session.State == SessionState.Ended:
                throw new HubException("The session has ended.");
            case SimulationHubProtocol.Services.C2 or SimulationHubProtocol.Services.Iap when session.State == SessionState.Lobby:
                throw new HubException("The exercise hasn't started yet: wait for the instructor.");
            case SimulationHubProtocol.Services.C2 or SimulationHubProtocol.Services.Iap when session.State == SessionState.Ended:
                throw new HubException("The session has ended.");
            case SimulationHubProtocol.Services.Instructor when !instructor:
                throw new HubException("The instructor console is the instructor's.");
            case SimulationHubProtocol.Services.Instructor when session.State == SessionState.Ended && !method.StartsWith("List", StringComparison.Ordinal):
                throw new HubException("The session has ended.");
            case SimulationHubProtocol.Services.Aar when !participant.Truth && session.State != SessionState.Ended
                                                         && method is nameof(IAarService.BuildReportAsync) or nameof(IAarService.CompareBranchesAsync):
                throw new HubException("Only the instructor can open the after-action review until the session has ended.");
        }

        if (service is SimulationHubProtocol.Services.C2 or SimulationHubProtocol.Services.Iap
            && Decide(session, participant.Role, CommandDispatcher.Contracts[service], method) is { Allowed: false } refused)
            throw new HubException(refused.Reason);
    }

    /// <summary>The matrix's answer for a role in this session, with the roles held by people present as filled.</summary>
    public static PermissionDecision Decide(SessionRuntime session, UserRole role, Type contract, string method)
    {
        var filled = session.Participants.Where(p => p.Present).Select(p => p.Role).ToHashSet();
        return PermissionMatrix.Decide(role, contract, method, filled.Contains);
    }
}
