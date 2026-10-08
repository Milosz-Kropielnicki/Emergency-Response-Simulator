using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.State;
using Emergency_Response_Simulator.ViewModels.Iap;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>
/// The dashboard: header, pop-out panels, status boards and history (Design Document §6, Appendix D).
/// Displays <see cref="CopView"/> — the live COP or a replay — and never ground truth. Commands go
/// through C2 and are refused while replaying.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly CopView _cop;
    private readonly ISimulationControl _simulation;
    private readonly IC2Service _c2;
    private readonly IRoutingService _routing;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _clockTimer;
    private int _refreshQueued;
    private DateTimeOffset _messageExpires;

    public MainViewModel(CopView cop, ISimulationControl simulation, IC2Service c2, IIapService iap, TimelineViewModel timeline,
        IRoutingService routing, DataSourceInfo dataSource)
    {
        _cop = cop;
        _simulation = simulation;
        _c2 = c2;
        _routing = routing;
        UnitDetail = new UnitDetailViewModel(routing);
        Command = new CommandViewModel(this, c2, cop);
        Command.InitialiseDefaults();
        Ics = new IcsViewModel(this, c2);
        Iap = new IapBuilderViewModel(this, iap, cop, simulation);
        Timeline = timeline;
        ZoneDrawing = new ZoneDrawingViewModel(c2, this);
        IncidentDetail = new IncidentDetailViewModel(this, c2);
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

    public TimelineViewModel Timeline { get; }

    public IncidentDetailViewModel IncidentDetail { get; }

    public UnitDetailViewModel UnitDetail { get; }

    /// <summary>Orders, resource requests, approvals and notifications.</summary>
    public CommandViewModel Command { get; }

    /// <summary>The selected incident's ICS organisation and span of control.</summary>
    public IcsViewModel Ics { get; }

    /// <summary>The Incident Action Plan builder for the selected incident (opened in its own window).</summary>
    public IapBuilderViewModel Iap { get; }

    /// <summary>Raised when the operator asks for the IAP builder.</summary>
    public event EventHandler? IapRequested;

    [RelayCommand]
    private void OpenIap() => IapRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The selected incident's planning state, e.g. "Period 1 (14:00–15:00) · v2 approved, briefed 14:20".</summary>
    [ObservableProperty] private string _planSummary = "";

    public bool IsReplay => _cop.IsReplay;

    // ---- Header ----

    [ObservableProperty] private string _incidentTitle = "NO ACTIVE INCIDENT";
    [ObservableProperty] private string _incidentSubtitle = "Monitoring";
    [ObservableProperty] private string _simClock = "--:--:--";
    [ObservableProperty] private string _simDate = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _timeScale = 1;

    /// <summary>Result of the last command, shown briefly in the header.</summary>
    [ObservableProperty] private string _commandMessage = "";
    [ObservableProperty] private bool _commandFailed;

    public IReadOnlyList<double> TimeScales { get; } = [1, 2, 5, 10, 30];

    // ---- Pop-out panels ----

    [ObservableProperty] private bool _isLeftPanelOpen = true;
    [ObservableProperty] private bool _isRightPanelOpen = true;
    [ObservableProperty] private bool _isTopPanelOpen;

    /// <summary>0 = comms hub, 1 = incident detail, 2 = unit detail, 3 = ICS organisation.</summary>
    [ObservableProperty] private int _rightPanelTab;

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
                new(MapLayerKeys.Reports, "Located reports", true),
                new(MapLayerKeys.FireUnits, "Fire units", true),
                new(MapLayerKeys.EmsUnits, "EMS units", true),
                new(MapLayerKeys.PoliceUnits, "Police units", true),
                new(MapLayerKeys.OtherUnits, "Other units", true),
                new(MapLayerKeys.Routes, "Planned routes", true),
                new(MapLayerKeys.Trails, "Unit trails (AVL)", true),
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

    // ---- Status boards ----

    public ObservableCollection<IncidentRow> IncidentBoard { get; } = [];
    public ObservableCollection<UnitRow> ResourceBoard { get; } = [];
    public ObservableCollection<ResourceSummary> ResourceSummary { get; } = [];
    public ObservableCollection<AlertRow> AlertFeed { get; } = [];
    public ObservableCollection<ReportRow> IntelligenceFeed { get; } = [];

    [ObservableProperty] private int _unacknowledgedAlerts;

    /// <summary>The incident the operator is working on: target for dispatch, report linking and zones.</summary>
    [ObservableProperty] private Guid? _selectedIncidentId;

    /// <summary>Two-way binding for the incident board's selected row.</summary>
    public IncidentRow? SelectedIncidentRow
    {
        get => IncidentBoard.FirstOrDefault(r => r.Id == SelectedIncidentId);
        set
        {
            if (value is not null && value.Id != SelectedIncidentId)
                SelectedIncidentId = value.Id;
        }
    }

    /// <summary>The unit shown in the unit panel and highlighted on the map.</summary>
    [ObservableProperty] private Guid? _selectedUnitId;

    /// <summary>Two-way binding for the resource board's selected row.</summary>
    public UnitRow? SelectedUnitRow
    {
        get => ResourceBoard.FirstOrDefault(r => r.Id == SelectedUnitId);
        set
        {
            if (value is not null && value.Id != SelectedUnitId)
                SelectedUnitId = value.Id;
        }
    }

    partial void OnSelectedUnitIdChanged(Guid? value)
    {
        var unit = value is { } id ? _cop.FindUnit(id) : null;
        UnitDetail.Load(unit, Now);
        OnPropertyChanged(nameof(SelectedUnitRow));
        if (unit is not null)
        {
            RightPanelTab = 2;
            IsRightPanelOpen = true;
            if (unit.Location is { } location)
                FocusRequested?.Invoke(this, GeoPoint.FromPoint(location));
        }
    }

    /// <summary>The moment being displayed: simulation time, or the replay time.</summary>
    private DateTimeOffset Now => _cop.ReplayTime ?? _simulation.SimTime;

    /// <summary>
    /// Ranks available units by road ETA to the selected incident, avoiding declared closures and hot zones
    /// (Design Document §7.5): "Engine 12 can reach the incident in approximately 6 minutes".
    /// </summary>
    public async Task RankClosestUnitsAsync()
    {
        IncidentDetail.Candidates.Clear();
        if (SelectedIncidentId is not { } id || _cop.FindIncident(id) is not { } incident) return;

        var target = GeoPoint.FromPoint(incident.Location);
        var avoid = _cop.Zones
            .Where(z => z.Type is ZoneType.RoadClosure or ZoneType.HotZone or ZoneType.FireExclusion)
            .Select(z => z.Area)
            .ToList();
        var available = _cop.Units.Where(u => u.Status == UnitStatus.Available && u.Location is not null).ToList();
        var departAt = _simulation.SimTime;

        IncidentDetail.RankingNote = _routing.IsReady ? "Ranking by road…" : "No road network: straight-line estimates.";
        var ranked = await Task.Run(() => available.Select(unit =>
        {
            var from = GeoPoint.FromPoint(unit.Location!);
            var route = _routing.Route(from, target, new RouteOptions(unit.Type, departAt, Emergency: true, Avoid: avoid));
            var seconds = route?.Duration.TotalSeconds
                          ?? GeoMath.DistanceMeters(from, target) * 1.3 / (45 / 3.6);
            var meters = route?.DistanceMeters ?? GeoMath.DistanceMeters(from, target) * 1.3;
            return new UnitCandidate(unit.Id, unit.Callsign, ResourceGroups.Label(unit.Type),
                $"{(int)(seconds / 60)}:{(int)(seconds % 60):D2}", meters < 1000 ? $"{meters:F0} m" : $"{meters / 1000:F1} km", seconds);
        }).OrderBy(c => c.Seconds).ToList());

        if (SelectedIncidentId != id) return; // selection moved on while ranking
        foreach (var candidate in ranked)
            IncidentDetail.Candidates.Add(candidate);
        IncidentDetail.RankingNote = ranked.Count == 0
            ? "No units available."
            : $"Road travel time (excludes ~45 s turnout); avoids {avoid.Count} declared closure/hazard zone(s).";
    }

    /// <summary>Raised when the operator picks an incident, so the map can centre on it.</summary>
    public event EventHandler<GeoPoint>? FocusRequested;

    partial void OnSelectedIncidentIdChanged(Guid? value)
    {
        var incident = value is { } id ? _cop.FindIncident(id) : null;
        IncidentDetail.Load(incident, selectionChanged: true);
        OnPropertyChanged(nameof(SelectedIncidentRow));
        ZoneDrawing.TargetChanged();
        if (incident is not null)
        {
            RightPanelTab = 1;
            IsRightPanelOpen = true;
            FocusRequested?.Invoke(this, GeoPoint.FromPoint(incident.Location));
        }
        RefreshResourceBoard();
        RefreshIntelligence();
        Command.Refresh();
        Ics.Load(incident);
        Iap.Load(incident);
        RefreshPlanSummary();
        _ = RankClosestUnitsAsync();
    }

    public Incident? FindIncident(Guid id) => _cop.FindIncident(id);

    // ---- Commands (all through C2; refused while replaying) ----

    /// <summary>Runs a C2 command and reports the outcome in the header.</summary>
    public async Task<CommandResult?> RunCommandAsync(Func<Task<CommandResult>> command, string successMessage,
        bool quietIfUnchanged = false)
    {
        if (_cop.IsReplay)
        {
            ShowCommandResult(CommandResult.Fail("Return to live before issuing commands."));
            return null;
        }

        var result = await command();
        if (!(quietIfUnchanged && !result.Succeeded && result.Error == "Nothing changed."))
            ShowCommandResult(result, successMessage);
        return result;
    }

    public void ShowCommandResult(CommandResult result, string? successMessage = null)
    {
        CommandFailed = !result.Succeeded;
        CommandMessage = result.Succeeded ? successMessage ?? "Done" : result.Error ?? "Command refused";
        _messageExpires = DateTimeOffset.Now.AddSeconds(6);
    }

    [RelayCommand]
    private async Task LiftZoneAsync(Guid zoneId) => await RunCommandAsync(() => _c2.LiftZoneAsync(zoneId), "Zone lifted");

    [RelayCommand]
    private async Task DispatchUnitAsync(Guid unitId)
    {
        if (SelectedIncidentId is not { } incidentId)
        {
            ShowCommandResult(CommandResult.Fail("Select an incident first."));
            return;
        }
        var callsign = _cop.FindUnit(unitId)?.Callsign;
        await RunCommandAsync(() => _c2.DispatchAsync(unitId, incidentId), $"{callsign} dispatched");
    }

    [RelayCommand]
    private async Task ReassignUnitAsync(Guid unitId)
    {
        if (SelectedIncidentId is not { } incidentId) return;
        await RunCommandAsync(() => _c2.ReassignAsync(unitId, incidentId),
            $"{_cop.FindUnit(unitId)?.Callsign} reassigned to {_cop.FindIncident(incidentId)?.Number}");
    }

    [RelayCommand]
    private async Task CancelUnitAsync(Guid unitId) =>
        await RunCommandAsync(() => _c2.CancelDispatchAsync(unitId, "Stood down by dispatcher"),
            $"{_cop.FindUnit(unitId)?.Callsign} stood down");

    public async Task RequestUnitStatusAsync(Guid unitId, UnitStatus status) =>
        await RunCommandAsync(() => _c2.UpdateUnitStatusAsync(unitId, status),
            $"{_cop.FindUnit(unitId)?.Callsign} → {EventDescriber.Humanize(status)}");

    [RelayCommand]
    private async Task AcknowledgeAlertAsync(Guid alertId) =>
        await RunCommandAsync(() => _c2.AcknowledgeAlertAsync(alertId), "Alert acknowledged");

    public async Task AssessReportAsync(Guid reportId, VerificationStatus verification)
    {
        var report = _cop.Reports.FirstOrDefault(r => r.Id == reportId);
        if (report is null) return;
        // Confirming raises confidence; downgrading to suspected lowers it.
        var confidence = verification switch
        {
            VerificationStatus.Confirmed or VerificationStatus.Known => Confidence.High,
            VerificationStatus.Suspected => Confidence.Low,
            _ => report.Confidence,
        };
        await RunCommandAsync(() => _c2.AssessReportAsync(reportId, verification, confidence), $"Report marked {verification}");
    }

    [RelayCommand]
    private async Task CreateIncidentFromReportAsync(Guid reportId)
    {
        var report = _cop.Reports.FirstOrDefault(r => r.Id == reportId);
        if (report?.Location is null)
        {
            ShowCommandResult(CommandResult.Fail("The report has no location to open an incident at."));
            return;
        }

        var result = await RunCommandAsync(() => _c2.CreateIncidentAsync(
            GuessIncidentType(report.Claim), IncidentPriority.High, GeoPoint.FromPoint(report.Location),
            address: null, name: null, fromReportId: reportId), "Incident created");

        if (result?.EntityId is { } incidentId)
            SelectedIncidentId = incidentId;
    }

    [RelayCommand]
    private async Task LinkReportAsync(Guid reportId)
    {
        if (SelectedIncidentId is not { } incidentId) return;
        await RunCommandAsync(() => _c2.LinkReportAsync(reportId, incidentId),
            $"Report attributed to {_cop.FindIncident(incidentId)?.Number}");
    }

    /// <summary>A starting guess from the caller's words; the operator corrects it in the incident panel.</summary>
    private static IncidentType GuessIncidentType(string claim)
    {
        var text = claim.ToLowerInvariant();
        if (text.Contains("explosion") || text.Contains("bang")) return IncidentType.Explosion;
        if (text.Contains("fire") || text.Contains("smoke")) return IncidentType.StructureFire;
        if (text.Contains("chemical") || text.Contains("gas") || text.Contains("leak")) return IncidentType.HazmatRelease;
        if (text.Contains("crash") || text.Contains("collision")) return IncidentType.RoadAccident;
        if (text.Contains("flood")) return IncidentType.Flood;
        if (text.Contains("collapse")) return IncidentType.StructuralCollapse;
        return IncidentType.Other;
    }

    // ---- Simulation control ----

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
        Timeline.Tick();
        if (SelectedUnitId is { } selectedUnit)
            UnitDetail.Load(_cop.FindUnit(selectedUnit), Now);

        if (CommandMessage.Length > 0 && DateTimeOffset.Now > _messageExpires)
            CommandMessage = "";
    }

    // ---- Refresh from the COP ----

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
        OnPropertyChanged(nameof(IsReplay));
        if (SelectedIncidentId is { } id && _cop.FindIncident(id) is null)
            SelectedIncidentId = null; // e.g. replaying to before it existed
        if (SelectedUnitId is { } unitId && _cop.FindUnit(unitId) is null)
            SelectedUnitId = null;

        RefreshHeader();
        RefreshIncidentBoard();
        RefreshResourceBoard();
        RefreshAlerts();
        RefreshIntelligence();
        RefreshComms();
        RefreshZones();
        RefreshWeather();
        IncidentDetail.Load(SelectedIncidentId is { } selected ? _cop.FindIncident(selected) : null, selectionChanged: false);
        Command.Refresh();
        Ics.Load(SelectedIncidentId is { } forIcs ? _cop.FindIncident(forIcs) : null);
        Iap.Load(SelectedIncidentId is { } forIap ? _cop.FindIncident(forIap) : null);
        RefreshPlanSummary();
    }

    private void RefreshPlanSummary()
    {
        if (SelectedIncidentId is not { } id || _cop.FindIncident(id) is null)
        {
            PlanSummary = "";
            return;
        }
        if (_cop.CurrentPeriod(id, Now) is not { } period)
        {
            PlanSummary = "No operational period yet";
            return;
        }

        var label = $"Period {period.Number} ({period.Window})";
        var latest = _cop.VersionsOf(period.Id).LastOrDefault();
        PlanSummary = _cop.ApprovedPlan(period.Id) is { } inForce
            ? $"{label} · v{inForce.Version} in force" + (inForce.BriefedAt is null ? ", not briefed" : "") +
              (latest is { } l && l.Id != inForce.Id ? $" · v{l.Version} {(l.Status == IapStatus.Draft ? "drafting" : "awaiting approval")}" : "")
            : latest is null ? $"{label} · no plan"
            : $"{label} · v{latest.Version} {(latest.Status == IapStatus.Draft ? "draft" : "awaiting approval")}";
        if (Now >= period.End) PlanSummary += " · period ended";
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
        var focus = (SelectedIncidentId is { } id ? _cop.FindIncident(id) : null)
            ?? _cop.Incidents
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
        IncidentSubtitle = $"{focus.Number}   Status: {Humanize(focus.Status).ToUpperInvariant()}   Priority: {focus.Priority}" +
                           (focus.IncidentCommanderName is { } ic ? $"   IC: {ic}" : "");
    }

    private void RefreshIncidentBoard()
    {
        IncidentBoard.Clear();
        foreach (var incident in _cop.Incidents.OrderByDescending(i => i.Status != IncidentStatus.Closed)
                     .ThenByDescending(i => i.Priority).ThenBy(i => i.ReportedAt))
        {
            IncidentBoard.Add(new IncidentRow(
                incident.Id,
                incident.Number,
                Humanize(incident.Type),
                incident.Priority,
                Humanize(incident.Status),
                incident.Address ?? $"{incident.Location.Y:F4}, {incident.Location.X:F4}",
                $"{incident.CasualtiesReported} / {incident.CasualtiesConfirmed}",
                incident.AssignedUnits.Count,
                incident.IncidentCommanderName ?? "—",
                Time(incident.LastUpdatedAt)));
        }
        OnPropertyChanged(nameof(SelectedIncidentRow));
    }

    private void RefreshResourceBoard()
    {
        ResourceBoard.Clear();
        // Each committed unit's task in its incident's plan in force (the IAP's resource assignment, §8.4).
        var plans = new Dictionary<Guid, IncidentActionPlan?>();
        string? PlanTask(Unit unit)
        {
            if (unit.AssignedIncidentId is not { } incidentId) return null;
            if (!plans.TryGetValue(incidentId, out var plan))
                plans[incidentId] = plan = _cop.PlanInForce(incidentId, Now);
            return plan?.Content.Assignments.FirstOrDefault(a => a.UnitId == unit.Id)?.Assignment;
        }

        foreach (var unit in _cop.Units.OrderBy(u => u.Agency?.Type).ThenBy(u => u.Callsign, StringComparer.Ordinal))
            ResourceBoard.Add(new UnitRow(this, unit, Now, PlanTask(unit)));
        OnPropertyChanged(nameof(SelectedUnitRow));

        ResourceSummary.Clear();
        foreach (var group in _cop.Units.GroupBy(u => ResourceGroups.For(u.Type)).OrderBy(g => g.Key))
        {
            ResourceSummary.Add(new ResourceSummary(group.Key,
                group.Count(u => u.Status == UnitStatus.Available), group.Count()));
        }
    }

    private void RefreshAlerts()
    {
        AlertFeed.Clear();
        foreach (var alert in _cop.Alerts.OrderBy(a => a.AcknowledgedAt is not null).ThenByDescending(a => a.RaisedAt).Take(100))
        {
            AlertFeed.Add(new AlertRow(alert.Id, Time(alert.RaisedAt), Humanize(alert.Category).ToUpperInvariant(),
                alert.Severity.ToString(), alert.Title, alert.Message, alert.AcknowledgedAt is not null));
        }
        UnacknowledgedAlerts = _cop.Alerts.Count(a => a.AcknowledgedAt is null);
    }

    private void RefreshIntelligence()
    {
        IntelligenceFeed.Clear();
        foreach (var report in _cop.Reports.OrderByDescending(r => r.ReceivedAt).Take(100))
            IntelligenceFeed.Add(new ReportRow(this, report));
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

    internal static string Time(DateTimeOffset at, bool seconds = false) =>
        at.ToLocalTime().ToString(seconds ? "HH:mm:ss" : "HH:mm");

    internal static string Humanize<T>(T value) where T : Enum => EventDescriber.Humanize(value);
}
