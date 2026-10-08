using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>
/// The time dimension of the COP (Design Document §6.6): a readable history log of the session and
/// a replay control that shows the whole COP — map and boards — as it stood at an earlier moment.
/// </summary>
public sealed partial class TimelineViewModel : ObservableObject
{
    private readonly IEventStore _store;
    private readonly SimulationSession _session;
    private readonly ICopService _live;
    private readonly CopView _view;
    private readonly IAarService _aar;
    private readonly ISimulationControl _simulation;
    private DateTimeOffset? _sessionStart;

    public TimelineViewModel(IEventStore store, SimulationSession session, ICopService live, CopView view,
        IAarService aar, ISimulationControl simulation)
    {
        _store = store;
        _session = session;
        _live = live;
        _view = view;
        _aar = aar;
        _simulation = simulation;

        Entries = CollectionViewSource.GetDefaultView(Rows);
        Entries.Filter = item => item is TimelineRow row && Matches(row);

        _view.ModeChanged += (_, _) => Application.Current.Dispatcher.Invoke(UpdateReplayState);
    }

    public ObservableCollection<TimelineRow> Rows { get; } = [];

    /// <summary>The filtered, newest-first history shown in the log.</summary>
    public ICollectionView Entries { get; }

    public IReadOnlyList<string> Filters { get; } =
        ["All", "Calls & reports", "Incidents & command", "Orders, requests & approvals", "Units", "Alerts", "Zones & weather"];

    [ObservableProperty] private string _selectedFilter = "All";
    [ObservableProperty] private bool _importantOnly;

    /// <summary>Instructor/AAR view: include ground-truth events the trainee never saw.</summary>
    [ObservableProperty] private bool _showTruth;

    // ---- Replay ----

    [ObservableProperty] private bool _isReplay;
    [ObservableProperty] private string _replayLabel = "";

    /// <summary>Slider position: seconds of simulation time since the session started.</summary>
    [ObservableProperty] private double _replaySeconds;
    [ObservableProperty] private double _maxSeconds = 1;
    [ObservableProperty] private string _sliderLabel = "";

    partial void OnSelectedFilterChanged(string value) => Entries.Refresh();
    partial void OnImportantOnlyChanged(bool value) => Entries.Refresh();
    partial void OnShowTruthChanged(bool value) => Entries.Refresh();
    partial void OnReplaySecondsChanged(double value) => SliderLabel = _sessionStart is { } start
        ? MainViewModel.Time(start.AddSeconds(value), seconds: true)
        : "";

    public async Task LoadAsync()
    {
        _store.Appended += (_, simEvent) =>
        {
            if (simEvent.SessionId == _session.Id)
                Application.Current.Dispatcher.BeginInvoke(() => Add(simEvent));
        };

        await foreach (var simEvent in _store.ReadAsync(_session.Id))
            Add(simEvent);
    }

    /// <summary>Keeps the slider's range in step with the clock (called by the main clock timer).</summary>
    public void Tick()
    {
        if (_sessionStart is not { } start) return;
        var wasAtEnd = ReplaySeconds >= MaxSeconds - 0.5;
        MaxSeconds = Math.Max(1, (_simulation.SimTime - start).TotalSeconds);
        if (wasAtEnd && !IsReplay)
            ReplaySeconds = MaxSeconds;
    }

    [RelayCommand]
    private async Task ReplayAsync()
    {
        if (_sessionStart is not { } start) return;
        await ReplayToAsync(start.AddSeconds(ReplaySeconds));
    }

    /// <summary>Double-clicking a log line shows the COP right after that event.</summary>
    [RelayCommand]
    private async Task ReplayToEntryAsync(TimelineRow? row)
    {
        if (row is null || _sessionStart is not { } start) return;
        ReplaySeconds = (row.SimTime - start).TotalSeconds;
        await ReplayToAsync(row.SimTime);
    }

    [RelayCommand]
    private void ReturnToLive()
    {
        _view.ShowLive();
        ReplaySeconds = MaxSeconds;
    }

    private async Task ReplayToAsync(DateTimeOffset at)
    {
        var snapshot = await _aar.ReplayToAsync(_session.Id, at);
        _view.ShowReplay(snapshot, at);
    }

    private void UpdateReplayState()
    {
        IsReplay = _view.IsReplay;
        ReplayLabel = _view.ReplayTime is { } at
            ? $"REPLAY — showing the COP as at {MainViewModel.Time(at, seconds: true)}. Commands are disabled."
            : "";
    }

    private void Add(SimEvent simEvent)
    {
        _sessionStart ??= simEvent.SimTime;

        var entry = EventDescriber.Describe(simEvent,
            id => _live.FindUnit(id)?.Callsign,
            id => _live.FindIncident(id)?.Number);
        if (entry is null) return;

        var row = new TimelineRow(entry.Sequence, entry.SimTime, MainViewModel.Time(entry.SimTime, seconds: true),
            entry.Category, entry.Text, entry.Important, entry.Visibility == EventVisibility.Truth);
        Rows.Insert(0, row); // newest first
    }

    private bool Matches(TimelineRow row)
    {
        if (row.IsTruth && !ShowTruth) return false;
        if (ImportantOnly && !row.Important) return false;

        return SelectedFilter switch
        {
            "Calls & reports" => row.Category is "CALL RECEIVED" or "REPORT" or "FIELD REPORT" or "ASSESSMENT",
            "Incidents & command" => row.Category is "INCIDENT CREATED" or "INCIDENT UPDATE" or "COMMAND" or "RESOURCE REQUEST",
            "Orders, requests & approvals" => row.Category is "ORDER" or "RESOURCE REQUEST" or "APPROVAL" or "NOTIFICATION",
            "Units" => row.Category.StartsWith("UNIT"),
            "Alerts" => row.Category.StartsWith('⚠') || row.Category == "ALERT",
            "Zones & weather" => row.Category is "ZONE" or "WEATHER",
            _ => true,
        };
    }
}
