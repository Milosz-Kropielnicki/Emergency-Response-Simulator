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

## AVL & routing (Phase 3)

- **Road network:** `RoadNetwork` builds a routable graph in memory from the imported roads. Ways that meet share a vertex, which is how junctions are found. Central Dublin is 37k nodes and 79k edges, built in about 1 s at start-up.
  - Speeds come from road class, capped near the posted `maxspeed`. Heavy apparatus is 15% slower.
  - One-way streets, roundabouts and motorways are respected. Driving against one is only a 3×-cost fallback.
  - A* finds the fastest route by travel time.
  - We use this rather than pgRouting because closures and obstructions change every simulation tick, and in-memory re-routing takes milliseconds.
- **Traffic:** `TimeOfDayTraffic` slows main roads at weekday peaks to 45% of free-flow speed and in the daytime to 70%. Blue lights recover part of the delay (the square root of the factor). Phase 6 adds live, simulated congestion.
- **Units drive routes:** `UnitResponseSystem` follows the legs at road speed and sends AVL fixes every 5 s while moving (every 60 s when stationary). It sends `RouteReported` when a route is planned or re-planned.
  - Crews avoid every declared road closure, hot zone and fire-exclusion zone, and re-plan when one is declared mid-journey.
  - **Routing complications:** a `RoadObstructed` truth event blocks a road without anyone being told. A crew that reaches it stops, assesses it for 40 s, radios a field report and re-routes.
  - A `UnitBrokeDown` truth event stops a vehicle; its AVL keeps reporting, and after 2 minutes the crew reports the fault and goes out of service.
- **AVL feed:** `AvlService` implements `IAvlService`: the latest fix per unit (ID, position, timestamp, speed, heading, status) plus a breadcrumb trail.
- **Derived alerts** (in `AttentionMonitor`, thresholds in the `Attention` settings):

  | Alert | Fires when |
  |---|---|
  | Stopped en route | No movement for 90 s or more |
  | Off planned route | Two consecutive fixes more than 150 m from the reported route |
  | Delayed: re-routed | Expected arrival pushed back by 1 min or more; counts the time spent stopped as well as the longer drive |
  | AVL signal lost | No fix for 60 s while en route; this fires well before the 4-minute comms-failure alert |
- **AVL + GIS in the UI:**
  - The map shows heading arrows, an ETA on each moving unit's label, planned routes (dashed) and AVL trails.
  - The unit panel shows location with the nearest road name, speed, heading, crew, equipment, assignment, ETA, distance to incident (straight line and by road) and AVL age.
  - The incident panel ranks available units by road ETA (avoiding declared closures), each with a Dispatch button.
- **Barrow Street scenario:**
  - At the start, a lorry sheds its load on MacMahon Bridge (unreported).
  - From minute 6, the first fire engine still driving breaks down.
  - `--Simulation:DemoAutoResponse=true` makes the scenario open the incident and dispatch a first response itself, for presentations.

## C2 dispatch & command (Phase 4)

Everything below goes through `IC2Service`, is validated against the live COP, and is recorded as events. `CommandResponseSystem` plays everyone on the other end. Chance outcomes are seeded from the event id, so replays are reproducible.

- **Dispatch:**
  - Dispatch, reassign and stand-down from the resource board.
  - The incident panel ranks available units by road ETA.
- **ICS structure** (per incident; `IncidentCommand`, applied identically to the COP and to ground truth):
  - the Incident Commander
  - command staff: Safety, Liaison, PIO
  - general staff: Operations, Planning, Logistics, Finance/Admin
  - groups and divisions under Operations, each with a supervisor and assigned units

  The ICS tab edits all of it.
- **Span of control:** `SpanOfControl.Assess` counts direct reports for every supervisor, ICS-recommended 3–7. Units not in a group report to Operations if it is staffed, otherwise to the IC. Over 7 raises an alert and applies a real penalty to orders passing through that supervisor:
  - read-back takes `1 + 0.5 × (span − 7)` times longer;
  - `min(50%, 10% × (span − 7))` of orders are lost;
  - the next band of bad luck produces a garbled read-back.
- **Orders:** to a unit, group or staffed position. The recipient reads back after 20 s (units) or 35 s (supervisors). A dead radio means no read-back, and "not acknowledged" alerts after 2 min. Orders are closed as completed or cancelled.
- **Resource requests:**

  | Kind | Decided by | Rule | Arrives after approval |
  |---|---|---|---|
  | Additional resources | Our own control room | Always approved, immediately | 8 min |
  | Mutual aid | Regional Duty Officer (after 2.5 min) | Approved only for High/Critical incidents | 15 min |
  | Specialist teams | National Directorate (after 3 min) | Approved only for High/Critical incidents | 25 min |

  Mutual aid and specialist teams also need a justification for the approver. Approved resources are registered as new units, under a provider agency where there is one (e.g. "Kildare Fire Service"). They appear at an entry point on the western or southern approach and are dispatched to the incident, driving in by road.
- **Approvals:** decisions others need from command (the scenario's Garda inspector asks for authority to evacuate). They are approved or denied with a note, the requester acknowledges, and a reminder alert comes after 3 min.
- **Notifications:** to hospitals, utilities, agencies and government. Replies arrive after 1–2.5 min, depending on the recipient (e.g. ESB offers to isolate supply).
- **UI:**
  - a Command & Control tab: orders, requests, approvals, notifications
  - an ICS tab: positions, span of control, groups, unit placement
  - a Reassign button on the resource board
  - a history filter for orders, requests and approvals

## IAP builder (Phase 5)

The Incident Action Plan is event-sourced like the rest of the COP. Each step is an IAP event (`OperationalPeriodStarted`, `IapDraftCreated`, `IapDraftSaved`, `IapSubmitted`, `IapReturned`, `IapApproved`, `IapBriefed`, `ObjectiveStatusChanged`), so plans appear in the history, replay and AAR. Saved drafts carry the whole document (`IapContent`). The `incident_action_plans` table stores the same document as jsonb (migration `IapBuilder`).

- **Operational periods:** numbered per incident. Each lasts between 10 minutes and 24 hours. Starting the next period early cuts the previous one short.
- **Versions and approval workflow:** a period has versions v1, v2, and so on.
  - A plan moves Draft → Pending approval → Approved. Returning a plan with comments sends it back to Draft.
  - Approving a new version supersedes the earlier approved one.
  - Only one version per period can be open at a time.
  - Approved versions are read-only. A revision is a new version, copied from the latest one.
- **Starting a draft:**
  - The first draft of a period is pulled from the COP.
  - A later period's first draft carries the previous plan forward. Achieved objectives drop out; open ones keep their ids, so progress tracking continues. Everything else is refreshed from the COP.
- **Objectives:** strategic → operational → tactical.
  - Each operational objective has a responsible section or group, resources, a performance target with an optional due time, and its tactics.
  - Suggestions come from `IapTemplates`.
- **Organisation:** the plan's own ICS 203. The builder draws it as an org chart (vacant positions dashed, spans over the limit in red).
- **Assignments:** each unit gets a group, an assignment and the objective it serves. A live column shows what the COP says about the unit.
- **Communications, medical and safety plans:**
  - The communications plan (ICS 205) gets default talkgroups for the agencies on scene, plus a tactical channel per group.
  - The medical plan (ICS 206) lists the nearest receiving hospitals from GIS, with blue-light road times. Maternity, dental and rehabilitation hospitals are excluded.
  - The safety plan (ICS 208) lists hazards from the COP's threats and hazard zones, with suggested mitigations, PPE, accountability and the safety message.
- **Compliance (`IapCompliance`):**
  - Errors block submission and approval:
    - no objectives
    - a strategic objective without operational objectives
    - no IC
    - no channels
    - no receiving hospital
    - no safety message
    - threats on the COP but no hazards in the plan
    - assignments to unknown groups
  - Warnings go to the approver with the plan:
    - objectives that can't be measured or have no tactics
    - span of control over the limit in the planned organisation
    - committed units missing from the plan
    - assignments the COP shows are not viable
- **Brief (push to the COP):** applies the approved plan through C2:
  - staffs positions
  - forms missing groups and stands down groups the plan no longer has once they are empty
  - dispatches planned units that are available
  - places units in their groups
  - orders each unit whose assignment changed since the last briefing (orders get the normal read-back)
- **Plan against reality (`AttentionMonitor`, category Planning):**
  - a busy incident with no IAP
  - a period ending (10 min warning) or ended
  - a period with no approved plan
  - a plan awaiting approval
  - an approved plan not yet briefed
  - "Plan not viable: Engine 12 is out of service"
  - an operational objective past its due time
- **UI:**
  - an IAP Builder window, opened from the header or the incident panel. It has sections for period and situation (with end-of-period reassessment), each ICS form, and review and approval (compliance list and the plan as text).
  - the plan summary in the incident panel
  - an "IAP task" column on the resource board
  - an "Action plans" history filter

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
