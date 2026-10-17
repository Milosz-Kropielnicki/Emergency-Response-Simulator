namespace Emergency_Response_Simulator.Simulation.Remote;

/// <summary>
/// One of the simulator's separate systems (Design Document §7, Phase 20), and how the server exposes it.
/// </summary>
/// <param name="Question">The one question the module answers.</param>
/// <param name="Contract">Its interface in Core.Contracts.</param>
/// <param name="HubService">Its name for the hub's <c>Call</c> and the REST API's service calls; null when it has no commands.</param>
/// <param name="RestPath">Where the REST API serves it, under /api/v1/sessions/{id}.</param>
/// <param name="EventSource">The event source its events carry, for clients that follow the stream.</param>
/// <param name="Delivery">How a client gets it: pushed in the event stream and projected locally, called on the server, or both.</param>
public sealed record ServiceModule(string Key, string Name, string Question, string Contract, string? HubService, string RestPath,
    string? EventSource, string Delivery);

public static class ServiceModules
{
    public static IReadOnlyList<ServiceModule> All { get; } =
    [
        new("cop", "Common Operating Picture", "What is happening?", "ICopService", null, "/cop", "cop",
            "Projected on each client from the perceived event stream; read over REST."),
        new("c2", "Command and control", "What should be done?", "IC2Service", SimulationHubProtocol.Services.C2, "/services/c2", "c2",
            "Commands called on the server, checked against its COP and the permission matrix."),
        new("iap", "Incident Action Planning", "What is the plan?", "IIapService", SimulationHubProtocol.Services.Iap, "/iap", "iap",
            "Commands called on the server; plans and ICS forms read over REST."),
        new("gis", "GIS", "What exists where?", "IGisService", SimulationHubProtocol.Services.Gis, "/gis", "gis",
            "Static layers and spatial queries on the server's PostGIS database."),
        new("avl", "Automatic Vehicle Location", "Where are my resources right now?", "IAvlService", null, "/avl", "avl",
            "Fixes pushed in the event stream; latest fixes read, and outside trackers' fixes fed, over REST."),
        new("plume", "Hazard dispersion", "Where is the hazard going?", "IPlumeService", SimulationHubProtocol.Services.Plume, "/plume", "plume",
            "The Python dispersion model, called through the server."),
        new("comms", "Communications", "Who said what, on which channel?", "ICommsService", null, "/comms", "comms",
            "The log projected from the event stream; transmissions sent as C2 commands."),
        new("aar", "After-action review", "What happened, and why?", "IAarService", SimulationHubProtocol.Services.Aar, "/aar", null,
            "Replay on the client's own stream; the full review built on the server."),
        new("integration", "Data standards", "What do other systems need, and what can they tell us?", "IIntegrationService",
            SimulationHubProtocol.Services.Integration, "/standards", "integration",
            "CAP, EDXL, NIEM and HL7 exported and imported on the server; the NIMS check."),
        new("control", "Simulation control", "When is it?", "ISimulationControl", SimulationHubProtocol.Services.Control, "/services/control", "engine",
            "The clock pushed to every client; start, pause and speed for the instructor."),
        new("instructor", "Instructor console", "What happens next?", "IInstructorService", SimulationHubProtocol.Services.Instructor, "/services/instructor",
            "instructor", "The instructor's injects, holds and scenarios."),
        new("assessment", "Assessment", "How are they doing?", "IAssessmentService", SimulationHubProtocol.Services.Assessment, "/services/assessment",
            "assessment", "Observers' and the instructor's notes."),
    ];
}
