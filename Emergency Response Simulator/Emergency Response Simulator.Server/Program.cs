using Emergency_Response_Simulator.Server;

// The simulation server (Phase 11 M1): the engine, event store and live COP run here, once, and every client connects
// over SignalR. The engine is the same one a standalone session runs.
ServerHost.Build(args).Run();
