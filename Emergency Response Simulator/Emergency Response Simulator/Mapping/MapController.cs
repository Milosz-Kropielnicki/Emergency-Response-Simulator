using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.Routing;
using Emergency_Response_Simulator.Simulation.Services;
using Emergency_Response_Simulator.Simulation.State;
using Emergency_Response_Simulator.ViewModels;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Manipulations;
using Mapsui.Nts;
using Mapsui.Styles;
using Mapsui.Styles.Thematics;
using Mapsui.UI.Wpf;
using Microsoft.Extensions.Configuration;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Mapping;

/// <summary>
/// Builds and maintains the map: base tiles for the selected view, static GIS layers from PostGIS,
/// and operational layers (zones, incidents, units, wind, hospitals) projected from the COP. The traffic view
/// adds congestion from the city's traffic feed. Instructor-only layers draw ground truth from the engine's
/// snapshot: real hazards, people, true unit positions and live traffic. Also handles map clicks for feature
/// info, nearest-facility lookups and zone drawing.
/// </summary>
public sealed class MapController
{
    private const string InfoTitleField = "info:title";
    private const string InfoLinesField = "info:lines";
    private const string RoleField = "role";
    private const string IncidentIdField = "incident:id";
    private const string UnitIdField = "unit:id";

    private readonly MapControl _control;
    private readonly MainViewModel _viewModel;
    private readonly ICopService _cop;
    private readonly IGisService? _gis;
    private readonly AvlService _avl;
    private readonly Dispatcher _dispatcher;
    private readonly Mapsui.Map _map = new();
    private readonly GeoPoint _home;

    private readonly Dictionary<string, MemoryLayer> _staticLayers = [];
    private readonly MemoryLayer _zones = new("Zones");
    private readonly MemoryLayer _reports = new("Reports");
    private readonly MemoryLayer _incidents = new("Incidents");
    private readonly MemoryLayer _routes = new("Routes");
    private readonly MemoryLayer _trails = new("Trails");
    private readonly MemoryLayer _units = new("Units");
    private readonly MemoryLayer _weather = new("Wind");
    private readonly MemoryLayer _drawing = new("Drawing");
    private readonly MemoryLayer _hospitals = new("Hospitals");
    private readonly MemoryLayer _traffic = new("Traffic");
    private readonly MemoryLayer _truth = new("Ground truth: hazards");
    private readonly MemoryLayer _people = new("Ground truth: people");
    private readonly MemoryLayer _trueUnits = new("Ground truth: units");

    private readonly InstructorViewModel _instructor;
    private readonly RoutingService _routing;
    private readonly TrafficFeed _feed;
    private readonly LiveTraffic _live;

    private string _view = BaseMaps.Street;
    private ILayer _baseLayer;
    private int _refreshQueued;

    public MapController(MapControl control, MainViewModel viewModel, ICopService cop, IGisService? gis, AvlService avl,
        IConfiguration mapSettings, RoutingService routing, TrafficFeed feed, LiveTraffic live)
    {
        _avl = avl;
        _instructor = viewModel.Instructor;
        _routing = routing;
        _feed = feed;
        _live = live;
        _control = control;
        _viewModel = viewModel;
        _cop = cop;
        _gis = gis;
        _dispatcher = control.Dispatcher;
        _home = new GeoPoint(mapSettings.GetValue("CenterLatitude", 53.344), mapSettings.GetValue("CenterLongitude", -6.26));

        _baseLayer = BaseMaps.Create(_view);
        _map.Layers.Add(_baseLayer);
        foreach (var definition in GisLayerKeys.All.OrderBy(d => d.DisplayOrder))
        {
            var layer = new MemoryLayer(definition.Name)
            {
                Features = [],
                Style = StaticLayerStyle(definition.Key),
                MaxVisible = MapStyles.ResolutionAtZoom(definition.MinZoom),
            };
            _staticLayers[definition.Key] = layer;
            _map.Layers.Add(layer);
        }

        foreach (var layer in new[]
                 {
                     _traffic, _zones, _truth, _reports, _routes, _trails, _people, _incidents, _hospitals, _units, _trueUnits, _weather, _drawing,
                 })
        {
            layer.Features = [];
            layer.Style = null; // operational features carry their own styles
            _map.Layers.Add(layer);
        }
        _drawing.Style = MapStyles.DrawingPreview;

        var (x, y) = WebMercator.FromLonLat(_home.Longitude, _home.Latitude);
        _map.Navigator.CenterOnAndZoomTo(new MPoint(x, y),
            MapStyles.ResolutionAtZoom(mapSettings.GetValue("ZoomLevel", 14)));
        _control.Map = _map;

        WireViewModel();
        _control.MapTapped += OnMapTapped;
        _control.MapPointerMoved += OnMapPointerMoved;
        _cop.Changed += (_, _) => QueueOperationalRefresh();
        _instructor.Refreshed += (_, _) => RefreshTruth();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedUnitId))
                RefreshOperational();
        };
        _viewModel.FocusRequested += (_, point) =>
        {
            var (fx, fy) = WebMercator.FromLonLat(point.Longitude, point.Latitude);
            _map.Navigator.CenterOn(new MPoint(fx, fy), 400);
        };

        ApplyVisibility();
        RefreshOperational();

        // Optional starting view (Map:InitialView = street | satellite | weather | traffic | terrain).
        if (mapSettings["InitialView"] is { Length: > 0 } initialView)
        {
            foreach (var view in _viewModel.MapViews)
                view.IsOn = view.Key == initialView;
        }
    }

    /// <summary>Loads every imported static layer from PostGIS. Safe to call once at start-up.</summary>
    public async Task LoadStaticLayersAsync(CancellationToken cancellationToken = default)
    {
        if (_gis is null)
        {
            foreach (var definition in GisLayerKeys.All)
                MarkUnavailable(definition.Key, "needs database");
            return;
        }

        IReadOnlyList<GisLayer> imported;
        try
        {
            imported = await _gis.GetLayersAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var definition in GisLayerKeys.All)
                MarkUnavailable(definition.Key, "database unavailable");
            _viewModel.ShowInfo("GIS data unavailable", [ex.Message]);
            return;
        }

        foreach (var definition in GisLayerKeys.All.OrderBy(d => d.DisplayOrder))
        {
            var layerInfo = imported.FirstOrDefault(l => l.Key == definition.Key);
            if (layerInfo is null)
            {
                MarkUnavailable(definition.Key, "not imported");
                continue;
            }

            var features = await _gis.GetAllFeaturesAsync(definition.Key, cancellationToken);
            // Projection and feature building is CPU work (50k buildings); keep it off the UI thread.
            var mapFeatures = await Task.Run(() => features.SelectMany(f => ToMapFeatures(f, definition)).ToList(), cancellationToken);

            var layer = _staticLayers[definition.Key];
            layer.Features = mapFeatures;
            layer.DataHasChanged();

            var toggle = definition.Key == GisLayerKeys.AreaBoundaries ? _viewModel.AreaCodes : _viewModel.FindLayerToggle(definition.Key);
            if (toggle is not null)
            {
                toggle.IsAvailable = features.Count > 0;
                toggle.Detail = features.Count.ToString("N0", CultureInfo.CurrentCulture);
            }
        }

        _map.RefreshGraphics();
    }

    // ---- View model wiring ----

    private void WireViewModel()
    {
        foreach (var toggle in _viewModel.LayerGroups.SelectMany(g => g.Items))
        {
            toggle.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(ToggleItem.IsOn)) return;
                if (toggle.Key.StartsWith("op:") || toggle.Key.StartsWith("zones:"))
                    RefreshOperational();
                else if (toggle.Key.StartsWith("truth:"))
                    RefreshTruth();
                else
                    ApplyVisibility();
            };
        }

        _viewModel.AreaCodes.PropertyChanged += OnToggleChanged;

        foreach (var view in _viewModel.MapViews)
        {
            view.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ToggleItem.IsOn) && view.IsOn)
                    SwitchView(view.Key);
            };
        }

        _viewModel.ZoneDrawing.ShapeChanged += (_, _) => RefreshDrawing(null);
    }

    private void OnToggleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToggleItem.IsOn))
            ApplyVisibility();
    }

    private void SwitchView(string view)
    {
        _view = view;
        _map.Layers.Remove(_baseLayer);
        _baseLayer = BaseMaps.Create(view);
        _map.Layers.Insert(0, _baseLayer);

        // The traffic view re-styles roads; the others use the normal road style.
        _staticLayers[GisLayerKeys.Roads].Style = StaticLayerStyle(GisLayerKeys.Roads);
        ApplyVisibility();
        RefreshOperational();
        RefreshTruth();
    }

    /// <summary>A layer shows when its toggle is on, or when the current view depends on it.</summary>
    private void ApplyVisibility()
    {
        foreach (var (key, layer) in _staticLayers)
        {
            var toggle = key == GisLayerKeys.AreaBoundaries ? _viewModel.AreaCodes : _viewModel.FindLayerToggle(key);
            var forced = (_view == BaseMaps.Traffic && key == GisLayerKeys.Roads)
                      || (_view == BaseMaps.Terrain && key == GisLayerKeys.Elevation);
            layer.Enabled = forced || toggle?.IsOn == true;
        }
        _map.RefreshGraphics();
    }

    private IStyle StaticLayerStyle(string key)
    {
        var style = MapStyles.ForStaticLayer(key, trafficView: _view == BaseMaps.Traffic);
        if (key is GisLayerKeys.Roads or GisLayerKeys.Buildings or GisLayerKeys.Water or GisLayerKeys.Railways
            or GisLayerKeys.Elevation or GisLayerKeys.AreaBoundaries or GisLayerKeys.Hydrants)
        {
            return style;
        }

        // Facilities mapped as areas get a faint footprint plus the symbol at their interior point.
        var area = MapStyles.FacilityArea(key);
        return new ThemeStyle(f => f[RoleField] as string == "area" ? area : style);
    }

    private void MarkUnavailable(string key, string reason)
    {
        var toggle = key == GisLayerKeys.AreaBoundaries ? _viewModel.AreaCodes : _viewModel.FindLayerToggle(key);
        if (toggle is null) return;
        toggle.IsAvailable = false;
        toggle.Detail = reason;
    }

    // ---- Static features ----

    private static IEnumerable<IFeature> ToMapFeatures(GisFeature feature, GisLayerDefinition definition)
    {
        var title = feature.Name ?? definition.Name;
        var lines = feature.Properties
            .Where(p => p.Key != "osm_id")
            .Select(p => $"{p.Key}: {p.Value}")
            .Prepend(definition.Name)
            .Append($"Source: {feature.Properties.GetValueOrDefault("osm_id", "elevation grid")}")
            .ToArray();

        var isFacility = definition.Group is GisLayerGroups.Facilities or GisLayerGroups.Infrastructure;
        if (isFacility && feature.Geometry is not Point)
        {
            yield return Tag(new GeometryFeature(WebMercator.FromWgs84(feature.Geometry)), title, lines, role: "area");
            yield return Tag(new GeometryFeature(WebMercator.FromWgs84(feature.Geometry.InteriorPoint)), title, lines, label: feature.Name);
            yield break;
        }

        var mapFeature = Tag(new GeometryFeature(WebMercator.FromWgs84(feature.Geometry)), title, lines, label: feature.Name);
        foreach (var (key, value) in feature.Properties)
            mapFeature[key] = value; // used by themed styles (road class, elevation)
        yield return mapFeature;
    }

    private static GeometryFeature Tag(GeometryFeature feature, string title, string[] lines, string? label = null, string? role = null)
    {
        feature[InfoTitleField] = title;
        feature[InfoLinesField] = lines;
        if (label is not null) feature[MapStyles.LabelField] = label;
        if (role is not null) feature[RoleField] = role;
        return feature;
    }

    // ---- Operational features (from the COP) ----

    private void QueueOperationalRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            RefreshOperational();
        });
    }

    private bool IsOn(string key) => _viewModel.FindLayerToggle(key)?.IsOn == true;

    private void RefreshOperational()
    {
        _zones.Features = _cop.Zones
            .Where(z => IsOn(MapStyles.ZoneToggleKey(z.Type)))
            .Select(ZoneFeature)
            .ToList();

        _reports.Features = IsOn(MapLayerKeys.Reports)
            ? _cop.Reports.Where(r => r.Location is not null).SelectMany(ReportFeatures).ToList()
            : [];

        _incidents.Features = IsOn(MapLayerKeys.Incidents)
            ? _cop.Incidents.Where(i => i.Status != IncidentStatus.Closed).Select(IncidentFeature).ToList()
            : [];

        var visibleUnits = _cop.Units
            .Where(u => u.Location is not null && IsOn(MapStyles.UnitToggleKey(u.Agency?.Type)))
            .ToList();
        var selected = _viewModel.SelectedUnitId;

        _units.Features = visibleUnits.Select(u => UnitFeature(u, u.Id == selected)).ToList();

        _routes.Features = IsOn(MapLayerKeys.Routes)
            ? visibleUnits.Where(u => u.PlannedRoute is not null && u.Status is UnitStatus.Dispatched or UnitStatus.EnRoute)
                .Select(u => RouteFeature(u, u.Id == selected)).ToList()
            : [];

        // Trails come from the live AVL feed, so they are not shown while replaying.
        _trails.Features = IsOn(MapLayerKeys.Trails) && !_viewModel.IsReplay
            ? visibleUnits.Select(u => TrailFeature(u.Id)).OfType<IFeature>().ToList()
            : [];

        _weather.Features = IsOn(MapLayerKeys.Weather) || _view == BaseMaps.Weather ? WindFeatures() : [];

        _hospitals.Features = IsOn(MapLayerKeys.Hospitals) ? _cop.Hospitals.Select(HospitalFeature).ToList() : [];

        foreach (var layer in new[] { _zones, _reports, _routes, _trails, _incidents, _hospitals, _units, _weather })
            layer.DataHasChanged();
        _map.RefreshGraphics();
    }

    private static IFeature ZoneFeature(Zone zone)
    {
        var projection = MetricProjection.For(GeoPoint.FromPoint(zone.Area.Centroid));
        var size = zone.Area is LineString
            ? $"Length: {projection.LengthMeters(zone.Area):N0} m"
            : $"Area: {projection.AreaSquareMeters(zone.Area) / 1_000_000:N3} km²";

        var feature = Tag(new GeometryFeature(WebMercator.FromWgs84(zone.Area)), zone.Name,
            [MainViewModel.Humanize(zone.Type), size, $"In force since {MainViewModel.Time(zone.EffectiveFrom)}"],
            label: zone.Name);
        AddStyles(feature, MapStyles.Zone(zone.Type));
        return feature;
    }

    /// <summary>
    /// A located report: a marker coloured by confidence plus a circle showing its stated location
    /// accuracy, so the operator sees how vague an early call really is (Design Document §6.7).
    /// </summary>
    private static IEnumerable<IFeature> ReportFeatures(Report report)
    {
        var location = GeoPoint.FromPoint(report.Location!);
        var lines = new[]
        {
            $"\"{report.Claim}\"",
            $"Source: {report.SourceName} ({MainViewModel.Humanize(report.Source)})",
            $"Confidence: {report.Confidence} · {report.Verification}",
            $"Location accuracy: {(report.LocationAccuracyMeters is { } m ? $"±{m:F0} m" : "unknown")}",
            $"Received: {MainViewModel.Time(report.ReceivedAt, seconds: true)}",
            $"Incident: {report.Incident?.Number ?? "not attributed"}",
        };

        if (report.LocationAccuracyMeters is { } accuracy and > 5)
        {
            var circle = Wgs84.CreatePolygon(Wgs84.Circle(location, accuracy, 36));
            var area = Tag(new GeometryFeature(WebMercator.FromWgs84(circle)), report.SourceName, lines);
            AddStyles(area, MapStyles.ReportAccuracy(report.Confidence));
            yield return area;
        }

        var (x, y) = WebMercator.FromLonLat(location.Longitude, location.Latitude);
        var marker = new PointFeature(x, y);
        marker[InfoTitleField] = report.SourceName;
        marker[InfoLinesField] = lines;
        AddStyles(marker, MapStyles.Report(report.Confidence));
        yield return marker;
    }

    private static IFeature IncidentFeature(Incident incident)
    {
        var feature = Tag(new GeometryFeature(WebMercator.FromWgs84(incident.Location)),
            $"{incident.Number} {incident.Name ?? MainViewModel.Humanize(incident.Type)}",
            [
                $"Priority: {incident.Priority}", $"Status: {MainViewModel.Humanize(incident.Status)}",
                $"Reported: {MainViewModel.Time(incident.ReportedAt)}", $"Units assigned: {incident.AssignedUnits.Count}",
                $"Casualties: {incident.CasualtiesReported} reported, {incident.CasualtiesConfirmed} confirmed",
            ],
            label: incident.Number);
        feature[IncidentIdField] = incident.Id;
        AddStyles(feature, MapStyles.Incident(incident.Priority));
        return feature;
    }

    private static IFeature UnitFeature(Unit unit, bool selected)
    {
        var moving = unit.SpeedKph >= 1;
        var label = unit.Status == UnitStatus.EnRoute && unit.Eta is { } eta
            ? $"{unit.Callsign} · ETA {(int)eta.TotalMinutes}:{eta.Seconds:D2}"
            : unit.Callsign;

        var feature = Tag(new GeometryFeature(WebMercator.FromWgs84(unit.Location!)), unit.Callsign,
            [
                $"{ResourceGroups.Label(unit.Type)} · {unit.Agency?.ShortName ?? "No agency"}",
                $"Status: {MainViewModel.Humanize(unit.Status)}",
                moving ? $"{unit.SpeedKph:F0} km/h heading {GeoMath.CompassPoint(unit.Heading)}" : "Stationary",
                $"Last AVL fix: {(unit.LastAvlUpdate is { } at ? MainViewModel.Time(at, seconds: true) : "—")}",
            ],
            label: label);
        feature[UnitIdField] = unit.Id;
        AddStyles(feature, MapStyles.Unit(unit.Agency?.Type, unit.Status, moving ? unit.Heading : null, selected, unit.CommsConnected));
        return feature;
    }

    private static IFeature HospitalFeature(Hospital hospital)
    {
        var feature = Tag(new GeometryFeature(WebMercator.FromWgs84(hospital.Location.ToPoint())), hospital.Name,
            [
                $"Emergency department: {hospital.Occupied}/{hospital.Capacity}" + (hospital.OnDiversion ? ", ON DIVERSION" : ""),
                hospital.Note ?? "",
                $"As reported at {MainViewModel.Time(hospital.ReportedAt)}",
            ],
            label: $"{hospital.Name.Replace(" Hospital", "").Replace(" University", "")} {hospital.Occupied}/{hospital.Capacity}");
        AddStyles(feature, MapStyles.HospitalStatus(hospital.OnDiversion, !hospital.OnDiversion && hospital.Load >= 0.9));
        return feature;
    }

    // ---- Traffic and ground truth ----

    /// <summary>
    /// The traffic view shows the city's traffic feed (what command can see, a couple of minutes old); the
    /// instructor's live-traffic layer shows the roads as they really are. Ground-truth layers come from the
    /// engine's snapshot via the instructor view, refreshed once a second.
    /// </summary>
    private void RefreshTruth()
    {
        var snapshot = _instructor.Snapshot;

        TrafficSnapshot? traffic = IsOn(MapLayerKeys.TruthTraffic) ? _live.Snapshot
            : _view == BaseMaps.Traffic && !_viewModel.IsReplay ? _feed.Snapshot
            : null;
        _traffic.Features = traffic is not null && _routing.Network is { } network ? CongestionFeatures(network, traffic) : [];

        _truth.Features = IsOn(MapLayerKeys.TruthHazards) ? TruthHazardFeatures(snapshot).ToList() : [];
        _people.Features = IsOn(MapLayerKeys.TruthPeople) ? PeopleFeatures(snapshot).ToList() : [];
        _trueUnits.Features = IsOn(MapLayerKeys.TruthUnits) ? TrueUnitFeatures(snapshot).ToList() : [];

        foreach (var layer in new[] { _traffic, _truth, _people, _trueUnits })
            layer.DataHasChanged();
        _map.RefreshGraphics();
    }

    private static List<IFeature> CongestionFeatures(RoadNetwork network, TrafficSnapshot traffic)
    {
        var features = new List<IFeature>();
        foreach (var (edgeId, factor) in traffic.Factors)
        {
            if (factor >= 0.8) continue;
            var edge = network.Edge(edgeId);
            var feature = Tag(new GeometryFeature(WebMercator.FromWgs84(network.EdgeLine(edgeId))), edge.Name ?? "Road",
                [factor < LiveTraffic.GridlockFactor ? "Gridlocked" : $"Traffic at {factor:P0} of normal speed",
                 traffic.DarkSignals.Contains(edgeId) ? "Junction ahead: traffic signals out" : "",
                 $"As of {MainViewModel.Time(traffic.At, seconds: true)}"]);
            AddStyles(feature, MapStyles.Congestion(factor));
            features.Add(feature);
        }
        return features;
    }

    private static IEnumerable<IFeature> TruthHazardFeatures(WorldSnapshot snapshot)
    {
        foreach (var outage in snapshot.Outages)
        {
            var area = Wgs84.CreatePolygon(Wgs84.Circle(outage.Centre, outage.RadiusMeters));
            var feature = Tag(new GeometryFeature(WebMercator.FromWgs84(area)), "Power outage (truth)",
                [outage.Cause, $"Since {MainViewModel.Time(outage.StartedAt)}, restoring {MainViewModel.Time(outage.RestoreAt)}"],
                label: "POWER OUT");
            AddStyles(feature, MapStyles.Outage);
            yield return feature;
        }

        foreach (var hazard in snapshot.Hazards)
        {
            var lines = new[] { hazard.Description, $"{hazard.AreaSquareMeters:N0} m² affected", $"Since {MainViewModel.Time(hazard.StartedAt)}" };
            if (hazard.Zones.Count > 0)
            {
                foreach (var (level, zone) in hazard.Zones)
                {
                    var feature = Tag(new GeometryFeature(WebMercator.FromWgs84(zone)), $"{hazard.Description} (truth)",
                        [.. lines, $"{level} concentration"], label: level == HazardLevel.Low ? hazard.Substance?.ToUpperInvariant() : null);
                    AddStyles(feature, MapStyles.TruthHazard(hazard.Kind, level));
                    yield return feature;
                }
            }
            else if (hazard.Footprint is { } footprint)
            {
                var feature = Tag(new GeometryFeature(WebMercator.FromWgs84(footprint)), $"{hazard.Description} (truth)", lines,
                    label: MainViewModel.Humanize(hazard.Kind).ToUpperInvariant());
                AddStyles(feature, MapStyles.TruthHazard(hazard.Kind));
                yield return feature;
            }
        }

        foreach (var blocked in snapshot.Obstructions)
        {
            var feature = Tag(new GeometryFeature(WebMercator.FromWgs84(blocked.Area)), "Road blocked (truth)", [blocked.Description]);
            AddStyles(feature, MapStyles.Obstruction);
            yield return feature;
        }

        foreach (var (centre, radius, description) in snapshot.Comms.BlackSpots)
        {
            var area = Wgs84.CreatePolygon(Wgs84.Circle(centre, radius));
            var feature = Tag(new GeometryFeature(WebMercator.FromWgs84(area)), "Radio black spot (truth)", [description], label: "NO RADIO");
            AddStyles(feature, MapStyles.BlackSpot);
            yield return feature;
        }

        foreach (var mast in snapshot.Comms.Masts.Where(m => m.Down || m.OnBattery))
        {
            var area = Wgs84.CreatePolygon(Wgs84.Circle(mast.Location, mast.RadiusMeters));
            var feature = Tag(new GeometryFeature(WebMercator.FromWgs84(area)), $"{mast.Name} (truth)",
                [mast.Down ? "Down: no mobile calls or data" : "On batteries"], label: mast.Down ? "NO MOBILE" : "MAST ON BATTERY");
            AddStyles(feature, MapStyles.Outage);
            yield return feature;
        }

        foreach (var site in snapshot.Sites)
        {
            var feature = Tag(new GeometryFeature(WebMercator.FromWgs84(site.Location.ToPoint())), $"{site.Name} (truth)",
                [MainViewModel.Humanize(site.Kind), site.Triggered ? "Set off" : "Not yet reached by any hazard"], label: site.Name);
            AddStyles(feature, MapStyles.Site(site.Triggered));
            yield return feature;
        }

        // Firefighters really in trouble (Phase 8), whether or not command has heard the Mayday.
        foreach (var distress in snapshot.Distress)
        {
            var feature = Tag(new GeometryFeature(WebMercator.FromWgs84(distress.Location.ToPoint())), $"{distress.Member} (truth)",
                [$"{distress.Callsign}: {distress.Cause}", distress.Heard ? "Mayday heard" : "Mayday NOT heard",
                 distress.RescueCallsign is { } rescuer ? $"{rescuer} {distress.RescueProgress:P0} through the rescue" : "No rescue team"],
                label: $"MAYDAY {distress.Member}");
            AddStyles(feature, MapStyles.Distress);
            yield return feature;
        }
    }

    private static IEnumerable<IFeature> PeopleFeatures(WorldSnapshot snapshot)
    {
        foreach (var person in snapshot.Civilians)
        {
            var (x, y) = WebMercator.FromLonLat(person.Location.Longitude, person.Location.Latitude);
            var feature = new PointFeature(x, y);
            AddStyles(feature, MapStyles.Civilian(person.State));
            yield return feature;
        }

        foreach (var casualty in snapshot.Casualties.Where(c => c.State != CasualtyState.AtHospital))
        {
            var feature = Tag(new GeometryFeature(WebMercator.FromWgs84(casualty.Location.ToPoint())), "Casualty (truth)",
                [Core.Events.EventDescriber.TriageLabel(casualty.Triage), MainViewModel.Humanize(casualty.State)]);
            AddStyles(feature, MapStyles.Casualty(casualty.Triage));
            yield return feature;
        }
    }

    /// <summary>Units whose real position is well away from where the COP shows them, or whose radio is dead.</summary>
    private IEnumerable<IFeature> TrueUnitFeatures(WorldSnapshot snapshot)
    {
        foreach (var unit in snapshot.Units)
        {
            var shown = _cop.FindUnit(unit.Id)?.Location is { } p ? GeoPoint.FromPoint(p) : (GeoPoint?)null;
            var off = shown is { } s ? GeoMath.DistanceMeters(s, unit.Location) : double.PositiveInfinity;
            if (off < 50 && !unit.RadioFailed && !unit.BrokenDown) continue;

            var state = unit.BrokenDown ? "broken down" : unit.RadioFailed ? "radio dead" : $"{off:F0} m from COP";
            var feature = Tag(new GeometryFeature(WebMercator.FromWgs84(unit.Location.ToPoint())), $"{unit.Callsign} (truth)",
                [MainViewModel.Humanize(unit.Phase), state], label: $"{unit.Callsign}: {state}");
            AddStyles(feature, MapStyles.TrueUnit);
            yield return feature;
        }
    }

    private static IFeature RouteFeature(Unit unit, bool selected)
    {
        var feature = Tag(new GeometryFeature(WebMercator.FromWgs84(unit.PlannedRoute!)), $"{unit.Callsign} planned route",
            [$"{(unit.RouteDistanceMeters ?? 0) / 1000:F1} km by road", $"ETA {(unit.Eta is { } eta ? $"{(int)eta.TotalMinutes}:{eta.Seconds:D2}" : "—")}"]);
        feature[UnitIdField] = unit.Id;
        AddStyles(feature, MapStyles.Route(unit.Agency?.Type, selected));
        return feature;
    }

    private IFeature? TrailFeature(Guid unitId)
    {
        var trail = _avl.GetTrail(unitId);
        if (trail.Count < 2) return null;

        var line = Wgs84.Factory.CreateLineString(trail.Select(f => f.Location.ToPoint().Coordinate).ToArray());
        var feature = new GeometryFeature(WebMercator.FromWgs84(line));
        AddStyles(feature, MapStyles.Trail);
        return feature;
    }

    /// <summary>A grid of arrows around the area showing the reported wind (perceived, not true).</summary>
    private List<IFeature> WindFeatures()
    {
        if (_cop.Weather is not { } weather) return [];

        var style = MapStyles.WindArrow(weather.WindFromDegrees, weather.WindSpeedMps);
        var features = new List<IFeature>();
        for (var row = -3; row <= 3; row++)
        {
            for (var col = -3; col <= 3; col++)
            {
                var north = GeoMath.Destination(_home, 0, row * 700);
                var point = GeoMath.Destination(north, 90, col * 700);
                var (x, y) = WebMercator.FromLonLat(point.Longitude, point.Latitude);
                var feature = new PointFeature(x, y);
                feature[InfoTitleField] = "Reported wind";
                feature[InfoLinesField] = new[] { _viewModel.WeatherSummary };
                AddStyles(feature, style);
                features.Add(feature);
            }
        }
        return features;
    }

    // ---- Interaction ----

    private void OnMapTapped(object? sender, MapEventArgs e)
    {
        var drawing = _viewModel.ZoneDrawing;
        if (drawing.IsActive)
        {
            if (e.GestureType == GestureType.DoubleTap)
            {
                _ = drawing.FinishAsync();
            }
            else if (e.GestureType == GestureType.SingleTap)
            {
                drawing.AddPoint(WebMercator.ToGeoPoint(e.WorldPosition.X, e.WorldPosition.Y));
            }
            e.Handled = true; // don't zoom on double-click while drawing
            return;
        }

        if (e.GestureType != GestureType.SingleTap) return;

        var info = e.GetMapInfo(_map.Layers.Where(l => l.Enabled && l is MemoryLayer && l != _drawing).ToList());
        if (info.Feature is { } feature && feature[InfoTitleField] is string title)
        {
            if (feature[IncidentIdField] is Guid incidentId)
                _viewModel.SelectedIncidentId = incidentId;
            if (feature[UnitIdField] is Guid unitId)
                _viewModel.SelectedUnitId = unitId;
            _viewModel.ShowInfo(title, feature[InfoLinesField] as string[] ?? []);
            return;
        }

        _ = ShowLocationInfoAsync(WebMercator.ToGeoPoint(e.WorldPosition.X, e.WorldPosition.Y));
    }

    private void OnMapPointerMoved(object? sender, MapEventArgs e)
    {
        if (_viewModel.ZoneDrawing.IsActive && _viewModel.ZoneDrawing.Points.Count > 0)
            RefreshDrawing(WebMercator.ToGeoPoint(e.WorldPosition.X, e.WorldPosition.Y));
    }

    /// <summary>Clicking empty map: coordinates, ground elevation and the nearest emergency facilities.</summary>
    private async Task ShowLocationInfoAsync(GeoPoint point)
    {
        var header = $"{point.Latitude:F5}, {point.Longitude:F5}";
        if (_gis is null)
        {
            _viewModel.ShowInfo(header, ["No GIS database configured."]);
            return;
        }

        _viewModel.ShowInfo(header, ["Looking up nearby facilities…"]);
        try
        {
            var lines = new List<string>();
            if (await _gis.GetElevationAsync(point) is { } elevation)
                lines.Add($"Ground elevation: {elevation:F0} m");

            string[] keys = [GisLayerKeys.Hospitals, GisLayerKeys.FireStations, GisLayerKeys.PoliceStations, GisLayerKeys.AmbulanceStations];
            foreach (var key in keys)
            {
                var nearest = (await _gis.FindNearestAsync(point, [key], 1)).FirstOrDefault();
                if (nearest is null) continue;
                var bearing = GeoMath.CompassPoint(GeoMath.BearingDegrees(point, GeoPoint.FromPoint(nearest.Feature.Geometry.Centroid)));
                lines.Add($"Nearest {GisLayerKeys.Find(key)!.Name.TrimEnd('s').ToLowerInvariant()}: " +
                          $"{nearest.Feature.Name ?? "(unnamed)"} — {FormatDistance(nearest.DistanceMeters)} {bearing}");
            }

            _viewModel.ShowInfo(header, lines.Count > 0 ? lines : ["No GIS data near this point."]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _viewModel.ShowInfo(header, [$"Lookup failed: {ex.Message}"]);
        }
    }

    private void RefreshDrawing(GeoPoint? cursor)
    {
        var drawing = _viewModel.ZoneDrawing;
        var points = drawing.Points.Concat(cursor is { } c ? [c] : []).ToList();

        var features = new List<IFeature>();
        if (drawing.IsActive && points.Count > 0)
        {
            var coordinates = points.Select(p =>
            {
                var (x, y) = WebMercator.FromLonLat(p.Longitude, p.Latitude);
                return new Coordinate(x, y);
            }).ToList();

            var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
            Geometry shape = coordinates.Count switch
            {
                1 => factory.CreatePoint(coordinates[0]),
                2 => factory.CreateLineString([.. coordinates]),
                _ when drawing.IsLine => factory.CreateLineString([.. coordinates]),
                _ => factory.CreatePolygon([.. coordinates, coordinates[0]]),
            };
            features.Add(new GeometryFeature(shape));
            features.AddRange(drawing.Points.Select(p =>
            {
                var (x, y) = WebMercator.FromLonLat(p.Longitude, p.Latitude);
                return (IFeature)new PointFeature(x, y);
            }));
        }

        _drawing.Features = features;
        _drawing.DataHasChanged();
        _map.RefreshGraphics();
    }

    /// <summary>
    /// Mapsui only expands a <see cref="StyleCollection"/> when it is a layer's style, so per-feature
    /// collections are flattened into the feature's own style list.
    /// </summary>
    private static void AddStyles(IFeature feature, IStyle style)
    {
        if (style is StyleCollection collection)
        {
            foreach (var inner in collection.Styles)
                AddStyles(feature, inner); // collections may nest
        }
        else
        {
            feature.Styles.Add(style);
        }
    }

    private static string FormatDistance(double meters) =>
        meters < 1000 ? $"{meters:F0} m" : $"{meters / 1000:F1} km";
}
