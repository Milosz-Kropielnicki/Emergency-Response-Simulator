using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Model;
using Microsoft.Win32;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>An incident to export, or every incident.</summary>
public sealed record ExportScope(Guid? Id, string Label);

/// <summary>A row of the NIMS check.</summary>
public sealed record NimsRow(string Area, string Severity, string Message, string Reference);

/// <summary>A unit type and its NIMS resource type.</summary>
public sealed record TypingRow(string Unit, string Discipline, string Nims);

/// <summary>
/// Data standards and NIMS (Phase 20): export the picture as CAP, EDXL, NIEM or HL7, bring an outside system's document
/// in, check the response against NIMS/ICS, and look up the NIMS terms. Over the network the work runs on the server,
/// which lets only the instructor bring data in.
/// </summary>
public sealed partial class DataExchangeViewModel(IIntegrationService integration, ICopService cop) : ObservableObject
{
    public ObservableCollection<DataStandard> Standards { get; } = [];
    public ObservableCollection<ExportScope> Incidents { get; } = [];
    public ObservableCollection<NimsRow> NimsFindings { get; } = [];
    public ObservableCollection<string> ImportNotes { get; } = [];

    public IReadOnlyList<TermCrosswalk> Crosswalk => NimsTerminology.Crosswalk;

    public IReadOnlyList<TypingRow> ResourceTypes { get; } = Enum.GetValues<UnitType>()
        .Select(t => (Type: t, Nims: NimsResourceTypes.Of(t)))
        .Select(x => new TypingRow(x.Type.ToString(), x.Nims.Discipline, x.Nims.Label)).ToList();

    public IReadOnlyList<string> Positions { get; } = Enum.GetValues<IcsRole>()
        .Select(r => $"{NimsTerminology.Title(r)}{(NimsTerminology.IsCommandStaff(r) ? " (Command Staff)" : r == IcsRole.IncidentCommander ? "" : " (General Staff)")}").ToList();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StandardDescription))] private DataStandard? _selectedStandard;
    [ObservableProperty] private ExportScope? _selectedIncident;
    [ObservableProperty] private string _document = "";
    [ObservableProperty] private string _status = "Choose a standard, then export the picture or import a document.";
    [ObservableProperty] private string _nimsSummary = "Run the check to see how the response measures up to NIMS/ICS.";

    private StandardDocument? _exported;

    public string StandardDescription => SelectedStandard is { } s
        ? $"{s.Description} ({(s.Exports && s.Imports ? "export and import" : s.Exports ? "export only" : "import only")})"
        : "";

    public async Task OpenAsync()
    {
        try
        {
            Replace(Standards, await integration.GetStandardsAsync());
            SelectedStandard ??= Standards.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Status = $"The standards aren't available: {ex.Message}";
        }
        RefreshIncidents();
    }

    private void RefreshIncidents()
    {
        var selected = SelectedIncident?.Id;
        Replace(Incidents, [new ExportScope(null, "All incidents"),
            .. cop.Incidents.OrderBy(i => i.Number).Select(i => new ExportScope(i.Id, $"{i.Number} {i.Name ?? i.Type.ToString()}"))]);
        SelectedIncident = Incidents.FirstOrDefault(i => i.Id == selected) ?? Incidents[0];
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (SelectedStandard is not { Exports: true } standard)
        {
            Status = "That standard can't be exported.";
            return;
        }
        RefreshIncidents();
        try
        {
            _exported = await integration.ExportAsync(standard.Key, SelectedIncident?.Id);
            Document = _exported.Content.Replace("\r\n", "\n").Replace('\r', '\n'); // HL7's segment breaks, shown as lines
            Status = $"{standard.Label}: {_exported.Items} item(s). Save it, or send it to the receiving system.";
        }
        catch (Exception ex)
        {
            Status = $"Export failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (_exported is null || Document.Length == 0)
        {
            Status = "Export something first.";
            return;
        }
        var extension = Path.GetExtension(_exported.FileName);
        var dialog = new SaveFileDialog { FileName = _exported.FileName, Filter = $"{SelectedStandard?.Name} (*{extension})|*{extension}|All files (*.*)|*.*", AddExtension = true };
        if (dialog.ShowDialog() != true) return;
        // HL7 v2 separates segments with a carriage return; everything else is saved as shown.
        File.WriteAllText(dialog.FileName, extension == ".hl7" ? _exported.Content : Document);
        Status = $"Saved to {dialog.FileName}";
    }

    [RelayCommand]
    private async Task ImportFileAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Standards documents (*.xml;*.cap;*.hl7;*.txt)|*.xml;*.cap;*.hl7;*.txt|All files (*.*)|*.*" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            Document = File.ReadAllText(dialog.FileName);
        }
        catch (IOException ex)
        {
            Status = $"Couldn't read the file: {ex.Message}";
            return;
        }
        await ImportTextAsync();
    }

    /// <summary>Takes in whatever is in the document box (pasted, or opened), as the selected standard.</summary>
    [RelayCommand]
    private async Task ImportTextAsync()
    {
        if (SelectedStandard is not { Imports: true } standard)
        {
            Status = $"{SelectedStandard?.Label ?? "That standard"} is export only.";
            return;
        }
        try
        {
            var result = await integration.ImportAsync(standard.Key, Document);
            Replace(ImportNotes, result.Notes);
            if (result.Reply is { Length: > 0 } reply) ImportNotes.Add("Reply: " + reply.Replace('\r', ' ').Trim());
            Status = result.Message;
        }
        catch (Exception ex)
        {
            Status = $"Import refused: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CheckNimsAsync()
    {
        RefreshIncidents();
        try
        {
            var report = await integration.CheckNimsAsync(SelectedIncident?.Id);
            Replace(NimsFindings, report.Findings.OrderBy(f => f.Severity).ThenBy(f => f.Area)
                .Select(f => new NimsRow(f.Area.ToString(), f.Severity.ToString(), f.Message, f.Reference)));
            NimsSummary = report.Findings.Count == 0
                ? "Nothing to check yet: no incident has resources committed."
                : $"Score {report.Score}/100 · {report.Count(NimsSeverity.Gap)} gap(s) · {report.Count(NimsSeverity.Advisory)} advisory · {report.Count(NimsSeverity.Met)} met";
        }
        catch (Exception ex)
        {
            NimsSummary = $"The check failed: {ex.Message}";
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}
