using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Aar;

/// <summary>
/// Branching AAR (Design Document §22): each key decision point, what the trainee did, what the recommended response
/// was, what doing nothing would have meant, and the likely difference, estimated from what really happened in the
/// session (when casualties worsened, when the building came down, when a firefighter's air ran out).
/// For a full re-run of the scenario under other command policies, see <see cref="BranchRunner"/>.
/// </summary>
public sealed partial class AarAnalyzer
{
    private List<AarBranchPoint> Branches()
    {
        var branches = new List<AarBranchPoint>();
        OpeningBranch(branches);
        DispatchBranch(branches);
        EvacuationSignalBranch(branches);
        MaydayBranches(branches);
        AuthorityToEvacuateBranch(branches);
        HazardZoneBranch(branches);
        HospitalBranch(branches);
        InformationSiloBranch(branches);
        LeadAgencyBranch(branches);
        return branches;
    }

    /// <summary>Casualties whose condition got worse (or who died) between two moments.</summary>
    private (int Worse, int Died) WorsenedBetween(DateTimeOffset from, DateTimeOffset to) =>
        (_truth.Worsened.Count(w => w.At > from && w.At <= to && w.To != Triage.Deceased),
         _truth.Worsened.Count(w => w.At > from && w.At <= to && w.To == Triage.Deceased));

    private static string Late(TimeSpan by) => $"{Duration(by)} later than recommended";

    private string CasualtyEffect(TimeSpan saving, DateTimeOffset firstHelp)
    {
        var (worse, died) = WorsenedBetween(firstHelp - saving, firstHelp);
        var consequence = worse + died == 0
            ? "No casualty got worse in that window."
            : $"In that window {worse} casualt{(worse == 1 ? "y" : "ies")} got worse" + (died > 0 ? $" and {died} died" : "") + " before help arrived.";
        return $"Help could have reached the scene about {Duration(saving)} sooner. {consequence}";
    }

    private void OpeningBranch(List<AarBranchPoint> branches)
    {
        var real = _truth.Incidents.Values.OrderBy(i => i.StartedAt).FirstOrDefault();
        var first = _reports.Where(r => real.StartedAt != default && r.At >= real.StartedAt && r.Location is not null).Select(r => (DateTimeOffset?)r.At).FirstOrDefault();
        if (first is not { } reported) return;

        var opened = All<IncidentCreated>().Where(x => x.Event.Source == EventSources.C2).Select(x => (DateTimeOffset?)x.Event.SimTime).FirstOrDefault();
        var confirmed = _reports.Where(r => r.At >= reported && r.Kind == ReportSource.FieldUnit).Select(r => (DateTimeOffset?)r.At).FirstOrDefault();
        var recommended = reported + TimeSpan.FromMinutes(2);
        var help = FirstAmbulanceOnScene() ?? End;

        if (opened is not { } at)
        {
            branches.Add(new AarBranchPoint("Opening the incident", null, "No incident opened.",
                "Open an incident within 2 min of the first credible call and dispatch on it.",
                confirmed is { } c ? $"Wait for a crew to confirm it ({Clock(c)})." : "Wait for confirmation.",
                $"Nothing was sent. {_truth.Dead} people died and {_truth.Hurt} were hurt with no response from you.", Rating.Poor));
            return;
        }
        var late = at - recommended;
        branches.Add(new AarBranchPoint("Opening the incident", at,
            $"Opened {Duration(at - reported)} after the first call ({Clock(reported)}).",
            "Within 2 min of the first credible call: callers are vague, but a bang and smoke is enough to act on.",
            confirmed is { } wait ? $"Waiting for a crew to confirm it would have meant opening at {Clock(wait)}, {Duration(wait - reported)} after the first call." : "Waiting for confirmation.",
            late <= TimeSpan.Zero ? "Your timing met the standard." : $"{Late(late)}. {CasualtyEffect(late, help)}",
            late <= TimeSpan.Zero ? Rating.Good : late <= TimeSpan.FromMinutes(2) ? Rating.Fair : Rating.Poor));
    }

    private void DispatchBranch(List<AarBranchPoint> branches)
    {
        var first = _incidents.Where(i => _firstDispatch.ContainsKey(i.Key)).OrderBy(i => i.Value.At).Select(i => (Id: i.Key, i.Value)).FirstOrDefault();
        if (first.Id == Guid.Empty) return;
        var opened = first.Value.At;
        var dispatched = _firstDispatch[first.Id];
        var units = All<UnitDispatched>().Count(x => x.Payload.IncidentId == first.Id && x.Event.SimTime - dispatched <= TimeSpan.FromMinutes(2));
        var late = dispatched - opened - TimeSpan.FromMinutes(1);
        var help = FirstAmbulanceOnScene() ?? End;
        branches.Add(new AarBranchPoint("First dispatch", dispatched,
            $"{units} unit(s) sent {Duration(dispatched - opened)} after opening {first.Value.Number}.",
            "A full first attendance (two engines, an ambulance, Garda) within 1 min of opening the incident.",
            "Sending one unit to look first.",
            late <= TimeSpan.Zero
                ? units >= 3 ? "Fast and in strength." : $"Fast, but only {units} unit(s) at first: the scene needed more."
                : $"{Late(late)}. {CasualtyEffect(late, help)}",
            late <= TimeSpan.Zero && units >= 3 ? Rating.Good : late <= TimeSpan.FromMinutes(2) ? Rating.Fair : Rating.Poor));
    }

    private void EvacuationSignalBranch(List<AarBranchPoint> branches)
    {
        var warning = _reports.Where(r => r.Kind == ReportSource.FieldUnit && IsCollapseWarning(r.Claim)).Select(r => (DateTimeOffset?)r.At).FirstOrDefault();
        var collapse = _truth.Collapses.Cast<DateTimeOffset?>().FirstOrDefault();
        if (warning is null && collapse is null) return;

        var signal = All<EvacuationSignalled>().Select(x => (DateTimeOffset?)x.Event.SimTime).FirstOrDefault();
        var trapped = collapse is { } fell ? _truth.Distress.Values.Count(d => d.Cause.Contains("collapse") && Math.Abs((d.At - fell).TotalSeconds) < 5) : 0;
        var yours = signal is { } s
            ? warning is { } w ? $"Evacuation signal {Duration(s - w)} after the first warning." : $"Evacuation signal at {Clock(s)}."
            : "No evacuation signal.";
        const string recommended = "Sound the evacuation signal within 1 min of a warning of collapse, then PAR.";
        // Crews take about 75 s to withdraw once they hear it.
        var safeBy = warning is { } warned ? warned + TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(75) : (DateTimeOffset?)null;

        string effect;
        Rating rating;
        if (collapse is not { } down)
        {
            effect = signal is null ? "The building did not come down this time; the warning was still a call to act on." : "Crews were out in good time.";
            rating = signal is null ? Rating.Fair : Rating.Good;
        }
        else if (trapped == 0)
        {
            effect = signal is { } sounded && sounded < down
                ? $"Every crew was out when it came down at {Clock(down)}."
                : $"Nobody was caught when it came down at {Clock(down)}, but that was luck: nobody was close enough.";
            rating = signal is { } early && early < down ? Rating.Good : Rating.Fair;
        }
        else
        {
            effect = $"It came down at {Clock(down)} with crews inside: {trapped} firefighter(s) trapped." +
                     (safeBy is { } by && by < down
                         ? $" Signalled at the recommended time, crews would have been out by about {Clock(by)}, {Duration(down - by)} before the collapse."
                         : warning is null ? " The warning never reached you (lost on the radio): it pays to ask crews for regular updates." : "");
            rating = warning is null ? Rating.Fair : Rating.Poor;
        }
        branches.Add(new AarBranchPoint("Evacuating the building", signal ?? warning, yours, recommended,
            $"No signal: crews stay inside{(collapse is { } c2 ? $" until it comes down at {Clock(c2)}" : "")}.", effect, rating));
    }

    private void MaydayBranches(List<AarBranchPoint> branches)
    {
        foreach (var mayday in _cop.Maydays)
        {
            var distress = _truth.Distress.Where(d => d.Value.UnitId == mayday.UnitId && d.Value.At <= mayday.DeclaredAt)
                .OrderByDescending(d => d.Value.At).Select(d => (d.Key, d.Value)).FirstOrDefault();
            var airOut = distress.Key != Guid.Empty ? distress.Value.At + TimeSpan.FromMinutes(distress.Value.Air) : (DateTimeOffset?)null;
            var ended = _truth.DistressEnds.FirstOrDefault(d => d.Id == distress.Key);
            var who = mayday.Member ?? mayday.Callsign;

            string yours, effect;
            Rating rating;
            if (mayday.RescueDeployedAt is not { } sent)
            {
                yours = "No rescue team sent.";
                effect = ended.Outcome is { } outcome ? $"{outcome}" : "They were still in trouble when the session ended.";
                rating = Rating.Poor;
            }
            else
            {
                var taken = sent - mayday.DeclaredAt;
                yours = $"Rescue team ({mayday.RescueCallsign}) sent {Duration(taken)} after the Mayday.";
                var late = taken - TimeSpan.FromMinutes(1);
                if (ended.Outcome is { } outcome && outcome.Contains("unconscious") && late > TimeSpan.Zero && airOut is { } runsOut)
                {
                    var earlier = ended.At - late;
                    effect = $"{outcome} Sent within a minute, the team would have reached them about {Clock(earlier)}" +
                             (earlier <= runsOut ? $", before their air ran out at {Clock(runsOut)}." : $", still after their air ran out at {Clock(runsOut)}.");
                    rating = earlier <= runsOut ? Rating.Poor : Rating.Fair;
                }
                else
                {
                    effect = ended.Outcome ?? "Still being rescued when the session ended.";
                    rating = Within(taken, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3));
                }
            }
            branches.Add(new AarBranchPoint($"Mayday: {who}", mayday.DeclaredAt, yours,
                "Rescue team in within 1 min; emergency traffic on the channel; PAR for everyone else.",
                airOut is { } air ? $"No rescue team: found by another crew long after their air ran out at {Clock(air)}." : "No rescue team.",
                effect, rating));

            var traffic = All<EmergencyTrafficDeclared>().Where(x => x.Payload.Active && x.Event.SimTime >= mayday.DeclaredAt)
                .Select(x => (DateTimeOffset?)x.Event.SimTime).FirstOrDefault();
            branches.Add(new AarBranchPoint($"Emergency traffic for {who}", traffic ?? mayday.DeclaredAt,
                traffic is { } t ? $"Declared {Duration(t - mayday.DeclaredAt)} after the Mayday." : "Not declared.",
                "Declare emergency traffic on the channel straight away.",
                "Leave routine traffic running.",
                traffic is { } d && d - mayday.DeclaredAt <= TimeSpan.FromMinutes(2)
                    ? "The channel was clear for the rescue."
                    : "Routine traffic competed with the Mayday: Mayday messages get stepped on and the rescue runs about 25 % slower.",
                Within(traffic - mayday.DeclaredAt, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)) is var r && r == Rating.NotApplicable ? Rating.Poor : r));
        }
    }

    private void AuthorityToEvacuateBranch(List<AarBranchPoint> branches)
    {
        var request = _cop.Approvals.FirstOrDefault(a => a.Subject.Contains("evacuat", StringComparison.OrdinalIgnoreCase));
        if (request is null) return;
        var decided = request.DecidedAt;
        var deadline = request.RequestedAt + TimeSpan.FromMinutes(2);
        var byFumes = _truth.Casualties.Values.Count(c => c.InjuredAt > deadline && (decided is null || c.InjuredAt < decided.Value + TimeSpan.FromMinutes(10))
                                                         && new[] { "smoke", "fumes", "chlorine", "gas" }.Any(w => c.Cause.Contains(w, StringComparison.OrdinalIgnoreCase)));
        branches.Add(new AarBranchPoint("Authority to evacuate", decided ?? request.RequestedAt,
            decided is { } d ? $"{(request.Status == ApprovalStatus.Approved ? "Approved" : "Refused")} {Duration(d - request.RequestedAt)} after the request." : "Never decided.",
            "Decide within 2 min; with smoke drifting over homes, approve and declare the evacuation zone.",
            "Refuse, or leave residents to shelter in place.",
            byFumes > 0 ? $"{byFumes} {(byFumes == 1 ? "person was" : "people were")} hurt by smoke or fumes after the point a quick decision could have moved them."
                : decided is null ? "Residents were left waiting for a decision." : "No one was hurt by smoke or fumes after the request.",
            decided is null ? Rating.Poor : Within(decided - request.RequestedAt, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5))));
    }

    private void HazardZoneBranch(List<AarBranchPoint> branches)
    {
        var report = _reports.Where(r => IsChemicalReport(r.Claim)).Select(r => (DateTimeOffset?)r.At).FirstOrDefault();
        if (report is not { } warned) return;
        var zones = All<ZoneDeclared>().Where(x => x.Event.Source == EventSources.C2 && x.Payload.Type is ZoneType.HotZone or ZoneType.WarmZone or ZoneType.ColdZone
                                                       or ZoneType.PlumeLow or ZoneType.PlumeModerate or ZoneType.PlumeHigh or ZoneType.ShelterInPlace or ZoneType.EvacuationZone)
            .Select(x => (DateTimeOffset?)x.Event.SimTime).FirstOrDefault();
        var release = _truth.Incidents.Count > 0 && _events.Any(e => e.Payload is HazardStarted { Kind: HazardKind.Plume });
        branches.Add(new AarBranchPoint("Chemical hazard", zones ?? warned,
            zones is { } z
                ? z <= warned ? "Zones were already declared before the first report of chemicals." : $"Zones declared {Duration(z - warned)} after the first report of chemicals."
                : "No hazard zones declared.",
            "Within 3 min: hot, warm and cold zones (or the plume zones), a Hazmat crew, and warn the public downwind.",
            "Treat it as an ordinary fire.",
            release
                ? zones is null ? "There really was a toxic release, and crews and the public had no zones to keep them out of it." : "There really was a toxic release; the zones kept people back from it."
                : "The store never released this time, but the precaution was right.",
            zones is not { } declared ? (release ? Rating.Poor : Rating.Fair)
                : declared <= warned ? Rating.Good : Within(declared - warned, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(6))));
    }

    private void HospitalBranch(List<AarBranchPoint> branches)
    {
        var casualties = All<IncidentUpdated>().Where(x => x.Payload.CasualtiesReported >= 5).Select(x => (DateTimeOffset?)x.Event.SimTime).FirstOrDefault()
                         ?? _reports.Where(r => r.Claim.Contains("casualties", StringComparison.OrdinalIgnoreCase)).Select(r => (DateTimeOffset?)r.At).FirstOrDefault();
        if (casualties is not { } known) return;
        var alerted = All<NotificationSent>().Where(x => x.Payload.Recipient.Contains("hospital", StringComparison.OrdinalIgnoreCase))
            .Select(x => (DateTimeOffset?)x.Event.SimTime).FirstOrDefault();
        var diversion = All<HospitalStatusReported>().Where(x => x.Payload.OnDiversion).Select(x => x.Payload.Name).Distinct().ToList();
        branches.Add(new AarBranchPoint("Hospital pre-alert", alerted ?? known,
            alerted is { } a ? (a < known ? "Hospitals alerted before casualties were reported." : $"Hospitals alerted {Duration(a - known)} after casualties were reported.") : "No hospital alerted.",
            "Alert the receiving hospitals within 5 min of a multi-casualty report so they can activate their major emergency plans.",
            "Let ambulances turn up unannounced.",
            diversion.Count > 0 ? $"{string.Join(", ", diversion)} went on diversion." + (alerted is null ? " A pre-alert opens surge capacity." : "")
                : "No receiving hospital had to divert.",
            alerted is null ? (diversion.Count > 0 ? Rating.Poor : Rating.Fair) : Within(alerted - known, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10))));
    }

    private DateTimeOffset? FirstAmbulanceOnScene() =>
        All<UnitStatusChanged>().Where(x => x.Payload.Status == UnitStatus.OnScene && OwnUnit(x.Payload.UnitId)
                                            && ResourceGroups.AgencyFor(_unitTypes.GetValueOrDefault(x.Payload.UnitId)) == AgencyType.Ems)
            .Select(x => (DateTimeOffset?)x.Event.SimTime).FirstOrDefault();
}
