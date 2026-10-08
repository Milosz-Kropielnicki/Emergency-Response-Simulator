# Architecture

Phase 0 foundations for the Emergency Response Simulator. See the Design Document (§5, §10, §26) for the reasoning behind these choices.

## Technical stack decision

| Concern | Choice |
|---|---|
| UI (COP, IAP Builder, dashboards) | C# / .NET 10, WPF, CommunityToolkit.Mvvm, Mapsui (map) |
| Simulation engine, C2, event stream | C# / .NET 10 |
| Hazard / plume models | Python 3.14, FastAPI service (`hazard-models/`), called over HTTP |
| GIS + persistence | PostgreSQL 17 + PostGIS 3.6, EF Core 10 (Npgsql + NetTopologySuite) |
| Geometry | NetTopologySuite, WGS84 (SRID 4326) everywhere |

Python runs as a separate local service instead of being embedded, which keeps the modules separate (§23). It also avoids depending on pythonnet, which does not yet support Python 3.14.

## Solution layout

```
Emergency Response Simulator/
├─ Emergency Response Simulator.Core        entities, events, module contracts (no dependencies but NTS)
├─ Emergency Response Simulator.Simulation  engine, world/perceived state, C2, AAR, plume client
├─ Emergency Response Simulator.Data        EF Core + PostGIS schema, event store, GIS service
├─ Emergency Response Simulator             WPF shell (composition root)
└─ Emergency Response Simulator.Tests       xUnit
hazard-models/                              Python plume service + tests
database/                                   local PostgreSQL setup script
```

Dependencies point inward: `App → Simulation, Data → Core`. Simulation and Data do not know about each other; the app wires them together.

## Truth vs perception

The simulator separates **what is happening** from **what command believes is happening** (§6.7, §10.1).

```
            ┌──────────── SimulationEngine (single writer) ────────────┐
 systems ──►│ tick: advance WorldState, emit events                      │
 C2 ───────►│ PublishAsync(command events)                               │
            └───────────────┬────────────────────────────────────────────┘
                            ▼
                 IEventStore (append-only, sequenced)
                 ├─ Truth events     ─► WorldState     (engine, instructor, AAR)
                 └─ Perceived events ─► PerceivedState (= ICopService, what the trainee sees)
```

- **`WorldState`** is ground truth: where the fire really is and how many people are really hurt. Systems advance it every tick.
- **`PerceivedState`** is the COP. It is built only from `Perceived` events, such as calls, reports, AVL fixes and orders. A fire exists on the COP only once someone reports it.
- **Commands** such as a dispatch are checked against the COP, not against ground truth, because commanders act on what they know. The resulting events then change the world as well.
- **Replay and AAR:** the COP at any moment can be rebuilt by replaying events up to that time (`IAarService.ReplayToAsync`).

### Event stream rules

- Every change is a `DomainEvent` wrapped in a `SimEvent`. The wrapper carries the sequence number, session, sim time, wall time, visibility and source module.
- The engine is the only writer for a session, so sequence order is causal order.
- In PostgreSQL, a trigger rejects any `UPDATE`, `DELETE` or `TRUNCATE` on `events`.
- To add an event type, add a record in `Core/Events/DomainEvents.cs` and a `[JsonDerivedType]` line. A test fails if you forget the second step.

## Module contracts (`Core/Contracts`)

| Contract | Question it answers | Status |
|---|---|---|
| `ICopService` | What is happening? | Implemented (`PerceivedState`) |
| `IC2Service` | Make it happen | Core commands implemented; orders, notifications and approvals come in Phase 4 |
| `IIapService` | What should we do? | Contract only, Phase 5 |
| `IGisService` | What exists where? | Implemented on PostGIS |
| `IAvlService` | Where are my resources? | Contract only, Phase 3 |
| `IPlumeService` | Where is the hazard going? | HTTP client plus Python stub model; real model comes in Phase 12 |
| `ICommsService` | Who said what, on which channel? | Contract only, Phase 7 |
| `IAarService` | How did we get here? | Timeline and replay implemented; metrics come in Phase 9 |
| `ISimulationControl` | Start, pause, speed | Implemented (`SimulationEngine`) |

## Local setup

```powershell
# 1. Database (prompts for the postgres superuser password; writes appsettings.Local.json, which is git-ignored)
powershell -ExecutionPolicy Bypass -File database\Setup-Database.ps1

# 2. Schema
cd "Emergency Response Simulator"
dotnet tool restore
dotnet tool run dotnet-ef database update --project "Emergency Response Simulator.Data"

# 3. Hazard-model service (optional until Phase 12)
cd ..\hazard-models
.venv\Scripts\python -m uvicorn plume_service.main:app --host 127.0.0.1 --port 8765
```

If no connection string is configured, the app runs with an in-memory event store.
