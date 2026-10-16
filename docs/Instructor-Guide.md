# Instructor Guide

How to run a multiplayer exercise on the Emergency Response Simulator: setting it up, running it live from the instructor console, and taking it into the debrief. The [Observer Guide](Observer-Guide.md) covers live assessment. The [README](../README.md) has the technical detail.

---

## 1. Before the session

**What you need**
- A **training PC** that runs the server and that every client can reach on the LAN (port 5180 by default).
- **PostgreSQL** on the server for a real exercise. In-memory is for rehearsals only, because the record is lost when the server stops.
- An **instructor PIN**. It is set in the server's configuration and never in a client's.
- *Optional, for voice transcription:* the Whisper model (`ggml-base.en.bin`) in `%LOCALAPPDATA%\ErsVoice\models\`. Without it, players can still talk to each other, but the simulated crews won't hear spoken orders, so players type them instead.

**Start the server**

```powershell
dotnet run --project "Emergency Response Simulator.Server" -- --Server:InstructorPin=2468
```

Browse to `http://training-pc:5180/` to see the sessions, their join codes and who is in them.

**Open the session**

```powershell
"Emergency Response Simulator.exe" --Mode=Client --Server=http://training-pc:5180 --Create=barrow-street --Name=Ines --Pin=2468 --Title="Blue watch"
```

You can also leave the options off and use the join dialog. You get a **6-character join code**: read it out or write it on the board.

**Plan the roles.** These are one person at a time:
- Incident Commander
- Operations
- Planning
- Logistics
- Finance/Admin
- Safety
- Liaison
- PIO

Dispatchers, agency chiefs and observers can be several people. **Any role nobody takes falls to the IC.** So with a small group you can run IC + Operations + Dispatcher, and the IC carries the rest. Add a PIO and Safety Officer as numbers allow.

| Group size | Suggested roles |
|---|---|
| 2 | IC, Dispatcher |
| 3–4 | IC, Operations, Dispatcher, (PIO) |
| 5–7 | add Safety, Planning, Liaison |
| 8+ | add Logistics, an agency chief (give them an agency's control room), observers |

---

## 2. The lobby

- Trainees join with the code, a name and a role. The join dialog explains voice recording and asks for consent; anyone who declines can listen on the radio but not talk.
- Until you start the exercise the clock is stopped and every command is refused ("wait for the instructor"), but everyone can look around the map and their workspace.
- In the **SESSION** tab you can **Assign** roles. Trainees can **hand over** their own role. Role changes in the lobby happen at once.
- **Exercise control messages** work in the lobby too. Use them for a safety brief or to say "we start in two minutes".

Start the exercise with **▶ Start the exercise** (SESSION tab) or the play button. Only you control the clock: pause, speed (1×–30×) and resume.

---

## 3. The instructor console

Open it with **🎛 Console** in the header. To run it on a second screen or machine, start a client with `--Role=Instructor --Pin=… --Console=true`. Everything you do here is **ground truth stamped with you**: trainees see only its effect in the world, and the review shows all of it.

### 3.1 INJECTS

- **The scenario script**, live: each inject's time and state (in 2:30 / waiting for its condition / fired / held / skipped).
  - **Fire now:** early, whatever its time or condition.
  - **Hold:** keeps it from going off; **Release** puts it back on the script.
  - **Skip:** drops it from this run.
- **Ad-hoc injects** from the template library. Each has sensible defaults and places itself near the largest live incident.
  - wind shift, escalation, secondary incident, 999 call
  - unit breakdown, radio failure, road blocked, hospital under pressure
  - hazardous release and release rate changes (Phase 13)
  - **social-media rumour:** you say whether it's true. A true one the PIO denies backfires on them.
  - **delivery delayed** and **local suppliers run out** (Phase 15)
  - **infectious disease outbreak** and **fatalities found** (Phase 16)
  - **infrastructure failure**, **GPS jamming or spoofing** and **ransomware attack** (Phase 17)

> **Tip:** Hold an inject when the team is already saturated, and fire one early when they are coasting. The review shows every inject that fired early, was held, or was fired ad hoc, so the debrief can account for the difficulty.

### 3.2 PLAYERS (the monitor)

For each participant:
- **Commands:** how many they have given, and how many recently.
- **Idle:** shown after 5 sim-minutes without a command. Observers never count as idle.
- **Desk:** what waits for their role right now, how much of it is overdue, and (on hover) what has waited longest. **OVERLOADED** in red means 6 or more items, or 3 or more overdue.
- **Last action**, plus a live activity feed of who did what.

Alongside these are **unacknowledged alerts**, **radio load** per channel (and emergency traffic), and **bookmarks**.

**Reading it:**
- **Idle and an empty desk:** the player is waiting for work. That's fine, or a moment for an inject.
- **Idle with a full desk:** the player is stuck. Consider an exercise-control nudge.
- **Overloaded IC with an empty Operations desk:** the IC isn't delegating. Note it for the debrief, or nudge.
- **Overloaded IC because roles are unfilled:** expected in small groups. Hold injects, or assign another role.

### 3.3 Bookmarks

Type a note and **🔖 Bookmark** to mark a moment for the debrief, for example "Alex didn't confirm Engine 4's read-back". It appears on the review's timeline and in its Instructor table.

### 3.4 Exercise control

The **EXERCISE CONTROL** box on the PLAYERS tab sends a message **outside the world**, to everyone or to one participant. It appears as an amber banner on their screen until they dismiss it. Nobody in the simulation hears it, and the review's timeline lists it.

Use it for:
- **Teaching points:** "EXERCISE: pause. Look at the wind direction before you place the cordon." Pause the clock as well.
- **Nudges:** "Bea, Operations hasn't been briefed on the plan yet."
- **Admin:** "Ten minutes left", "Swap roles at the next pause".

> Messages to one person travel in everyone's event stream, and only that person's screen shows them. That is fine for a classroom, but not for anything confidential.

### 3.5 ROLE-PLAY

Speak as anyone in the world. It reaches command through the normal comms, with their delays, black spots and busy channels:

| Kind | Becomes |
|---|---|
| Radio | A transmission on a channel from a crew (use their call sign), and a field report |
| Agency | A chat message from another agency, utility or hospital |
| Caller | A 999 call in the queue |
| Journalist | A media enquiry for the PIO (or the IC, with no PIO) |
| Official | An approval request on the IC's desk |
| Politician | A political call for the IC or PIO. The office is taken from who you say you are: "Minister…" or "Council…" |

### 3.6 AI ROLES

These are the parts the simulation plays:
- Regional Duty Officer (mutual aid)
- National Directorate
- outside organisations
- the media
- the politicians
- each AI agency's control room

**Take over** a role and its decisions wait for you in the console. Answer them in the role's name, and they go down the same path as an automatic decision. **Hand back** and the simulation answers whatever is still waiting.

- **The media** taken over: no journalist rings and no coverage runs unless you play one. Public confidence is still tracked for the review.
- **The politicians** taken over: no calls or visit requests come in unless you play them, and silence costs nothing you don't choose.
- **An agency's control room** can be **given** to a participant who joined as an **agency chief**. They task that agency's units and answer command's requests and invitations. If they leave, the agency goes back to the simulation.

---

## 4. What the simulation does on its own (and how to steer it)

**Roles played by people are theirs.** If a participant plays the PIO, the simulation never answers the media in their name, and politicians ask for them. The same rule applies to every role a person holds.

**The media and public confidence:**
- **Public confidence** (0–100, starting at 60) is ground truth: trainees never see the number. It appears among the world facts on your INSTRUCTOR board.
- **What raises it:**
  - timely statements, especially ones with clear protective advice ("stay indoors…", "evacuate…")
  - a minister's visit to a safe scene
- **What lowers it:**
  - journalists left waiting 10 minutes
  - an evacuation or shelter zone with no public advice from command within 10 minutes
  - another agency contradicting the official line, which happens when there is no joint information centre
  - a minister going public after unanswered calls
  - a VIP visit while the hazard is live
- **Coverage:** every 12 minutes, and at once when the tone turns, an outlet reports supportive, mixed or critical coverage. Command sees it as a media report.

**The politicians:**
- **Calls:** the local TD, then the council's chief executive, then the minister's office ring about serious incidents, more often the worse it is. An unanswered call escalates. Ignore the minister's office and the minister goes public.
- **Visit requests:** after 25 minutes of a Critical incident (40 for High), the TD or the minister asks to **visit the scene**. It lands as an approval on the IC's desk.
  - **Approving while the hazard is live:** the party turns up at the cordon wanting to go forward, which raises a Safety alert and costs confidence. The review marks it as a weakness.
  - **Putting it off until the scene is safe:** the review marks it as a strength.

> **Steering tip:** If the team is ignoring the PIO function, role-play a journalist with a pointed question, or fire the **social media rumour** template.

**The public** (Phase 14). Most of this arrives as reports and calls; the PUBLIC tab is where the team works it.
- **Vulnerable people in every zone:**
  - Some can't leave without help, and wait.
  - Rough-sleepers are never reached by door-knocking.
  - Pet owners stay, or go back in, unless somewhere takes animals (the team notifies an animal welfare service).
  - The council's register comes back after 8 minutes and misses people. **Assisted evacuation** gets them out.
  - Anyone still there after 30 minutes is a liability in the review.
- **Community leaders** ring when their area is put under an order. Called back, people listen to them and they say who needs help.
- **The Garda:** an evacuation ordered without consulting the Garda brings a call from the Chief Superintendent, and a legal exposure unless it is answered quickly.
- **Looting** happens in evacuated streets with no gardaí there or cordon over them.
- **Volunteers** gather at the cordon. Left alone, they go in, and some get hurt.
- **Families:** relatives ring for news. Nothing is traced until someone opens a **family assistance centre**.
- **Rumours** spread until answered, some true and some not. The PIO should check before denying. A **press conference** answers the journalists and deals with the rumours it names.
- **The decision log:** the review checks that significant decisions were logged with their reasons.

> **Steering tip:** On a hazmat or fire exercise, the public side is easy to forget. If the team is all tactics, let a rumour run, or fire a true one and watch whether the PIO checks before denying it. If they're overloaded, take over the politicians (which also silences community leaders and agency heads) or turn the public side down with `Simulation:PublicPressures=false` for a first run.

**Logistics** (Phase 15). Nothing here announces itself loudly; the LOGISTICS tab and the supply unit's warnings are where the team sees it.
- **Supplies run out:** appliances bring a first stock (foam, suits, kits, batteries, a little water and food), and the work gets through it. On a chemical fire, foam goes in under an hour; water and food in an hour or two.
- **Warnings come first:** the supply unit warns with 20 minutes left. Watch whether Logistics orders then, or waits until it has run out.
- **Three tiers:** local is quick but runs out; regional needs a reason; national needs a declared major emergency. Deliveries need a staging area or base camp to unload at.
- **Fuel:** pumping burns a quarter of a tank an hour. Without fuel ordered to the scene, engines start running dry after a couple of hours.
- **Wear:** worn appliances warn before they fail ("pump overheating"). Servicing then almost always saves them; ignored, they break down and need a mechanic or the workshop.
- **Money:** every hour is costed, overtime past the end of a shift, mutual aid as billed. Contracts over €25,000 need a written justification. A claim to central government is only possible after a major emergency is declared.
- **Injects:** **Delivery delayed** holds up the next delivery; **Local suppliers run out** forces orders up a tier. The **unit breakdown** inject now needs repairing: "flat tyre" can be fixed on site, "pump seal has failed" needs the workshop.

**Health** (Phase 16). The HEALTH tab is where Operations, Logistics and Liaison work it.
- **Patients are tagged and tracked:** each casualty an ambulance crew treats gets a tag, and the board follows them to hospital.
- **Specialist centres:** burns go to St. James's, major trauma to the Mater, children to Temple Street. That happens only if Operations sets destinations. Otherwise crews take the nearest hospital, and a patient there who needs a specialist service is transferred 45 minutes later; a P1 may not survive the wait.
- **Blood:** a few major trauma patients drain a hospital's blood. The hospital says when it is low; the IBTS restocks 40 minutes after it is asked.
- **The dead:** nobody is moved until the coroner is notified. A full hospital mortuary keeps the dead in the emergency department. A temporary mortuary takes them and identifies them faster, and families are told only once a body is identified.
- **Public health:** a biological or radioactive release (the **hazardous release** inject with "biological aerosol" or "radioactive material") or an **infectious disease outbreak** brings an HSE Public Health alert. Points of dispensing need doses (prophylaxis, from the supply chain) and a public statement telling people where to go. Isolation and quarantine without HSE Public Health are a legal exposure.
- **Behavioural health:** distress brings the worried well to emergency departments. A helpline and counselling teams help, and their psychological triage refers the high-risk to psychiatric beds.
- **Injects:** **Infectious disease outbreak**, and **Fatalities found** (the dead at the largest incident).

**Evacuation, shelter & infrastructure** (Phase 17). The EVAC & INFRA tab is where Operations, Logistics, Liaison, Planning and Safety work it.
- **Evacuation:** the gardaí report each zone's population, how many are out and when it will be clear. Without a route people go straight out, downwind included. Buses carry those on foot; traffic control points keep the cars moving. Re-entry sends everyone home: if the hazard is still there, or the ground is contaminated, that is recorded.
- **Rest centres:** a quarter of evacuees (more of the elderly) need one. Until one opens they have nowhere to go, and the desk says so. A centre needs meals, water and bedding from the incident's stock, staff to register people, and security once it fills.
- **Infrastructure:** in Barrow Street the fire can reach a gas regulator (a leak that can ignite) and the substation's power cut takes the Grand Canal Dock pumping station off after an hour on standby, so hydrants weaken and hospitals lose capacity. The DART runs through smoke or the evacuation zone until someone tells Irish Rail.
- **Cyber and GPS:** jamming freezes units on the map; spoofing moves them, which is worse. Ransomware on the control room's CAD silences status buttons until the IC orders paper working. On water treatment, a boil water notice is needed unless the works go manual within 45 minutes. Telling the NCSC halves recovery.
- **Environment:** fire-water run-off goes to the canal unless contained; past 20,000 L the fish die and Inland Fisheries Ireland ring. Monitoring teams give the air, water and soil readings that should come before re-entry.
- **Injects:** **Infrastructure failure**, **GPS jamming or spoofing**, and **Ransomware attack**.

> **Steering tip:** For a short session, fire **Ransomware attack** on the control room dispatch while units are moving: the team has to notice the COP has gone stale. For evacuation, declare a zone from a plume projection and watch whether anyone sets a route before people walk into the cloud. `Simulation:Protection=false` leaves it all out.

> **Steering tip:** The quickest way to exercise the medical side is **Fatalities found** and an **Incident escalates** with extra casualties on an explosion: tags, destinations, blood, the coroner and the mortuary all follow. For public health, fire an outbreak at a rest centre and watch whether the IC involves HSE Public Health before ordering isolation. `Simulation:Health=false` leaves it all out.

> **Steering tip:** Logistics only bites on longer exercises. For a short one, fire **Local suppliers run out** on foam early in a chemical incident, or a breakdown on a key appliance. To leave it out of a first run, set `Simulation:Logistics=false`.

---

## 5. Voice radio

- **Push to talk:** hold `` ` `` (backtick) anywhere in the main window, or the **Hold to talk** button in COMMS HUB → VOICE.
- **Channels follow the role.** You hear and can talk on every channel; observers listen on every channel and never talk.
- **Half-duplex:** one talker per channel. Keying up on a busy channel is refused with who has it.
- **Patches:** when Logistics patches two channels, they become **one talk group**. One talker across both, and everyone on either channel hears it.
- **Emergency traffic:** while it is declared on a channel, only the IC, Operations, Safety, dispatchers and you can key up. Everyone else is told to keep off the air.
- **Spoken orders** to simulated crews are transcribed and read back, the same as typed ones. Every transmission is recorded and plays back from the review's timeline.

---

## 6. Ending and the debrief

1. **End the session** (SESSION tab, with a reason). The clock stops for good and commands are refused; the record stays.
2. Open the **After-Action Review**. Before the end, only you (and observers you've given truth) can open it; afterwards everyone can.
3. Walk through:
   - **Findings:** strengths and weaknesses, including political calls, public confidence (where it ended, its lowest point and the biggest drops), visit decisions, and role handovers.
   - **Participants:** roles over time, commands, decision ratings, observer ratings, and combined score and level.
   - **Observer notes and Competencies:** each player's ratings by competency area.
   - **Legal & liability:** how many significant decisions were logged with their reasons, and each exposure an inquiry would ask about. Some will be marked mitigated: an evacuation without the Garda, vulnerable people left behind, an unguarded zone, a volunteer hurt, an untrue denial, families waiting over an hour.
   - **Forms (📄 Forms, Phase 18):** before the debrief, export every form for the incident in one document. The decision log and each player's ICS 214 show every order, request and approval they gave, when, in which role, and the reason they logged. A command with no reason is the question to ask. The IAP package's cover shows how the plan changed version by version.
   - **Evacuation, shelter & infrastructure:** for each zone how many really got out and when it was clear; rest centres, registration and shortages; infrastructure failures and gas ignitions; ransomware and GPS; run-off and wildlife. Residents sent home to danger, trains run through it, no boil water notice, and pollution not reported to the EPA show under legal & liability.
   - **Medical, fatalities & public health:** patients right first time, transfers and deaths; the coroner, the dead at the scene and identification; the exposed protected in time; distress, counselling and the worried well. The dead left at a scene over 3 hours, and isolation or quarantine without HSE Public Health, show under legal & liability.
   - **Logistics & finance:** each supply outage and breakdown, with when it was warned of and when anyone acted; appliances that ran dry; the cost breakdown and the claim. A contract over €25,000 with no justification shows under legal & liability.
   - **Instructor:** what you injected, held, skipped and role-played. This shows how hard you made it.
   - **Timeline:** filter to one participant, play the recorded radio (▶), and find your bookmarks and exercise-control messages.
4. **Export** to HTML or CSV for the training record.

---

## 7. Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| A trainee's buttons are all disabled | The exercise hasn't started, their role doesn't hold that function, or they joined as an observer. The SESSION tab lists their functions. |
| "X is for the PIO, not the Operations Section Chief" | The permission matrix is working. That function belongs to someone present. |
| A trainee dropped | They reconnect automatically and resume where they were. After 60 s away they count as gone and their exclusive role is freed. |
| Spoken orders aren't answered | There's no Whisper model on the server, so players should type orders on the radio instead. |
| Nobody rings from the media / politicians | You have taken that role over (AI ROLES), or the incident is Low priority. |
| "Channel is patched to …, where … is talking" | Two channels are patched, so wait for the other talker. |
