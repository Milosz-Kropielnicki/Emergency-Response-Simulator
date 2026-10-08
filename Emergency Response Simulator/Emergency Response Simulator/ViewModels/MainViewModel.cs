using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Geo;
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
    private readonly IC2Service _c2;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _clockTimer;
    private int _refreshQueued;

    public MainViewModel(ICopService cop, ISimulationControl simulation, IC2Service c2, DataSourceInfo dataSource)
    {
        _cop = cop;
        _simulation = simulation;
        _c2 = c2;
        ZoneDrawing = new ZoneDrawingViewModel(c2);
        LayerGroups = BuildLayerGroups();
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

    /// <summary>
    /// Left panel: map layer toggles (Design Document §6.8). Operational layers come from the COP;
    /// the rest are the static GIS layers, whose availability is filled in once they load.
    /// </summary>
    public IReadOnlyList<LayerGroup> LayerGroups { get; }

    public ToggleItem? FindLayerToggle(string key) =>
        LayerGroups.SelectMany(g => g.Items).FirstOrDefault(t => t.Key == key);

    private static IReadOnlyList<LayerGroup> BuildLayerGroups()
    {
        var groups = new List<LayerGroup>
        {
            new("OPERATIONAL",
            [
                new(MapLayerKeys.Incidents, "Incidents", true),
                new(MapLayerKeys.FireUnits, "Fire units", true),
                new(MapLayerKeys.EmsUnits, "EMS units", true),
                new(MapLayerKeys.PoliceUnits, "Police units", true),
                new(MapLayerKeys.OtherUnits, "Other units", true),
                new(MapLayerKeys.Weather, "Wind (reported)"),
            ]),
            new("ZONES",
            [
                new(MapLayerKeys.HazardZones, "Hot / warm / cold & hazard", true),
                new(MapLayerKeys.EvacuationZones, "Evacuation & shelter", true),
                new(MapLayerKeys.CommandZones, "Command, staging & landing", true),
                new(MapLayerKeys.TrafficZones, "Road closures & traffic", true),
                new(MapLayerKeys.SearchZones, "Search areas & cordons", true),
                new(MapLayerKeys.PerimeterZones, "Incident perimeters", true),
            ]),
        };

        // Area boundaries are switched from the top panel ("Area codes"), not listed here.
        foreach (var group in GisLayerKeys.All.Where(d => d.Key != GisLayerKeys.AreaBoundaries).GroupBy(d => d.Group))
        {
            groups.Add(new LayerGroup(group.Key.ToUpperInvariant(),
                [.. group.Select(d => new ToggleItem(d.Key, d.Name, d.VisibleByDefault) { IsAvailable = false, Detail = "loading…" })]));
        }

        return groups;
    }

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

    // ---- Map tools ----

    public ZoneDrawingViewModel ZoneDrawing { get; }

    public ObservableCollection<ActiveZoneItem> ActiveZones { get; } = [];

    /// <summary>Wind as last reported to command, shown on the map.</summary>
    [ObservableProperty] private string _weatherSummary = "No weather report";

    /// <summary>Details of whatever was clicked on the map.</summary>
    [ObservableProperty] private bool _isInfoOpen;
    [ObservableProperty] private string _infoTitle = "";
    public ObservableCollection<string> InfoLines { get; } = [];

    public void ShowInfo(string title, IEnumerable<string> lines)
    {
        InfoTitle = title;
        InfoLines.Clear();
        foreach (var line in lines)
            InfoLines.Add(line);
        IsInfoOpen = true;
    }

    [RelayCommand]
    private void CloseInfo() => IsInfoOpen = false;

    [RelayCommand]
    private async Task LiftZoneAsync(Guid zoneId) => await _c2.LiftZoneAsync(zoneId);

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
        RefreshZones();
        RefreshWeather();
    }

    private void RefreshZones()
    {
        ActiveZones.Clear();
        foreach (var zone in _cop.Zones.OrderBy(z => z.EffectiveFrom))
            ActiveZones.Add(new ActiveZoneItem(zone.Id, zone.Name, Humanize(zone.Type)));
    }

    private void RefreshWeather()
    {
        if (_cop.Weather is not { } weather)
        {
            WeatherSummary = "No weather report";
            return;
        }

        var toward = GeoMath.CompassPoint(weather.WindFromDegrees + 180);
        WeatherSummary = $"Wind from {GeoMath.CompassPoint(weather.WindFromDegrees)} ({weather.WindFromDegrees:F0}°) → {toward} " +
                         $"· {weather.WindSpeedMps:F1} m/s · {weather.TemperatureC:F0}°C · {weather.Source} {Time(weather.ObservedAt)}";
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

    internal static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm");

    /// <summary>"StructureFire" → "Structure fire".</summary>
    internal static string Humanize<T>(T value) where T : Enum
    {
        var name = value.ToString();
        var spaced = string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));
        return spaced;
    }
}
