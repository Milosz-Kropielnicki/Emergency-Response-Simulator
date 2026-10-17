# Observer Guide

How to watch and assess players during a multiplayer exercise. Observers are **read-only**: you see the exercise and record how players are doing, but you cannot change anything in it. See also the [Instructor Guide](Instructor-Guide.md).

---

## 1. Joining

```powershell
"Emergency Response Simulator.exe" --Mode=Client --Server=http://training-pc:5180 --Code=8X6UF9 --Name=Olly --Role=Observer
```

You can also use the join dialog and pick **Observer**. You can join before or during the exercise. The header shows **OBSERVING · read-only**.

**What you can't do:**
- Every command button is disabled, and the server refuses any command from an observer, whatever the client sends.
- You can't hand over a role or control the clock.
- On the radio you **listen on every channel** but never talk.

---

## 2. What you see

- **By default, the players' picture:** the COP as command knows it, with the same delays, gaps and errors.
- **With ground truth:** the instructor can let you see truth from their SESSION tab ("Let them see truth"). You then also see the real world: the true fire, casualties and wind, the instructor's board, and the history behind it. The header says **with ground truth**. It lasts while you observe, and the instructor can take it away.

> Assessing with truth shows you *why* a decision was good or bad. Assessing without it shows you what the player *could have known*. Agree with the instructor which one you're doing.

---

## 3. Following a player

In the **ASSESS** tab, choose a player under **FOLLOW**:
- **Their layout:** your view switches to their role's workspace, and switches again if their role changes.
- **Where they act:** with each command they give, the map goes to the incident or unit concerned, and a line describes what they just did.
- **Their desk:** the **DESK** tab shows what waits for *them* right now, most urgent first. It is the quickest way to see whether they are keeping up, and what they are leaving undone.

Choose **Nobody (free view)** to look around on your own.

---

## 4. Recording notes and ratings

In **ASSESS → NOTE AND RATE**:

1. **Who:** the player the note is about. Following someone selects them.
2. **Area:** the competency it assesses. The area stays selected between notes, so you can watch one area for a while. Choose *Overall* for a general impression.

   | Area | Look for |
   |---|---|
   | Command & control | Taking and keeping command, structure, delegation, span of control, handovers |
   | Situational awareness | Knowing what's happening and what it will do next; checking reports |
   | Decision making | Timely, sound priorities, resources and approvals |
   | Communication | Radio discipline, orders and read-backs, briefings, the public and media |
   | Teamwork & liaison | Working with the team and the other agencies |
   | Safety | Zones, PARs, Maydays, rehab, protective actions; refusing unsafe visits |

3. **Rating** on the rubric: **Competent**, **Developing** or **Not yet competent**. You can leave it at *No rating* for a note only.
4. **Note:** what you saw. Be specific and factual: "Confirmed Engine 4's garbled read-back within 20 s" is better than "good comms".
5. **Tag their latest action** (on by default): links the note to the event it is about, so the review shows the note beside it.
6. **📝 Record note.**

**Rules:**
- Notes are ground truth: **players never see them during the exercise**.
- Every observer and the instructor sees every note, so you can mark together.
- Only players are assessed: not observers and not the instructor, and nobody assesses themselves.
- A rating alone, or a note alone, is fine.

> **Good practice:**
> - Rate the behaviour, not the outcome. A good decision can still go wrong in the simulation.
> - Note strengths as well as gaps.
> - Rate an area several times over the exercise rather than once at the end. The review averages them.
> - Use the instructor's bookmarks and exercise-control messages (they appear on the timeline) to anchor your notes in the debrief.

---

## 5. How notes become scores

In the **After-Action Review**:

- **Observer notes:** every note, with time, player, area, rating, the event it was about, and who recorded it. Notes also appear on the timeline (category ASSESSMENT) and in the player's own filtered timeline.
- **Competencies:** each player's rated notes **per area**, averaged and put back on the rubric (Competent = 100, Developing = 50, Not yet competent = 0; 75+ is competent, 40+ developing).
- **Legal & liability** (Phase 14): the decision log's coverage and the exposures an inquiry would ask about. Notes that tie a player to them (Communication for an untrue denial, Safety for people left behind) make the debrief concrete.
- **NIMS/ICS compliance** (Phase 20, **🔗 Standards** in the header): run the check during the exercise to support a Command or Communications note. It shows span of control, no Safety Officer, a period without an approved plan, and codes on the radio, each with the NIMS reference to cite.
- **Scenario & training** (Phase 19): the difficulty level the team reached, its coping scores, and the incidents within the incident. A secondary device that caught crews, or a shooter who was never stopped, is a Safety note.
- **Forms and the decision log** (Phase 18, **📄 Forms** in the header): during the exercise, open a player's ICS 214 to see what they have done and why, attributed to them. It is the quickest way to check a note before you rate it. After it, the decision log and the IAP package's version history support Decision making and Planning notes.
- **Evacuation, shelter & infrastructure** (Phase 17): how many really got out of each zone, rest centres, infrastructure, cyber and the environment. The moment a zone is declared with no route, or a ransomware report sits on the desk with nobody ordering manual working, is a good one for a Decision making or Situational awareness note.
- **Medical, fatalities & public health** (Phase 16): right hospital first time, the coroner and the dead, protection in time, and counselling. The Operations player's destinations and the IC's handling of HSE Public Health are good moments for a Decision making or Teamwork & liaison note.
- **Logistics & finance** (Phase 15): supply outages and breakdowns, each with when it was warned of and when anyone acted, and the costs. Watch the Logistics and Finance players' LOGISTICS tab: a note at the moment a warning goes unanswered is worth more than the outage it leads to.
- **Participants:** each player's **observer score** (all their ratings, areas and overall) combined evenly with how their own decisions rated in the simulation. Either one stands alone if the other is missing. The **overall level** goes back onto the rubric.
- **Exports:** the HTML and CSV exports include the notes, the competency table and the scores.

---

## 6. Etiquette in the room

- Don't prompt or help the players. If something needs saying, tell the instructor, who can send an exercise-control message.
- Keep your radio muted to the room. You can hear every channel, and voices carry.
- If you need to leave, just close the client. You can rejoin with the same code.
