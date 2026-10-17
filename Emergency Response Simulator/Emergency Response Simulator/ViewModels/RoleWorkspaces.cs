using Emergency_Response_Simulator.Accessibility;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>Right-panel tabs, in the order the main window shows them.</summary>
public enum WorkspacePanel { Comms, Incident, Unit, Ics, Crews, Agencies, Session, Assess, Desk, Public, Logistics, Health, Protection }

/// <summary>Bottom tabs, in the order the main window shows them.</summary>
public enum WorkspaceBoard { Status, Command, History, Instructor }

/// <summary>
/// A role's default layout (Phase 11 M3): the right-panel tabs it works from, the one in front, the bottom board, and the
/// map layers it cares about. Every workspace shows the same COP; it only arranges it. Permissions are separate.
/// </summary>
/// <param name="Role">The role this is the default for; null for the full layout.</param>
public sealed record RoleWorkspace(UserRole? Role, string Name, string Purpose, IReadOnlyList<WorkspacePanel> Panels, WorkspacePanel Focus,
    IReadOnlyList<WorkspaceBoard> Boards, WorkspaceBoard Board, bool LayerPanel, IReadOnlyDictionary<string, bool> Layers)
{
    public override string ToString() => Name;
}

public static class RoleWorkspaces
{
    private static readonly WorkspacePanel[] AllPanels = Enum.GetValues<WorkspacePanel>();
    private static readonly WorkspaceBoard[] AllBoards = Enum.GetValues<WorkspaceBoard>();

    /// <summary>Everything, as standalone sessions have always shown it.</summary>
    public static readonly RoleWorkspace Full = new(null, "Full layout", "Every panel and board.", AllPanels, WorkspacePanel.Comms,
        AllBoards, WorkspaceBoard.Status, true, new Dictionary<string, bool>());

    public static IReadOnlyList<RoleWorkspace> All { get; } =
    [
        // The IC starts at the desk: decisions waiting, and whatever falls to them from the roles nobody has taken.
        Of(UserRole.IncidentCommander, "Command overview: what waits for your decision, incidents, the command structure and other agencies.",
            [WorkspacePanel.Desk, WorkspacePanel.Incident, WorkspacePanel.Ics, WorkspacePanel.Agencies, WorkspacePanel.Public, WorkspacePanel.Logistics, WorkspacePanel.Health, WorkspacePanel.Protection, WorkspacePanel.Comms, WorkspacePanel.Crews], WorkspacePanel.Desk,
            WorkspaceBoard.Command, layers: [(MapLayerKeys.Trails, false), (MapLayerKeys.Routes, false), (MapLayerKeys.Jurisdictions, true)]),
        Of(UserRole.Dispatcher, "Calls, units and the radio: who is free and how far away; GPS trouble.",
            [WorkspacePanel.Comms, WorkspacePanel.Incident, WorkspacePanel.Unit, WorkspacePanel.Protection], WorkspacePanel.Comms,
            WorkspaceBoard.Status, layers: [(MapLayerKeys.Routes, true), (MapLayerKeys.Trails, true), (MapLayerKeys.Reports, true), (MapLayerKeys.Gnss, true)]),
        Of(UserRole.Operations, "Tactics: units, groups and orders at the scene; where the patients go; evacuation routes and traffic.",
            [WorkspacePanel.Incident, WorkspacePanel.Ics, WorkspacePanel.Unit, WorkspacePanel.Comms, WorkspacePanel.Crews, WorkspacePanel.Public, WorkspacePanel.Logistics, WorkspacePanel.Health, WorkspacePanel.Protection], WorkspacePanel.Ics,
            WorkspaceBoard.Command, layers: [(MapLayerKeys.Routes, true), (MapLayerKeys.HazardZones, true), (MapLayerKeys.CommandZones, true), (MapLayerKeys.EvacuationRoutes, true)]),
        Of(UserRole.Planning, "The situation and the plan: reports, the IAP and objectives; environmental monitoring.",
            [WorkspacePanel.Incident, WorkspacePanel.Ics, WorkspacePanel.Agencies, WorkspacePanel.Protection], WorkspacePanel.Incident,
            WorkspaceBoard.Status, layers: [(MapLayerKeys.Reports, true), (MapLayerKeys.Weather, true), (MapLayerKeys.Trails, false)]),
        Of(UserRole.Logistics, "Resources and support: supplies, fuel and repairs, staging, requests, relief, the comms plan, hospitals, rest centres, generators.",
            [WorkspacePanel.Logistics, WorkspacePanel.Health, WorkspacePanel.Protection, WorkspacePanel.Comms, WorkspacePanel.Crews, WorkspacePanel.Unit, WorkspacePanel.Incident, WorkspacePanel.Public], WorkspacePanel.Logistics,
            WorkspaceBoard.Command, layers: [(MapLayerKeys.Hospitals, true), (MapLayerKeys.CommandZones, true), (MapLayerKeys.Trails, false), (MapLayerKeys.Shelters, true), (MapLayerKeys.Infrastructure, true)]),
        Of(UserRole.FinanceAdmin, "Costs and commitments: what the response costs, contracts, claiming it back, resource requests and mutual aid.",
            [WorkspacePanel.Logistics, WorkspacePanel.Incident, WorkspacePanel.Agencies], WorkspacePanel.Logistics,
            WorkspaceBoard.Command, layerPanel: false, layers: [(MapLayerKeys.Trails, false), (MapLayerKeys.Routes, false)]),
        Of(UserRole.Safety, "Responder safety: crews, Maydays, PARs, hazards and the weather; air, water and soil readings.",
            [WorkspacePanel.Crews, WorkspacePanel.Incident, WorkspacePanel.Comms, WorkspacePanel.Protection], WorkspacePanel.Crews,
            WorkspaceBoard.Status, layers: [(MapLayerKeys.HazardZones, true), (MapLayerKeys.Weather, true), (MapLayerKeys.TrafficZones, true), (MapLayerKeys.Environment, true)]),
        Of(UserRole.Liaison, "Other agencies: unified command, assistance, disputes and jurisdictions; public health and counselling; utilities and rest centres.",
            [WorkspacePanel.Agencies, WorkspacePanel.Public, WorkspacePanel.Health, WorkspacePanel.Protection, WorkspacePanel.Incident, WorkspacePanel.Comms], WorkspacePanel.Agencies,
            WorkspaceBoard.Command, layers: [(MapLayerKeys.Jurisdictions, true), (MapLayerKeys.PoliceUnits, true), (MapLayerKeys.OtherUnits, true)]),
        Of(UserRole.PublicInformation, "The public and the media: rumours, press conferences, statements, enquiries, evacuation and shelter areas.",
            [WorkspacePanel.Public, WorkspacePanel.Agencies, WorkspacePanel.Incident], WorkspacePanel.Public,
            WorkspaceBoard.Status, layers: [(MapLayerKeys.EvacuationZones, true), (MapLayerKeys.Trails, false), (MapLayerKeys.Routes, false), (MapLayerKeys.Shelters, true), (MapLayerKeys.EvacuationRoutes, true)]),
        Of(UserRole.AgencyChief, "Your agency's part: unified command, orders and disputes.",
            [WorkspacePanel.Agencies, WorkspacePanel.Incident, WorkspacePanel.Comms, WorkspacePanel.Unit], WorkspacePanel.Agencies,
            WorkspaceBoard.Command, layers: [(MapLayerKeys.Jurisdictions, true)]),
        Of(UserRole.Instructor, "Everything, with ground truth: the instructor's board in front.",
            AllPanels, WorkspacePanel.Session, WorkspaceBoard.Instructor, boards: AllBoards),
        Of(UserRole.Observer, "Watching and assessing: follow a player, note how they do, and the history.",
            [WorkspacePanel.Assess, WorkspacePanel.Incident, WorkspacePanel.Comms, WorkspacePanel.Agencies, WorkspacePanel.Public, WorkspacePanel.Logistics, WorkspacePanel.Health, WorkspacePanel.Protection], WorkspacePanel.Assess, WorkspaceBoard.History, boards: AllBoards),
    ];

    /// <summary>What the workspace picker offers: the full layout, then each role's.</summary>
    public static IReadOnlyList<RoleWorkspace> Choices() => [Full, .. All];

    public static RoleWorkspace For(UserRole role) => All.First(w => w.Role == role);

    // ---- Hardening (Phase 21) ----

    /// <summary>What a workspace may show this user, and where it opens.</summary>
    public sealed record Effective(IReadOnlySet<WorkspacePanel> Panels, WorkspacePanel Focus, IReadOnlySet<WorkspaceBoard> Boards, WorkspaceBoard Board);

    /// <summary>
    /// What a workspace shows this user, whatever was picked or customised. The instructor's board only where the
    /// instructor's tools are (standalone, or the instructor's own client): a trainee choosing the full or the instructor
    /// layout doesn't get it. Assessment only for assessors; the session tab only in a networked session. Focus and board
    /// never land on something hidden. An empty set falls back to the desk.
    /// </summary>
    public static Effective Resolve(RoleWorkspace workspace, bool assessor, bool instructorTools, bool networked)
    {
        var panels = workspace.Panels
            .Where(p => p != WorkspacePanel.Assess || assessor)
            .Where(p => p != WorkspacePanel.Session || networked)
            .ToHashSet();
        if (panels.Count == 0) panels.Add(WorkspacePanel.Desk);
        var focus = panels.Contains(workspace.Focus) ? workspace.Focus : AllPanels.First(panels.Contains);

        var boards = workspace.Boards.Where(b => b != WorkspaceBoard.Instructor || instructorTools).ToHashSet();
        if (boards.Count == 0) boards.Add(WorkspaceBoard.Status);
        boards.Add(WorkspaceBoard.Status); // the status boards are everyone's
        var board = boards.Contains(workspace.Board) ? workspace.Board : AllBoards.First(boards.Contains);
        return new Effective(panels, focus, boards, board);
    }

    /// <summary>
    /// A role's workspace as the user customised it (Display settings). Only panels and boards the role's default could
    /// show can be chosen, so a customisation never widens what a role sees; the desk and the session tab are always kept;
    /// names that don't parse are dropped. Null: the default.
    /// </summary>
    public static RoleWorkspace Customise(RoleWorkspace basis, WorkspaceCustomisation? custom)
    {
        if (custom is null) return basis;
        var allowed = basis.Role is null || basis.Role == UserRole.Instructor ? AllPanels.ToHashSet() : AllPanels.Where(p => p != WorkspacePanel.Assess || Roles.IsAssessor(basis.Role.Value)).ToHashSet();
        var panels = custom.Panels
            .Select(n => Enum.TryParse<WorkspacePanel>(n, out var p) ? p : (WorkspacePanel?)null)
            .OfType<WorkspacePanel>()
            .Where(allowed.Contains)
            .Append(WorkspacePanel.Desk).Append(WorkspacePanel.Session)
            .Distinct().OrderBy(p => p).ToList();
        var focus = Enum.TryParse<WorkspacePanel>(custom.Focus, out var f) && panels.Contains(f) ? f : panels.Contains(basis.Focus) ? basis.Focus : panels[0];
        var board = Enum.TryParse<WorkspaceBoard>(custom.Board, out var b) && basis.Boards.Contains(b) ? b : basis.Board;
        return basis with { Panels = panels, Focus = focus, Board = board };
    }

    public static WorkspaceCustomisation Describe(RoleWorkspace workspace) =>
        new([.. workspace.Panels.Select(p => p.ToString())], workspace.Focus.ToString(), workspace.Board.ToString());

    /// <summary>What is wrong with a workspace definition: the built-in ones are held to none.</summary>
    public static IReadOnlyList<string> Problems(RoleWorkspace workspace)
    {
        var problems = new List<string>();
        if (!workspace.Panels.Contains(workspace.Focus)) problems.Add($"{workspace.Name}: opens on {workspace.Focus}, which it doesn't show.");
        if (!workspace.Boards.Contains(workspace.Board)) problems.Add($"{workspace.Name}: opens on the {workspace.Board} board, which it doesn't show.");
        if (workspace.Panels.Distinct().Count() != workspace.Panels.Count) problems.Add($"{workspace.Name}: a panel is listed twice.");
        if (workspace.Role is { } role)
        {
            if (!workspace.Panels.Contains(WorkspacePanel.Desk)) problems.Add($"{workspace.Name}: no desk.");
            if (!workspace.Panels.Contains(WorkspacePanel.Session)) problems.Add($"{workspace.Name}: no session tab.");
            if (workspace.Panels.Contains(WorkspacePanel.Assess) && !Roles.IsAssessor(role)) problems.Add($"{workspace.Name}: assessment is for assessors.");
            if (workspace.Boards.Contains(WorkspaceBoard.Instructor) && role != UserRole.Instructor && role != UserRole.Observer)
                problems.Add($"{workspace.Name}: the instructor's board in a player's workspace.");
        }
        foreach (var layer in workspace.Layers.Keys.Where(k => !MapLayerKeys.All.Contains(k)))
            problems.Add($"{workspace.Name}: no map layer {layer}.");
        return problems;
    }

    private static RoleWorkspace Of(UserRole role, string purpose, WorkspacePanel[] panels, WorkspacePanel focus, WorkspaceBoard board,
        bool layerPanel = true, WorkspaceBoard[]? boards = null, params (string Key, bool On)[] layers) =>
        // Every role has its desk (Phase 12) and the session tab, whatever else it works from.
        new(role, Roles.Name(role), purpose, [.. panels.Append(WorkspacePanel.Desk).Append(WorkspacePanel.Session).Distinct()], focus,
            boards ?? [WorkspaceBoard.Status, WorkspaceBoard.Command, WorkspaceBoard.History], board, layerPanel,
            layers.ToDictionary(l => l.Key, l => l.On));
}
