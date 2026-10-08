using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>
/// The dashboard shell: header, pop-out panels and the status boards along the bottom
/// (Design Document §6.8, Appendix D). Reads only the COP, never ground truth.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ICopService _cop;
    private readonly ISimulationControl _simulation;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _clockTimer;
    private int _refreshQueued;

    public MainViewModel(ICopService cop, ISimulationControl simulation, DataSourceInfo dataSource)
    {
        _cop = cop;
        _simulation = simulation;
        _dispatcher = Application.Current.Dispatcher;
        EventStoreLabel = $"Event store: {dataSource.EventStore}";

        foreach (var channel in CommsChannels)
            channel.PropertyChanged += (_, _) => RefreshComms();

        _cop.Changed += (_, _) => QueueRefresh();

        _clockTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => UpdateClock(), _dispatcher);
        _clockTimer.Start();

        UpdateClock();
        Refresh();
    }

    public string EventStoreLabel { get; }

    // ---- Header ----

    [ObservableProperty] private string _incidentTitle = "NO ACTIVE INCIDENT";
    [ObservableProperty] private string _incidentSubtitle = "Monitoring";
    [ObservableProperty] private string _simClock = "--:--:--";
    [ObservableProperty] private string _simDate = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _timeScale = 1;

    public IReadOnlyList<double> TimeScales { get; } = [1, 2, 5, 10, 30];

    // ---- Pop-out panels ----

    [ObservableProperty] private bool _isLeftPanelOpen = true;
    [ObservableProperty] private bool _isRightPanelOpen = true;
    [ObservableProperty] private bool _isTopPanelOpen;

    /// <summary>Left panel: map layer toggles with the defaults from Design Document §6.8.</summary>
    public ObservableCollection<ToggleItem> MapLayers { get; } =
    [
        new("incidents", "Incidents", true),
        new("units", "Emergency units", true),
        new("hospitals", "Hospitals", true),
        new("roads", "Roads", true),
        new("evacuation", "Evacuation zones", true),
        new("command", "Command zones", true),
        new("utilities", "Utilities"),
        new("weather", "Weather"),
        new("cctv", "CCTV"),
        new("traffic", "Traffic"),
        new("terrain", "Terrain"),
        new("infrastructure", "Critical infrastructure"),
        new("population", "Population density"),
        new("hazmat", "Hazardous materials"),
        new("communications", "Communications"),
    ];

    /// <summary>Top panel: base map style (one at a time).</summary>
    public ObservableCollection<ToggleItem> MapViews { get; } =
    [
        new("street", "Street", true),
        new("satellite", "Satellite"),
        new("weather", "Weather"),
        new("traffic", "Traffic"),
        new("terrain", "Terrain"),
    ];

    public ToggleItem AreaCodes { get; } = new("area-codes", "Area codes");

    /// <summary>Right panel: which communication feeds are shown.</summary>
    public ObservableCollection<ToggleItem> CommsChannels { get; } =
    [
        new("fire", "Fire", true),
        new("ems", "EMS", true),
        new("police", "Police", true),
        new("interagency", "Inter-agency", true),
        new("calls", "Emergency calls", true),
        new("field", "Field reports", true),
    ];

    public ObservableCollection<FeedItem> CommsFeed { get; } = [];

    // ---- Bottom boards ----

    public ObservableCollection<FeedItem> IncidentBoard { get; } = [];
    public ObservableCollection<ResourceSummary> ResourceBoard { get; } = [];
    public ObservableCollection<FeedItem> AlertFeed { get; } = [];

    [RelayCommand]
    private async Task TogglePlayAsync()
    {
        if (_simulation.IsRunning)
            await _simulation.PauseAsync();
        else
            await _simulation.StartAsync();
        IsRunning = _simulation.IsRunning;
    }

    [RelayCommand]
    private async Task SetTimeScaleAsync(double scale)
    {
        await _simulation.SetTimeScaleAsync(scale);
        TimeScale = _simulation.TimeScale;
    }

    [RelayCommand]
    private void ToggleLeftPanel() => IsLeftPanelOpen = !IsLeftPanelOpen;

    [RelayCommand]
    private void ToggleRightPanel() => IsRightPanelOpen = !IsRightPanelOpen;

    private void UpdateClock()
    {
        var local = _simulation.SimTime.ToLocalTime();
        SimClock = local.ToString("HH:mm:ss");
        SimDate = local.ToString("ddd d MMM yyyy");
        IsRunning = _simulation.IsRunning;
        TimeScale = _simulation.TimeScale;
    }

    /// <summary>COP changes can arrive in bursts from the engine thread; coalesce them into one UI refresh.</summary>
    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
            return;

        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        });
    }

    private void Refresh()
    {
        RefreshHeader();
        RefreshIncidentBoard();
        RefreshResourceBoard();
        RefreshAlerts();
        RefreshComms();
    }

    private void RefreshHeader()
    {
        var focus = _cop.Incidents
            .Where(i => i.Status != IncidentStatus.Closed)
            .OrderByDescending(i => i.Priority)
            .ThenBy(i => i.ReportedAt)
            .FirstOrDefault();

        if (focus is null)
        {
            IncidentTitle = "NO ACTIVE INCIDENT";
            IncidentSubtitle = "Monitoring";
            return;
        }

        IncidentTitle = $"INCIDENT: {(focus.Name ?? Humanize(focus.Type)).ToUpperInvariant()}";
        IncidentSubtitle = $"{focus.Number}   Status: {Humanize(focus.Status).ToUpperInvariant()}   Priority: {focus.Priority}";
    }

    private void RefreshIncidentBoard()
    {
        IncidentBoard.Clear();
        foreach (var incident in _cop.Incidents.OrderByDescending(i => i.Priority).ThenBy(i => i.ReportedAt))
        {
            IncidentBoard.Add(new FeedItem(
                Time(incident.ReportedAt),
                $"{incident.Number}  {Humanize(incident.Type)}",
                $"{Humanize(incident.Status)} · {incident.AssignedUnits.Count} units · " +
                $"{incident.CasualtiesReported} casualties reported",
                incident.Priority.ToString()));
        }
    }

    private void RefreshResourceBoard()
    {
        ResourceBoard.Clear();
        foreach (var group in _cop.Units.GroupBy(u => ResourceGroup(u.Type)).OrderBy(g => g.Key))
        {
            ResourceBoard.Add(new ResourceSummary(
                group.Key,
                group.Count(u => u.Status == UnitStatus.Available),
                group.Count()));
        }
    }

    private void RefreshAlerts()
    {
        AlertFeed.Clear();
        foreach (var alert in _cop.Alerts.OrderByDescending(a => a.RaisedAt).Take(50))
            AlertFeed.Add(new FeedItem(Time(alert.RaisedAt), alert.Title, alert.Message, alert.Severity.ToString()));
    }

    private void RefreshComms()
    {
        var showCalls = CommsChannels.First(c => c.Key == "calls").IsOn;
        var showField = CommsChannels.First(c => c.Key == "field").IsOn;

        CommsFeed.Clear();
        var reports = _cop.Reports
            .Where(r => (showCalls && r.Source == ReportSource.EmergencyCall)
                     || (showField && r.Source == ReportSource.FieldUnit))
            .OrderByDescending(r => r.ReceivedAt)
            .Take(50);

        foreach (var report in reports)
        {
            CommsFeed.Add(new FeedItem(
                Time(report.ReceivedAt),
                report.SourceName,
                $"\"{report.Claim}\"  [{report.Confidence} confidence, {report.Verification}]",
                report.Confidence.ToString()));
        }
    }

    private static string ResourceGroup(UnitType type) => type switch
    {
        UnitType.Engine => "Fire Engines",
        UnitType.Ladder => "Ladder Trucks",
        UnitType.Hazmat => "Hazmat Teams",
        UnitType.AmbulanceAls or UnitType.AmbulanceBls => "Ambulances",
        UnitType.Patrol or UnitType.Traffic or UnitType.Motorcycle or UnitType.Supervisor => "Police Units",
        UnitType.Swat => "Tactical Units",
        _ => Humanize(type),
    };

    private static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm");

    /// <summary>"StructureFire" → "Structure fire".</summary>
    private static string Humanize<T>(T value) where T : Enum
    {
        var name = value.ToString();
        var spaced = string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));
        return spaced;
    }
}
