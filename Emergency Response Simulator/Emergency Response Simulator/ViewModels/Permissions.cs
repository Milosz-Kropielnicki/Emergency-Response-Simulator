using System.Reflection;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Remote;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>
/// What this player may do (Phase 11 M3), mirroring the server's command authoriser so the commands their role can't
/// give are disabled. The server decides; this only spares the player buttons that would be refused. The role is the
/// one the event stream says they hold now (assignments and handovers included), and the IC's share of unfilled
/// functions comes from who the stream says is present. A standalone session has one person in every role: all allowed.
/// </summary>
public sealed class Permissions(ICopService cop, SimulationConnection? connection)
{
    private string _state = "";

    public bool Applies => connection is not null;

    /// <summary>Raised when the role, who is present, or the session's stage changed what is allowed.</summary>
    public event EventHandler? Changed;

    public UserRole? Role => connection?.Ticket is { } ticket
        ? cop.Participants.FirstOrDefault(p => p.Id == ticket.ParticipantId)?.Role ?? ticket.Role
        : null;

    private bool Running => connection?.Session?.State is SessionState.Running;

    private Func<UserRole, bool> Filled()
    {
        var filled = cop.Participants.Where(p => p.Present).Select(p => p.Role).ToHashSet();
        return filled.Contains;
    }

    public PermissionDecision Decide<TContract>(string method)
    {
        if (Role is not { } role) return PermissionDecision.Yes;
        if (!Running)
            return new(false, connection?.Session?.State == SessionState.Ended ? "The session has ended." : "The exercise hasn't started yet.");
        return PermissionMatrix.Decide<TContract>(role, method, Filled());
    }

    public bool Can<TContract>(string method) => Decide<TContract>(method).Allowed;

    public bool C2(string method) => Can<IC2Service>(method);

    public bool Iap(string method) => Can<IIapService>(method);

    /// <summary>The functions this player holds now, their own and any that fell to them, e.g. for the session tab.</summary>
    public IReadOnlyList<string> Functions => Role is { } role ? PermissionMatrix.FunctionsOf(role, Filled()) : [];

    /// <summary>The functions that are this player's only because their holders' roles are unfilled (the IC's share).</summary>
    public IReadOnlyList<string> Inherited => Role is UserRole.IncidentCommander
        ? PermissionMatrix.Commands.Where(c => !c.Anyone && PermissionMatrix.Decide(UserRole.IncidentCommander, c.Contract, c.Method, Filled()).ByFallback)
            .Select(c => c.Function).Distinct().OrderBy(f => f).ToList()
        : [];

    /// <summary>Raises <see cref="Changed"/> if anything that decides permissions moved since the last look.</summary>
    public void Refresh()
    {
        if (!Applies) return;
        var state = $"{Role}|{connection!.Session?.State}|" + string.Join(",", cop.Participants.Where(p => p.Present).Select(p => p.Role).Distinct().Order());
        if (state == _state) return;
        _state = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Tells every command on a view model to ask again whether it can run.</summary>
    public static void Requery(object viewModel)
    {
        foreach (var property in viewModel.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => typeof(IRelayCommand).IsAssignableFrom(p.PropertyType)))
            (property.GetValue(viewModel) as IRelayCommand)?.NotifyCanExecuteChanged();
    }
}
