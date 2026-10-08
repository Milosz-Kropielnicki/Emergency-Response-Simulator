using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>
/// Drawing operational boundaries on the map (Design Document §6.2, §7.2): pick a type, click points,
/// then finish to declare it through C2 so it enters the event stream like any other order.
/// Point-like places (command post, staging, landing zone) and hot/warm/cold rings need a single click.
/// Zones are attached to the selected incident.
/// </summary>
public partial class ZoneDrawingViewModel(IC2Service c2, MainViewModel owner) : ObservableObject
{
    private readonly List<GeoPoint> _points = [];
    private readonly Dictionary<string, int> _counters = [];

    /// <summary>Raised whenever the in-progress shape changes, so the map can redraw the preview.</summary>
    public event EventHandler? ShapeChanged;

    public IReadOnlyList<ZoneTypeOption> ZoneTypes => AllZoneTypes;

    private static readonly ZoneTypeOption[] AllZoneTypes =
    [
        new(ZoneType.HotZone, "Hot / warm / cold (click centre)", ZoneDrawMode.HazardRings),
        new(ZoneType.IncidentPerimeter, "Incident perimeter"),
        new(ZoneType.HotZone, "Hot zone"),
        new(ZoneType.WarmZone, "Warm zone"),
        new(ZoneType.ColdZone, "Cold zone"),
        new(ZoneType.EvacuationZone, "Evacuation zone"),
        new(ZoneType.ShelterInPlace, "Shelter in place"),
        new(ZoneType.SearchArea, "Search area"),
        new(ZoneType.PoliceCordon, "Police cordon"),
        new(ZoneType.FireExclusion, "Fire exclusion zone"),
        new(ZoneType.TrafficControl, "Traffic control area"),
        new(ZoneType.RoadClosure, "Road closure (line)", ZoneDrawMode.Line),
        new(ZoneType.CommandPost, "Command post (click)", ZoneDrawMode.Point, 25),
        new(ZoneType.StagingArea, "Staging area (click)", ZoneDrawMode.Point, 40),
        new(ZoneType.LandingZone, "Landing zone (click)", ZoneDrawMode.Point, 30),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint))]
    private ZoneTypeOption? _selectedType = AllZoneTypes[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint))]
    private bool _isActive;

    [ObservableProperty]
    private string? _error;

    public IReadOnlyList<GeoPoint> Points => _points;

    public bool IsLine => SelectedType?.Mode == ZoneDrawMode.Line;

    private int MinimumPoints => SelectedType?.Mode switch
    {
        ZoneDrawMode.Line => 2,
        ZoneDrawMode.Point or ZoneDrawMode.HazardRings => 1,
        _ => 3,
    };

    public string Hint
    {
        get
        {
            var incident = owner.SelectedIncidentId is { } id ? owner.FindIncident(id)?.Number : null;
            var target = incident is null ? "No incident selected; the zone will stand alone." : $"Attached to {incident}.";
            if (!IsActive) return $"Choose a type, then click Draw. {target}";
            return MinimumPoints == 1
                ? $"Click the map to place it. Esc cancels. {target}"
                : $"Click the map to add points ({_points.Count} so far, need {MinimumPoints}). " +
                  $"Double-click or Finish to declare; Esc cancels. {target}";
        }
    }

    [RelayCommand]
    private void Start()
    {
        SelectedType ??= ZoneTypes[0];
        _points.Clear();
        Error = null;
        IsActive = true;
        Changed();
    }

    public void AddPoint(GeoPoint point)
    {
        if (!IsActive) return;
        _points.Add(point);
        Changed();

        if (MinimumPoints == 1)
            _ = FinishAsync();
    }

    [RelayCommand]
    private void Cancel()
    {
        _points.Clear();
        IsActive = false;
        Changed();
    }

    [RelayCommand]
    public async Task FinishAsync()
    {
        if (!IsActive || SelectedType is not { } option) return;
        if (_points.Count < MinimumPoints)
        {
            Error = $"Add at least {MinimumPoints} points.";
            return;
        }

        var incidentId = owner.SelectedIncidentId;
        var name = $"{option.Label.Split(" (")[0]} {_counters[option.Label] = _counters.GetValueOrDefault(option.Label) + 1}";

        var result = option.Mode switch
        {
            ZoneDrawMode.HazardRings => await owner.RunCommandAsync(
                () => c2.EstablishHazardZonesAsync(_points[0], 50, 150, 400, incidentId), "Hot / warm / cold zones established"),
            ZoneDrawMode.Point => await owner.RunCommandAsync(
                () => c2.DeclareZoneAsync(option.Type, name, Wgs84.Circle(_points[0], option.RadiusMeters, 24), incidentId),
                $"{name} declared"),
            _ => await owner.RunCommandAsync(
                () => c2.DeclareZoneAsync(option.Type, name, [.. _points], incidentId), $"{name} declared"),
        };

        if (result is { Succeeded: false })
        {
            Error = result.Error;
            return;
        }

        Cancel();
    }

    partial void OnSelectedTypeChanged(ZoneTypeOption? value) => Changed();

    /// <summary>The selected incident changed, which changes what new zones attach to.</summary>
    internal void TargetChanged() => OnPropertyChanged(nameof(Hint));

    private void Changed()
    {
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(IsLine));
        ShapeChanged?.Invoke(this, EventArgs.Empty);
    }
}

public enum ZoneDrawMode
{
    Polygon,
    Line,

    /// <summary>One click places a small circle (command post, staging area, landing zone).</summary>
    Point,

    /// <summary>One click places concentric hot, warm and cold zones.</summary>
    HazardRings,
}

public sealed record ZoneTypeOption(ZoneType Type, string Label, ZoneDrawMode Mode = ZoneDrawMode.Polygon, double RadiusMeters = 0);
