using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Aar;

/// <summary>
/// Health in the review (Phase 16).
/// <list type="bullet">
/// <item><b>Patients</b>: how many were tagged, how many reached a hospital that could treat them first time, how many
/// needed a transfer, and how many died waiting at the scene or in hospital. Destinations are timed from the first patient
/// with that need.</item>
/// <item><b>Blood</b>: each time a hospital ran low, and how soon command asked the IBTS for more.</item>
/// <item><b>The dead</b>: when the coroner was notified, how long the dead lay at a scene, the temporary mortuary, and
/// identifications. A body left at a scene for more than <see cref="DeadAtSceneLimit"/> is a liability.</item>
/// <item><b>Public health</b>: of everyone really exposed, how many were protected in time; when points of dispensing,
/// isolation and quarantine came after the alert.</item>
/// <item><b>Behavioural health</b>: counselling against the distress reported, the worried well at emergency departments,
/// and the families of the dead.</item>
/// </list>
/// </summary>
public sealed partial class AarAnalyzer
{
    public static readonly TimeSpan DeadAtSceneLimit = TimeSpan.FromHours(3);

    private readonly Dictionary<Guid, (DateTimeOffset At, Specialty? Need, Guid? Incident)> _tags = [];
    private readonly HashSet<Guid> _leftScene = [];
    private readonly HashSet<Guid> _arrivedAt = [];
    private int _rightFirstTime, _patientTransfers, _diedAwaiting, _diedInHospital;
    private readonly Dictionary<(Guid, Specialty), DateTimeOffset> _firstNeed = [];
    private readonly Dictionary<Guid, DateTimeOffset> _bloodLowAt = [];
    private readonly List<(DateTimeOffset LowAt, Guid Hospital, DateTimeOffset? AskedAt)> _bloodEpisodes = [];
    private readonly HashSet<Guid> _deadTruth = [];
    private readonly Dictionary<Guid, DateTimeOffset> _deadSince = [];
    private readonly List<TimeSpan> _deadAtScene = [];
    private DateTimeOffset? _firstDead, _coronerAt, _mortuaryAt;
    private int _identified, _peakDeadAtScene;
    private readonly Dictionary<Guid, (DateTimeOffset At, HealthThreatKind Kind, string Agent)> _threatAlerts = [];
    private readonly Dictionary<Guid, HealthThreatOutcome> _threatOutcomes = [];
    private readonly Dictionary<Guid, DateTimeOffset> _firstPod = [];
    private readonly Dictionary<Guid, DateTimeOffset> _isolatedAt = [];
    private DateTimeOffset? _firstDistress, _helplineAt, _facCounsellingAt;
    private readonly Dictionary<Guid, int> _distressPeak = [];
    private readonly Dictionary<Guid, int> _seenByTeam = [];
    private readonly Dictionary<Guid, int> _worriedWellNow = [];
    private int _worriedWellPeak;

    private void TrackHealth(SimEvent simEvent)
    {
        var at = simEvent.SimTime;
        var decision = simEvent.Visibility == EventVisibility.Perceived && simEvent.Source == EventSources.C2;
        switch (simEvent.Payload)
        {
            // Patients.
            case PatientTagged e:
                _tags[e.PatientId] = (at, e.Need, e.IncidentId);
                if (e is { Need: { } need, IncidentId: { } incident }) _firstNeed.TryAdd((incident, need), at);
                break;
            case PatientTransported e:
                _leftScene.Add(e.PatientId);
                break;
            case PatientArrived e when _arrivedAt.Add(e.PatientId):
                if (e.RightPlace) _rightFirstTime++;
                break;
            case PatientTransferred:
                _patientTransfers++;
                break;
            case PatientDied e:
                if (_arrivedAt.Contains(e.PatientId)) _diedInHospital++;
                else if (!_leftScene.Contains(e.PatientId)) _diedAwaiting++;
                break;

            // Blood.
            case HospitalResourcesReported e:
                if (e.BloodUnits < Systems.PatientTrackingSystem.BloodLow && _bloodLowAt.TryAdd(e.HospitalId, at))
                    _bloodEpisodes.Add((at, e.HospitalId, null));
                else if (e.BloodUnits >= Systems.PatientTrackingSystem.BloodLow) _bloodLowAt.Remove(e.HospitalId);
                _worriedWellNow[e.HospitalId] = e.WorriedWell;
                _worriedWellPeak = Math.Max(_worriedWellPeak, _worriedWellNow.Values.Sum());
                break;
            case BloodRequested e:
                var episode = _bloodEpisodes.FindLastIndex(b => b.Hospital == e.HospitalId && b.AskedAt is null);
                if (episode >= 0) _bloodEpisodes[episode] = _bloodEpisodes[episode] with { AskedAt = at };
                break;

            // The dead.
            case CasualtyInjured { Triage: Triage.Deceased } e:
                _deadTruth.Add(e.CasualtyId);
                break;
            case CasualtyChanged { Triage: Triage.Deceased } e:
                _deadTruth.Add(e.CasualtyId);
                break;
            case BodiesReported e:
                if (e.Count > 0)
                {
                    _firstDead ??= at;
                    _deadSince.TryAdd(e.IncidentId, at);
                }
                else if (_deadSince.Remove(e.IncidentId, out var since)) _deadAtScene.Add(at - since);
                _peakDeadAtScene = Math.Max(_peakDeadAtScene, e.Count);
                break;
            case NotificationSent e when e.Recipient.Contains("coroner", StringComparison.OrdinalIgnoreCase):
                _coronerAt ??= at;
                break;
            case TemporaryMortuaryOpened:
                _mortuaryAt ??= at;
                if (decision) _significant.Add((simEvent.Sequence, at, $"{Clock(at)} temporary mortuary opened"));
                break;
            case VictimIdentified:
                _identified++;
                break;

            // Public health.
            case PublicHealthAlert e:
                _threatAlerts.TryAdd(e.ThreatId, (at, e.Kind, e.Agent));
                break;
            case HealthThreatOutcome e:
                _threatOutcomes[e.ThreatId] = e;
                break;
            case DispensingPointOpened e:
                _firstPod.TryAdd(e.ThreatId, at);
                break;
            case IsolationOrdered e:
                _isolatedAt.TryAdd(e.ThreatId, at);
                if (decision) _significant.Add((simEvent.Sequence, at, $"{Clock(at)} isolation ordered"));
                break;
            case QuarantineOrdered when decision:
                _significant.Add((simEvent.Sequence, at, $"{Clock(at)} quarantine ordered"));
                break;

            // Behavioural health.
            case PsychosocialNeedReported e:
                if (e.Distressed > 0) _firstDistress ??= at;
                _distressPeak[e.IncidentId] = Math.Max(_distressPeak.GetValueOrDefault(e.IncidentId), e.Distressed);
                break;
            case CrisisCounsellingDeployed e:
                if (e.Setting == CounsellingSetting.Helpline) _helplineAt ??= at;
                if (e.Setting == CounsellingSetting.FamilyAssistanceCentre) _facCounsellingAt ??= at;
                break;
            case PsychologicalTriageReported e:
                _seenByTeam[e.DeploymentId] = e.Seen;
                break;
        }
    }

    /// <summary>What called for a health decision, and how quickly it should have followed.</summary>
    private (DateTimeOffset At, string What, TimeSpan Good, TimeSpan Fair)? HealthTrigger(DomainEvent payload) => payload switch
    {
        TransportDestinationSet { Need: { } need, HospitalId: not null } e when _firstNeed.TryGetValue((e.IncidentId, need), out var first) =>
            (first, $"First {Health.Name(need).ToLowerInvariant()} patient tagged", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)),
        BloodRequested e when _bloodLowAt.TryGetValue(e.HospitalId, out var low) =>
            (low, "Hospital reported blood low", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)),
        TemporaryMortuaryOpened when _firstDead is { } dead => (dead, "First dead reported", TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(45)),
        IsolationOrdered e when _threatAlerts.TryGetValue(e.ThreatId, out var alert) =>
            (alert.At, $"HSE Public Health confirmed {alert.Agent}", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)),
        QuarantineOrdered e when _threatAlerts.TryGetValue(e.ThreatId, out var alert) =>
            (alert.At, $"HSE Public Health confirmed {alert.Agent}", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)),
        DispensingPointOpened e when _threatAlerts.TryGetValue(e.ThreatId, out var alert) =>
            (alert.At, $"HSE Public Health confirmed {alert.Agent}", TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(45)),
        CrisisCounsellingDeployed when _firstDistress is { } distress => (distress, "Distress first reported", TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(45)),
        _ => null,
    };

    private AarHealth HealthSummary()
    {
        var atScene = _deadAtScene.Concat(_deadSince.Values.Select(since => End - since)).ToList();
        var exposed = _threatOutcomes.Values.Sum(o => o.Exposed);
        return new AarHealth(_tags.Count, _arrivedAt.Count, _rightFirstTime, _patientTransfers, _diedAwaiting, _diedInHospital, _deadTruth.Count,
            _firstDead is { } first && _coronerAt is { } coroner ? coroner - first : null, atScene.Count > 0 ? atScene.Max() : null, _identified,
            exposed, _threatOutcomes.Values.Sum(o => o.ProtectedInTime), _threatOutcomes.Values.Sum(o => o.Ill), _distressPeak.Values.Sum(),
            _seenByTeam.Values.Sum(), _worriedWellPeak);
    }

    /// <summary>Liabilities from the dead: bodies left too long at a scene.</summary>
    private IEnumerable<AarLiability> HealthLiabilities() =>
        _deadSince.Where(d => End - d.Value > DeadAtSceneLimit).Select(d => new AarLiability(d.Value, "The dead left at the scene",
            $"The dead at {(_incidents.TryGetValue(d.Key, out var i) ? i.Number : "an incident")} were still there {Duration(End - d.Value)} later" +
            (_coronerAt is null ? ": the coroner was never notified, so nobody could move them." : ".")));

    private IEnumerable<AarFinding> HealthFindings()
    {
        var summary = HealthSummary();
        if (summary.Arrived > 0)
            yield return new AarFinding(summary.RightFirstTimePercent >= 80,
                $"{summary.RightFirstTime} of {summary.Arrived} patients reached a hospital that could treat them first time ({summary.RightFirstTimePercent}%)" +
                (summary.Transfers > 0 ? $"; {summary.Transfers} needed a transfer." : "."));
        if (summary.DiedAwaitingTransport > 0)
            yield return new AarFinding(false, $"{summary.DiedAwaitingTransport} tagged patient(s) died at the scene waiting for transport.");

        foreach (var (low, hospital, asked) in _bloodEpisodes)
        {
            var name = _cop.Hospitals.FirstOrDefault(h => h.Id == hospital)?.Name ?? "A hospital";
            yield return asked is { } when
                ? new AarFinding(when - low <= TimeSpan.FromMinutes(15), $"{Clock(low)} {name} reported blood low; the IBTS was asked {Duration(when - low)} later.")
                : new AarFinding(false, $"{Clock(low)} {name} reported blood low and nobody asked the IBTS for more.");
        }

        if (_firstDead is { } firstDead)
        {
            yield return _coronerAt is { } coroner
                ? new AarFinding(coroner - firstDead <= TimeSpan.FromMinutes(20), $"Coroner notified {Duration(coroner - firstDead)} after the first dead were reported.")
                : new AarFinding(false, "The dead were reported and the coroner was never notified: nobody could be recovered.");
            if (summary.LongestAtScene is { } longest && longest > TimeSpan.FromHours(2))
                yield return new AarFinding(false, $"The dead lay at the scene for up to {Duration(longest)}.");
            if (_mortuaryAt is null && _peakDeadAtScene >= 3)
                yield return new AarFinding(false, $"Up to {_peakDeadAtScene} dead at a scene and no temporary mortuary: they went to hospital mortuaries, or stayed.");
            if (summary.Fatalities > 0)
                yield return new AarFinding(summary.Identified >= summary.Fatalities, $"{summary.Identified} of {summary.Fatalities} dead identified by the end.");
        }

        foreach (var (threat, (alertAt, kind, agent)) in _threatAlerts)
        {
            var outcome = _threatOutcomes.GetValueOrDefault(threat);
            if (outcome is { Exposed: > 0 } && kind != HealthThreatKind.Infectious)
            {
                var percent = (int)Math.Round(100.0 * outcome.ProtectedInTime / outcome.Exposed);
                yield return new AarFinding(percent >= 70, $"{agent}: {outcome.ProtectedInTime} of {outcome.Exposed} exposed given {Health.Countermeasure(kind)} in time ({percent}%)" +
                                                           (outcome.Ill > 0 ? $"; {outcome.Ill} fell ill, {outcome.Deaths} died." : "."));
            }
            if (!_firstPod.ContainsKey(threat))
                yield return new AarFinding(false, $"{agent}: no point of dispensing was opened after HSE Public Health's alert at {Clock(alertAt)}.");
            if (kind == HealthThreatKind.Infectious)
            {
                yield return _isolatedAt.TryGetValue(threat, out var isolated)
                    ? new AarFinding(isolated - alertAt <= TimeSpan.FromMinutes(30), $"{agent}: cases isolated {Duration(isolated - alertAt)} after the alert" +
                                                                                        (outcome is not null ? $"; {outcome.Ill} ill in all." : "."))
                    : new AarFinding(false, $"{agent}: the cases were never isolated" + (outcome is not null ? $"; {outcome.Ill} ill in all." : "."));
            }
        }

        if (summary.Distressed >= 20)
            yield return summary.SeenByCounsellors > 0
                ? new AarFinding(true, $"Crisis counselling saw {summary.SeenByCounsellors} of about {summary.Distressed} people in distress.")
                : new AarFinding(false, $"About {summary.Distressed} people were reported in distress and no crisis counselling was arranged.");
        if (summary.WorriedWellPeak >= 5 && _helplineAt is null)
            yield return new AarFinding(false, $"Up to {summary.WorriedWellPeak} people with no injury filled emergency department places; a helpline would have kept most of them away.");
        if (_centreOpened is not null && _deadTruth.Count > 0 && _facCounsellingAt is null)
            yield return new AarFinding(false, "Families of the dead came to the family assistance centre and no counsellors were there for them.");
    }
}
