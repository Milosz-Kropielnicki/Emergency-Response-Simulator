using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>One zone of a projection, as the panel lists it.</summary>
public sealed record PlumeZoneRow(string Level, string Name, string Reach, string Threshold, string Inside);

/// <summary>
/// The selected incident's plume projection (Phase 13), in the INCIDENT tab: run the dispersion model on what command
/// knows, see how far each zone reaches and what is inside it, and turn the zones into declared protective actions —
/// evacuate the moderate and high area, shelter in place across the low one. The map draws the zones; this says what
/// they mean. A projection is a prediction from reported information, and the panel says so.
/// </summary>
public sealed partial class PlumeViewModel(MainViewModel owner, IC2Service c2, ICopService cop) : ObservableObject
{
    /// <summary>What can be projected: one of each material class the model knows, and the common gases. Free text is fine too.</summary>
    public IReadOnlyList<string> Substances { get; } =
        ["chlorine", "ammonia", "sulphur dioxide", "hydrogen sulphide", "smoke", "toxic gas", "biological aerosol", "radioactive material"];

    [ObservableProperty] private string _substance = "chlorine";
    [ObservableProperty] private string _rate = "0.5";
    [ObservableProperty] private string _duration = "30";

    [ObservableProperty] private bool _hasProjection;
    [ObservableProperty] private string _heading = "";
    [ObservableProperty] private string _inputs = "";
    [ObservableProperty] private string _advisory = "";
    [ObservableProperty] private string _impactSummary = "";
    [ObservableProperty] private bool _hasPressing;

    public ObservableCollection<PlumeZoneRow> Zones { get; } = [];

    private Guid? _incidentId;
    private PlumeProjection? _current;

    public void Load(Incident? incident)
    {
        _incidentId = incident?.Id;
        _current = incident is null ? null : cop.PlumeProjections.FirstOrDefault(p => p.IsCurrent && p.IncidentId == incident.Id);
        HasProjection = _current is not null;
        if (_current is not { } projection)
        {
            Zones.Clear();
            return;
        }

        Heading = $"{(projection.Automatic ? "Updated" : "Run")} {MainViewModel.Time(projection.RunAt)}" +
                  (projection.Automatic ? " after the reported wind or release changed" : "");
        Inputs = $"{projection.Inputs} · {projection.MaterialClass} · {projection.Model}";
        Advisory = projection.Advisory;

        var rows = projection.Zones.OrderByDescending(z => PlumeLevels.Severity(z.Level)).Select(z => new PlumeZoneRow(
            z.Level,
            PlumeLevels.Name(z.Level),
            $"{z.MaxDistanceMeters:N0} m",
            z.DoseRateMsvPerHour is { } dose ? $"{dose:0.##} mSv/h" : $"{z.ThresholdMgPerM3:0.#####} mg/m³",
            string.Join(", ", projection.Impacts.Where(i => i.Level == z.Level)
                .Select(i => i.Names.Count > 0 ? $"{i.Kind}: {string.Join(", ", i.Names)}" : $"{i.Count} {i.Kind.ToLowerInvariant()}(s)"))))
            .ToList();
        if (!Zones.SequenceEqual(rows))
        {
            Zones.Clear();
            foreach (var row in rows) Zones.Add(row);
        }

        var pressing = projection.Pressing.ToList();
        HasPressing = pressing.Count > 0;
        ImpactSummary = projection.Zones.Count == 0
            ? "Nothing above the lowest threshold at that release rate."
            : pressing.Count > 0
                ? "Inside the moderate or high zone: " + string.Join("; ", pressing.Select(i => $"{i.Count} {i.Kind.ToLowerInvariant()}(s)"))
                : projection.Impacts.Count > 0 ? "Only the low zone reaches anything sensitive." : "No schools, hospitals or homes found inside (or no GIS data loaded).";
    }

    private bool CanProject => owner.Permissions.C2(nameof(IC2Service.PredictPlumeAsync));

    [RelayCommand(CanExecute = nameof(CanProject))]
    private async Task ProjectAsync()
    {
        if (_incidentId is not { } id) return;
        if (!double.TryParse(Rate, NumberStyles.Float, CultureInfo.CurrentCulture, out var rate)
            || !double.TryParse(Duration, NumberStyles.Float, CultureInfo.CurrentCulture, out var duration))
        {
            owner.ShowCommandResult(CommandResult.Fail("Release rate (kg/s) and duration (minutes) must be numbers."));
            return;
        }
        await owner.RunCommandAsync(() => c2.PredictPlumeAsync(Substance, id, null, rate, duration), "Plume projected");
    }

    private bool CanDeclare => owner.Permissions.C2(nameof(IC2Service.DeclareZoneAsync));

    /// <summary>Evacuate everything inside the moderate zone (which contains the high one).</summary>
    [RelayCommand(CanExecute = nameof(CanDeclare))]
    private Task EvacuateAsync() => DeclareAsync(PlumeLevels.Moderate, ZoneType.EvacuationZone, "Evacuation");

    /// <summary>Shelter in place across the low zone.</summary>
    [RelayCommand(CanExecute = nameof(CanDeclare))]
    private Task ShelterAsync() => DeclareAsync(PlumeLevels.Low, ZoneType.ShelterInPlace, "Shelter in place");

    private async Task DeclareAsync(string level, ZoneType type, string what)
    {
        if (_current is not { } projection) return;
        if (projection.Zone(level) is not { Boundary.Count: >= 4 } zone)
        {
            owner.ShowCommandResult(CommandResult.Fail($"The projection has no {level} zone to declare."));
            return;
        }
        await owner.RunCommandAsync(() => c2.DeclareZoneAsync(type, $"{what}: projected {projection.Substance} plume", zone.Boundary, projection.IncidentId),
            $"{what} declared over the projected {level} zone");
    }
}
