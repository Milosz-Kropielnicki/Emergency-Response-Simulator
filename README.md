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
├─ Emergency Response Simulator.Server      ASP.NET Core + SignalR host for networked sessions (Phase 11)
├─ Emergency Response Simulator.Client      SignalR client: mirrored event stream, remote service proxies (Phase 11)
├─ Emergency Response Simulator             WPF shell (composition root; standalone or client)
└─ Emergency Response Simulator.Tests       xUnit
hazard-models/                              Python plume service + tests
database/                                   local PostgreSQL setup script
docs/                                       instructor and observer guides (Phase 12)
```

Dependencies point inward: `App → Client, Simulation, Data → Core` and `Server → Simulation, Data → Core`. Simulation and Data do not know about each other; the app (or the server) wires them together. The wire contract both sides share lives in `Simulation/Remote`.

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
| `IPlumeService` | Where is the hazard going? | Implemented: HTTP client to the Python dispersion model (Phase 13), which drives real releases and command's projections; the in-engine plume is the fallback |
| `ICommsService` | Who said what, on which channel? | Implemented (`CommsService` over the COP's comms log) |
| `IAarService` | How did we get here? | Implemented: timeline, replay, the analysed review, branch re-runs (Phase 9) |
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
| Traffic | Esri Dark Gray Canvas | Roads styled by class, plus congestion from the city traffic feed (Phase 6) |
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

## Simulation engine & basic AI (Phase 6)

The world now runs on its own. Each system advances `WorldState` every tick and records what changes as **truth** events. Command learns about any of it only through **perceived** events: calls, crew reports, hospital and utility reports, AVL. `Simulation:WorldModels = false` turns it all off and leaves the scripted world of earlier phases. Chance is seeded from ids, so a session replays the same way.

- **Hazards** (`Simulation/Hazards`, advanced by `HazardSystem`). A real structure fire or explosion gets a fire hazard; a flood incident gets a flood; a hazmat release gets a plume.

  | Model | How it works |
  |---|---|
  | Fire spread | Cellular automaton on 15 m cells. Spread depends on fuel (buildings burn, water doesn't) and wind behind the fire: about 2 m/min in still air, about 6 m/min downwind at 7 m/s. Engines working at the scene put out burning cells within 90 m and cool the cells around them. |
  | Flood | Water enters at the source and runs downhill over the elevation grid. Streets under 25 cm or more become real obstructions, which crews find by driving into them. |
  | Simple plume | Ground-level Gaussian plume with Briggs urban dispersion, class D. Low, moderate and high zones come from AEGL-style thresholds. The cloud only reaches as far as the wind has carried it, swings with the wind, and clears after the release stops. Since Phase 13 this is the fallback for when the Python dispersion model isn't running. |

  Terrain (`HazardTerrain`) comes from the imported buildings, water and elevation layers, and is uniform when there is no GIS. Footprints are recorded every minute (`HazardFootprintChanged`).
- **Agents:**
  - *Civilians* (`CivilianSystem`):
    - notice the hazard (smoke, smell, water), then evacuate, come to look, shelter or panic;
    - get hurt in the fire, the cloud or deep water;
    - some ring 999 a minute or two later, with locations 30–250 m out.
    - People inside a declared evacuation or shelter-in-place zone are warned over 2–10 minutes; 85 % comply. Evacuees with cars drive out as vehicle agents.
  - *Casualties and EMS* (`MedicalSystem`):
    - Untreated casualties deteriorate: P3 → P2 → P1 → dead.
    - Ambulance crews at the scene treat the most urgent casualty, then take one P1 or two others to the nearest hospital that has room.
    - Handover takes 8 minutes, and the crew then becomes available again.
    - The first crew sends casualty counts to command.
  - *Responders*: crews drive at the speed the roads really allow (`LiveTraffic`). Their navigation plans with the traffic feed, and they re-route when a jam costs them far more than promised.
- **Traffic** (`TrafficSystem`, every 10 s). Each road segment gets a congestion factor from:
  - queues spilling back up to three junctions from closures and blockages, growing over 12 minutes;
  - signalised junctions going dark in power cuts (45 % flow, 80 % with police directing traffic);
  - evacuee cars filling streets (Greenshields).

  Below 20 % of normal speed a road is gridlocked. Command's traffic view and ETA estimates use `TrafficFeed`, a copy refreshed every two minutes, so jams appear late.
- **Cascading effects** are recorded as `CascadeOccurred` truth events, e.g. fire reaches the chemical store → chlorine release → drums rupture (rate × 2.5); fire reaches the substation → power out within 600 m → signals dark at 6 junctions → "Ambulance 14 delayed about 1.6 min". Others:
  - a hospital on generators loses 15 % of its capacity;
  - a full hospital diverts arriving ambulances;
  - a closure causes gridlock on the roads leading into it.
- **Dynamic scenario updates:**

  | Changes | How |
  |---|---|
  | Wind | `WeatherSystem` drifts the true wind ±25° around the prevailing direction. The met service reports every 30 minutes, rounded and 10 minutes old. |
  | Release rate | `HazardRateChanged`, e.g. drums rupturing |
  | Road closures | Floodwater opens and closes them |
  | Casualty count | Follows the actual casualties and only ever grows |
  | Hospital capacity | Surge plan (+30 %) when command notifies the hospital; a burst pipe, or generators in a power cut |

  Hospitals report their load every 15 minutes, and straight away at 90 % or on diversion. The attention monitor alerts on what they *report*, and also when an en-route unit's ETA slips two minutes or more in traffic.
- **AI-controlled agencies** (`AgencyAiSystem`): agencies registered with `AiControlled` run their own control rooms. In the demo roster these are North District fire, Dublin South ambulance and Garda Roads Policing.
  - Their units are on the COP (resource board: "(own control)", with their job in the Assignment column), but C2 refuses to dispatch them.
  - They take routine jobs around the city.
  - Six minutes into a major incident, their own callers have told them about it. Without being asked, they:
    - set a traffic cordon;
    - direct traffic at dark junctions;
    - send ambulances when casualties wait;
    - send an engine to a large fire nobody is fighting.
  - They go to the incident's real location and tell command what they did. A major incident takes priority over their routine work.
- **Truth vs perception:**
  - `HazardReportingSystem` is the bridge: fire crews send a size-up and progress every 5 minutes. Their area estimates carry a consistent per-crew bias; they warn about exposures they can see ("hazard placards for chlorine on unit 4"). Crews sent to the wrong place report "nothing showing here, heavy smoke to the E". Crews inside the cloud report the smell, then symptoms.
  - The engine takes an immutable `WorldSnapshot` after every step.
  - `TruthComparison` lists where the COP and the world disagree:
    - an incident not on the COP;
    - casualties found versus casualties reported;
    - hazards nobody has reported;
    - a dead radio the COP still shows as in contact;
    - a hospital that diverts while the COP thinks it has room;
    - the true wind versus the reported wind.
- **UI:**
  - **Instructor · Ground truth** tab: truth vs COP, the cascade chain, and the world right now.
  - **Ground truth (instructor)** map layers, off by default and drawn in purple: real hazards, outages, blockages and chemical/substation sites; civilians and casualties; true unit positions where they differ from the COP; live traffic.
  - The trainee's **Traffic** view now shows feed congestion.
  - Hospitals appear on the map with their reported load, and are listed beside Notifications in Command & Control.
  - A "World & cascades" filter in the history log.
- **Barrow Street** is now emergent:
  - The neighbour is right: a chlorine store sits 70 m north-east.
  - Left unchecked, the fire reaches it at about minute 9, after the wind backs south-west, and the substation at about minute 21–24.
  - The demo response's first engine warns about the placards a minute before the store is reached.
  - St. James's loses two resus bays at minute 10.

## Communications realism (Phase 7)

Every message between the world and command now travels over a simulated network (`CommsSystem`, Order 95). What arrives can be late, broken, garbled or nothing at all. Messages that never arrive are truth events (`TransmissionLost`); what was heard is perceived (`CommsLogged`), and the COP keeps it as a comms log. `Simulation:CommsRealism = false` delivers everything at once and intact. Without a comms system, systems' messages go straight to the COP as before.

- **How each kind of message travels** (`CommsNet`):

  | Kind | Carries | Can go wrong |
  |---|---|---|
  | Voice radio | Crews' reports, read-backs, sitreps | Queues for airtime, collides, garbles, gets lost |
  | Mobile data | Status buttons and AVL | Needs coverage. Status messages are stored and sent when it returns; position fixes are dropped |
  | Agency chat | Other control rooms, utilities, hospitals, notification replies | A short delay |
  | 999 line | Calls from the public | Limited call-takers |

- **Channels** (`RadioPlan`, matching the IAP's ICS 205 names): FIRE CMD 1, FIRE TAC 2, FIRE TAC 3, AMB OPS 1, GARDA OPS and INTER-AGENCY 1, plus the 999 line and agency chat.
  - Crews work on their agency's channel. Command can move them to another one (e.g. a tactical channel), using the comms hub or `AssignChannelAsync`.
  - Other services' own radio systems are known but not monitored:
    - the AI agencies: FIRE NORTH, AMB SOUTH, GARDA RP;
    - mutual aid: KILDARE FIRE, NAS MIDLANDS, NATIONAL TEAMS.
- **Airtime and congestion:**
  - Each channel carries one transmission at a time. Airtime is 1.5 s to key up plus about 150 words a minute; patched channels share airtime.
  - Crews wait for a gap, emergency traffic first, and give up after 90 s.
  - A channel over 75 % busy garbles more, and raises a "congested" alert.
- **Push-to-talk and radio discipline:**
  - Command transmits by holding the button. Let go before the message is finished and the end is cut off. Keying up while a crew is talking doubles both ("doubled with Engine 4").
  - Crews answer only if they hear their call sign: "copy", a status ("send me your status"), or "say again".
  - Command's own transmissions get discipline notes: no call sign, over 30 words, codes instead of plain language, filler words.
- **Closed loop:**
  - Orders go out on the recipient's channel when it is free. Nobody reads back an order they never heard.
  - Read-backs come over the radio and can be unclear. Command confirms them, or marks them wrong, which repeats the order. An unclear read-back left alone raises an alert after a minute.
  - "Say again" asks a crew to repeat a broken message.
  - Orders to a crew on a channel command can't reach are refused.
- **Degraded communications** (truth):

  | Problem | Effect |
  |---|---|
  | Radio black spots (`RadioDeadZonePlaced`) | 70 % of transmissions lost, the rest garbled; crews retry. Mobile data is lost too. |
  | Handheld batteries | Start part-charged and run down at 25 %/h on scene. Below 15 % they break up, more so the flatter they get. A crew reports low batteries and swaps them 8 minutes later; a flat radio is silent. |
  | Mobile masts (`HazardSiteKind.CellTower`) | Run 45 minutes on batteries in a power cut, then fail (the Phase 6 cascade continues), or fail straight away if fire or floodwater reaches them. With a mast down, mobile 999 calls under it don't ring (30 % try a landline) and vehicle data there stops. |

- **Interoperability:**
  - A mutual-aid crew's transmissions are not heard. Its control room phones command to ask for a patch, and command sees an alert that it can't reach the crew.
  - A patch (`PatchChannelsAsync`) takes a technician 3 minutes to set up. AI agencies' control rooms relay their crews' reports by phone, a minute or two late.
- **Delays, missed calls, corrupted messages, the 999 line:**
  - 3 call-takers each take 60–120 s per call.
  - Callers hold 45–150 s, then hang up. A missed call shows for a callback, with an alert; the callback reaches them unless their phone has no signal.
  - Broken messages lose words; garbled ones keep only fragments. A garbled report never reaches the COP; a broken one does, with lower confidence.
- **Language and cultural barriers:**
  - 8 % of residents have little English. Their calls give fragments and a vaguer location until a language-line interpreter joins 2–4 minutes later with the full account.
  - Only 50 % of them follow an English-only evacuation order. Notifying a translation, interpreting or community liaison service brings it back to 85 %.
- **Scenario (Barrow Street):**
  - a radio black spot under the railway bridge on the north-west approach;
  - a mobile mast fed by the substation;
  - a Polish-speaking caller at minute 3½ whose mother can't walk;
  - the first engine working at the fire running low on batteries at minute 9.
  - Scripted calls now queue on the 999 line like everyone else's.
- **UI:**
  - The **comms hub** (right panel):
    - filters by service, 999 calls and agency chat;
    - live channel status: who is transmitting and airtime in the last 2 minutes;
    - a push-to-talk composer, with channel, call signs and a hold-to-talk button;
    - missed calls with **Call back**;
    - the feed of what was heard, with quality badges, discipline notes and **Say again**;
    - patches and channel assignment.
  - Orders show **Read-back correct** / **Wrong: repeat order**.
  - The instructor tab and map add what went unheard and why, crews in black spots or on flat batteries, masts down, and black spots.

## Human factors & crew management (Phase 8)

Every unit now carries a crew of named people (`CrewSystem`, Order 16), each with a role, qualifications and a place in their shift. How tired and stressed they really are is truth (`WorldCrew`). Command sees only the roster, the clock and what crews say (`Crew` on the COP), and crews tend to say it late. `Simulation:HumanFactors = false` turns all of this off; without a crew model, units behave as before.

- **Rosters** (`CrewRoster`, seeded from the call sign, so Engine 4 always has the same crew):
  - fire crews have an officer, a driver and firefighters; ambulance crews a paramedic and/or EMTs; Garda units Gardaí;
  - qualifications come from the unit's equipment: BA, Hazmat, USAR, Swiftwater, ALS (paramedic), BLS (EMT). A certificate can be **lapsed**, which means trained but not allowed to do it;
  - shifts: fire and Garda 10 h, ambulance 12 h. The demo roster puts Engine 4 and Engine 7 near the end of their day, and Ambulance 14 in the last hour of its twelve. Ambulance 21's paramedic registration has lapsed, and one of Hazmat 2's technicians is out of date.
- **Fatigue and stress** (truth, per person):

  | Fatigue per hour | |
  |---|---|
  | Working inside a burning building (BA) | 0.45 (the driver stays at the pump: 0.12) |
  | Fighting the fire from outside | 0.20 |
  | Other work at a scene (casualties, cordons) | 0.12 |
  | On duty otherwise | 0.02 |
  | In rehab | −0.80 |
  | Past the end of the shift | × 1.5 |

  - Stress comes from deaths nearby, a collapse, a Mayday (worse for the crew it happens to) and working in heat or fumes. Each new shock adds less to someone already shaken. It eases slowly, faster in rehab, and with peer support.
  - **Effects** ("decision speed, radio discipline, error rates"):
    - a tired crew turns out, sizes up and reads back up to about twice as slowly;
    - from fatigue 0.5 it garbles more transmissions and gets more read-backs wrong;
    - it puts out less fire, and less still when short-handed or working from outside.
  - Officers report "getting tired" at 0.6 (the truth turned tired at 0.5), "exhausted" at 0.85, "shaken" once after a traumatic event, and when they are 30 minutes past the end of their shift.
  - People are stood down when they are spent (fatigue 0.97), after an acute stress reaction (stress 0.85 for 5–15 minutes), or by the rehab medic.
- **Shifts, relief and handover:**
  - crews at their station change watch at the end of their shift;
  - committed crews carry on until command relieves them (`RequestReliefAsync`). A relief crew takes about 10–14 minutes to arrive, then the unit works at half pace while they hand over;
  - **Full briefing**: a 5-minute handover, nothing lost.
  - **Quick changeover**: 1.5 minutes, and the new crew may not know:
    - the channel it had been moved to (it comes up on its normal one, and command's orders on the old one aren't heard);
    - its orders (60 % each; it says "no tasking was handed over" when asked for its status);
    - a blocked road;
    - that the building was evacuated (it may go back inside).
- **Accountability and safety:**
  - **PAR** (`RequestParAsync`) goes out on the radio to every crew at the scene. Each crew that hears it counts heads and answers "5 of 5", or "4 of 5, missing Firefighter Byrne!". Crews that don't answer within 2 minutes raise a critical alert.
  - The **evacuation signal** (`SignalEvacuationAsync`) goes out on the radio, and air horns reach 65 % of the crews that miss it. Crews withdraw, report a PAR once out, and the incident goes defensive: later crews are told to work from outside.
  - **Emergency traffic** (`DeclareEmergencyTrafficAsync`): routine traffic on the channel holds, without giving up, until it is lifted. Only Maydays, evacuation PARs and rescue reports go out.
- **Mayday:**
  - A burning structure **collapses** 20–28 minutes after the fire starts, unless it is out first. The crew nearest the fire warns 4 minutes before ("roof's sagging…"), and again urgently at 1½ minutes.
  - Crews still inside within 60 m are caught: someone on the nearest crew for certain, others by chance. Firefighters working inside also get lost or fall now and then, more often when tired.
  - The firefighter calls "MAYDAY MAYDAY MAYDAY…" with where they are and how much air they have (8–18 minutes). The call goes over the radio at top priority and can be stepped on on a busy channel nobody has cleared; they call again until heard. If it never gets through, a PAR shows them missing, and command can declare the Mayday itself (`DeclareMaydayAsync`).
  - **Rescue team** (`DeployRescueTeamAsync`): a fire crew at the scene with two BA wearers goes in for everyone in trouble from that crew. It takes about 9 minutes for someone trapped and 5 for someone lost. It is faster with two USAR technicians for a collapse, and slower for a short or tired crew, or without emergency traffic.
  - Outcomes:
    - rescued in time: unhurt (stood down for a check) or burned (a P2 casualty);
    - too late: unconscious and out of air (P1);
    - not rescued: lost firefighters may find their own way out; otherwise another crew finds them long after their air has gone.
- **Skills and certification:**
  - Orders say what they need from their wording: "decontaminate" or "chlorine" needs two Hazmat technicians; "water rescue" two Swiftwater; "collapse" or "shoring" two USAR; "interior attack" or "search the building" a BA pair; "paramedic" or "ALS" one paramedic.
  - A crew without the people declines ("unable, we've nobody… qualified on board"), and the order shows as **Declined**.
  - The IAP flags assignments a crew isn't qualified for.
  - An ambulance whose paramedic has lapsed treats at BLS: casualties stay stable twice as long instead of three times.
- **Rehab and peer support:**
  - **Rehab** (`SendToRehabAsync`) lasts 20 minutes. The crew reports status On scene, recovers, sees the medic, and goes back to work.
  - **Peer support** (`ArrangePeerSupportAsync`): the team arrives in 15 minutes and waits until the crew is off the line. It brings stress down and heads off stress reactions.
  - Other services rotate their own crews.
- **Alerts** (from the COP):
  - Mayday;
  - no rescue team after a minute;
  - channel not cleared for emergency traffic;
  - PAR needed after a Mayday;
  - PAR due every 20 minutes while crews work;
  - PAR unanswered, or someone missing;
  - rehab due after 40 minutes on task;
  - shift ending, or past its end;
  - crew exhausted or shaken;
  - order declined;
  - someone stood down.
- **UI:**
  - The new **CREWS** tab in the right panel. Its header counts active Maydays.
  - **Safety:** Call PAR, Evacuation signal, and emergency traffic per channel.
  - **Mayday cards:** what was heard and a rescue-team picker.
  - **Accountability:** the latest PAR, with **Declare Mayday** for missing or silent crews.
  - **Crews:** for each crew, its shift (overtime in amber), time on task, its last condition report, qualifications, relief and peer-support status, and an expandable crew list. Buttons: Rehab, Relieve, Quick relief and Peer support.
  - The unit tab lists the crew, qualifications, shift and condition, and the resource board's Crew column shows people on duty and condition.
  - For the instructor: real fatigue and stress per crew, firefighters in distress (also marked on the map), and what handovers lost. The comparison shows crews more tired than they've admitted, and relief crews on a channel command doesn't know about.

## After-action review & metrics (Phase 9)

The review (`AarAnalyzer`, behind `IAarService.BuildReportAsync`) is worked out from the event stream alone, so the same session always gives the same report.
- Perceived events rebuild the COP as it stood at each moment, so each decision is judged on what was known just before it.
- Truth events rebuild what was really happening.

Open it with **📊 AAR** in the header. It opens in its own window, so the COP can replay beside it.

- **Decision timeline:**
  - Every command (C2 or IAP) is listed with what prompted it, how long it took, what the COP showed and what was really happening.
  - It is rated against the standard where there is one:

    | Decision | Prompt | Good / fair within |
    |---|---|---|
    | Open an incident | First report nearby | 2 / 4 min |
    | First dispatch to it | Incident opened | 1 / 3 min |
    | Decide a request | Request | 2 / 5 min |
    | Evacuation signal | First collapse warning | 1 / 2.5 min; poor if after the collapse |
    | Rescue team | Mayday | 1 / 3 min |
    | Emergency traffic, PAR | Mayday | 1 / 2 min, 2 / 5 min |
    | Hazard zones | First report of chemicals | 3 / 6 min |
    | Callback, read-back confirmation, patch | Missed call, unclear read-back, "can't raise you" | 1–2 / 2–5 min |

  - Orders to crews without the qualification are rated poor.
  - **COP then** replays the main window to that moment. The **Timeline** tab replays every action, message and dispatch (ground truth optional).
- **Performance metrics:**

  | Area | Measures |
  |---|---|
  | Dispatch | First report to incident opened, to first dispatch, to first unit on scene; how long the emergency went unreported (context) |
  | Resources | Utilisation and peak commitment; units never committed; time lost en route; longest spell without rehab; crews kept on past their shift |
  | Communications | Messages heard broken or garbled; "say again" on unclear ones; transmissions never heard (truth); unclear read-backs resolved; orders never read back; orders a crew wasn't qualified for; radio discipline; missed calls rung back |
  | Command | Alerts acknowledged; time to decide requests; IAP objectives achieved (or no plan); span of control exceeded |
  | Safety | Longest gap between PARs; Mayday to rescue team; emergency traffic for Maydays; firefighters in distress |
  | Outcome (truth) | Casualties and deaths (and deaths before treatment); largest fire |

  These roll up into a score and **Competent / Developing / Not yet competent**. Any poor key decision holds back "Competent".
- **Heatmaps and analytics:**
  - A commitment chart shows each unit's status over the session.
  - Two heatmaps cover 3.6 km around the incident (150 m cells), from the AVL feed: **resource commitment** (unit-minutes committed) and **delays en route** (minutes stopped or under 10 km/h). Each names its hotspots.
  - A delay table lists every hold-up of a minute or more and every re-route that pushed arrival back, with where and why.
- **Branching AAR:**
  - Each key decision point gets what you did, the recommended response, the alternative, and the likely difference, estimated from what really happened.
  - Decision points: opening the incident, first dispatch, evacuating the building, each Mayday and its emergency traffic, authority to evacuate, the chemical hazard, the hospital pre-alert.
  - Example: "It came down at 14:23:10 with crews inside: 1 firefighter trapped. Signalled at the recommended time, crews would have been out 1 min 45 s before the collapse."
  - **Same scenario, other commanders** (`BranchRunner`, Barrow Street only) re-runs the scenario headless, for as long as your session ran:
    - under the **recommended response** (`ExpertPolicy`: it sees only the COP and acts with human delays; it confirms the location before opening, sends a full first attendance, sets hazard zones, approves the evacuation, pre-alerts hospitals, sounds the evacuation signal on a warning, and so on);
    - with **no response**.
    - Each policy is run several times and averaged, because the world is random. The results sit beside your outcome, with what each commander did.
  - Each branch has its own event store, traffic and COP, and shares only the read-only road network and terrain.
- **Exports** (`AarExport`):
  - A self-contained **HTML report**: summary and verdict, debrief points, outcome comparison, key decisions, metrics, commitment chart and heatmaps (SVG), delays, decision timeline, key events, and a sign-off block with trainee, role, assessor, the assessor's verdict, comments and signature lines. It prints to PDF from any browser.
  - A **CSV** with every metric, branch point, outcome, decision, unit, delay and event in one sheet.

## Multi-agency & interoperability (Phase 10)

The other agencies are now joint partners with priorities and knowledge of their own (`InteragencySystem`, Order 19). Their truth (what they know and haven't said, how they feel about command, what they're waiting on, the EOC's staffing) is in `WorldInteragency`; the COP shows only what reaches command. Every command goes through `IC2Service` as events, so it replays and is judged in the AAR. `Simulation:MultiAgency = false` turns the friction model off.

- **Jurisdictions** (`JurisdictionDeclared`, schematic boundaries):
  - the design document's city / county / state / federal tiers, as city (Dublin City Council), county (Dún Laoghaire–Rathdown, Kildare), regional (the Dublin Major Emergency region, standing in for state) and national (the Government, standing in for federal);
  - two estates whose authority must agree measures inside them: **Grand Canal Dock basin** (Waterways Ireland), about 200 m north of the Barrow Street fire and downwind once the wind backs, and **Dublin Port**;
  - shown on the map ("Jurisdictions & estates"); clicking the map lists every jurisdiction at that spot.
- **Unified command** (`InviteToUnifiedCommandAsync`): shared command, not just coordination.
  - An agency with a stake joins about 3 minutes after being invited (1½ with a liaison officer), and says what it will push for. A stake means its units are at the scene, its estate is close or inside a zone, or it has a statutory role: investigators for an explosion, the regulator for a chemical site, the council for a High incident or a zone, public health for casualties or a plume. Agencies with no stake decline.
  - Once in, command can **task its units** at that incident; it **shares what it knows** within 30 seconds; zones in its estate need no separate agreement; it releases more of its reserve; and disputes with it settle faster.
  - Every member must **sign the IAP**: approval is refused until an objective covers each member's priority (e.g. "traffic" for Garda Roads Policing), and the approval names them all.
- **Liaison and PIO** (ICS positions that now do something):
  - The **liaison officer** rings round the agencies every 4 minutes and brings back what they know (not from a hostile agency), halves the wait for unified command, and halves negotiations.
  - The **PIO** answers media enquiries 2 minutes after they come in, with a holding statement that passes on the protective advice in force ("residents inside the evacuation zone should evacuate now…").
  - Command can issue its own statements (`IssuePublicStatementAsync`). A statement telling people what to do reaches people in a zone within 1–3 minutes instead of 2–10, and lifts compliance by 8 points.
- **EOC activation levels** (`SetEocLevelAsync`):

  | Level | Staff | Takes | Brings | Situation reports |
  |---|---|---|---|---|
  | Normal | duty officer on call | — | nothing | — |
  | Monitoring | 2 | 5 min | watching only | hourly |
  | Partial | 7 | 15 min | a declared major emergency: agencies release half their reserves, mutual-aid decisions 40 % sooner, disputes can be escalated, agencies' information relayed, a joint information centre | every 30 min |
  | Full | 12 | 30 min | all reserves released, council rest centres and buses, national coordination (and overtime) | every 20 min |

  - Seats fill one by one; standing down is immediate. Situation reports (`SubmitSitrepAsync`, "Draft from COP") are asked for at each level's interval; an overdue one raises an alert and slows escalated decisions.
  - The council's duty officer suggests standing the EOC up once an incident has been High for 10 minutes or a protective zone is declared.
- **Interagency friction:**
  - **Resource hoarding** (`RequestAgencyAssistanceAsync`): asked for units, an AI-run agency keeps a third of its fleet back for its own area. It keeps half back when relations are strained and gives nothing when hostile. Unified command halves the reserve, partial activation halves it again, and full activation releases everything. Released units come under command until stood down.
  - **Information silos** (`InformationHeld`, truth): what an agency knows reaches command through unified command (30 s), the liaison officer's rounds, the EOC (5 min after partial activation), a direct question (`RequestAgencyBriefingAsync`, 75 s), or when it gets round to volunteering it (20 minutes, longer when strained; a hostile agency volunteers only life-safety items, late). At Barrow Street:
    - Garda Roads Policing see two people trapped on a balcony (6½ min);
    - the Health and Safety Authority's register confirms 1,000 kg of hypochlorite next door (7½ min);
    - Waterways Ireland know 15 people live on boats in the basin, downwind (8 min);
    - the council's rest centre takes 120, not 200 (10 min);
    - the investigators have intelligence on the tenant (11 min);
    - public health hears of chlorine symptoms in Ringsend once a release has run 3 minutes.
  - **Competing priorities**: each agency states its priority (in the AGENCIES tab and when it joins), and pushes it in disputes and at IAP approval.
  - **Conflicting messages**: 6 minutes after an evacuation or shelter zone, if no joint information centre is working (a PIO, with unified command or the EOC) and no joint statement has gone out, the council (or the police) puts out its own contradictory line, and compliance drops by 30 % until a joint statement corrects it.
  - **The media**: enquiries arrive every 4–9 minutes by priority. Unanswered for 12 minutes, speculation appears on social media and worried residents ring 999.
- **Mutual aid agreements and authority disputes:**
  - Standing agreements (`MutualAidAgreementRegistered`) with Kildare Fire Service (up to 4 engines, ladders or tenders; not hazmat), the National Ambulance Service in the Midlands (up to 4; only once a major emergency is declared) and Garda Kildare (up to 4). Each has its own approver, decision time, lead time and terms.
  - Mutual-aid requests follow them: capped at what is left of the agreement, sometimes one fewer while the provider keeps cover (unless the EOC is at full activation), and refused outside the terms. A unit type no agreement covers goes to the National Directorate, which releases it only for a Critical incident (4 minutes to decide, 30 to arrive).
  - **Disputes** (`ResolveDisputeAsync`): Concede, Negotiate (4 min; less with a liaison officer or in unified command; more when relations are poor), Assert (immediate, relations get worse) or Escalate (the EOC decides in 5 min; needs partial activation).

    | Dispute | Raised when | While open |
    |---|---|---|
    | Jurisdiction | A zone reaches into an estate whose authority isn't in unified command and hasn't agreed | The zone stops at the boundary: nobody there is warned. Conceding leaves the authority to warn its own people 8 min later |
    | Mutual-aid terms | A mutual-aid crew is ordered to do something the agreement excludes (e.g. hazmat) | The order is declined. Negotiating or asserting gets it done; conceding or the EOC withdraws it |
    | Lead agency | Barrow Street, 12 min in, with the fire still burning: the Garda investigators claim the lead for a possible crime scene | Conceding hands them the lead at once and firefighting goes defensive. Negotiating gives a staged handover once the fire is out |

  - Overruled agencies remember: relations go Good → Strained → Hostile (conceding improves them), with the effects above.
  - An objection unanswered for 3 minutes, and media enquiries waiting 8 minutes, come back as alerts.
- **AAR:**
  - A **Multi-agency** section: liaison officer and PIO appointed; unified command; how long other agencies' information took to reach command (and how); authority disputes and how they were settled; EOC activation against when the incident became a major emergency; situation reports on time; media enquiries answered; conflicting public messages; units released by other agencies.
  - Decisions are rated: liaison, PIO and the first invitation within 10 min of opening; the EOC within 15 min of a major emergency; disputes answered within 3 min (asserting is at best fair; conceding the lead while the fire burns is poor); sitreps by their due time; warnings within 5 min of a zone.
  - New branch points: **information held by other agencies** (when the most urgent item reached you, against unified command) and **who leads**.
  - The recommended response (`ExpertPolicy`) appoints a liaison officer and PIO, invites agencies with a stake, stands the EOC up for a chemical fire, negotiates, warns the public and sends sitreps.
- **UI:**
  - The new **AGENCIES** tab in the right panel. Its header counts open disputes.
  - **Dispute cards** with Concede, Negotiate, Assert and Escalate, each with a tooltip saying what it is likely to do.
  - **Command** for the selected incident: single or unified, the lead agency, the jurisdictions there, members and what they want, and Invite.
  - **EOC**: level and seats, what each level brings, the situation report due, and Draft from COP / Send sitrep.
  - **Public information**: PIO and joint-information-centre status, statements (contradictions in red) and media enquiries.
  - **Other agencies**: type, jurisdiction, units free, priority, standing, unified command and assistance status, and Ask for units / Ask what they know.
  - **Mutual-aid agreements**.
  - For the instructor, the truth comparison lists everything agencies know and haven't shared.

## Server & clients (Phase 11, M1–M7)

The engine, event store and live COP can now run once, on a **simulation server**, with any number of WPF clients connected over SignalR. Standalone (everything in one process, as before) is still the default.

**How it fits together:**
- **Event feed** (`EventFeed`): a client asks for the stream after the last sequence it applied. The server queues live events for it at once, sends the history from the store, then drains the queue, skipping what the history covered. So nothing is missed or sent twice, and one pump per connection keeps the stream in order.
- **Visibility is decided on the server:** a connection that isn't an instructor never has a truth event queued, read or sent. Instructors join their session's `truth` group.
- **The client** (`SimulationConnection`) keeps a `MirrorEventStore`: the server's events with the server's sequence numbers. The COP (`PerceivedState`), AVL trails, the timeline, the comms log and replay run on it unchanged. When the link drops it reconnects (0, 2, 5, then every 10 s, without a limit) and resumes from its last sequence.
- **Commands:** `RemoteProxy` implements `IC2Service` and `IIapService` for every interface method, one for one, with no hand-written forwarding. On the server, `CommandDispatcher` runs the call on the real service, which validates it against the server's live COP as in standalone. A successful command returns only once the client's own COP shows its result.
  - `ISimulationControl`: start, pause and speed go to the server; the clock comes from its pushes and moves on locally between them.
  - `IAarService`: replay and the timeline run on the client's copy (a trainee's has no truth in it). The full review and branch re-runs run on the server, and only the instructor may ask for them until roles arrive (M3).
  - `ICommsService` is the usual comms service over the local COP, sending through the C2 proxy.
- **Pushes** (`Broadcaster`): the clock to everyone every 0.5 s; the reported traffic feed when it refreshes; and to the `truth` group only, the `WorldSnapshot` and live traffic about once a second.
- **Wire format** (`WireJson`): `EventJson` plus value tuples and NTS geometries (as WKT).

### Sessions, identity, attribution, concurrency (M2)

- **Lifecycle:** create (pick a scenario) → lobby → join (code, name, role) → the instructor starts → end.
  - An instructor opens a session with the server's PIN, picking a scenario (`ScenarioCatalog`: Barrow Street, or free play). The server builds that session its own engine, world, COP and services (`SessionRuntime`), sharing only the event store (keyed by session) and the GIS database, so several sessions can run at once.
  - The instructor gets a 6-character **join code** (no look-alike characters). It stops letting anyone new in when the session ends or after `Server:JoinCodeLifetime` (12 h).
  - In the **lobby** the clock is stopped and C2/IAP commands are refused ("wait for the instructor"); people can look around. Only the instructor starts the exercise (the Start button, or the play button) and controls the clock.
  - **Ending** stops the clock for good and refuses commands and new joins. The record stays, and from then on everyone can open the review.
  - Events: `SessionOpened`, `ParticipantJoined`, `ParticipantLeft`, `RoleAssigned`, `RoleHandedOver`, `SessionEnded` (perceived, so they're in everyone's stream and the AAR).
- **Identity** (decision D3): a display name and a role. There are no accounts, and an instructor also needs the PIN.
  - The IC, the command staff and the section chiefs are **one person at a time**; dispatchers, agency chiefs, instructors and observers can be several.
  - The instructor can **assign** roles. A role holder can **hand over** to someone present and take another role. The instructor role can only be joined with the PIN, never given.
  - Each participant gets a **reconnect token** (kept in the client's memory only): after a dropped link the client takes its place back as the same person.
  - Away longer than `Server:DropGrace` (60 s), they count as gone (`ParticipantLeft`, which frees an exclusive role). Coming back later is a rejoin; if their position was filled meanwhile, they come back as an observer.
- **Attribution:** every event carries `ActorId` and `ActorRole` on the envelope (with an `events` table migration, `EventActors`).
  - The server sets an ambient `ICommandContext` (AsyncLocal) from the connection's identity around each call. The engine stamps whoever it names on the events the call records. Nothing a client sends says who it is.
  - The engine's own work, scenarios, AI parties and standalone sessions have no actor. Starting the clock carries the instructor.
- **Concurrency:** each session's **`CommandGate`** runs C2 and IAP commands one at a time, so each is checked against a COP that already holds every earlier command's result. Two players dispatching the same unit at once: one succeeds, the other is told it isn't available.
- **AAR:**
  - Decisions and timeline entries name who gave the command and in which role.
  - A **Participants** table shows roles over time, time in the session, commands, how their own decisions rated, mean response time, score, and what most of their commands were about.
  - The decisions and timeline tabs filter by participant (`AarReport.DecisionsOf` / `TimelineOf`). The HTML and CSV exports include both.
- **Clients:**
  - The **join dialog** (server, name; join with code and role, or open a session as instructor) appears unless the command line says it all.
  - The **SESSION** tab (client mode only) shows the session, code and state, and who is in it. The instructor gets Start, End and Assign; anyone can hand their own role over.
  - The header shows the code and who you are. A refused command shows the server's reason, as standalone failures do.

**Running it:**

```powershell
# Server: PostgreSQL required (decision D4); the instructor PIN lives in its configuration, not the clients'.
dotnet run --project "Emergency Response Simulator.Server" -- --Server:InstructorPin=2468
# Development without a database:
dotnet run --project "Emergency Response Simulator.Server" -- --ConnectionStrings:Ers= --Server:AllowInMemoryStore=true --Server:InstructorPin=2468

# The instructor opens a session (or leave the options off and use the join dialog)
"Emergency Response Simulator.exe" --Mode=Client --Server=http://training-pc:5180 --Create=barrow-street --Name=Ines --Pin=2468 --Title="Blue watch"
# Trainees join with the code the instructor reads out
"Emergency Response Simulator.exe" --Mode=Client --Server=http://training-pc:5180 --Code=8X6UF9 --Name=Alex --Role=IncidentCommander
```

- The server listens on port 5180 (`Urls` in its `appsettings.json`) and lists its sessions, codes and participants at `/`.
- A client with a GIS database configured still loads the static map layers and road network from it; its events always come from the server.
- Without a database, routing falls back to straight lines on both sides.

### Roles, permissions, workspaces (M3)

- **Permission matrix** (`Core/Model/Permissions.cs`, `PermissionMatrix`): every C2 and IAP command belongs to the roles whose function it is. Examples:
  - **Dispatchers and Operations:** tasking units.
  - **Logistics (and Finance):** ordering resources and the comms plan.
  - **Safety and Operations:** zones and responder safety.
  - **Planning:** the IAP; the IC approves it and the IC or Operations briefs it.
  - **The Liaison Officer:** other agencies.
  - **The PIO:** public statements.

  A few commands are anyone's (the radio, acknowledging alerts). Observers give none. The instructor may give any, and is recorded doing so. A command not in the matrix is refused, so a new one has to be placed in it.
- **Unfilled roles fall to the IC** (decision D5): the IC may give any command whose function nobody present holds. With no PIO, the IC speaks to the public; once a PIO joins, only the PIO does. If the PIO leaves, the function returns to the IC.
- **Enforcement:** the server's **`CommandAuthorizer`** checks every call before it reaches a service.
  - It uses the role the server holds for the connection and the roles of the people present. Nothing the client sends counts.
  - A refusal comes back as a failed command naming who the function belongs to, e.g. "Public information is for the Public Information Officer, not the Operations Section Chief."
  - It also holds the M2 stage rules: the lobby, the clock, and the live review.
- **Clients mirror it** (`ViewModels/Permissions.cs`): the role's own `CanExecute` disables every command button the role can't use.
  - It reads the same matrix, the role the event stream says you hold now, and who is present, so buttons change as roles move.
  - The SESSION tab lists your functions, and for the IC, which unfilled ones are theirs for now.
  - Standalone, one person holds every role.
- **ICS staffing:** `AssignIcsPositionAsync` takes an optional participant (`userId`).
  - Staffing a position with someone in the session gives them that role, recorded as a `RoleAssigned` by the IC, and the function stops falling to the IC. A blank name means theirs.
  - A position someone else holds has to be handed over instead.
  - The ICS panel has a participant picker per position in client mode.
- **Role handover** follows the Phase 8 crew handover model (`Crews:BriefedHandover` / `QuickHandover`):
  - **Holding on:** once the exercise runs, the outgoing holder keeps the role for 5 minutes of simulation time with a full briefing, or 1.5 with a quick changeover (`RoleHandoverBegun`). A paused clock pauses it too.
  - **Handing over:** `RoleHandedOver` then records the role's open business from the COP (live incidents, open orders and read-backs, pending approvals and IAPs, requests, disputes, Maydays, media enquiries): what the briefing covered, or what a quick changeover never passed on.
  - **In the review:** the AAR lists each role handover as a strength or a weakness.
  - **Calling off:** a handover is called off if either person leaves or their roles change (`RoleHandoverCancelled`).
  - **In the lobby:** there is nothing to brief, so a handover happens at once.
- **Workspaces** (`ViewModels/RoleWorkspaces.cs`): a default layout per role over the same COP.
  - Each layout sets the right-panel tabs the role works from, the one in front, the bottom board, and the map layers it cares about. For example:
    - **Safety:** crews, hazard zones and wind.
    - **Liaison:** agencies and jurisdictions.
    - **PIO:** public information and evacuation areas.
  - The client switches to the new role's layout when the role changes; the header's picker offers every layout, including the full one.
  - Workspaces only arrange; permissions are separate.

### Instructor console (M4)

- **Its own window:**
  - **Opening it:** the 🎛 Console button opens it for the instructor (or anyone, standalone); trainees don't get it.
  - **As a separate client:** `--Console=true` starts the console on its own, e.g. on a second machine joined as `--Role=Instructor`; there can be several instructors.
  - **The contract:** actions go through `IInstructorService` (`Core/Contracts`). The server lets only the instructor call it and runs it through the session's command gate.
- **Injects:** the scenario script live, with each inject's time and state (in 2:30, waiting for its condition, fired 14:05, held, skipped).
  - **Fire now:** early, whatever its time or condition.
  - **Hold:** it waits past its time until released.
  - **Release:** if its time has passed, it goes off at the next tick.
  - **Skip:** it never goes off.
  - **Ad hoc from the template library** (`InjectTemplates`): wind shift (reported or not), incident escalates, secondary incident, 999 call, unit breaks down, radio fails, road blocked, hospital under pressure, social media rumour. Each takes parameters with defaults and places itself relative to the largest live incident.
  - **What's recorded:** every inject that goes off is recorded as `InjectFired`: on time from the script, early, or ad hoc. Holds and skips are recorded as `InjectHeld` / `InjectReleased` / `InjectSkipped`.
- **Player monitor** (`PlayerMonitor`), from the event stream and the COP:
  - each participant's commands, recent ones, and the time since their last;
  - **idle** after 5 sim-minutes without a command (observers never count as idle);
  - a live activity feed of who did what;
  - unacknowledged alerts with their age;
  - radio load per channel (on air, emergency traffic);
  - **bookmarks** for the debrief (`InstructorBookmarked`, optionally about a participant).
- **Role-play:** speak as anyone, recorded as `InstructorRolePlay`. It reaches command through the usual comms, with their delays, black spots and busy channels:
  - **a crew on a radio channel:** a voice transmission and a field report;
  - **another agency, utility or hospital:** a chat message;
  - **a 999 caller:** a call in the queue;
  - **a journalist ringing command:** a media enquiry the PIO must answer;
  - **an official asking for a decision:** an approval request.
- **Take over and hand back AI roles** (the role-player registry). Taken over, a role's decisions wait in the console for the instructor, who answers in the role's name (`AiRoleTakenOver` / `AiRoleHandedBack`, held in `WorldState.HumanRoles`):
  - **Regional Duty Officer:** mutual aid, including requests under the agreements.
  - **National Directorate:** specialist teams, and mutual aid outside the agreements.
  - **Outside organisations:** replies to notifications.
  - **The media:** journalists ring only when the instructor plays one.
  - **Each AI-run agency's control room:** its own jobs and initiative, and its answers to invitations to unified command and requests for units.

  The systems that played them (`CommandResponseSystem`, `InteragencySystem`, `AgencyAiSystem`) carry on with everything else. A hand-made decision goes down the same path as an automatic one: approved mutual aid sets off with the usual lead time and counts against its agreement. Hand a role back and the simulation answers whatever is still waiting.
- **Hidden from the trainees:** everything the instructor does is ground truth stamped with the instructor, so trainees see only its effect in the world: the call, the message, the wind change once reported.
- **In the review:**
  - An **Instructor** table lists injects fired early, held, skipped or made ad hoc, role-play, roles taken over and handed back, decisions made in a role, and bookmarks. Injects that went off on time are counted rather than listed.
  - In the timeline, whatever the role-play produced in the world (the message, call, enquiry, report, approval request or decision) shows as "Instructor role-play as …". The trainee's own commands are never marked.
  - The HTML and CSV exports include both.

### AI role players: filling the gaps (M5)

- **Political pressure** (`PoliticalPressureSystem`, `Simulation:PoliticalPressure`, on by default):
  - **Who rings:** the local TD, the council's chief executive and the minister's office ring command about a serious incident (`PoliticalCallReceived`), asking for the Incident Commander, or the PIO once that position is staffed.
  - **How often:** by what command has reported. First call after about 4 minutes for a Critical incident, 7 for High, 12 for Medium; then every 10 / 15 / 25 minutes, sooner with five or more casualties or a protective action (evacuation or shelter) in force. Low-priority incidents don't make the news.
  - **Calling back:** the IC or PIO calls back from the AGENCIES tab (`AnswerPoliticalCallAsync`). The pressure eases a step and the next call comes a full interval later.
  - **Ignored:** a call not returned within 5 minutes moves the pressure up (`PoliticalFallout`, ground truth). The council chief executive rings, then the minister's office, each knowing the last call wasn't returned. Ignore the minister's office and the minister goes public: an RTÉ report ("not been briefed") and an alert, then quiet for 20 minutes.
  - **Played by the instructor:** the instructor can take the politicians over in the console. Then nobody rings unless the instructor plays one (role-play kind "Politician"; the office is read from who they say they are), and silence costs nothing the instructor doesn't choose.
  - **In the review:** returning a call is a decision timed from the call (good within 3 minutes, fair within 5). The findings say how many calls were returned and how quickly, and what each ignored call cost. Instructor-played calls are marked as role-play.
- **Agencies run by participants** (the stretch goal). "Who runs this agency's control room?" is now the registry's answer, not the bare flag: `Agency.Controller` (Command, Simulation or Participant) alongside `AiControlled`, which still means "independent of command". A participant runs it via `AgencyControlChanged`, recorded in the stream so everyone sees who runs which agency:
  - **Giving it:** the instructor gives an agency's control room to a participant who joined as an **agency chief** (console → AI ROLES → Give). One agency per chief; the instructor can't take over one a participant runs.
  - **Their units:** the chief tasks the agency's own units (dispatch, stand down, reassign, status, orders) and nobody else's; the server refuses any other unit. Command still can't task them outside unified command. The resource board marks them "yours" for the chief and "run by …" for everyone else.
  - **Answering for the agency:** command's requests for units and invitations to unified command wait for the chief, who answers in the AGENCIES tab's YOUR AGENCY section (`AnswerAgencyRequestAsync`). The answer is carried out exactly as the simulation's or the instructor's would be.
  - **The simulation steps back:** `AgencyAiSystem` stops the agency's routine jobs and initiative, and `InteragencySystem` stops answering for it.
  - **Getting it back:** the instructor can hand the agency back, and it returns to the simulation automatically if the chief leaves the session.
  - **The permission matrix:** agency chiefs can now task units, checked by C2 against the agency they run. An agency chief's functions never fall to the IC: they belong to another agency.

### Observer mode (M6)

- **Read-only:**
  - **On the server:** an observer is refused every command, whatever their client sends: C2, IAP, the clock, the instructor console, and handing over a role ("Observers watch: they don't give commands.").
  - **On the client:** the client is read-only too. `CopView.IsReadOnly` (and `ReadOnlyReason`) generalises `IsReplay`: a replay of the past, or an observing client. Every command button is disabled, and the header says "OBSERVING · read-only".
- **What they see:** the COP (perceived only), or with ground truth as well when the instructor grants it per observer from the SESSION tab ("Let them see truth" / "Stop", `ObserverTruthChanged`).
  - **Granting truth:** the server moves their connections into the truth group (world snapshots, live traffic) and retunes their event stream. The truth events they missed are backfilled into their mirror in sequence order, so their history holds the whole stream with no gaps, and new ones follow.
  - **Taking it away:** stops new truth from then on.
  - **Only while observing:** a grant lasts only while they observe; given a playing role, they lose it.
- **Follow participant X:** the observer's view switches to X's role workspace (and again if X's role changes), and goes to the incident or unit of each command X gives, with a running "what they just did" line. The observer's default workspace starts on the ASSESS tab.
- **Live assessment** (`IAssessmentService`, for observers and the instructor; `AssessmentNoteRecorded`):
  - **Notes and ratings:** time-stamped notes and ratings on the rubric (Competent / Developing / Not yet competent), tagged to a player and, by default, to that player's latest action (its event sequence). A rating alone, or a note alone, will do.
  - **Who sees them:** notes are ground truth, so trainees never receive them. Every assessor is sent every note, even an observer without truth, so observers and the instructor mark together.
  - **Who is assessed:** players only. Nobody assesses themselves, or an observer or the instructor.
- **In the review:**
  - **Timeline:** notes appear on it (category ASSESSMENT), and in the participant's own timeline when filtered to them. An Observer notes table shows who, the rating, the note, what it was about (the tagged event, described) and who noted it.
  - **Per-participant scoring:** how their own decisions rated (0–100) and their observer ratings (Competent 100, Developing 50, Not yet competent 0, averaged) combine evenly. Either stands alone when the other is missing. The result goes back onto the rubric: 75 and over is competent, 40 and over developing.
  - **Exports:** the HTML and CSV exports include both.

### Voice communications (M7)

- **Client audio:**
  - **Devices:** NAudio WASAPI, shared mode, communications category, captured as 48 kHz mono and sent as Opus in 20 ms frames (Concentus, `Simulation/Voice`).
  - **Push to talk:** a real key, `` ` `` (backtick) by default (`Voice:PushToTalkKey` on the client), held anywhere in the main window, or the "Hold to talk" button in the COMMS HUB's VOICE panel.
  - **The microphone** is open only while push-to-talk is held; letting go, or the window losing focus, ends the transmission.
  - **Listening:** incoming transmissions are mixed to the speakers; the panel shows who is on air on each of your channels.
- **The server's voice router** (`Server/Voice/VoiceRouter`) works through `IVoiceTransport`, implemented over the simulation hub's SignalR connections; a UDP or WebRTC media path could replace it without touching the router.
  - **Channel membership follows the role (and so its workspace)** (`VoiceChannels`):
    - **IC:** fire command and inter-agency.
    - **Operations:** fire command and tactical 2 and 3.
    - **Safety:** tactical 2 and fire command.
    - **Dispatchers:** fire command, ambulance and Garda.
    - **Liaison, PIO and Finance:** inter-agency.
    - **Agency chiefs:** inter-agency plus the ambulance and Garda channels.
    - **The instructor:** every channel.
    - **Observers:** listen on every channel and never talk.
  - **Half-duplex:** one talker per channel. Keying up on a busy channel is refused with who has it ("FIRE TAC 2 is busy: Bea is talking."). Talkers don't hear themselves. A transmission is cut off after `Voice:MaxTransmission` (60 s), and a dropped link ends it.
  - **The Phase 7 model, applied to audio:** every transmission is band-limited to a handset's 300–3,000 Hz with a little hiss. Keying up over simulated traffic on the channel doubles with it, so the audio comes out garbled for everyone, and the crews hear it garbled too.
- **Talking to simulated parties:**
  - **Transcription:** each transmission's clean audio is transcribed by Whisper (Whisper.net on the CPU, `ggml-base.en.bin`, `Voice:WhisperModel`). The prompt is biased with the session's call signs, and "engine four" is normalised to "Engine 4", only after a call-sign word, so "no one" stays as said.
  - **Into the radio traffic:** the transcript enters `TransmitAsync` as the speaker's transmission (the event, stamped with them), so a crew that hears its call sign answers. An instruction addressed to a crew ("Engine 4, Control, proceed to Barrow Street, over") is now read back ("Control, Engine 4, received: proceed to Barrow Street, over."), typed or spoken.
  - **Without a model:** voice still works between players; it just isn't transcribed.
  - **The engine:** Whisper.net rather than faster-whisper (your choice), so there's no Python runtime. It sits behind `ISpeechRecognizer`, so another engine can replace it.
- **Recording:**
  - **Each transmission** is kept as an Opus clip as it sounded on the radio (`Voice:ClipDirectory`, `%LOCALAPPDATA%\ErsVoice\clips\{session}\{clip}.erso`), with `RadioAudioRecorded` (channel, speaker, length, quality, transcript, and the radio call it became).
  - **Retention:** recordings older than `Voice:RetentionDays` (30) are deleted at start-up and daily.
  - **Consent at join:** the join dialog explains the recording and the transcription and asks for consent (`--VoiceConsent=true` on the command line). It is recorded as `VoiceConsentRecorded` with the retention period. Without it a participant listens but can't talk.
- **AAR:** every recorded transmission is on the timeline ("Voice: Bea on FIRE CMD 1 (3.2 s): …") with a ▶ button that plays the clip through the headset, fetched from the server.

**Exit criteria, as tested** (`VoiceTests`):
- **Two players on FIRE TAC 2, half-duplex:** while Bea has the channel, Sam is refused. Sam and an observer hear her (through the radio effect); the IC, who isn't on TAC 2, doesn't, and Bea doesn't hear herself. Then Sam has the channel and Bea hears him.
- **A spoken order to Engine 4 is read back.**
- **Every transmission plays back from the review:** each clip is fetched through a client and decodes to its full length.
- **Real speech:** a test also speaks a call with Windows' speech synthesiser, transcribes it with the real Whisper model when it's installed, and checks that Engine 4 answers.

The base.en model is about 148 MB (from Hugging Face, `ggerganov/whisper.cpp`). Put it at `%LOCALAPPDATA%\ErsVoice\models\ggml-base.en.bin`, or point `Voice:WhisperModel` at it.

## Multiplayer completion (Phase 12)

Phase 12 completes and hardens the Phase 11 multiplayer trainer. For running exercises, see the **[Instructor Guide](docs/Instructor-Guide.md)** and the **[Observer Guide](docs/Observer-Guide.md)**.

### Roles

**What a person plays is theirs:**
- Ground truth now knows who plays which session role (`WorldState.Players`, from joins, assignments and handovers).
- `WorldState.Staffed(position)` counts an ICS position as staffed when someone is named on the chart, or when a participant plays the role.
- The simulation never acts for a role a person plays. A simulated PIO on the chart still answers the press with holding statements, but a PIO played by a participant answers for themselves.
- Politicians ask for a participant PIO, and the liaison and joint-information-centre effects count a participant Liaison or PIO, even before the IC names them on the chart.

**The desk** (`Simulation/State/RoleDesk`, the **DESK** tab) lists what waits for a role to act on now, most urgent and oldest first:
- Maydays to send a rescue team to
- approvals and IAPs to decide
- read-backs to confirm (garbled ones flagged)
- incidents nobody is assigned to
- missed 999 calls
- journalists and politicians to answer
- disputes
- the EOC situation report falling due
- for an agency chief, their agency's requests and invitations

Each item belongs to a function the role holds now, by the permission matrix. So the IC's desk also holds whatever falls to them from unfilled roles, and moves on when someone takes the role. Choosing an item goes to its incident. Standalone, one person's desk holds everything; an observer sees the desk of whoever they follow.

### Instructor console

- **The player monitor** shows each player's desk (waiting, overdue, longest waiting). A player is **overloaded** at 6 items or 3 overdue (`PlayerMonitor.OverloadedAt` / `OverdueAt`), and the tab header counts them alongside the idle.
- **Exercise control** (`IInstructorService.SendExerciseMessageAsync`, `ExerciseControlMessage`): the instructor speaks to everyone, or to one participant, outside the world.
  - It shows as an amber banner until dismissed. Nobody in the simulation hears it.
  - It is a perceived session event, so it reaches the players' streams, and the review's timeline lists it ("Exercise control to Bea: …").
  - Only the instructor may send one.
- **Public confidence** appears among the world facts on the instructor's board (`WorldSnapshot.PublicConfidence`).

### AI role players: the media and the politicians in depth

**Media coverage and public confidence** (`MediaCoverageSystem`, `Simulation:MediaCoverage`, on by default):
- Ground truth tracks how far the public trusts the response (0–100, from 60), as `PublicConfidenceChanged` with its reason.
- **Raised by:**
  - command's statements, more for clear protective advice and less for a flood of them
  - a minister's visit to a safe scene
- **Lowered by:**
  - a journalist left waiting 10 minutes, who runs the story anyway
  - an evacuation or shelter zone with no public advice from command within 10 minutes
  - another agency contradicting the official line
  - a minister going public unbriefed
  - a VIP visit while the hazard is live
- **Coverage:** every 12 minutes while a newsworthy incident runs, and at once when the tone turns, an outlet reports supportive, mixed or critical coverage. It reaches command as a media report ("Coverage: …").
- **Taken over by the instructor:** with the media taken over, no coverage runs.

**Requests to visit the scene** (`PoliticalPressureSystem`):
- **When:** after 25 minutes of a Critical incident (40 for High), the local TD, or the minister once the pressure has reached her office, asks to visit. It arrives as an approval on the IC's desk.
- **The decision:** recorded as `PoliticalVisitDecided`, with whether the hazard was live: a fire, release or flood within 800 m not over, or a protective action in force.
- **Approved:** the party arrives at the cordon 15 minutes later. If the hazard is still live, that raises a Safety alert.
- **Played by the instructor:** while the instructor plays the politicians, no visit requests come in.

**In the review:**
- where public confidence ended and its lowest point
- the biggest drops (8 or more), each with its cause
- visit decisions: approving a visit while the hazard is live is a weakness; putting it off, or hosting it once safe, is a strength

### Voice

- **Patches:** channels patched together (Phase 7) are **one talk group**. There is one talker across them, and everyone on any of them hears it. Keying up elsewhere in the group is refused ("AMB OPS 1 is patched to FIRE TAC 3, where Lou is talking.").
- **Emergency traffic:** while it is declared on a channel (or one patched to it), only those working the emergency (the IC, Operations, Safety, dispatchers and the instructor) can key up (`VoiceChannels.MayTalkUnderEmergencyTraffic`).

### Observer assessment

- **Competency areas:** notes can name a competency area (`Competency`):
  - command & control
  - situational awareness
  - decision making
  - communication
  - teamwork & liaison
  - safety

  `IAssessmentService.RecordNoteAsync` takes it as an optional `area`, and `AssessmentNoteRecorded.Area` records it. The ASSESS tab keeps the chosen area between notes.
- **In the review:** a **Competencies** table (`AarReport.Competencies`) gives each player's rated notes per area, averaged and put back on the rubric. The notes table, the timeline and the HTML and CSV exports show the area too.

### Workspaces, finalised

- Every role's workspace now includes its **DESK** and the SESSION tab.
- The **IC** starts at the desk: decisions waiting, and whatever falls to them.
- A test checks every role has a workspace whose focus and board are among its panels, a desk, voice channels, and (for playing roles) functions of its own.

**Tests:** `MultiplayerCompletionTests`.

## Plume & hazard models (Phase 13)

The Python hazard-model service (`hazard-models/`) now holds a real dispersion model, and the simulator uses it twice: for the **real cloud** (ground truth) and for **command's projections** (what the trainees work from). The two can disagree, because the projection is run on reported information.

### The dispersion model (`plume_service/dispersion.py`)

A Gaussian plume for a continuous release, with what changes the answer:

| Effect | How |
|---|---|
| Atmospheric stability | Pasquill–Gifford classes A–F, with the Briggs (1973) sigma curves. A still, clear night (F) carries a cloud more than twice as far as a sunny afternoon (A). |
| Terrain | Roughness length; 0.4 m and above (built-up) uses the urban curves, which mix a plume out sooner than open country. |
| Plume rise | Briggs buoyant rise for a release lighter than the air around it (hot smoke, ammonia), capped by stable air. The worst ground-level concentration is then downwind of the source, not at it. |
| Dense gas | Heavier than air (chlorine, sulphur dioxide): no rise, a shallower and wider cloud that stays on the ground. An approximation of a proper slumping model (DEGADIS, SLAB). |
| Depletion and decay | Deposition thins the cloud with distance; radiological and biological material lose strength with time in the air. |
| A travelling cloud | It reaches only as far as the wind has carried it (`elapsedMinutes`). Once the release stops (or the inventory runs out), the back of the cloud moves off too and it drifts away as a band, leaving clear air at the source. |

**Inputs:** substance, release rate, duration, quantity, release temperature and pressure, release height, weather (wind, temperature, humidity, stability, pressure) and terrain. Everything beyond the original five fields is optional.

**Output:** low, moderate and high concentration zones as polygons. Each zone carries its threshold (and, for radioactive material, the dose rate), its area, how far it reaches, and when the cloud arrives.

**Substances** (`plume_service/substances.py`, `GET /v1/substances`): all five material classes in the deliverable, with AEGL-style 60-minute thresholds.

| Class | Substances |
|---|---|
| Chemical | Chlorine, ammonia, sulphur dioxide, hydrogen sulphide, and an unidentified industrial chemical |
| Toxic gas | Unidentified toxic gas |
| Smoke | Smoke |
| Biological | Biological aerosol (thresholds as rough infectious-dose bands) |
| Radiological | Radioactive material (thresholds as 0.1 / 1 / 10 mSv/h) |

Any other name is treated as a toxic gas with chlorine-like thresholds, keeping the name given. The thresholds are teaching values, not a basis for real decisions. The C# fallback (`Hazards/Substance`) carries the same library: keep the two in step.

**Tests:** 22 pytest tests check the contract and the physics: nesting, travelling downwind, stability, wind, terrain, dense gas against buoyant release, dose rates, decay, the detached cloud and the inventory.

### The real cloud (`PlumeDispersionSystem`)

- **Asking the model:** for every live release, the engine asks the service where the cloud is now, from the **true** weather and the **true** release. The stability class is estimated from wind, time of day, season and humidity (`AtmosphericStability`).
- **Recorded as truth:** the answer is recorded as `PlumePredicted` (ground truth), and the plume model adopts its zones. Crew exposure, the footprint, the instructor's map and the casualty systems all use the service's cloud.
- **Dynamic updates:** it asks again when the wind turns 15° or changes by 1.5 m/s, when the release rate is revised or the release stops, and otherwise every 2 minutes so the leading edge keeps moving. A stopped release is still modelled, so the cloud drifts away.
- **Never stalls:** the service is called off the engine thread, so a slow or missing service never holds the clock up. Without it, the in-engine Gaussian plume carries on and is tried again.
- **On the instructor's board:** the board says which model is driving each cloud.
- **Replay:** the predictions are events, so a replay rebuilds the same cloud without the service. Branch re-runs use the in-engine model.
- **Switching it off:** `Simulation:DispersionService = false` forces the in-engine model.

### Command's projection, and what it covers

- **Running it:** `IC2Service.PredictPlumeAsync` runs the model on what command knows: the **reported** wind and command's estimate of the release.
  - It is the **Hazard prediction** function: Planning, Safety and Operations, and it falls to the IC while any of them is unfilled.
  - The INCIDENT tab's **PLUME PROJECTION** section runs it.
- **The result** (`PlumeProjected`, perceived) goes on the COP and on the map ("Projected plume (prediction)", dashed amber to red), with the inputs, each zone's reach and threshold, and the substance's advice.
- **GIS overlay** (`PlumeImpacts`): each zone is intersected with the static layers. Each place is counted once, in the worst zone it falls in.
  - Named: schools, hospitals, shelters and critical infrastructure.
  - Counted: homes (residential buildings).

  Without a GIS database the projection still works; it just can't say what is underneath.
- **Alerts:**
  - **"School within projected hazard area"**: one alert for each school, hospital or shelter inside the moderate or high zone (Critical in the high zone).
  - **"About N home(s) within projected hazard area"**: one alert covering the homes.
- **From projection to action:** **Evacuate moderate zone** and **Shelter low zone** declare protective zones over the projection's outlines in one click.
- **Dynamic updates:** when the *reported* wind turns 30° or more, or its speed changes by 3 m/s or more, the projection is re-run and supersedes the old one. It is marked as an update, and re-runs at most every 2 minutes.
  - It reads only the COP, never the true wind or release.
  - The release rate stays command's own estimate. To revise it, project again with the new figure.
- **The desk:** a hazmat incident nobody has projected waits on the desk of whoever holds hazard prediction.

### Releases, and what crews notice

- **Instructor injects:**
  - **Hazardous release:** any of the eight materials, at a rate and inventory, near the incident.
  - **Release rate changes:** worsens, eases or stops the latest release, and the model re-predicts.
- **What crews notice depends on what it is:**
  - an irritant gas they smell, then feel (chlorine's bleach, hydrogen sulphide's rotten egg);
  - smoke they see;
  - radioactive material shows only on dosimeters, with the dose rate;
  - a biological aerosol is noticed by nobody, except a hazmat crew's detector.

### In the review

- **Timeline:** projections appear under HAZARD; the real model's predictions appear as truth.
- **Findings:**
  - how soon each hazmat release was projected (within 10 minutes is prompt), or that it never was;
  - whether the schools, hospitals and homes a projection found in the moderate or high zone were then covered by an evacuation or shelter zone, and how long that took (within 15 minutes is in time).
- **Not a decision:** the alerts a projection raises are its consequences, so they are not judged as decisions.

**Tests:**
- `PlumeModelTests`: fake service and GIS, so these always run.
- `PlumeContractTests`: the wire format both ways.
- `PlumeServiceTests`: against the real Python service; these skip unless it is running.

## Local setup

```powershell
# 1. Database (prompts for the postgres superuser password; writes appsettings.Local.json, which is git-ignored)
powershell -ExecutionPolicy Bypass -File database\Setup-Database.ps1

# 2. Schema
cd "Emergency Response Simulator"
dotnet tool restore
dotnet tool run dotnet-ef database update --project "Emergency Response Simulator.Data"

# 3. Hazard-model service (Phase 13; optional: without it the in-engine plume is used and projections are refused)
cd ..\hazard-models
py -3.14 -m venv .venv                                  # once
.venv\Scripts\python -m pip install -r requirements.txt  # once
.venv\Scripts\python -m pytest -q                        # optional: the model's own tests
.venv\Scripts\python -m uvicorn plume_service.main:app --host 127.0.0.1 --port 8765
```

If no connection string is configured, the app runs with an in-memory event store.

The simulator looks for the hazard-model service at `HazardModels:BaseUrl` (`http://127.0.0.1:8765/`). In a networked session only the **server** needs it. The `PlumeServiceTests` run against it when it is up and skip otherwise (`ERS_HAZARD_MODELS` points them elsewhere).
