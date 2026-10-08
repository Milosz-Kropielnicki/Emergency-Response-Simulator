using CommunityToolkit.Mvvm.ComponentModel;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>A named on/off switch: a map layer, a map view, a comms channel.</summary>
public partial class ToggleItem(string key, string label, bool isOn = false) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isOn = isOn;
}

/// <summary>One line of the resource board, e.g. "Fire Engines   3 / 4 available".</summary>
public sealed record ResourceSummary(string Group, int Available, int Total)
{
    public string Text => $"{Available} / {Total} available";
}

/// <summary>A line in a feed panel.</summary>
public sealed record FeedItem(string Time, string Title, string Detail, string Severity);

public sealed record DataSourceInfo(string EventStore);
