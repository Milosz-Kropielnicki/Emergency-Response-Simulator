using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>Right-panel tabs, in the order the main window shows them.</summary>
public enum WorkspacePanel { Comms, Incident, Unit, Ics, Crews, Agencies, Session }

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
        Of(UserRole.IncidentCommander, "Command overview: incidents, the command structure, approvals and other agencies.",
            [WorkspacePanel.Incident, WorkspacePanel.Ics, WorkspacePanel.Agencies, WorkspacePanel.Comms, WorkspacePanel.Crews], WorkspacePanel.Incident,
            WorkspaceBoard.Command, layers: [(MapLayerKeys.Trails, false), (MapLayerKeys.Routes, false), (MapLayerKeys.Jurisdictions, true)]),
        Of(UserRole.Dispatcher, "Calls, units and the radio: who is free and how far away.",
            [WorkspacePanel.Comms, WorkspacePanel.Incident, WorkspacePanel.Unit], WorkspacePanel.Comms,
            WorkspaceBoard.Status, layers: [(MapLayerKeys.Routes, true), (MapLayerKeys.Trails, true), (MapLayerKeys.Reports, true)]),
        Of(UserRole.Operations, "Tactics: units, groups and orders at the scene.",
            [WorkspacePanel.Incident, WorkspacePanel.Ics, WorkspacePanel.Unit, WorkspacePanel.Comms, WorkspacePanel.Crews], WorkspacePanel.Ics,
            WorkspaceBoard.Command, layers: [(MapLayerKeys.Routes, true), (MapLayerKeys.HazardZones, true), (MapLayerKeys.CommandZones, true)]),
        Of(UserRole.Planning, "The situation and the plan: reports, the IAP and objectives.",
            [WorkspacePanel.Incident, WorkspacePanel.Ics, WorkspacePanel.Agencies], WorkspacePanel.Incident,
            WorkspaceBoard.Status, layers: [(MapLayerKeys.Reports, true), (MapLayerKeys.Weather, true), (MapLayerKeys.Trails, false)]),
        Of(UserRole.Logistics, "Resources and support: requests, relief, the comms plan, hospitals.",
            [WorkspacePanel.Comms, WorkspacePanel.Crews, WorkspacePanel.Unit, WorkspacePanel.Incident], WorkspacePanel.Crews,
            WorkspaceBoard.Command, layers: [(MapLayerKeys.Hospitals, true), (MapLayerKeys.CommandZones, true), (MapLayerKeys.Trails, false)]),
        Of(UserRole.FinanceAdmin, "Costs and commitments: resource requests and mutual aid.",
            [WorkspacePanel.Incident, WorkspacePanel.Agencies], WorkspacePanel.Agencies,
            WorkspaceBoard.Command, layerPanel: false, layers: [(MapLayerKeys.Trails, false), (MapLayerKeys.Routes, false)]),
        Of(UserRole.Safety, "Responder safety: crews, Maydays, PARs, hazards and the weather.",
            [WorkspacePanel.Crews, WorkspacePanel.Incident, WorkspacePanel.Comms], WorkspacePanel.Crews,
            WorkspaceBoard.Status, layers: [(MapLayerKeys.HazardZones, true), (MapLayerKeys.Weather, true), (MapLayerKeys.TrafficZones, true)]),
        Of(UserRole.Liaison, "Other agencies: unified command, assistance, disputes and jurisdictions.",
            [WorkspacePanel.Agencies, WorkspacePanel.Incident, WorkspacePanel.Comms], WorkspacePanel.Agencies,
            WorkspaceBoard.Command, layers: [(MapLayerKeys.Jurisdictions, true), (MapLayerKeys.PoliceUnits, true), (MapLayerKeys.OtherUnits, true)]),
        Of(UserRole.PublicInformation, "The public and the media: statements, enquiries, evacuation and shelter areas.",
            [WorkspacePanel.Agencies, WorkspacePanel.Incident], WorkspacePanel.Agencies,
            WorkspaceBoard.Status, layers: [(MapLayerKeys.EvacuationZones, true), (MapLayerKeys.Trails, false), (MapLayerKeys.Routes, false)]),
        Of(UserRole.AgencyChief, "Your agency's part: unified command, orders and disputes.",
            [WorkspacePanel.Agencies, WorkspacePanel.Incident, WorkspacePanel.Comms, WorkspacePanel.Unit], WorkspacePanel.Agencies,
            WorkspaceBoard.Command, layers: [(MapLayerKeys.Jurisdictions, true)]),
        Of(UserRole.Instructor, "Everything, with ground truth: the instructor's board in front.",
            AllPanels, WorkspacePanel.Session, WorkspaceBoard.Instructor, boards: AllBoards),
        Of(UserRole.Observer, "Watching: the map, the incidents and the history.",
            [WorkspacePanel.Incident, WorkspacePanel.Comms], WorkspacePanel.Incident, WorkspaceBoard.History, boards: AllBoards),
    ];

    /// <summary>What the workspace picker offers: the full layout, then each role's.</summary>
    public static IReadOnlyList<RoleWorkspace> Choices() => [Full, .. All];

    public static RoleWorkspace For(UserRole role) => All.First(w => w.Role == role);

    private static RoleWorkspace Of(UserRole role, string purpose, WorkspacePanel[] panels, WorkspacePanel focus, WorkspaceBoard board,
        bool layerPanel = true, WorkspaceBoard[]? boards = null, params (string Key, bool On)[] layers) =>
        new(role, Roles.Name(role), purpose, [.. panels.Append(WorkspacePanel.Session).Distinct()], focus,
            boards ?? [WorkspaceBoard.Status, WorkspaceBoard.Command, WorkspaceBoard.History], board, layerPanel,
            layers.ToDictionary(l => l.Key, l => l.On));
}
