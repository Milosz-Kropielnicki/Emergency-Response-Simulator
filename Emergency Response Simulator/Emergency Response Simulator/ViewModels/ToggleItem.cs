using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>A named on/off switch: a map layer, a map view, a comms channel.</summary>
public partial class ToggleItem(string key, string label, bool isOn = false) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isOn = isOn;

    /// <summary>Secondary text, e.g. a feature count or "not imported".</summary>
    [ObservableProperty]
    private string? _detail;

    /// <summary>False when there is nothing to show (e.g. the layer has not been imported).</summary>
    [ObservableProperty]
    private bool _isAvailable = true;
}

/// <summary>A heading in the layer panel with its toggles.</summary>
public sealed record LayerGroup(string Title, ObservableCollection<ToggleItem> Items);

/// <summary>One line of the resource board, e.g. "Fire Engines   3 / 4 available".</summary>
public sealed record ResourceSummary(string Group, int Available, int Total)
{
    public string Text => $"{Available} / {Total} available";
}

/// <summary>A zone currently in force, listed so it can be lifted.</summary>
public sealed record ActiveZoneItem(Guid Id, string Name, string Type);

public sealed record DataSourceInfo(string EventStore);
