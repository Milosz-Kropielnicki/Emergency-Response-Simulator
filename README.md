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

## GIS (Phase 1)

### Coordinate systems

| Use | System | Where |
|---|---|---|
| Storage, events, contracts | WGS84 lat/lon (EPSG:4326) | PostGIS columns `geometry(…,4326)`, `GeoPoint` |
| Map rendering | Web Mercator (EPSG:3857) | `WebMercator`; never used for measurement |
| Metric calculations (buffers, areas, lengths) | UTM zone for the area (Dublin: 29N, EPSG:32629) | `MetricProjection` (ProjNet) |
| Quick point-to-point distance and bearing | Great circle | `GeoMath` |

Spatial indexing: every geometry column has a GiST index. `IGisService.FindNearestAsync` walks the index with the KNN operator `<->`, then re-ranks the candidates by true `geography` distance in metres.

### Static layers

The `Emergency Response Simulator.GisImport` console tool fills the static layers listed in `GisLayerKeys`:
- **From OpenStreetMap, via the Overpass API:** roads, buildings, water, railways, area boundaries, hospitals, fire, police and ambulance stations, shelters, hydrants, schools and critical infrastructure.
- **From the Open-Meteo elevation API (Copernicus DEM):** an elevation grid.

Raw downloads are cached in `gis-data/cache/` (git-ignored), so a re-import runs offline. Each layer is replaced in a single transaction.

```powershell
cd "Emergency Response Simulator"
dotnet run --project "Emergency Response Simulator.GisImport" -- --help
dotnet run --project "Emergency Response Simulator.GisImport"                       # all layers, central Dublin
dotnet run --project "Emergency Response Simulator.GisImport" -- --layers roads --refresh
dotnet run --project "Emergency Response Simulator.GisImport" -- --connection ErsTest
```

Licences: OSM data is © OpenStreetMap contributors (ODbL), and the elevation data is Copernicus DEM (CC BY 4.0). Each layer records its source in `gis_layers.source`.

### Dynamic layers

Road closures, evacuation zones, perimeters, search areas and operational zones are drawn on the map and declared through `IC2Service.DeclareZoneAsync`. They become `ZoneDeclared` and `ZoneLifted` events, so they replay in the AAR like any other order. They are not written to the static tables.

### Map views

| View | Background | Simulator overlay |
|---|---|---|
| Street | OpenStreetMap | — |
| Satellite | Esri World Imagery | — |
| Weather | Esri Dark Gray Canvas | Arrows for the *reported* wind (`WeatherObserved`), not the true wind |
| Traffic | Esri Dark Gray Canvas | Roads styled by class; congestion arrives with the traffic simulation in Phase 6 |
| Terrain | OpenTopoMap | Elevation grid |

## COP (Phase 2)

- **Incidents and resources** are edited only through `IC2Service`: create from a report, update, assign an incident commander, update unit status, assess and link reports, acknowledge alerts, establish hot/warm/cold zones. Every edit is an event.
- **Status dynamics:** `UnitStatusRules` enforces AVAILABLE → DISPATCHED → EN ROUTE → ON SCENE → OPERATING → TRANSPORTING → AVAILABLE, plus cancellation and out of service. `UnitResponseSystem` moves dispatched crews through turnout, travel (straight line × 1.3 detour; Phase 3 adds routing), arrival and work, sending AVL fixes and heartbeats. A unit whose radio has failed (`UnitRadioFailed`, a truth event) keeps working in the world, but command hears nothing from it.
- **Attention management:** `AttentionMonitor` reads the *live COP* each tick and raises each condition once, re-arming it when the condition clears:

  | Alert | Fires when |
  |---|---|
  | Critical | An incident goes critical, or reported casualties reach the mass-casualty threshold |
  | Resource shortage | A unit type drops to the shortage threshold or below |
  | Situation change | Reported wind shifts by 45° or more, or its speed changes by 5 m/s or more |
  | Communication failure | A committed unit has been silent longer than the timeout (4 min) |

  Thresholds are in the `Attention` section of the settings.
- **Time dimension:** `EventDescriber` turns events into history lines. `CopView` is what the UI displays: either the live COP or a replay snapshot from `IAarService.ReplayToAsync`. C2 always validates against the live COP, and commands are refused while replaying. The history log can show ground-truth events for instructors and AAR.
- **Scenario:** with `Simulation:Scenario = barrow-street`, `BarrowStreetScenario` plays timed injects:
  - a vague call, then a better one
  - a wrong social-media rumour
  - a police confirmation
  - a wind shift
  - a suspected chemical store
  - Engine 7's radio failing
  - hospital pressure

  It never creates the incident; that is the trainee's job. `--Simulation:AutoStart=true` starts the clock automatically.

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
