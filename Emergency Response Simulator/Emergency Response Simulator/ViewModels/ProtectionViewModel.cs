using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Systems;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>One evacuation zone: the gardaí's figures, the route and the buses.</summary>
public sealed record EvacuationRow(Guid ZoneId, string Name, string Progress, string Route, string Buses, bool Alarm);

/// <param name="Alarm">Something has run out, it is over capacity, or there has been trouble.</param>
public sealed record ShelterRow(Guid ShelterId, string Name, string Status, string Detail, bool CanSecure, bool Alarm);

/// <param name="Alarm">0 normal, 1 degraded (on backup, suspended, isolated), 2 failed or leaking.</param>
public sealed record AssetRow(Guid SiteId, string Name, string Status, string Detail, bool CanGenerate, bool CanIsolate, int Alarm);

public sealed record CyberRow(Guid AttackId, string System, string Status, bool CanFallback, bool Alarm);

public sealed record MonitorRow(string What, string Reading, int Alarm);

/// <summary>
/// The EVAC &amp; INFRA tab (Phase 17): population protection and critical systems. Evacuation zones with their routes,
/// buses, traffic control points and re-entry (Operations, the IC); rest centres (Logistics, Liaison); infrastructure as the
/// operators report it, with generators and gas isolation; GPS interference and ransomware; and environmental monitoring,
/// run-off and impacts (Planning, Safety, Operations). The same COP for everyone; who may act is the permission matrix's to say.
/// </summary>
public sealed partial class ProtectionViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private readonly IC2Service _c2;
    private readonly ICopService _cop;

    public ProtectionViewModel(MainViewModel owner, IC2Service c2, ICopService cop)
    {
        (_owner, _c2, _cop) = (owner, c2, cop);
        _selectedMedium = Media[0];
    }

    [ObservableProperty] private string _tabHeader = "EVAC & INFRA";
    [ObservableProperty] private string _incidentLabel = "";

    private Guid? Incident =>
        _owner.SelectedIncidentId is { } id && _cop.FindIncident(id) is not null ? id
        : _cop.Incidents.Where(i => i.Status != IncidentStatus.Closed).OrderByDescending(i => i.Priority).Select(i => (Guid?)i.Id).FirstOrDefault();

    private GeoPoint? IncidentLocation => Incident is { } id && _cop.FindIncident(id) is { } incident ? GeoPoint.FromPoint(incident.Location) : null;

    /// <summary>Upwind: where the smoke and the cloud are not going.</summary>
    private double Upwind => _cop.Weather?.WindFromDegrees ?? 270;

    // ---- Evacuation ----

    public ObservableCollection<EvacuationRow> Evacuations { get; } = [];
    [ObservableProperty] private string _evacuationSummary = "";
    [ObservableProperty] private string _busCount = "4";

    private bool CanRoute => _owner.Permissions.C2(nameof(IC2Service.SetEvacuationRouteAsync));

    /// <summary>
    /// A route from the middle of the zone out the upwind side: to the newest rest centre if one is open, else to a
    /// point 400 m beyond the zone's edge.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRoute))]
    private async Task SetRouteAsync(Guid zoneId)
    {
        if (_cop.Zones.FirstOrDefault(z => z.Id == zoneId) is not { } zone) return;
        var centre = GeoPoint.FromPoint(zone.Area.Centroid);
        var shelter = _cop.Protection.Shelters.LastOrDefault();
        var radius = zone.Area.Coordinates.Max(c => GeoMath.DistanceMeters(centre, new GeoPoint(c.Y, c.X)));
        var bearing = shelter is null ? Upwind : GeoMath.BearingDegrees(centre, shelter.Location);
        var exit = GeoMath.Destination(centre, bearing, radius + 100);
        IReadOnlyList<GeoPoint> route = shelter is null ? [centre, GeoMath.Destination(centre, bearing, radius + 400)] : [centre, exit];
        var result = await _owner.RunCommandAsync(() => _c2.SetEvacuationRouteAsync(zoneId, route, shelter?.Id), "Evacuation route set");
        if (result is { Succeeded: true, Detail: { } detail }) _owner.ShowCommandResult(result, detail);
    }

    private bool CanBuses => _owner.Permissions.C2(nameof(IC2Service.RequestEvacuationBusesAsync));

    [RelayCommand(CanExecute = nameof(CanBuses))]
    private async Task RequestBusesAsync(Guid zoneId)
    {
        if (!int.TryParse(BusCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var buses))
        {
            _owner.ShowCommandResult(CommandResult.Fail("Buses must be a whole number."));
            return;
        }
        var result = await _owner.RunCommandAsync(() => _c2.RequestEvacuationBusesAsync(zoneId, buses), "Buses requested");
        if (result is { Succeeded: true, Detail: { } detail }) _owner.ShowCommandResult(result, detail);
    }

    private bool CanControlTraffic => _owner.Permissions.C2(nameof(IC2Service.PlaceTrafficControlPointAsync));

    /// <summary>A traffic control point where the zone's route leaves it (or on the upwind edge, with no route).</summary>
    [RelayCommand(CanExecute = nameof(CanControlTraffic))]
    private async Task PlaceControlPointAsync(Guid zoneId)
    {
        if (_cop.Zones.FirstOrDefault(z => z.Id == zoneId) is not { } zone) return;
        var centre = GeoPoint.FromPoint(zone.Area.Centroid);
        var radius = zone.Area.Coordinates.Max(c => GeoMath.DistanceMeters(centre, new GeoPoint(c.Y, c.X)));
        var route = _cop.Protection.Evacuation(zoneId)?.Route;
        var bearing = route is { Count: >= 2 } ? GeoMath.BearingDegrees(centre, route[^1]) : Upwind;
        var at = GeoMath.Destination(centre, bearing, radius);
        var result = await _owner.RunCommandAsync(() => _c2.PlaceTrafficControlPointAsync($"{zone.Name} exit", at), "Traffic control point requested");
        if (result is { Succeeded: true, Detail: { } detail }) _owner.ShowCommandResult(result, detail);
    }

    private bool CanReEnter => _owner.Permissions.C2(nameof(IC2Service.AuthoriseReEntryAsync));

    [RelayCommand(CanExecute = nameof(CanReEnter))]
    private async Task ReEnterAsync(Guid zoneId)
    {
        var name = _cop.Zones.FirstOrDefault(z => z.Id == zoneId)?.Name ?? "the zone";
        if (MessageBox.Show($"Let the residents of {name} go home? The zone is lifted.", "Re-entry", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            != MessageBoxResult.OK) return;
        var result = await _owner.RunCommandAsync(() => _c2.AuthoriseReEntryAsync(zoneId), "Re-entry authorised");
        if (result is { Succeeded: true, Detail: { } detail }) _owner.ShowCommandResult(result, detail);
    }

    // ---- Rest centres ----

    public ObservableCollection<ShelterRow> Shelters { get; } = [];
    [ObservableProperty] private string _shelterSummary = "";
    [ObservableProperty] private string _shelterName = "Ringsend Community Centre";
    [ObservableProperty] private string _shelterCapacity = "200";

    private bool CanShelter => _owner.Permissions.C2(nameof(IC2Service.OpenShelterAsync));

    /// <summary>A rest centre a kilometre and a half upwind of the selected incident, each further one round to the side.</summary>
    [RelayCommand(CanExecute = nameof(CanShelter))]
    private async Task OpenShelterAsync()
    {
        if (IncidentLocation is not { } at)
        {
            _owner.ShowCommandResult(CommandResult.Fail("Select the incident the rest centre is for."));
            return;
        }
        if (!int.TryParse(ShelterCapacity, NumberStyles.Integer, CultureInfo.InvariantCulture, out var capacity))
        {
            _owner.ShowCommandResult(CommandResult.Fail("Capacity must be a whole number."));
            return;
        }
        var site = GeoMath.Destination(at, Upwind + _cop.Protection.Shelters.Count * 35, 1500);
        var result = await _owner.RunCommandAsync(() => _c2.OpenShelterAsync(ShelterName, site, capacity, Incident), "Rest centre opening");
        if (result is { Succeeded: true, Detail: { } detail }) _owner.ShowCommandResult(result, detail);
    }

    private bool CanSecure => _owner.Permissions.C2(nameof(IC2Service.ArrangeShelterSecurityAsync));

    [RelayCommand(CanExecute = nameof(CanSecure))]
    private Task SecureAsync(Guid shelterId) => _owner.RunCommandAsync(() => _c2.ArrangeShelterSecurityAsync(shelterId), "Security arranged");

    // ---- Infrastructure ----

    public ObservableCollection<AssetRow> Assets { get; } = [];

    private bool CanGenerate => _owner.Permissions.C2(nameof(IC2Service.DeployGeneratorAsync));

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GeneratorAsync(Guid siteId)
    {
        var result = await _owner.RunCommandAsync(() => _c2.DeployGeneratorAsync(siteId), "Generator on its way");
        if (result is { Succeeded: true, Detail: { } detail }) _owner.ShowCommandResult(result, detail);
    }

    private bool CanIsolate => _owner.Permissions.C2(nameof(IC2Service.IsolateGasAsync));

    [RelayCommand(CanExecute = nameof(CanIsolate))]
    private async Task IsolateAsync(Guid siteId)
    {
        var result = await _owner.RunCommandAsync(() => _c2.IsolateGasAsync(siteId), "Gas isolation requested");
        if (result is { Succeeded: true, Detail: { } detail }) _owner.ShowCommandResult(result, detail);
    }

    // ---- Cyber and GPS ----

    public ObservableCollection<CyberRow> Cyber { get; } = [];
    [ObservableProperty] private string _gnssSummary = "";
    [ObservableProperty] private string _voicePositionsLabel = "Voice positions";

    private bool CanVoicePositions => _owner.Permissions.C2(nameof(IC2Service.SetManualPositionReportingAsync));

    [RelayCommand(CanExecute = nameof(CanVoicePositions))]
    private Task ToggleVoicePositionsAsync()
    {
        var on = !_cop.Protection.ManualPositionReporting;
        return _owner.RunCommandAsync(() => _c2.SetManualPositionReportingAsync(on), on ? "Crews to give positions by voice" : "Voice positions stood down");
    }

    private bool CanFallback => _owner.Permissions.C2(nameof(IC2Service.ActivateCyberFallbackAsync));

    [RelayCommand(CanExecute = nameof(CanFallback))]
    private async Task FallbackAsync(Guid attackId)
    {
        var result = await _owner.RunCommandAsync(() => _c2.ActivateCyberFallbackAsync(attackId), "Manual working ordered");
        if (result is { Succeeded: true, Detail: { } detail }) _owner.ShowCommandResult(result, detail);
    }

    // ---- Environment ----

    public ObservableCollection<MonitorRow> Monitors { get; } = [];
    [ObservableProperty] private string _environmentSummary = "";

    public IReadOnlyList<Choice<EnvironmentMedium>> Media { get; } =
        [.. Enum.GetValues<EnvironmentMedium>().Select(m => new Choice<EnvironmentMedium>(m, Protection.Name(m)))];

    [ObservableProperty] private Choice<EnvironmentMedium> _selectedMedium;

    private bool CanMonitor => _owner.Permissions.C2(nameof(IC2Service.DeployEnvironmentalMonitoringAsync));

    /// <summary>Air: 400 m downwind of the incident. Water: where its drains reach the river or canal. Soil: at the incident.</summary>
    [RelayCommand(CanExecute = nameof(CanMonitor))]
    private async Task MonitorAsync()
    {
        if (IncidentLocation is not { } at)
        {
            _owner.ShowCommandResult(CommandResult.Fail("Select the incident to sample around."));
            return;
        }
        var medium = SelectedMedium.Value;
        var site = medium switch
        {
            EnvironmentMedium.Air => GeoMath.Destination(at, Upwind + 180, 400),
            EnvironmentMedium.Water => EnvironmentSystem.NearestWatercourse(at).At,
            _ => GeoMath.Destination(at, 90, 30),
        };
        var result = await _owner.RunCommandAsync(() => _c2.DeployEnvironmentalMonitoringAsync(medium, site), "Monitoring team on the way");
        if (result is { Succeeded: true, Detail: { } detail }) _owner.ShowCommandResult(result, detail);
    }

    private bool CanContain => _owner.Permissions.C2(nameof(IC2Service.ContainRunoffAsync));

    [RelayCommand(CanExecute = nameof(CanContain))]
    private async Task ContainAsync()
    {
        if (Incident is not { } incident)
        {
            _owner.ShowCommandResult(CommandResult.Fail("Select the incident whose run-off to contain."));
            return;
        }
        var result = await _owner.RunCommandAsync(() => _c2.ContainRunoffAsync(incident), "Run-off containment ordered");
        if (result is { Succeeded: true, Detail: { } detail }) _owner.ShowCommandResult(result, detail);
    }

    // ---- Refresh ----

    public void Refresh()
    {
        var protection = _cop.Protection;
        var incidentId = Incident;
        var incident = incidentId is { } id ? _cop.FindIncident(id) : null;
        IncidentLabel = incident is null ? "No incident" : $"{incident.Number} · {incident.Address ?? EventDescriber.Humanize(incident.Type)}";
        string ShelterName(Guid? s) => s is { } sid ? protection.Shelters.FirstOrDefault(x => x.Id == sid)?.Name ?? "a rest centre" : "";

        // Evacuation.
        var zones = protection.Evacuations.Where(z => z.ReEntryAt is null && _cop.Zones.Any(c => c.Id == z.ZoneId)).ToList();
        EvacuationSummary = zones.Count == 0 ? "No evacuation zone in force. Declare one on the map (or from the PLUME tab)."
            : $"{zones.Count} zone(s) · {protection.ControlPoints.Count(p => p.StaffedAt is not null)} of {protection.ControlPoints.Count} traffic control point(s) staffed";
        Sync(Evacuations, zones.Select(z => new EvacuationRow(z.ZoneId, z.Name,
            z.ReportedAt is null ? $"ordered {MainViewModel.Time(z.OrderedAt)}, no report yet"
                : $"~{z.Left} out · ~{z.Remaining} in" + (z.ClearanceMinutes is { } c ? c == 0 ? " · clear" : $" · clear in {Protection.Clearance(c)}" : " · not moving"),
            z.Route.Count == 0 ? "NO ROUTE: straight out" : z.ShelterId is { } s ? $"route to {ShelterName(s)}" : "route set",
            z.BusesRequested == 0 ? "no buses" : $"{z.BusesWorking}/{z.BusesRequested} buses working",
            z.Route.Count == 0 || z.ClearanceMinutes is null or > 60)).ToList());

        // Rest centres.
        var unsheltered = protection.Unsheltered.Values.Sum();
        ShelterSummary = (protection.Shelters.Count == 0 ? "No rest centre open." : $"{protection.Shelters.Sum(s => s.Occupants)} evacuees in {protection.Shelters.Count} rest centre(s).")
                         + (unsheltered > 0 ? $" About {unsheltered} with NOWHERE TO GO." : "");
        Sync(Shelters, protection.Shelters.Select(s => new ShelterRow(s.Id, s.Name,
            s.Ready ? $"{s.Occupants}/{s.Capacity} · {s.Registered} registered" : $"setting up since {MainViewModel.Time(s.OpenedAt)}",
            string.Join(" · ", new[]
            {
                s.Short.Count > 0 ? "OUT OF " + string.Join(", ", s.Short.Select(k => Supplies.Name(k).ToUpperInvariant())) : null,
                s.Secured ? "security on site" : s.SecurityArrangedAt is not null ? "security on the way" : "no security",
                s.Incidents > 0 ? $"{s.Incidents} incident(s)" : null,
            }.OfType<string>()),
            s.SecurityArrangedAt is null, s.Short.Count > 0 || s.Occupants > s.Capacity || s.Incidents > 0)).ToList());

        // Infrastructure.
        Sync(Assets, protection.Infrastructure.OrderByDescending(a => Alarm(a.Status)).ThenBy(a => a.Name).Select(a => new AssetRow(a.Id,
            $"{Protection.Name(a.Kind)}: {a.Name}", Protection.Name(a.Status) + (a.Customers > 0 ? $" · ~{a.Customers:N0} customers" : ""),
            a.Detail + (a.GeneratorRequestedAt is { } g ? $" Generator asked for {MainViewModel.Time(g)}." : "")
                     + (a.IsolationRequestedAt is { } i && a.Status != InfrastructureStatus.Isolated ? $" Isolation asked for {MainViewModel.Time(i)}." : ""),
            a is { Kind: InfrastructureKind.Water or InfrastructureKind.Telecom, GeneratorRequestedAt: null } && a.Status != InfrastructureStatus.Normal,
            a is { Kind: InfrastructureKind.Gas, IsolationRequestedAt: null } && a.Status == InfrastructureStatus.Leaking,
            Alarm(a.Status))).ToList());

        // Cyber and GPS.
        var gnss = protection.Gnss.Where(g => g.EndedAt is null).ToList();
        GnssSummary = gnss.Count == 0 ? "No GPS interference reported." : string.Join(" ", gnss.Select(g =>
            $"GPS {(g.Kind == GnssInterferenceKind.Jamming ? "JAMMED" : "SPOOFED")} within ~{g.RadiusMeters:F0} m (reported {MainViewModel.Time(g.ReportedAt)}): " +
            (g.Kind == GnssInterferenceKind.Jamming ? "AVL there has stopped." : "AVL there is wrong.")));
        VoicePositionsLabel = protection.ManualPositionReporting ? "Stand down voice positions" : "Voice positions";
        Sync(Cyber, protection.Cyber.Select(c => new CyberRow(c.Id, c.System,
            c.RestoredAt is { } r ? $"restored {MainViewModel.Time(r)}"
                : c.Impact + (c.FallbackAt is { } f ? $" Manual working since {MainViewModel.Time(f)}." : ""),
            c is { RestoredAt: null, FallbackAt: null }, c.RestoredAt is null)).ToList());

        // Environment.
        var contained = incidentId is { } forIncident && protection.RunoffContained.TryGetValue(forIncident, out var since) ? since : (DateTimeOffset?)null;
        EnvironmentSummary = (contained is { } c2 ? $"Run-off containment ordered {MainViewModel.Time(c2)}." : "Run-off not contained: fire-water goes down the drains.")
                             + (protection.Impacts.Count > 0 ? " " + string.Join(" ", protection.Impacts.Select(i => i.What + ".")) : "");
        Sync(Monitors, protection.Monitors.Select(m => new MonitorRow(Protection.Name(m.Medium),
            m.ReadingAt is { } at ? $"{Protection.Name(m.Level!.Value)} ({MainViewModel.Time(at)}): {m.Reading}" : $"team on the way since {MainViewModel.Time(m.DeployedAt)}",
            m.Level switch { PollutionLevel.High => 2, PollutionLevel.Moderate => 1, _ => 0 })).ToList());

        var pressing = zones.Count(z => z.Route.Count == 0) + (unsheltered > 0 ? 1 : 0) + protection.Shelters.Count(s => s.Short.Count > 0)
                       + protection.Infrastructure.Count(a => a.Status is InfrastructureStatus.Failed or InfrastructureStatus.Leaking)
                       + protection.Cyber.Count(c => c is { RestoredAt: null, FallbackAt: null }) + (gnss.Count > 0 && !protection.ManualPositionReporting ? 1 : 0);
        TabHeader = pressing > 0 ? $"EVAC & INFRA ({pressing})" : "EVAC & INFRA";
    }

    private static int Alarm(InfrastructureStatus status) => status switch
    {
        InfrastructureStatus.Failed or InfrastructureStatus.Leaking => 2,
        InfrastructureStatus.Normal => 0,
        _ => 1,
    };

    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> rows)
    {
        if (target.SequenceEqual(rows)) return;
        target.Clear();
        foreach (var row in rows) target.Add(row);
    }
}
