# Participant Guide: Workspaces, Display and Accessibility

For everyone at a simulator screen, and for crews using the field companion on a phone. The instructor's and observers' own work is covered in the [Instructor Guide](Instructor-Guide.md) and the [Observer Guide](Observer-Guide.md).

---

## 1. Your workspace

A **workspace** arranges the screen for a role. Every workspace shows the same picture: it only decides which panels you work from, which one is in front, which board is at the bottom and which map layers are on. Workspaces never change what you are **allowed** to do; that is the permission matrix's job.

When you join in a role, its workspace is chosen for you. You can pick another from the drop-down in the header at any time.

| Workspace | Opens on | Works from |
|---|---|---|
| Incident Commander | Desk | incidents, ICS, agencies, public, logistics, health, evacuation, comms, crews |
| Dispatcher | Comms | incidents, units, evacuation and GPS |
| Operations Section Chief | ICS | incidents, units, comms, crews, public, logistics, health, evacuation |
| Planning Section Chief | Incident | ICS, agencies, environmental monitoring |
| Logistics Section Chief | Logistics | health, evacuation, comms, crews, units, incidents, public |
| Finance/Admin | Logistics | incidents, agencies |
| Safety Officer | Crews | incidents, comms, evacuation and environment |
| Liaison Officer | Agencies | public, health, evacuation, incidents, comms |
| Public Information Officer | Public | agencies, incidents |
| Agency chief | Agencies | incidents, comms, units |
| Instructor | Session | everything, with the instructor's board |
| Observer | Assessment | incidents, comms, agencies, public, logistics, health, evacuation, and the history |

Every role also has its **desk** and, in a networked session, the **session** tab.

**Safeguards:**

- The **instructor's board** only appears where the instructor's tools are: a standalone session, or the instructor's own client. A trainee who picks the *Full layout* or *Instructor* workspace doesn't get it.
- **Assessment** only appears for observers and the instructor.
- A workspace never opens on a panel it hides.

### Customising it

Open **⚙ Display → Workspace** (Ctrl+,):

1. Tick the panels you want and choose the one in front.
2. Choose **Apply**. The change is kept on this computer for that workspace.
3. To undo it, choose **Reset to the role's default**.

A customisation can only choose among the panels the role's default could show, so it never widens what a role sees. The desk and the session tab can't be removed.

### More than one monitor

Any side panel can go into a window of its own:

1. Bring the panel to the front and press **Ctrl+Shift+P**.
2. Drag the new window to another screen. Closing it puts the panel back.
3. In **⚙ Display → Workspace**, choose **Save this layout**.

Next time you choose that workspace, the panels open where you left them. If a monitor is no longer there, they come back onto the screens you have.

---

## 2. Display and accessibility

All of this is in **⚙ Display** in the header. It is applied at once, kept on this computer, and never sent to the server.

| Setting | Choices |
|---|---|
| **Theme** | **Dark** (the default); **Light**, for bright rooms and projectors; **Night operations**, red on black at low brightness with the map dimmed, to keep your night vision in a dark command vehicle or outdoors; **High contrast**, white on black with the strongest colours, for low vision |
| **Colour vision** | **Standard**; **Red–green safe**, for protanopia and deuteranopia (the Okabe–Ito colours: vermillion against bluish green, OK shown in blue in the light theme); **Blue–yellow safe**, for tritanopia (reds, oranges and greens, never blue against green). The map's agency, zone, triage, plume and traffic colours change with it. |
| **Reading font** | **Standard** (Segoe UI); **Dyslexia-friendly** (OpenDyslexic or Lexend if installed, otherwise Verdana); **Hyperlegible**, for low vision (Atkinson Hyperlegible if installed, otherwise Verdana) |
| **Size** | 100% to 200%: text and controls together |
| **Language** | English, Gaeilge, Polski (below) |

Every theme and colour-vision combination keeps text and status colours at **WCAG AA contrast (4.5:1)** on every background. The tests check this, and check with a colour-blindness simulation that the red–green palettes keep apart what the standard one doesn't. With the standard colours, a deuteranope sees critical red and OK green as almost the same.

In the panels, status colours always come with words (a status, a level, a label), and map symbols carry their callsign or name.

### Screen readers

Narrator, NVDA and JAWS work with the main window:

- Every header button has a spoken name. Symbol buttons don't read out as "multiplication x".
- The outcome of each command (*Unit dispatched*, *Not permitted…*) is a live region, so it is announced as it arrives.
- The keyboard focus is always visible on buttons.

The map itself is visual. Everything on it is also in the incident, unit, comms and desk panels as text.

### Languages

The header, the main panels' tabs, display settings, shortcuts and voice-command messages are in **English**, **Irish** and **Polish**. The field companion is fully translated.

What players type, and what the simulation says on the radio and in reports, stays in English, the language of the exercise. The Irish and Polish strings are a first translation: have a native speaker check them before an exercise relies on them.

---

## 3. Keyboard shortcuts

None needs the mouse, and none gets in the way of typing: letters and digits always need Ctrl, and the function keys stand alone. **F1** shows them. Change any of them in **⚙ Display → Shortcuts**. A clash, a bare letter or the push-to-talk key is refused, and the default is kept.

| Action | Key |
|---|---|
| Start or pause the clock | F5 |
| Faster / slower | Ctrl+= / Ctrl+- |
| Show or hide map layers | Ctrl+L |
| Show or hide the side panel | Ctrl+R |
| Next / previous panel | Ctrl+PageDown / Ctrl+PageUp |
| Pop the panel out to its own window | Ctrl+Shift+P |
| Incident Action Plan | Ctrl+Shift+I |
| ICS forms | Ctrl+Shift+F |
| After-action review | Ctrl+Shift+A |
| Data standards and NIMS | Ctrl+Shift+D |
| Display and accessibility | Ctrl+, |
| Acknowledge the oldest alert | Ctrl+Shift+K |
| Night operations on or off | Ctrl+Shift+N |
| Show the shortcuts | F1 |
| Push to talk (radio) | ` (backtick), unless configured |

---

## 4. Voice commands

Turn them on in **⚙ Display**. Then say **"Simulator"** followed by the command. The wake word stops radio traffic and conversation from being taken as commands. Voice commands pause while you hold push-to-talk.

| Say "Simulator, …" | |
|---|---|
| start · resume · start the clock | starts the clock |
| pause · stop the clock | pauses it |
| faster · speed up · slower · slow down | the speed |
| show comms · show incident · show unit · show command · show crews · show agencies · show session · show assessment · show desk · show public · show logistics · show health · show evacuation | brings that panel to the front |
| next panel · previous panel · side panel · layers · pop out | the panels |
| action plan · forms · review · standards · display settings | opens those |
| acknowledge · acknowledge alert | the oldest alert |
| night mode · day mode | the theme |
| shortcuts · help | the shortcuts |

**How it works:**

- Recognition is Windows' own offline English recogniser. Only these phrases are listened for, and nothing leaves the computer.
- If no English recogniser is installed, the setting says so. Add one in Windows *Settings → Time & language → Speech*.
- The commands are English whatever the interface language.

---

## 5. The field companion (phones)

Crews in the field can join the exercise from a phone. Open **http://*server*:5180/field/** in the phone's browser. It can be added to the home screen and opens without a signal.

1. **Join** with the session's code (from the instructor), your name, and your unit's callsign (*Engine 12*).
2. From then on your phone speaks for that unit:
   - **Field report:** what you see, with your location if you tick it. With no signal, reports wait on the phone and go when it reconnects.
   - **Status:** en route, on scene, working, transporting, available, out of service.
   - **Check in at the incident (ICS 211):** with how many crew. The unit goes on scene and command gets the check-in.
   - **Orders:** to your unit, each with **Acknowledge**.
   - **PAR:** when command calls one, how many crew you have and who is missing.
   - **Share my position every 30 seconds:** off unless you turn it on.

Everything arrives on the picture as information **from your unit**, signed with your name (*Engine 12 (Ciara Byrne, field app)*), the way a radio message would. The phone can only speak for the unit you joined as. A unit run by another agency's own control room can't be joined.

The app works in English, Irish and Polish. It follows the phone's light or dark setting and has its own **Night** button. Buttons are large enough for gloves.
