using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.ViewModels.Iap;

// Editable rows of the IAP builder. Each one tells the builder when it changes, so the draft is marked
// unsaved and the compliance check, org chart and document preview are refreshed.

public abstract class IapEntry(IapBuilderViewModel owner, IList? parent) : ObservableObject
{
    protected IapBuilderViewModel Owner { get; } = owner;

    /// <summary>Removes the row from its list.</summary>
    public IRelayCommand RemoveCommand => _remove ??= new RelayCommand(() => parent?.Remove(this));
    private IRelayCommand? _remove;

    /// <summary>Properties that come from the live COP rather than the planner.</summary>
    protected virtual bool IsLive(string? propertyName) => false;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!IsLive(e.PropertyName))
            Owner.MarkDirty();
    }
}

/// <summary>Strategic objective, e.g. "Protect life".</summary>
public sealed partial class StrategicEntry : IapEntry
{
    public StrategicEntry(IapBuilderViewModel owner, IList parent, StrategicObjective objective) : base(owner, parent)
    {
        Id = objective.Id;
        _statement = objective.Statement;
        foreach (var operational in objective.Operational)
            Operational.Add(new OperationalEntry(owner, this, operational));
        Operational.CollectionChanged += (_, _) => owner.ObjectivesChanged();
    }

    public Guid Id { get; }
    public ObservableCollection<OperationalEntry> Operational { get; } = [];

    [ObservableProperty] private string _statement;
    [ObservableProperty] private string _number = "";
    [ObservableProperty] private string _newOperational = "";

    /// <summary>Operational objectives that usually serve this strategic objective.</summary>
    public IReadOnlyList<string> Suggestions => IapTemplates.OperationalFor(Statement);

    partial void OnStatementChanged(string value)
    {
        OnPropertyChanged(nameof(Suggestions));
        Owner.ObjectivesChanged();
    }

    protected override bool IsLive(string? propertyName) => propertyName is nameof(Number) or nameof(Suggestions) or nameof(NewOperational);

    [RelayCommand]
    private void AddOperational()
    {
        var statement = NewOperational.Trim();
        Operational.Add(new OperationalEntry(Owner, this, new OperationalObjective { Statement = statement }));
        NewOperational = "";
    }

    public StrategicObjective ToModel() => new()
    {
        Id = Id,
        Statement = Statement,
        Operational = Operational.Select(o => o.ToModel()).ToList(),
    };
}

/// <summary>Operational objective with its target, resources and tactics.</summary>
public sealed partial class OperationalEntry : IapEntry
{
    public OperationalEntry(IapBuilderViewModel owner, StrategicEntry parent, OperationalObjective objective) : base(owner, parent.Operational)
    {
        Id = objective.Id;
        _statement = objective.Statement;
        _responsible = objective.Responsible ?? "";
        _resourcesSummary = objective.ResourcesSummary ?? "";
        _performanceTarget = objective.PerformanceTarget ?? "";
        _targetTime = objective.TargetTime is { } due ? due.ToLocalTime().ToString("HH:mm") : "";
        foreach (var tactic in objective.Tactics)
            Tactics.Add(new TacticEntry(owner, Tactics, tactic.Id, tactic.Description));
        Tactics.CollectionChanged += (_, _) => owner.MarkDirty();
        _progress = owner.ProgressOf(Id);
    }

    public Guid Id { get; }
    public ObservableCollection<TacticEntry> Tactics { get; } = [];

    [ObservableProperty] private string _statement;
    [ObservableProperty] private string _responsible;
    [ObservableProperty] private string _resourcesSummary;
    [ObservableProperty] private string _performanceTarget;

    /// <summary>"HH:mm" on the operational period's day.</summary>
    [ObservableProperty] private string _targetTime;

    [ObservableProperty] private string _number = "";
    [ObservableProperty] private string _newTactic = "";

    /// <summary>Progress, tracked on the COP independently of the plan's version.</summary>
    [ObservableProperty] private ObjectiveStatus _progress;

    partial void OnStatementChanged(string value) => Owner.ObjectivesChanged();

    partial void OnProgressChanged(ObjectiveStatus value)
    {
        if (!Owner.IsLoading)
            _ = Owner.SetProgressAsync(Id, value);
    }

    protected override bool IsLive(string? propertyName) => propertyName is nameof(Number) or nameof(Progress) or nameof(NewTactic);

    [RelayCommand]
    private void AddTactic()
    {
        Tactics.Add(new TacticEntry(Owner, Tactics, Guid.NewGuid(), NewTactic.Trim()));
        NewTactic = "";
    }

    public OperationalObjective ToModel() => new()
    {
        Id = Id,
        Statement = Statement,
        Responsible = Responsible,
        ResourcesSummary = ResourcesSummary,
        PerformanceTarget = PerformanceTarget,
        TargetTime = Owner.ParseTime(TargetTime),
        Tactics = Tactics.Select(t => new TacticalTask { Id = t.Id, Description = t.Description }).ToList(),
    };
}

public sealed partial class TacticEntry(IapBuilderViewModel owner, IList parent, Guid id, string description) : IapEntry(owner, parent)
{
    public Guid Id { get; } = id;
    [ObservableProperty] private string _description = description;
}

/// <summary>One ICS position in the plan's organisation (ICS 203).</summary>
public sealed partial class PositionEntry(IapBuilderViewModel owner, IcsRole role, string name) : IapEntry(owner, null)
{
    public IcsRole Role { get; } = role;
    public string Label { get; } = IapDocument.RoleName(role);
    public string Staff { get; } = role switch
    {
        IcsRole.IncidentCommander => "Command",
        IcsRole.Safety or IcsRole.Liaison or IcsRole.PublicInformation => "Command staff",
        _ => "General staff",
    };

    [ObservableProperty] private string _name = name;

    /// <summary>Who holds the position on the COP now, when different from the plan.</summary>
    [ObservableProperty] private string? _copHolder;

    protected override bool IsLive(string? propertyName) => propertyName == nameof(CopHolder);
}

public sealed partial class GroupEntry(IapBuilderViewModel owner, IList parent, IapGroup group) : IapEntry(owner, parent)
{
    [ObservableProperty] private string _name = group.Name;
    [ObservableProperty] private IcsGroupKind _kind = group.Kind;
    [ObservableProperty] private string _supervisor = group.Supervisor ?? "";

    partial void OnNameChanged(string value) => Owner.GroupsChanged();

    public IapGroup ToModel() => new() { Name = Name, Kind = Kind, Supervisor = Supervisor };
}

/// <summary>Unit → assignment (ICS 204), with what the COP says about the unit now.</summary>
public sealed partial class AssignmentEntry(IapBuilderViewModel owner, IList parent, IapAssignment assignment) : IapEntry(owner, parent)
{
    public Guid UnitId { get; } = assignment.UnitId;
    [ObservableProperty] private string _callsign = assignment.Callsign;
    [ObservableProperty] private string _assignment = assignment.Assignment;
    [ObservableProperty] private string _group = assignment.Group ?? "";
    [ObservableProperty] private Guid? _objectiveId = assignment.ObjectiveId;

    [ObservableProperty] private string _unitType = "";
    [ObservableProperty] private string _copStatus = "";

    /// <summary>Why the COP says this assignment can't be carried out, if it can't (§8.6).</summary>
    [ObservableProperty] private string? _problem;

    protected override bool IsLive(string? propertyName) => propertyName is nameof(UnitType) or nameof(CopStatus) or nameof(Problem) or nameof(Callsign);

    public void UpdateLive(Unit? unit, string? problem)
    {
        if (unit is not null)
        {
            Callsign = unit.Callsign;
            UnitType = ResourceGroups.Label(unit.Type);
            CopStatus = EventDescriber.Humanize(unit.Status) + (unit.AssignedIncident is { } at ? $" · {at.Number}" : "");
        }
        else
        {
            CopStatus = "Not on the roster";
        }
        Problem = problem;
    }

    public IapAssignment ToModel() => new()
    {
        UnitId = UnitId,
        Callsign = Callsign,
        Assignment = Assignment,
        Group = Group,
        ObjectiveId = ObjectiveId,
    };
}

public sealed partial class ChannelEntry(IapBuilderViewModel owner, IList parent, RadioChannel channel) : IapEntry(owner, parent)
{
    [ObservableProperty] private string _function = channel.Function;
    [ObservableProperty] private string _channel = channel.Channel;
    [ObservableProperty] private string _assignedTo = channel.AssignedTo;
    [ObservableProperty] private string _remarks = channel.Remarks ?? "";

    public RadioChannel ToModel() => new() { Function = Function, Channel = Channel, AssignedTo = AssignedTo, Remarks = Remarks };
}

public sealed partial class HospitalEntry(IapBuilderViewModel owner, IList parent, ReceivingHospital hospital) : IapEntry(owner, parent)
{
    private readonly double? _distanceKm = hospital.DistanceKm;
    [ObservableProperty] private string _name = hospital.Name;
    [ObservableProperty] private string _travelMinutes = hospital.TravelMinutes is { } m ? m.ToString("0.#") : "";
    [ObservableProperty] private string _capabilities = hospital.Capabilities ?? "";

    public string Distance => _distanceKm is { } km ? $"{km:0.0} km" : "";

    public ReceivingHospital ToModel() => new()
    {
        Name = Name,
        DistanceKm = _distanceKm,
        TravelMinutes = double.TryParse(TravelMinutes, out var minutes) ? minutes : null,
        Capabilities = Capabilities,
    };
}

public sealed partial class HazardEntry(IapBuilderViewModel owner, IList parent, SafetyHazard hazard) : IapEntry(owner, parent)
{
    [ObservableProperty] private string _hazard = hazard.Hazard;
    [ObservableProperty] private string _mitigation = hazard.Mitigation ?? "";

    public SafetyHazard ToModel() => new() { Hazard = Hazard, Mitigation = Mitigation };
}

/// <summary>A single line of text in a list, e.g. one PPE requirement.</summary>
public sealed partial class TextEntry(IapBuilderViewModel owner, IList parent, string text) : IapEntry(owner, parent)
{
    [ObservableProperty] private string _text = text;
}

/// <summary>An operational objective offered in the assignment board, e.g. "1.2 Evacuate…".</summary>
public sealed partial class ObjectiveOption(Guid? id, string label) : ObservableObject
{
    public Guid? Id { get; } = id;
    [ObservableProperty] private string _label = label;
}

public sealed record UnitOption(Guid Id, string Label);

public sealed record PeriodRow(Guid Id, int Number, string Label);

public sealed record VersionRow(Guid Id, int Version, string Label, IapStatus Status);

public sealed record PeriodLength(TimeSpan Length, string Label);

/// <summary>A compliance finding as shown in the builder.</summary>
public sealed record IssueRow(bool IsError, string Section, string Message, int SectionIndex);

/// <summary>A builder section in the left-hand navigation, with its compliance badge.</summary>
public sealed partial class SectionItem(string title, IapSection? section) : ObservableObject
{
    public string Title { get; } = title;
    public IapSection? Section { get; } = section;
    [ObservableProperty] private int _errors;
    [ObservableProperty] private int _warnings;
}
