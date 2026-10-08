using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>
/// Drawing a dynamic GIS object on the map (Design Document §7.2): pick a type, click points,
/// then finish to declare it through C2 so it enters the event stream like any other order.
/// </summary>
public partial class ZoneDrawingViewModel(IC2Service c2) : ObservableObject
{
    private readonly List<GeoPoint> _points = [];
    private readonly Dictionary<ZoneType, int> _counters = [];

    /// <summary>Raised whenever the in-progress shape changes, so the map can redraw the preview.</summary>
    public event EventHandler? ShapeChanged;

    public IReadOnlyList<ZoneTypeOption> ZoneTypes => AllZoneTypes;

    private static readonly ZoneTypeOption[] AllZoneTypes =
    [
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
        new(ZoneType.RoadClosure, "Road closure (line)"),
        new(ZoneType.StagingArea, "Staging area"),
        new(ZoneType.CommandPost, "Command post"),
        new(ZoneType.LandingZone, "Landing zone"),
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

    public bool IsLine => SelectedType?.Type == ZoneType.RoadClosure;

    private int MinimumPoints => IsLine ? 2 : 3;

    public string Hint => !IsActive
        ? "Choose a type, then click Draw."
        : $"Click the map to add points ({_points.Count} so far, need {MinimumPoints}). Double-click or Finish to declare; Esc cancels.";

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
        if (!IsActive || SelectedType is null) return;
        if (_points.Count < MinimumPoints)
        {
            Error = $"Add at least {MinimumPoints} points.";
            return;
        }

        var type = SelectedType.Type;
        var number = _counters[type] = _counters.GetValueOrDefault(type) + 1;
        var result = await c2.DeclareZoneAsync(type, $"{SelectedType.Label.Replace(" (line)", "")} {number}", [.. _points]);

        if (!result.Succeeded)
        {
            Error = result.Error;
            return;
        }

        Cancel();
    }

    partial void OnSelectedTypeChanged(ZoneTypeOption? value) => Changed();

    private void Changed()
    {
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(IsLine));
        ShapeChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record ZoneTypeOption(ZoneType Type, string Label);
