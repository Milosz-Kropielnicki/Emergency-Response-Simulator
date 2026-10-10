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
| `IPlumeService` | Where is the hazard going? | HTTP client plus Python stub model (the trainee's prediction); a simple truth plume runs in-engine since Phase 6; real model comes in Phase 12 |
| `ICommsService` | Who said what, on which channel? | Implemented (`CommsService` over the COP's comms log) |
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
  | Simple plume | Ground-level Gaussian plume with Briggs urban dispersion, class D. Low, moderate and high zones come from AEGL-style thresholds. The cloud only reaches as far as the wind has carried it, swings with the wind, and clears after the release stops. Phase 12 brings the full models. |

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
