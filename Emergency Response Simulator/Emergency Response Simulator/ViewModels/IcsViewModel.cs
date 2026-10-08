using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>
/// The selected incident's ICS organisation (Design Document §8.1): who holds each position, the groups
/// and divisions under Operations, which units report where, and every supervisor's span of control.
/// </summary>
public sealed partial class IcsViewModel(MainViewModel owner, IC2Service c2) : ObservableObject
{
    [ObservableProperty] private bool _hasIncident;

    public ObservableCollection<PositionRow> Positions { get; } = [];
    public ObservableCollection<GroupRow> Groups { get; } = [];
    public ObservableCollection<SpanRow> Spans { get; } = [];

    /// <summary>Units reporting directly to Operations (or the IC), with a group picker each.</summary>
    public ObservableCollection<UnitPlacementRow> Ungrouped { get; } = [];

    [ObservableProperty] private string _newGroupName = "";
    [ObservableProperty] private string _newGroupSupervisor = "";
    [ObservableProperty] private IcsGroupKind _newGroupKind = IcsGroupKind.Group;
    public IReadOnlyList<IcsGroupKind> GroupKinds { get; } = Enum.GetValues<IcsGroupKind>();

    private Guid? _incidentId;

    public void Load(Incident? incident)
    {
        HasIncident = incident is not null;
        _incidentId = incident?.Id;
        Groups.Clear();
        Spans.Clear();
        Ungrouped.Clear();
        if (incident is null)
        {
            Positions.Clear();
            return;
        }

        // Position rows hold typed-in names, so only rebuild them when the incident changes.
        if (Positions.Count == 0 || Positions[0].IncidentId != incident.Id)
        {
            Positions.Clear();
            foreach (var role in Enum.GetValues<IcsRole>())
                Positions.Add(new PositionRow(this, incident.Id, role, incident.Command.Positions.GetValueOrDefault(role)));
        }
        else
        {
            foreach (var row in Positions)
                row.Holder = incident.Command.Positions.GetValueOrDefault(row.Role);
        }

        var groupOptions = incident.Command.Groups.Select(g => new GroupOption(g.Id, g.Name))
            .Prepend(new GroupOption(null, "(report to command)")).ToList();
        foreach (var group in incident.Command.Groups)
        {
            var members = incident.AssignedUnits.Where(u => group.UnitIds.Contains(u.Id))
                .Select(u => new UnitPlacementRow(this, u.Id, u.Callsign, EventDescriber.Humanize(u.Status), groupOptions, group.Id))
                .ToList();
            Groups.Add(new GroupRow(group.Id, group.Name, EventDescriber.Humanize(group.Kind), group.Supervisor ?? "No supervisor named", members));
        }

        foreach (var unit in incident.AssignedUnits.Where(u => incident.Command.GroupOf(u.Id) is null).OrderBy(u => u.Callsign, StringComparer.Ordinal))
            Ungrouped.Add(new UnitPlacementRow(this, unit.Id, unit.Callsign, EventDescriber.Humanize(unit.Status), groupOptions, null));

        foreach (var span in SpanOfControl.Assess(incident.Command, incident.AssignedUnits.Select(u => u.Id).ToList()))
            Spans.Add(new SpanRow(span.Supervisor, span.DirectReports,
                span.IsOverloaded ? "Over" : span.IsUnderUsed ? "Under" : span.DirectReports == 0 ? "Idle" : "OK"));
    }

    public Task AssignAsync(IcsRole role, string name) => _incidentId is { } id
        ? owner.RunCommandAsync(() => c2.AssignIcsPositionAsync(id, role, name), $"{name} is {EventDescriber.Humanize(role)}")
        : Task.CompletedTask;

    public Task PlaceAsync(Guid unitId, Guid? groupId) =>
        owner.RunCommandAsync(() => c2.AssignUnitToGroupAsync(unitId, groupId), "Unit placed");

    [RelayCommand]
    private async Task FormGroupAsync()
    {
        if (_incidentId is not { } id) return;
        var result = await owner.RunCommandAsync(() => c2.FormGroupAsync(id, NewGroupName, NewGroupKind, NewGroupSupervisor),
            $"{NewGroupName} formed");
        if (result?.Succeeded == true)
        {
            NewGroupName = "";
            NewGroupSupervisor = "";
        }
    }

    [RelayCommand]
    private Task DisbandGroupAsync(Guid groupId) => _incidentId is { } id
        ? owner.RunCommandAsync(() => c2.DisbandGroupAsync(id, groupId), "Group disbanded; its units report to command")
        : Task.CompletedTask;
}

public sealed partial class PositionRow(IcsViewModel owner, Guid incidentId, IcsRole role, string? holder) : ObservableObject
{
    public Guid IncidentId { get; } = incidentId;
    public IcsRole Role { get; } = role;
    public string Label { get; } = EventDescriber.Humanize(role);

    /// <summary>Command staff (Safety, Liaison, PIO) or general staff (sections).</summary>
    public string Staff { get; } = role switch
    {
        IcsRole.IncidentCommander => "Command",
        IcsRole.Safety or IcsRole.Liaison or IcsRole.PublicInformation => "Command staff",
        _ => "General staff",
    };

    [ObservableProperty] private string? _holder = holder;
    [ObservableProperty] private string _newHolder = holder ?? "";

    [RelayCommand]
    private Task AssignAsync() => owner.AssignAsync(Role, NewHolder);
}

public sealed record GroupOption(Guid? Id, string Name);

public sealed record GroupRow(Guid Id, string Name, string Kind, string Supervisor, IReadOnlyList<UnitPlacementRow> Members);

public sealed record SpanRow(string Supervisor, int DirectReports, string Rating);

/// <summary>A unit with a drop-down to move it between groups.</summary>
public sealed class UnitPlacementRow(IcsViewModel owner, Guid unitId, string callsign, string status,
    IReadOnlyList<GroupOption> options, Guid? currentGroup)
{
    public string Callsign { get; } = callsign;
    public string Status { get; } = status;
    public IReadOnlyList<GroupOption> Options { get; } = options;

    public GroupOption? Placement
    {
        get => Options.FirstOrDefault(o => o.Id == currentGroup);
        set
        {
            if (value is not null && value.Id != currentGroup)
                _ = owner.PlaceAsync(unitId, value.Id);
        }
    }
}
