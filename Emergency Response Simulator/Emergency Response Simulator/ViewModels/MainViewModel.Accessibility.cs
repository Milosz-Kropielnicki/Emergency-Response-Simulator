using Emergency_Response_Simulator.Accessibility;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>
/// Phase 21: what the shortcuts and voice commands drive, and the workspaces hardened and customised. Every action here
/// is one a mouse user already has; these just reach it from the keyboard or by voice.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The panels the current workspace shows this user, in tab order.</summary>
    public IReadOnlyList<WorkspacePanel> VisiblePanels =>
        SelectedWorkspace is { } workspace ? [.. Enum.GetValues<WorkspacePanel>().Where(Effective(workspace).Panels.Contains)] : [];

    private RoleWorkspaces.Effective Effective(RoleWorkspace workspace) =>
        RoleWorkspaces.Resolve(workspace, Assessment.IsAssessor, IsConsoleAvailable, Session.IsNetworked);

    /// <summary>Applies the user's customised workspaces, keeping the one chosen.</summary>
    public void ApplyWorkspaceCustomisations(IReadOnlyDictionary<string, WorkspaceCustomisation> customisations)
    {
        var selected = SelectedWorkspace?.Name;
        Workspaces = [.. RoleWorkspaces.Choices().Select(w => RoleWorkspaces.Customise(w, customisations.GetValueOrDefault(w.Name)))];
        var again = Workspaces.FirstOrDefault(w => w.Name == selected) ?? RoleWorkspaces.Full;
        SelectedWorkspace = null;
        SelectedWorkspace = again;
    }

    /// <summary>Brings a panel to the front, if this workspace shows it.</summary>
    public bool ShowPanel(WorkspacePanel panel)
    {
        if (!VisiblePanels.Contains(panel)) return false;
        IsRightPanelOpen = true;
        RightPanelTab = (int)panel;
        return true;
    }

    /// <summary>The next (or previous) panel this workspace shows, wrapping round.</summary>
    public void CyclePanel(int direction)
    {
        var panels = VisiblePanels;
        if (panels.Count == 0) return;
        var index = panels.ToList().IndexOf((WorkspacePanel)RightPanelTab);
        var next = panels[((index < 0 ? 0 : index + direction) % panels.Count + panels.Count) % panels.Count];
        ShowPanel(next);
    }

    /// <summary>Starts or pauses the clock; with <paramref name="run"/>, only if it isn't already that way.</summary>
    public async Task RunClockAsync(bool? run)
    {
        if (run is { } wanted && wanted == IsRunning) return;
        await TogglePlayCommand.ExecuteAsync(null);
    }

    /// <summary>One step up or down the speed choices.</summary>
    public async Task StepSpeedAsync(int direction)
    {
        var scales = TimeScales.ToList();
        var current = scales.FindIndex(s => s >= TimeScale);
        var next = Math.Clamp((current < 0 ? scales.Count - 1 : current) + direction, 0, scales.Count - 1);
        if (scales[next] != TimeScale) await SetTimeScaleCommand.ExecuteAsync(scales[next]);
    }

    /// <summary>Acknowledges the alert that has waited longest.</summary>
    public async Task AcknowledgeOldestAlertAsync()
    {
        if (_cop.Alerts.Where(a => a.AcknowledgedAt is null).OrderBy(a => a.RaisedAt).FirstOrDefault() is { } alert)
            await AcknowledgeAlertCommand.ExecuteAsync(alert.Id);
        else
            ShowCommandResult(Core.Contracts.CommandResult.Fail("No alert is waiting."));
    }
}
