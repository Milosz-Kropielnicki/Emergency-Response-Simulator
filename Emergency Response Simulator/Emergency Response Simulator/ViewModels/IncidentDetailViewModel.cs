using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>
/// The selected incident as a parent object (Design Document §6.5): its facts, the resources, reports
/// and zones tied to it, and the edits command can make. Edits are sent through C2 as one update.
/// </summary>
public sealed partial class IncidentDetailViewModel(MainViewModel owner, IC2Service c2) : ObservableObject
{
    public IReadOnlyList<IncidentPriority> Priorities { get; } = Enum.GetValues<IncidentPriority>();
    public IReadOnlyList<IncidentStatus> Statuses { get; } = Enum.GetValues<IncidentStatus>();

    [ObservableProperty] private bool _hasIncident;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _summary = "";

    // Editable fields, loaded from the COP when the selection changes (not on every refresh,
    // so the operator's unsaved edits are not overwritten).
    [ObservableProperty] private IncidentPriority _priority;
    [ObservableProperty] private IncidentStatus _status;
    [ObservableProperty] private string _casualtiesReported = "0";
    [ObservableProperty] private string _casualtiesConfirmed = "0";
    [ObservableProperty] private bool _evacuationRequired;
    [ObservableProperty] private string _threats = "";
    [ObservableProperty] private string _commander = "";

    [ObservableProperty] private string _hotRadius = "50";
    [ObservableProperty] private string _warmRadius = "150";
    [ObservableProperty] private string _coldRadius = "400";

    public ObservableCollection<string> Units { get; } = [];
    public ObservableCollection<string> Reports { get; } = [];
    public ObservableCollection<string> Zones { get; } = [];

    private Guid? _incidentId;
    private GeoPoint _location;

    public void Load(Incident? incident, bool selectionChanged)
    {
        HasIncident = incident is not null;
        if (incident is null)
        {
            _incidentId = null;
            return;
        }

        _incidentId = incident.Id;
        _location = GeoPoint.FromPoint(incident.Location);
        Title = $"{incident.Number}  {incident.Name ?? EventDescriber.Humanize(incident.Type)}";
        Summary = $"{EventDescriber.Humanize(incident.Type)} · {incident.Address ?? $"{_location.Latitude:F5}, {_location.Longitude:F5}"}\n" +
                  $"Reported {MainViewModel.Time(incident.ReportedAt)} · last updated {MainViewModel.Time(incident.LastUpdatedAt)}";

        if (selectionChanged)
        {
            Priority = incident.Priority;
            Status = incident.Status;
            CasualtiesReported = incident.CasualtiesReported.ToString();
            CasualtiesConfirmed = incident.CasualtiesConfirmed.ToString();
            EvacuationRequired = incident.EvacuationRequired;
            Threats = string.Join(", ", incident.Threats);
            Commander = incident.IncidentCommanderName ?? "";
        }

        Replace(Units, incident.AssignedUnits.Select(u => $"{u.Callsign} — {EventDescriber.Humanize(u.Status)}"));
        Replace(Reports, incident.Reports.OrderByDescending(r => r.ReceivedAt)
            .Select(r => $"{MainViewModel.Time(r.ReceivedAt)} {r.SourceName}: \"{r.Claim}\" [{r.Verification}]"));
        Replace(Zones, incident.Zones.Select(z => $"{z.Name}"));
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (_incidentId is not { } id) return;
        if (!int.TryParse(CasualtiesReported, out var reported) || !int.TryParse(CasualtiesConfirmed, out var confirmed))
        {
            owner.ShowCommandResult(CommandResult.Fail("Casualty counts must be whole numbers."));
            return;
        }

        var threats = Threats.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var current = owner.FindIncident(id);
        if (!string.IsNullOrWhiteSpace(Commander) && Commander.Trim() != current?.IncidentCommanderName)
            await owner.RunCommandAsync(() => c2.AssignIncidentCommanderAsync(id, Commander), $"{Commander.Trim()} has command");

        await owner.RunCommandAsync(() => c2.UpdateIncidentAsync(id, Priority, Status, reported, confirmed,
            EvacuationRequired, threats), "Incident updated", quietIfUnchanged: true);
    }

    [RelayCommand]
    private async Task EstablishHazardZonesAsync()
    {
        if (_incidentId is not { } id) return;
        if (!double.TryParse(HotRadius, out var hot) || !double.TryParse(WarmRadius, out var warm) || !double.TryParse(ColdRadius, out var cold))
        {
            owner.ShowCommandResult(CommandResult.Fail("Zone radii must be numbers (metres)."));
            return;
        }

        await owner.RunCommandAsync(() => c2.EstablishHazardZonesAsync(_location, hot, warm, cold, id), "Hot / warm / cold zones established");
    }

    private static void Replace(ObservableCollection<string> target, IEnumerable<string> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }
}
