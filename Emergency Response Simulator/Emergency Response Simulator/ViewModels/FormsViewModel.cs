using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Documentation;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.State;
using Microsoft.Win32;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>
/// The documentation unit's window (Phase 18): any ICS form, the IAP package, situation reports and the decision log,
/// generated from the COP and the event stream as this client sees them, previewed and exported. Read-only, so open to
/// every role: the forms are the record, and anyone may read the record.
/// </summary>
public sealed partial class FormsViewModel(IEventStore store, SimulationSession session, CopView cop) : ObservableObject
{
    public IReadOnlyList<Choice<IcsFormKind>> Forms { get; } =
        [.. IcsForms.All.Select(k => new Choice<IcsFormKind>(k, $"{IcsForms.Number(k)}  {IcsForms.Title(k)}"))];

    public ObservableCollection<Choice<Guid?>> Incidents { get; } = [];
    public ObservableCollection<Choice<Guid?>> Plans { get; } = [];
    public ObservableCollection<Choice<Guid?>> People { get; } = [];

    [ObservableProperty] private Choice<IcsFormKind>? _selectedForm;
    [ObservableProperty] private Choice<Guid?>? _selectedIncident;
    [ObservableProperty] private Choice<Guid?>? _selectedPlan;
    [ObservableProperty] private Choice<Guid?>? _selectedPerson;
    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _usesPlan;
    [ObservableProperty] private bool _usesPerson;

    private IcsForm? _form;
    private bool _loading;

    partial void OnSelectedFormChanged(Choice<IcsFormKind>? value) => _ = GenerateAsync();
    partial void OnSelectedIncidentChanged(Choice<Guid?>? value) => _ = GenerateAsync();
    partial void OnSelectedPlanChanged(Choice<Guid?>? value) => _ = GenerateAsync();
    partial void OnSelectedPersonChanged(Choice<Guid?>? value) => _ = GenerateAsync();

    /// <summary>Fills the choices from the COP and shows the first form.</summary>
    public async Task OpenAsync()
    {
        _loading = true;
        try
        {
            Replace(Incidents, cop.Incidents.OrderByDescending(i => i.Status != IncidentStatus.Closed).ThenByDescending(i => i.Priority)
                .Select(i => new Choice<Guid?>(i.Id, $"{i.Number} {i.Name ?? EventDescriber.Humanize(i.Type)}")));
            Replace(People, [new(null, "Everyone"), .. cop.Participants.Select(p => new Choice<Guid?>(p.Id, p.Label))]);
            SelectedIncident ??= Incidents.FirstOrDefault();
            SelectedPerson ??= People[0];
            SelectedForm ??= Forms[0];
        }
        finally
        {
            _loading = false;
        }
        await GenerateAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => OpenAsync();

    private async Task GenerateAsync()
    {
        if (_loading || SelectedForm is not { } form) return;
        var kind = form.Value;
        UsesPlan = IcsForms.FromPlan(kind);
        UsesPerson = kind is IcsFormKind.Ics214 or IcsFormKind.DecisionLog;
        RefreshPlans();
        var builder = await BuilderAsync();
        _form = builder.Build(kind, SelectedIncident?.Value, UsesPlan ? SelectedPlan?.Value : null, UsesPerson ? SelectedPerson?.Value : null);
        Preview = kind == IcsFormKind.IapPackage
            ? string.Join(Environment.NewLine + Environment.NewLine, builder.Package(SelectedIncident?.Value, SelectedPlan?.Value).Select(IcsForms.ToText))
            : IcsForms.ToText(_form);
        Status = $"{form.Name} · as of {cop.AsOf.ToLocalTime():HH:mm}";
    }

    /// <summary>The incident's plan versions, newest first, defaulting to the one in force.</summary>
    private void RefreshPlans()
    {
        var incident = SelectedIncident?.Value;
        var versions = cop.ActionPlans.Where(p => p.IncidentId == incident).OrderByDescending(p => p.OperationalPeriod?.Number).ThenByDescending(p => p.Version)
            .Select(p => new Choice<Guid?>(p.Id, $"{p.Label} ({EventDescriber.Humanize(p.Status).ToLowerInvariant()})")).ToList();
        List<Choice<Guid?>> choices = [new(null, "In force (or latest)"), .. versions];
        if (Plans.Select(p => p.Value).SequenceEqual(choices.Select(c => c.Value))) return;
        var keep = SelectedPlan?.Value;
        _loading = true;
        try
        {
            Replace(Plans, choices);
            SelectedPlan = Plans.FirstOrDefault(p => p.Value == keep) ?? Plans[0];
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task<FormBuilder> BuilderAsync()
    {
        var events = new List<SimEvent>();
        await foreach (var e in store.ReadAsync(session.Id)) events.Add(e);
        return new FormBuilder(cop, events);
    }

    [RelayCommand]
    private void Copy()
    {
        if (Preview.Length == 0) return;
        Clipboard.SetText(Preview);
        Status = "Copied to the clipboard";
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_form is null) return;
        var forms = _form.Kind == IcsFormKind.IapPackage ? (await BuilderAsync()).Package(SelectedIncident?.Value, SelectedPlan?.Value) : [_form];
        Save($"{_form.Number} {_form.Title}", IcsForms.ToHtml(forms));
    }

    /// <summary>Every form for the selected incident in one document: the record of the incident so far.</summary>
    [RelayCommand]
    private async Task ExportAllAsync()
    {
        var builder = await BuilderAsync();
        var incident = SelectedIncident?.Value;
        var forms = IcsForms.All.Where(k => k != IcsFormKind.IapPackage).Select(k => builder.Build(k, incident)).ToList();
        Save($"Incident documentation {SelectedIncident?.Name}", IcsForms.ToHtml(forms, $"Incident documentation — {SelectedIncident?.Name}"));
    }

    private void Save(string title, string html)
    {
        var name = string.Concat($"{title} {cop.AsOf.ToLocalTime():yyyy-MM-dd HHmm}".Split(Path.GetInvalidFileNameChars()));
        var dialog = new SaveFileDialog { Filter = "Web page (*.html)|*.html", FileName = $"{name}.html", AddExtension = true };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, html, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Status = $"Saved to {dialog.FileName}";
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}
