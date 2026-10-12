using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>One thing waiting on the desk.</summary>
public sealed record DeskRow(string Function, string Text, string Age, DeskUrgency Urgency, Guid? IncidentId);

/// <summary>
/// The DESK tab (Phase 12): what waits for this player's role to act on now (<see cref="RoleDesk"/>), most urgent and
/// oldest first; choosing an item goes to its incident. The same rules that decide who may act on something decide
/// whose desk it is on, so the IC's desk also holds whatever falls to them from unfilled roles. Standalone, one person
/// holds every role; an observer sees the desk of whoever they follow.
/// </summary>
public partial class DeskViewModel(CopView cop, Permissions permissions, AssessmentViewModel assessment, Action<Guid> select) : ObservableObject
{
    public ObservableCollection<DeskRow> Items { get; } = [];

    [ObservableProperty] private string _tabHeader = "DESK";
    [ObservableProperty] private string _summary = "";

    [RelayCommand]
    private void Open(DeskRow? row)
    {
        if (row?.IncidentId is { } incident) select(incident);
    }

    public void Refresh()
    {
        var present = cop.Participants.Where(p => p.Present).Select(p => p.Role).ToHashSet();
        var (role, me, whose) = Whose();
        var desk = role is { } r ? RoleDesk.For(r, cop, present.Contains, me) : [];
        var now = cop.AsOf;

        var rows = desk.Select(d => new DeskRow(d.Function, d.Text, Age(d, now), d.Urgency, d.IncidentId)).ToList();
        if (!Items.SequenceEqual(rows))
        {
            Items.Clear();
            foreach (var row in rows) Items.Add(row);
        }
        var pressing = desk.Count(d => d.Urgency != DeskUrgency.Routine);
        TabHeader = desk.Count == 0 ? "DESK" : pressing > 0 ? $"DESK ({desk.Count}, {pressing}!)" : $"DESK ({desk.Count})";
        Summary = role is null ? "Follow a player to see what waits on their desk."
            : desk.Count == 0 ? $"Nothing waiting for {whose}."
            : $"{desk.Count} waiting for {whose}" + (pressing > 0 ? $", {pressing} pressing." : ".");
    }

    /// <summary>Whose desk to show: the player's own; the followed player's for an observer; everything standalone.</summary>
    private (UserRole? Role, Guid? Me, string Whose) Whose()
    {
        if (!permissions.Applies) return (UserRole.IncidentCommander, null, "you");
        if (permissions.Role is { } mine && Roles.IsPlayer(mine)) return (mine, permissions.Me, "you");
        if (assessment.Following?.Id is { } followed && cop.Participants.FirstOrDefault(p => p.Id == followed) is { } person && Roles.IsPlayer(person.Role))
            return (person.Role, person.Id, person.DisplayName);
        return (null, null, "");
    }

    private static string Age(DeskItem item, DateTimeOffset now) =>
        item.Since > now ? $"due {MainViewModel.Time(item.Since)}" : $"{(int)item.Age(now).TotalMinutes} min";
}
