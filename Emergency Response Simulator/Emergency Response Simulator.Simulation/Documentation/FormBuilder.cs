using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Documentation;

/// <summary>
/// The documentation unit (Phase 18): standard ICS forms, the IAP package, situation reports and the decision log,
/// generated from what command knows — the COP and the perceived event stream — never from ground truth. Regenerating a
/// form later says the same about the same events; the decision log attributes every order, request, approval and plan to
/// the participant who gave it, in the role they held (Phase 11).
/// </summary>
public sealed class FormBuilder
{
    private readonly ICopService _cop;
    private readonly IReadOnlyList<SimEvent> _events;
    private readonly DateTimeOffset _now;

    /// <param name="events">The session's events; ground truth among them is ignored.</param>
    public FormBuilder(ICopService cop, IEnumerable<SimEvent> events)
    {
        _cop = cop;
        _events = [.. events.Where(e => e.Visibility == EventVisibility.Perceived).OrderBy(e => e.Sequence)];
        _now = cop.AsOf;
    }

    // ---- The forms ----

    /// <param name="incidentId">The incident (default: the highest-priority open one).</param>
    /// <param name="planId">For the IAP forms: a version of the plan (default: the one in force, else the period's latest).</param>
    /// <param name="participantId">For the ICS 214 and the decision log: one person's (default: everyone's).</param>
    public IcsForm Build(IcsFormKind kind, Guid? incidentId = null, Guid? planId = null, Guid? participantId = null)
    {
        var incident = Incident(incidentId);
        var plan = IcsForms.FromPlan(kind) ? Plan(incident, planId) : null;
        var sections = kind switch
        {
            IcsFormKind.Ics201 => Ics201(incident),
            IcsFormKind.Ics202 => Ics202(incident, plan),
            IcsFormKind.Ics203 => Ics203(incident, plan),
            IcsFormKind.Ics204 => Ics204(incident, plan),
            IcsFormKind.Ics205 => Ics205(incident, plan),
            IcsFormKind.Ics205A => Ics205A(incident),
            IcsFormKind.Ics206 => Ics206(incident, plan),
            IcsFormKind.Ics207 => Ics207(incident, plan),
            IcsFormKind.Ics208 => Ics208(incident, plan),
            IcsFormKind.Ics209 => Ics209(incident),
            IcsFormKind.Ics210 => Ics210(incident),
            IcsFormKind.Ics211 => Ics211(incident),
            IcsFormKind.Ics213 => Ics213(incident),
            IcsFormKind.Ics214 => Ics214(incident, participantId),
            IcsFormKind.Ics215 => Ics215(incident, plan),
            IcsFormKind.Ics215A => Ics215A(incident, plan),
            IcsFormKind.Ics220 => Ics220(incident, plan),
            IcsFormKind.Sitrep => Sitreps(),
            IcsFormKind.DecisionLog => [DecisionLogSection(DecisionLog(incident?.Id, participantId))],
            _ => Cover(incident, plan),
        };
        return new IcsForm(kind, IcsForms.Title(kind), Header(incident, plan, participantId, kind), sections, _now, PreparedBy(kind, incident, plan, participantId));
    }

    /// <summary>The IAP as issued: the cover with its version history and changes, then each of its forms.</summary>
    public IReadOnlyList<IcsForm> Package(Guid? incidentId = null, Guid? planId = null)
    {
        var incident = Incident(incidentId);
        var plan = Plan(incident, planId);
        return [Build(IcsFormKind.IapPackage, incident?.Id, plan?.Id), .. IcsForms.IapForms.Select(k => Build(k, incident?.Id, plan?.Id))];
    }

    private Incident? Incident(Guid? incidentId) =>
        incidentId is { } id ? _cop.FindIncident(id)
        : _cop.Incidents.Where(i => i.Status != IncidentStatus.Closed).OrderByDescending(i => i.Priority).ThenBy(i => i.ReportedAt).FirstOrDefault()
          ?? _cop.Incidents.OrderByDescending(i => i.ReportedAt).FirstOrDefault();

    private IncidentActionPlan? Plan(Incident? incident, Guid? planId)
    {
        if (planId is { } id) return _cop.ActionPlans.FirstOrDefault(p => p.Id == id);
        if (incident is null) return null;
        return _cop.PlanInForce(incident.Id, _now)
               ?? (_cop.CurrentPeriod(incident.Id, _now) is { } period ? _cop.VersionsOf(period.Id).LastOrDefault() : null);
    }

    private OperationalPeriod? Period(Incident? incident, IncidentActionPlan? plan) =>
        plan is not null ? _cop.OperationalPeriods.FirstOrDefault(p => p.Id == plan.OperationalPeriodId)
        : incident is null ? null : _cop.CurrentPeriod(incident.Id, _now);

    private List<FormField> Header(Incident? incident, IncidentActionPlan? plan, Guid? participantId, IcsFormKind kind)
    {
        var period = Period(incident, plan);
        var header = new List<FormField>
        {
            new("Incident", incident is null ? "—" : $"{incident.Name ?? EventDescriber.Humanize(incident.Type)} ({incident.Number})"),
            new("Operational period", period is null ? "—" : $"{period.Number}: {period.Start.ToLocalTime():dd/MM HH:mm}–{period.End.ToLocalTime():HH:mm}"),
            new("Date / time prepared", _now.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)),
        };
        if (plan is not null) header.Add(new("IAP version", $"v{plan.Version} · {IapDocument.StatusLine(plan)}"));
        if (participantId is { } p && kind is IcsFormKind.Ics214 or IcsFormKind.DecisionLog)
            header.Add(new("Name", _cop.Participants.FirstOrDefault(x => x.Id == p)?.Label ?? "Unknown participant"));
        return header;
    }

    /// <summary>The position that owns the form, and who fills it (named on the chart, or the participant playing it).</summary>
    private string PreparedBy(IcsFormKind kind, Incident? incident, IncidentActionPlan? plan, Guid? participantId)
    {
        if (participantId is { } p && kind == IcsFormKind.Ics214 && _cop.Participants.FirstOrDefault(x => x.Id == p) is { } person)
            return person.Label;
        if (plan?.PreparedBy is { } preparer && IcsForms.FromPlan(kind) && IcsForms.Owner(kind) == IcsRole.Planning)
            return $"{preparer} (Planning)";
        var owner = IcsForms.Owner(kind);
        var name = incident?.Command.Positions.GetValueOrDefault(owner)
                   ?? _cop.Participants.FirstOrDefault(x => x.Present && x.Role == Roles.Of(owner))?.DisplayName;
        return $"{IapDocument.RoleName(owner)}" + (name is null ? " (unfilled: the Incident Commander)" : $": {name}");
    }

    private static string Time(DateTimeOffset? at) => at is { } t ? t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : "—";

    private static FormTable Table(IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<string>> rows) => new(columns, [.. rows]);

    private static FormSection Missing(string title, string why) => new(title, Text: why);

    private string Unit(Guid id) => _cop.FindUnit(id)?.Callsign ?? "unknown unit";

    // ---- 201: incident briefing ----

    private List<FormSection> Ics201(Incident? incident)
    {
        if (incident is null) return [Missing("Incident", "No incident is open.")];
        var sections = new List<FormSection>
        {
            new("Situation", Situation(incident)),
            new("Map: zones and facilities", Table: Table(["Zone", "Type", "In force since"],
                _cop.Zones.Where(z => z.IncidentId == incident.Id || z.IncidentId is null).Select(z => (IReadOnlyList<string>)[z.Name, EventDescriber.Humanize(z.Type), Time(z.EffectiveFrom)])),
                Text: $"Incident at {Place(incident)}."),
        };
        var plan = _cop.PlanInForce(incident.Id, _now);
        sections.Add(new("Current objectives", Text: plan is null
            ? "No IAP in force yet: objectives as given in the initial briefing."
            : string.Join(Environment.NewLine, plan.Content.OperationalObjectives.Select(o => $"{plan.Content.NumberOf(o.Id)} {o.Statement}"))));
        sections.Add(new("Actions taken so far", Table: Table(["Time", "Action", "By"],
            DecisionLog(incident.Id).Where(r => r.Kind != "Plan").Select(r => (IReadOnlyList<string>)[Time(r.At), r.Text, r.Actor]))));
        sections.Add(Organisation(incident, null));
        sections.Add(new("Resources", Table: ResourceTable(incident)));
        return sections;
    }

    private List<FormField> Situation(Incident incident)
    {
        var fields = new List<FormField>
        {
            new("Type", EventDescriber.Humanize(incident.Type)),
            new("Location", Place(incident)),
            new("Priority / status", $"{incident.Priority} · {EventDescriber.Humanize(incident.Status)}"),
            new("Reported", incident.ReportedAt.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture)),
            new("Incident commander", incident.Command.Positions.GetValueOrDefault(IcsRole.IncidentCommander) ?? incident.IncidentCommanderName ?? "not assigned"),
            new("Threats", incident.Threats.Count == 0 ? "none reported" : string.Join(", ", incident.Threats)),
            new("Casualties", $"{incident.CasualtiesReported} reported, {incident.CasualtiesConfirmed} confirmed"),
            new("Evacuation", incident.EvacuationRequired ? "required" : "not required"),
        };
        if (_cop.Weather is { } weather)
            fields.Add(new("Weather", $"wind from the {GeoMath.CompassPoint(weather.WindFromDegrees)} at {weather.WindSpeedMps:F0} m/s, {weather.TemperatureC:F0} °C ({weather.Source})"));
        if (incident.Command.IsUnified)
            fields.Add(new("Unified command", string.Join(", ", incident.Command.Unified.Select(m => m.Agency))));
        return fields;
    }

    private static string Place(Incident incident) => incident.Address ?? $"{incident.Location.Y:F5}, {incident.Location.X:F5}";

    /// <summary>When each unit was sent to the incident and when it got there, from the event stream.</summary>
    private Dictionary<Guid, (DateTimeOffset? Dispatched, DateTimeOffset? Arrived)> Movements(Guid incidentId)
    {
        var movements = new Dictionary<Guid, (DateTimeOffset? Dispatched, DateTimeOffset? Arrived)>();
        var sentHere = new HashSet<Guid>();
        foreach (var e in _events)
            switch (e.Payload)
            {
                case UnitDispatched d when d.IncidentId == incidentId:
                    sentHere.Add(d.UnitId);
                    movements[d.UnitId] = (movements.GetValueOrDefault(d.UnitId).Dispatched ?? e.SimTime, movements.GetValueOrDefault(d.UnitId).Arrived);
                    break;
                case UnitDispatched d:
                    sentHere.Remove(d.UnitId);
                    break;
                case UnitStatusChanged { Status: UnitStatus.OnScene or UnitStatus.Operating } s when sentHere.Contains(s.UnitId):
                    var (dispatched, arrived) = movements[s.UnitId];
                    movements[s.UnitId] = (dispatched, arrived ?? e.SimTime);
                    break;
            }
        return movements;
    }

    private FormTable ResourceTable(Incident incident)
    {
        var movements = Movements(incident.Id);
        return Table(["Resource", "Type", "Agency", "Dispatched", "On scene", "Status", "Assignment"],
            incident.AssignedUnits.OrderBy(u => u.Callsign, StringComparer.Ordinal).Select(u => (IReadOnlyList<string>)
            [
                u.Callsign, ResourceGroups.Label(u.Type), u.Agency?.ShortName ?? "—", Time(movements.GetValueOrDefault(u.Id).Dispatched),
                Time(movements.GetValueOrDefault(u.Id).Arrived), EventDescriber.Humanize(u.Status), u.Tasking ?? incident.Command.GroupOf(u.Id)?.Name ?? "",
            ]));
    }

    private FormSection Organisation(Incident incident, IncidentActionPlan? plan)
    {
        var fields = Enum.GetValues<IcsRole>()
            .Select(role => (role, name: plan?.Content.NameFor(role) ?? incident.Command.Positions.GetValueOrDefault(role)))
            .Where(p => p.name is not null)
            .Select(p => new FormField(IapDocument.RoleName(p.role), p.name!))
            .ToList();
        if (fields.Count == 0) fields.Add(new("Incident Commander", "not assigned"));
        return new("Organisation", fields);
    }

    // ---- The IAP forms ----

    private static FormSection NoPlan(Incident? incident) =>
        Missing("No incident action plan", incident is null ? "No incident is open." : "No IAP has been drafted for this operational period: open the IAP builder.");

    private List<FormSection> Ics202(Incident? incident, IncidentActionPlan? plan)
    {
        if (plan is null) return [NoPlan(incident)];
        var c = plan.Content;
        var rows = new List<IReadOnlyList<string>>();
        for (var s = 0; s < c.Objectives.Count; s++)
        {
            rows.Add([$"{s + 1}", c.Objectives[s].Statement, "", ""]);
            foreach (var o in c.Objectives[s].Operational)
                rows.Add([c.NumberOf(o.Id) ?? "", o.Statement, o.Responsible ?? "", (o.PerformanceTarget ?? "") + (o.TargetTime is { } due ? $" by {Time(due)}" : "")]);
        }
        var included = IcsForms.IapForms.Where(k => k != IcsFormKind.Ics202).Select(IcsForms.Number);
        return
        [
            new("Objectives", Table: Table(["No.", "Objective", "Responsible", "Target"], rows)),
            new("Operational period command emphasis", Text: c.CommandersIntent ?? "—"),
            new("General situational awareness", Text: c.SituationSummary ?? "—"),
            new("Site safety plan", [new("Required", c.Safety.Hazards.Count > 0 ? "Yes: see ICS 208 and 215A" : "No hazards listed"),
                new("Safety message", c.Safety.Message ?? "—")]),
            new("Attachments", Text: string.Join(", ", included)),
        ];
    }

    private List<FormSection> Ics203(Incident? incident, IncidentActionPlan? plan)
    {
        if (incident is null) return [Missing("Organisation", "No incident is open.")];
        var groups = plan?.Content.Groups.Select(g => (g.Name, Kind: g.Kind.ToString(), g.Supervisor,
                         Units: plan.Content.Assignments.Count(a => string.Equals(a.Group, g.Name, StringComparison.OrdinalIgnoreCase))))
                     ?? incident.Command.Groups.Select(g => (g.Name, Kind: g.Kind.ToString(), g.Supervisor, Units: g.UnitIds.Count));
        var sections = new List<FormSection>
        {
            Organisation(incident, plan),
            new("Divisions and groups", Table: Table(["Division / group", "Kind", "Supervisor", "Resources"],
                groups.Select(g => (IReadOnlyList<string>)[g.Name, g.Kind, g.Supervisor ?? "—", g.Units.ToString(CultureInfo.InvariantCulture)]))),
            new("Agency representatives", Table: Table(["Agency", "In unified command since"],
                incident.Command.Unified.Select(m => (IReadOnlyList<string>)[m.Agency, Time(m.JoinedAt)]))),
        };
        if (plan is null) sections.Insert(0, new("Source", Text: "No IAP for this period: the organisation as the COP shows it now."));
        return sections;
    }

    private List<FormSection> Ics204(Incident? incident, IncidentActionPlan? plan)
    {
        if (plan is null) return [NoPlan(incident)];
        var c = plan.Content;
        var groups = c.Groups.Select(g => (Name: g.Name, g.Supervisor)).ToList();
        if (c.Assignments.Any(a => string.IsNullOrWhiteSpace(a.Group))) groups.Add(("Reporting to Operations", c.NameFor(IcsRole.Operations)));
        var sections = new List<FormSection>();
        foreach (var (name, supervisor) in groups)
        {
            var direct = name == "Reporting to Operations";
            var assignments = c.Assignments.Where(a => direct ? string.IsNullOrWhiteSpace(a.Group) : string.Equals(a.Group, name, StringComparison.OrdinalIgnoreCase)).ToList();
            var objectives = assignments.Select(a => a.ObjectiveId).OfType<Guid>().Distinct()
                .Select(id => c.OperationalObjectives.FirstOrDefault(o => o.Id == id)).OfType<OperationalObjective>().ToList();
            var channel = c.Communications.Channels.FirstOrDefault(ch => ch.AssignedTo.Contains(name, StringComparison.OrdinalIgnoreCase))
                          ?? c.Communications.Channels.FirstOrDefault(ch => ch.Function.Contains("tactical", StringComparison.OrdinalIgnoreCase));
            var safety = c.Safety.Analysis.FirstOrDefault(r => string.Equals(r.Area, name, StringComparison.OrdinalIgnoreCase))
                         ?? c.Safety.Analysis.FirstOrDefault(r => r.Area == "All crews");
            sections.Add(new($"{name}",
                [
                    new("Supervisor", supervisor ?? "—"),
                    new("Radio", channel is null ? "see ICS 205" : $"{channel.Channel} ({channel.Function})"),
                    new("Work assignment", objectives.Count == 0 ? "as below" : string.Join("; ", objectives.Select(o => $"{c.NumberOf(o.Id)} {o.Statement}"))),
                    new("Special instructions", string.Join("; ", objectives.SelectMany(o => o.Tactics).Select(t => t.Description).Where(t => t.Length > 0).DefaultIfEmpty("—"))),
                    new("Safety", safety is null ? c.Safety.Message ?? "—" : $"{safety.Hazards} → {safety.Mitigations}"),
                ],
                Table(["Resource", "Assignment", "Objective"],
                    assignments.Select(a => (IReadOnlyList<string>)[a.Callsign, a.Assignment, a.ObjectiveId is { } id ? c.NumberOf(id) ?? "" : ""]))));
        }
        return sections.Count > 0 ? sections : [Missing("Assignments", "The plan assigns no resources.")];
    }

    private List<FormSection> Ics205(Incident? incident, IncidentActionPlan? plan)
    {
        if (plan is not null && plan.Content.Communications.Channels.Count > 0)
            return
            [
                new("Radio plan", Table: Table(["Function", "Channel", "Assigned to", "Remarks"],
                    plan.Content.Communications.Channels.Select(ch => (IReadOnlyList<string>)[ch.Function, ch.Channel, ch.AssignedTo, ch.Remarks ?? ""]))),
                new("Special instructions", Text: plan.Content.Communications.Notes ?? "—"),
            ];
        // No plan: the channels the units at the incident are actually on.
        var units = incident?.AssignedUnits ?? [];
        return
        [
            new("Source", Text: "No radio plan in an IAP: the channels in use now."),
            new("Channels in use", Table: Table(["Channel", "Units"],
                units.Where(u => u.Channel is not null).GroupBy(u => u.Channel!).Select(g => (IReadOnlyList<string>)[g.Key, string.Join(", ", g.Select(u => u.Callsign))]))),
        ];
    }

    private List<FormSection> Ics205A(Incident? incident)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var agency in _cop.Agencies.OrderBy(a => a.Name))
            rows.Add([agency.Name, EventDescriber.Humanize(agency.Type), agency.AiControlled ? "via its own control room" : "on our radio",
                agency.Jurisdiction ?? ""]);
        foreach (var hospital in _cop.Hospitals.OrderBy(h => h.Name))
            rows.Add([hospital.Name, "Receiving hospital", "emergency department", $"ED {hospital.Occupied}/{hospital.Capacity}"]);
        foreach (var recipient in _cop.Notifications.Select(n => n.Recipient).Distinct(StringComparer.OrdinalIgnoreCase).Order())
            if (!rows.Any(r => string.Equals(r[0], recipient, StringComparison.OrdinalIgnoreCase)))
                rows.Add([recipient, "Notified", "by phone / message", ""]);
        foreach (var shelter in _cop.Protection.Shelters)
            rows.Add([shelter.Name, "Rest centre", "manager on site", $"{shelter.Occupants}/{shelter.Capacity}"]);
        return [new("Contacts", Table: Table(["Agency / facility", "Role", "How to reach", "Notes"], rows))];
    }

    private List<FormSection> Ics206(Incident? incident, IncidentActionPlan? plan)
    {
        var m = plan?.Content.Medical;
        var sections = new List<FormSection>();
        if (m is null) sections.Add(new("Source", Text: "No IAP for this period: medical facilities as the COP shows them."));
        else
            sections.Add(new("Medical arrangements",
            [
                new("Medical lead", m.MedicalLead ?? "—"),
                new("Casualty clearing station", m.CasualtyClearingStation ?? "—"),
                new("Ambulance loading point", m.AmbulanceLoadingPoint ?? "—"),
                new("Injured responder", m.EmergencyProcedures ?? "—"),
                new("Temporary mortuary", m.Mortuary ?? _cop.Health.Fatalities.MortuaryName ?? "none open"),
            ]));
        sections.Add(new("Medical units", Table: Table(["Unit"],
            (m?.MedicalUnits.Count > 0 ? m.MedicalUnits
                : incident?.AssignedUnits.Where(u => ResourceGroups.AgencyFor(u.Type) == AgencyType.Ems).Select(u => u.Callsign).ToList() ?? [])
            .Select(u => (IReadOnlyList<string>)[u]))));
        var planned = m?.Hospitals ?? [];
        sections.Add(new("Hospitals", Table: Table(["Hospital", "Travel", "Specialist services", "ED now"],
            _cop.Hospitals.OrderBy(h => planned.FindIndex(p => string.Equals(p.Name, h.Name, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : 99).ThenBy(h => h.Name)
                .Select(h =>
                {
                    var p = planned.FirstOrDefault(x => string.Equals(x.Name, h.Name, StringComparison.OrdinalIgnoreCase));
                    var beds = _cop.Health.Hospitals.GetValueOrDefault(h.Id)?.Specialties.Select(s => $"{Health.Name(s.Specialty)} {s.Free}/{s.Beds}") ?? [];
                    return (IReadOnlyList<string>)
                    [
                        h.Name, p?.TravelMinutes is { } t ? $"{t:F0} min" : "—", string.Join(", ", beds.DefaultIfEmpty(p?.Capabilities ?? "—")),
                        $"{h.Occupied}/{h.Capacity}" + (h.OnDiversion ? " DIVERTING" : ""),
                    ];
                }))));
        var destinations = m?.PatientDestinations.Count > 0 ? m.PatientDestinations
            : _cop.Health.Destinations.Where(d => d.IncidentId == incident?.Id)
                .Select(d => $"{Health.Need(d.Need)} → {_cop.Hospitals.FirstOrDefault(h => h.Id == d.HospitalId)?.Name}").ToList();
        sections.Add(new("Patient destinations", Text: destinations.Count == 0 ? "Nearest open hospital (none set)." : string.Join(Environment.NewLine, destinations)));
        var patients = _cop.Health.Patients.Where(p => p.IncidentId == incident?.Id).ToList();
        if (patients.Count > 0)
            sections.Add(new("Patients so far", [new("Tagged", patients.Count.ToString(CultureInfo.InvariantCulture)),
                new("At the scene / in hospital / died", $"{patients.Count(p => p.State == CasualtyState.TreatedOnScene)} / {patients.Count(p => p.ArrivedAt is not null)} / {patients.Count(p => p.DiedAt is not null)}")]));
        return sections;
    }

    private List<FormSection> Ics207(Incident? incident, IncidentActionPlan? plan)
    {
        if (incident is null) return [Missing("Organisation chart", "No incident is open.")];
        string Name(IcsRole role) => plan?.Content.NameFor(role) ?? incident.Command.Positions.GetValueOrDefault(role) ?? "(unfilled)";
        var lines = new List<string>
        {
            $"Incident Commander: {Name(IcsRole.IncidentCommander)}" +
            (incident.Command.IsUnified ? $"  [unified command with {string.Join(", ", incident.Command.Unified.Select(u => u.Agency))}]" : ""),
        };
        foreach (var staff in new[] { IcsRole.Safety, IcsRole.Liaison, IcsRole.PublicInformation })
            lines.Add($"├─ {IapDocument.RoleName(staff)}: {Name(staff)}");
        lines.Add($"├─ {IapDocument.RoleName(IcsRole.Operations)}: {Name(IcsRole.Operations)}");
        var groups = plan?.Content.Groups.Select(g => (g.Name, g.Supervisor, Units: plan.Content.Assignments
                         .Where(a => string.Equals(a.Group, g.Name, StringComparison.OrdinalIgnoreCase)).Select(a => a.Callsign).ToList()))
                     ?? incident.Command.Groups.Select(g => (g.Name, g.Supervisor, Units: g.UnitIds.Select(Unit).ToList()));
        foreach (var (name, supervisor, units) in groups)
            lines.Add($"│   ├─ {name} ({supervisor ?? "no supervisor"}): {(units.Count == 0 ? "no resources" : string.Join(", ", units))}");
        foreach (var section in new[] { IcsRole.Planning, IcsRole.Logistics, IcsRole.FinanceAdmin })
            lines.Add($"{(section == IcsRole.FinanceAdmin ? "└─" : "├─")} {IapDocument.RoleName(section)}: {Name(section)}");
        return [new("Chart", Text: string.Join(Environment.NewLine, lines))];
    }

    private List<FormSection> Ics208(Incident? incident, IncidentActionPlan? plan)
    {
        if (plan is null) return [NoPlan(incident)];
        var s = plan.Content.Safety;
        return
        [
            new("Safety message", Text: s.Message ?? "—"),
            new("Hazards and mitigations", Table: Table(["Hazard", "Mitigation"], s.Hazards.Select(h => (IReadOnlyList<string>)[h.Hazard, h.Mitigation ?? "—"]))),
            new("Protective equipment and accountability",
            [
                new("PPE", s.Ppe.Count == 0 ? "—" : string.Join("; ", s.Ppe)),
                new("Accountability", s.Accountability ?? "—"),
                new("Safety Officer", plan.Content.NameFor(IcsRole.Safety) ?? "(the Incident Commander)"),
            ]),
        ];
    }

    private List<FormSection> Ics215(Incident? incident, IncidentActionPlan? plan)
    {
        if (plan is null) return [NoPlan(incident)];
        var c = plan.Content;
        var staging = _cop.Zones.FirstOrDefault(z => z.IncidentId == incident?.Id && z.Type == ZoneType.StagingArea)?.Name ?? "the incident command post";
        var areas = c.Groups.Select(g => g.Name).ToList();
        if (c.Assignments.Any(a => string.IsNullOrWhiteSpace(a.Group))) areas.Add("Reporting to Operations");
        return
        [
            new("Worksheet", Table: Table(["Division / group", "Work assignment", "Resources (have)", "Overhead", "Report to", "Special equipment"],
                areas.Select(area =>
                {
                    var direct = area == "Reporting to Operations";
                    var assigned = c.Assignments.Where(a => direct ? string.IsNullOrWhiteSpace(a.Group) : string.Equals(a.Group, area, StringComparison.OrdinalIgnoreCase)).ToList();
                    var units = assigned.Select(a => _cop.FindUnit(a.UnitId)).OfType<Unit>().ToList();
                    var objectives = assigned.Select(a => a.ObjectiveId).OfType<Guid>().Distinct().Select(id => c.NumberOf(id)).OfType<string>();
                    return (IReadOnlyList<string>)
                    [
                        area, string.Join("; ", assigned.Select(a => a.Assignment).Where(a => a.Length > 0).Distinct().DefaultIfEmpty(string.Join(", ", objectives))),
                        string.Join(", ", units.GroupBy(u => ResourceGroups.Label(u.Type)).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}")),
                        direct ? c.NameFor(IcsRole.Operations) ?? "—" : c.Groups.FirstOrDefault(g => g.Name == area)?.Supervisor ?? "—",
                        staging, string.Join(", ", units.SelectMany(u => u.Capabilities).Distinct().Take(4)),
                    ];
                }))),
            new("Resources required", Text: string.Join(Environment.NewLine,
                c.OperationalObjectives.Where(o => !string.IsNullOrWhiteSpace(o.ResourcesSummary)).Select(o => $"{c.NumberOf(o.Id)}: {o.ResourcesSummary}").DefaultIfEmpty("As assigned."))),
        ];
    }

    private List<FormSection> Ics215A(Incident? incident, IncidentActionPlan? plan)
    {
        if (plan is null) return [NoPlan(incident)];
        var s = plan.Content.Safety;
        var rows = s.Analysis.Count > 0
            ? s.Analysis.Select(r => (IReadOnlyList<string>)[r.Area, r.Hazards, r.Mitigations])
            : s.Hazards.Count > 0
                ? [["All crews", string.Join("; ", s.Hazards.Select(h => h.Hazard)), string.Join("; ", s.Hazards.Select(h => h.Mitigation).OfType<string>())]]
                : [];
        return
        [
            new("Safety analysis", Table: Table(["Division / group", "Hazards / risks", "Mitigations"], rows)),
            new("Approved", [new("Safety Officer", plan.Content.NameFor(IcsRole.Safety) ?? "(the Incident Commander)"),
                new("Plan approved", plan.ApprovedAt is { } at ? $"{Time(at)} by {plan.ApprovedBy}" : "not yet")]),
        ];
    }

    private List<FormSection> Ics220(Incident? incident, IncidentActionPlan? plan)
    {
        var aircraft = _cop.Units.Where(u => u.Type is UnitType.Helicopter or UnitType.MedevacHelicopter or UnitType.Drone).OrderBy(u => u.Callsign).ToList();
        var air = plan?.Content.Communications.Channels.Where(c => c.Function.Contains("air", StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        var landing = _cop.Zones.Where(z => z.Type == ZoneType.LandingZone).Select(z => z.Name).ToList();
        if (aircraft.Count == 0 && landing.Count == 0) return [new("Air operations", Text: "No air operations this period.")];
        return
        [
            new("Aircraft", Table: Table(["Aircraft", "Type", "Agency", "Status", "Assignment"],
                aircraft.Select(u => (IReadOnlyList<string>)[u.Callsign, ResourceGroups.Label(u.Type), u.Agency?.ShortName ?? "—", EventDescriber.Humanize(u.Status),
                    u.AssignedIncidentId == incident?.Id ? u.Tasking ?? "at the incident" : "not assigned"]))),
            new("Frequencies and landing zones",
            [
                new("Air-to-ground", air.Count == 0 ? "not in the radio plan" : string.Join(", ", air.Select(c => c.Channel))),
                new("Landing zones", landing.Count == 0 ? "none declared" : string.Join(", ", landing)),
            ]),
        ];
    }

    // ---- 209: incident status summary ----

    private List<FormSection> Ics209(Incident? incident)
    {
        if (incident is null) return [Missing("Status", "No incident is open.")];
        var sections = new List<FormSection> { new("Incident", Situation(incident)) };

        var units = incident.AssignedUnits;
        sections.Add(new("Resources", Table: Table(["Agency", "Committed", "En route", "On scene / working", "Out of service"],
            units.GroupBy(u => u.Agency?.ShortName ?? EventDescriber.Humanize(ResourceGroups.AgencyFor(u.Type))).OrderBy(g => g.Key).Select(g => (IReadOnlyList<string>)
            [
                g.Key, g.Count().ToString(CultureInfo.InvariantCulture),
                g.Count(u => u.Status is UnitStatus.Dispatched or UnitStatus.EnRoute).ToString(CultureInfo.InvariantCulture),
                g.Count(u => u.Status is UnitStatus.OnScene or UnitStatus.Operating).ToString(CultureInfo.InvariantCulture),
                g.Count(u => u.Status == UnitStatus.OutOfService).ToString(CultureInfo.InvariantCulture),
            ]))));

        var health = _cop.Health;
        var patients = health.Patients.Where(p => p.IncidentId == incident.Id).ToList();
        sections.Add(new("Casualties",
        [
            new("Reported / confirmed", $"{incident.CasualtiesReported} / {incident.CasualtiesConfirmed}"),
            new("Patients tagged", patients.Count.ToString(CultureInfo.InvariantCulture)),
            new("In hospital", patients.Count(p => p.ArrivedAt is not null && p.DiedAt is null).ToString(CultureInfo.InvariantCulture)),
            new("Dead", (health.Fatalities.AtScene.GetValueOrDefault(incident.Id) + health.Fatalities.Recovered).ToString(CultureInfo.InvariantCulture)),
        ]));

        var protection = _cop.Protection;
        var evacuations = protection.Evacuations.Where(z => z.IncidentId == incident.Id && z.ReEntryAt is null).ToList();
        if (evacuations.Count > 0 || protection.Shelters.Count > 0)
            sections.Add(new("Population protection", Table: Table(["Zone / centre", "Figures"],
                evacuations.Select(z => (IReadOnlyList<string>)[z.Name, $"~{z.Estimated} people, ~{z.Left} out, ~{z.Remaining} still in"])
                    .Concat(protection.Shelters.Select(s => (IReadOnlyList<string>)[s.Name, $"{s.Occupants}/{s.Capacity} in the rest centre, {s.Registered} registered"]))
                    .Append([("Nowhere to go"), protection.Unsheltered.GetValueOrDefault(incident.Id).ToString(CultureInfo.InvariantCulture)]))));

        var trouble = protection.Infrastructure.Where(a => a.Status != InfrastructureStatus.Normal).Select(a => $"{a.Name}: {Protection.Name(a.Status)}")
            .Concat(protection.Cyber.Where(c => c.RestoredAt is null).Select(c => $"Ransomware: {c.System}"))
            .Concat(protection.Gnss.Where(g => g.EndedAt is null).Select(g => $"GPS {g.Kind.ToString().ToLowerInvariant()}"))
            .ToList();
        if (trouble.Count > 0) sections.Add(new("Critical infrastructure", Text: string.Join(Environment.NewLine, trouble)));

        sections.Add(new("Zones in force", Text: string.Join(", ", incident.Zones.Select(z => z.Name).DefaultIfEmpty("none"))));
        sections.Add(new("Support",
        [
            new("Cost to date", CostRates.Money(_cop.Logistics.TotalCost)),
            new("EOC", $"{EventDescriber.Humanize(_cop.Eoc.Level)}" + (_cop.Eoc.SitrepDue is { } due ? $", next situation report due {Time(due)}" : "")),
            new("IAP in force", _cop.PlanInForce(incident.Id, _now) is { } plan ? plan.Label : "none"),
        ]));
        return sections;
    }

    // ---- 210 and 211: resource status changes and check-in ----

    private List<FormSection> Ics210(Incident? incident)
    {
        var rows = new List<IReadOnlyList<string>>();
        var assignedTo = new Dictionary<Guid, Guid>();
        foreach (var e in _events)
        {
            switch (e.Payload)
            {
                case UnitDispatched d:
                    assignedTo[d.UnitId] = d.IncidentId;
                    if (incident is null || d.IncidentId == incident.Id)
                        rows.Add([Time(e.SimTime), Unit(d.UnitId), "Dispatched", _cop.FindIncident(d.IncidentId)?.Number ?? "", Actor(e)]);
                    break;
                case UnitStatusChanged s when incident is null || assignedTo.GetValueOrDefault(s.UnitId) == incident.Id:
                    rows.Add([Time(e.SimTime), Unit(s.UnitId), EventDescriber.Humanize(s.Status),
                        assignedTo.TryGetValue(s.UnitId, out var at) ? _cop.FindIncident(at)?.Number ?? "" : "", e.Source == EventSources.C2 ? Actor(e) : "crew (status button / radio)"]);
                    break;
            }
        }
        return [new("Status changes", Table: Table(["Time", "Resource", "New status", "Incident", "Reported by"], rows.TakeLast(250)))];
    }

    private List<FormSection> Ics211(Incident? incident)
    {
        if (incident is null) return [Missing("Check-in", "No incident is open.")];
        var arrivals = Movements(incident.Id).Where(m => m.Value.Arrived is not null).OrderBy(m => m.Value.Arrived).ToList();
        return
        [
            new("Checked in", Table: Table(["Time", "Resource", "Type", "Agency", "Crew", "Home station", "Assignment"],
                arrivals.Select(m =>
                {
                    var unit = _cop.FindUnit(m.Key);
                    return (IReadOnlyList<string>)
                    [
                        Time(m.Value.Arrived), unit?.Callsign ?? "unknown", unit is null ? "" : ResourceGroups.Label(unit.Type), unit?.Agency?.ShortName ?? "—",
                        unit?.CrewSize.ToString(CultureInfo.InvariantCulture) ?? "", unit?.HomeStation ?? "", unit?.Tasking ?? incident.Command.GroupOf(m.Key)?.Name ?? "",
                    ];
                }))),
        ];
    }

    // ---- 213 and 214: messages and activity logs ----

    private List<FormSection> Ics213(Incident? incident)
    {
        var senders = _events.Where(e => e.Payload is NotificationSent).ToDictionary(e => ((NotificationSent)e.Payload).NotificationId, Actor);
        return
        [
            new("Messages", Table: Table(["Time", "To", "From", "Message", "Reply"],
                _cop.Notifications.Where(n => incident is null || n.IncidentId is null || n.IncidentId == incident.Id).OrderBy(n => n.SentAt)
                    .Select(n => (IReadOnlyList<string>)[Time(n.SentAt), n.Recipient, senders.GetValueOrDefault(n.Id, "Command"), n.Message,
                        n.Reply is null ? "—" : $"{Time(n.RepliedAt)} {n.Reply}"]))),
        ];
    }

    private List<FormSection> Ics214(Incident? incident, Guid? participantId)
    {
        var log = DecisionLog(incident?.Id, participantId);
        if (participantId is not null)
        {
            var person = _cop.Participants.FirstOrDefault(p => p.Id == participantId);
            var roles = _events.Where(e => e.Payload is RoleAssigned r && r.ParticipantId == participantId)
                .Select(e => $"{Time(e.SimTime)} {Roles.Name(((RoleAssigned)e.Payload).Role)}").ToList();
            return
            [
                new("Position", [new("Name", person?.DisplayName ?? "Unknown"), new("Role now", person is null ? "—" : Roles.Name(person.Role)),
                    new("Roles held", roles.Count == 0 ? "—" : string.Join(", ", roles))]),
                new("Activity log", Table: Table(["Time", "Activity", "Why"], log.Select(r => (IReadOnlyList<string>)[Time(r.At), r.Text, r.Rationale ?? ""]))),
            ];
        }
        // Everyone's, each under their own name.
        return [.. log.GroupBy(r => r.Actor).OrderBy(g => g.Key).Select(g => new FormSection($"{g.Key}" + (g.First().Role is { } role ? $" ({role})" : ""),
            Table: Table(["Time", "Activity", "Why"], g.Select(r => (IReadOnlyList<string>)[Time(r.At), r.Text, r.Rationale ?? ""]))))
            .DefaultIfEmpty(new FormSection("Activity", Text: "Nothing recorded yet."))];
    }

    // ---- Situation reports ----

    private List<FormSection> Sitreps()
    {
        var sent = _events.Where(e => e.Payload is SitrepSubmitted).ToList();
        return
        [
            new("EOC", [new("Level", EventDescriber.Humanize(_cop.Eoc.Level)), new("Last sent", Time(_cop.Eoc.LastSitrepAt)), new("Next due", Time(_cop.Eoc.SitrepDue))]),
            new("Draft of the next report", Text: DraftSitrep(_cop)),
            new("Sent to the EOC", Table: Table(["Time", "From", "Report"], sent.Select(e => (IReadOnlyList<string>)[Time(e.SimTime), Actor(e), ((SitrepSubmitted)e.Payload).Text]))),
        ];
    }

    /// <summary>
    /// A situation report as the EOC wants it (Phase 18): per incident, the situation, casualties, resources, people
    /// protected, infrastructure and the plan; then what is needed. Plain text, ready to send.
    /// </summary>
    public static string DraftSitrep(ICopService cop)
    {
        var incidents = cop.Incidents.Where(i => i.Status != IncidentStatus.Closed).OrderByDescending(i => i.Priority).ToList();
        if (incidents.Count == 0) return "No incident open.";
        var lines = new List<string> { $"SITREP {cop.AsOf.ToLocalTime():HH:mm}" };
        foreach (var i in incidents)
        {
            lines.Add($"{i.Number} {EventDescriber.Humanize(i.Type).ToLowerInvariant()}{(i.Address is null ? "" : $", {i.Address}")}: " +
                      $"{EventDescriber.Humanize(i.Status).ToLowerInvariant()}, priority {i.Priority}.");
            if (i.Threats.Count > 0) lines.Add($"  Threats: {string.Join(", ", i.Threats)}.");
            var patients = cop.Health.Patients.Count(p => p.IncidentId == i.Id);
            var dead = cop.Health.Fatalities.AtScene.GetValueOrDefault(i.Id);
            lines.Add($"  Casualties: {Math.Max(i.CasualtiesReported, i.CasualtiesConfirmed)} reported" +
                      (patients > 0 ? $", {patients} tagged" : "") + (dead > 0 ? $", {dead} dead at the scene" : "") + ".");
            lines.Add($"  Resources: {i.AssignedUnits.Count} committed ({i.AssignedUnits.Count(u => u.Status is UnitStatus.OnScene or UnitStatus.Operating)} on scene).");
            foreach (var zone in cop.Protection.Evacuations.Where(z => z.IncidentId == i.Id && z.ReEntryAt is null))
                lines.Add($"  Evacuation {zone.Name}: ~{zone.Left} out, ~{zone.Remaining} still in.");
            if (cop.Protection.Unsheltered.GetValueOrDefault(i.Id) is > 0 and var nowhere) lines.Add($"  About {nowhere} evacuees need shelter.");
            if (cop.PlanInForce(i.Id, cop.AsOf) is { } plan) lines.Add($"  IAP {plan.Label} in force.");
            if (i.Command.IsUnified) lines.Add($"  Unified command with {string.Join(", ", i.Command.Unified.Select(m => m.Agency))}.");
        }
        var shelters = cop.Protection.Shelters;
        if (shelters.Count > 0) lines.Add($"Rest centres: {shelters.Sum(s => s.Occupants)} evacuees in {shelters.Count}.");
        var infrastructure = cop.Protection.Infrastructure.Where(a => a.Status is InfrastructureStatus.Failed or InfrastructureStatus.Leaking).Select(a => a.Name).ToList();
        if (infrastructure.Count > 0) lines.Add($"Infrastructure down: {string.Join(", ", infrastructure)}.");
        if (cop.Protection.Cyber.Where(c => c.RestoredAt is null).Select(c => c.System).ToList() is { Count: > 0 } attacks)
            lines.Add($"Cyber: {string.Join(", ", attacks)} affected.");
        var needs = cop.ResourceRequests.Where(r => r.Status == ResourceRequestStatus.Requested).Select(r => r.Description).ToList();
        lines.Add(needs.Count > 0 ? $"Needs: {string.Join("; ", needs)}." : "No outstanding requests.");
        return string.Join(Environment.NewLine, lines);
    }

    // ---- The IAP package ----

    private List<FormSection> Cover(Incident? incident, IncidentActionPlan? plan)
    {
        if (plan is null) return [NoPlan(incident)];
        var versions = _cop.VersionsOf(plan.OperationalPeriodId);
        var previous = versions.LastOrDefault(v => v.Version < plan.Version);
        var changes = previous is null ? [] : IapVersions.Compare(previous.Content, plan.Content);
        return
        [
            new("This plan",
            [
                new("Version", $"v{plan.Version} of {versions.Count}"),
                new("Status", IapDocument.StatusLine(plan)),
                new("Prepared by", plan.PreparedBy ?? "—"),
                new("Approved by", plan.ApprovedBy is { } by ? $"{by} at {Time(plan.ApprovedAt)}" : "not yet approved"),
            ]),
            new("Version history", Text: string.Join(Environment.NewLine, IapVersions.History(versions))),
            new(previous is null ? "Changes" : $"Changes from v{previous.Version}",
                Text: previous is null ? "First version for this period." : changes.Count == 0 ? "No changes." : string.Join(Environment.NewLine, changes.Select(c => "• " + c))),
            new("Contents", Text: string.Join(Environment.NewLine, IcsForms.IapForms.Select(k => $"{IcsForms.Number(k)}  {IcsForms.Title(k)}"))),
        ];
    }

    // ---- The decision log ----

    /// <summary>
    /// Every order, request, approval, plan and message command gave, time-stamped and attributed to the participant who
    /// gave it in the role they held (Phase 11), with the reason the decision log records for it; and the decisions
    /// asked of command, with who asked. Oldest first.
    /// </summary>
    public IReadOnlyList<DecisionLogRow> DecisionLog(Guid? incidentId = null, Guid? participantId = null)
    {
        var reasons = new Dictionary<long, string>();
        foreach (var e in _events)
            if (e.Payload is DecisionLogged { AboutSequence: { } about } logged)
                reasons[about] = logged.Rationale;

        var rows = new List<DecisionLogRow>();
        foreach (var e in _events)
        {
            var command = e.Source is EventSources.C2 or EventSources.Iap;
            DecisionLogRow? row = e.Payload switch
            {
                DecisionLogged { AboutSequence: not null } => null, // a reason for another row
                DecisionLogged d => Row(e, "Decision", "Logged decision", d.Decision, d.Rationale),
                ApprovalRequested a => new DecisionLogRow(e.Sequence, e.SimTime, "Request", "Approval asked of command", $"{a.Subject}: {a.Details}",
                    a.RequestedBy, null, null, a.IncidentId, null),
                _ when command && Kind(e.Payload) is { } kind && Describe(e) is { } entry => Row(e, kind, entry.Category, entry.Text, reasons.GetValueOrDefault(e.Sequence)),
                _ => null,
            };
            if (row is null) continue;
            if (participantId is { } who && row.ActorId != who) continue;
            if (incidentId is { } id && row.IncidentId is { } rowIncident && rowIncident != id) continue;
            rows.Add(row);
        }
        return rows;
    }

    private DecisionLogRow Row(SimEvent e, string kind, string category, string text, string? rationale) =>
        new(e.Sequence, e.SimTime, kind, category, text, Actor(e), e.ActorRole is { } role ? Roles.Name(role) : null, e.ActorId, IncidentOf(e.Payload), rationale);

    private static FormSection DecisionLogSection(IReadOnlyList<DecisionLogRow> rows) =>
        new("Decisions", Table: Table(["Time", "Kind", "Decision", "By", "Role", "Why"],
            rows.Select(r => (IReadOnlyList<string>)[r.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), r.Kind, r.Text, r.Actor, r.Role ?? "", r.Rationale ?? ""])));

    /// <summary>What kind of record a command is, or null for bookkeeping that isn't a decision (saving a draft, acknowledging an alert, radio chatter).</summary>
    private static string? Kind(DomainEvent payload)
    {
        if (payload is AlertAcknowledged or IapDraftSaved or IapDraftCreated or RadioCallMade or RepeatRequested or UnitPositionReported) return null;
        var name = payload.GetType().Name;
        if (payload is NotificationSent or PublicStatementIssued or SitrepSubmitted or PressConferenceHeld or RumourRebutted) return "Message";
        if (payload is OrderIssued or OrderClosed or ReadBackConfirmed or UnitDispatched or UnitDispatchCancelled or EvacuationSignalled or ParRequested
            or UnitStatusChanged or UnitStaged) return "Order";
        if (name.StartsWith("Iap", StringComparison.Ordinal) || payload is OperationalPeriodStarted)
            return payload is IapApproved or IapReturned ? "Approval" : "Plan";
        if (name.EndsWith("Requested", StringComparison.Ordinal) || name.EndsWith("Ordered", StringComparison.Ordinal) && payload is SuppliesOrdered) return "Request";
        if (payload is ApprovalDecided or AgencyRequestAnswered or PoliticalVisitDecided or DisputeResolutionChosen or PoliticalCallAnswered) return "Approval";
        return "Decision";
    }

    private TimelineEntry? Describe(SimEvent e) =>
        EventDescriber.Describe(e, id => _cop.FindUnit(id)?.Callsign, id => _cop.FindIncident(id)?.Number,
            id => _cop.Agencies.FirstOrDefault(a => a.Id == id)?.Name, id => _cop.Participants.FirstOrDefault(p => p.Id == id)?.DisplayName);

    /// <summary>Who gave a command: the participant (Phase 11), or, in a single-seat session, simply command.</summary>
    private string Actor(SimEvent e) =>
        e.ActorId is { } id ? _cop.Participants.FirstOrDefault(p => p.Id == id)?.DisplayName ?? "A participant"
        : e.Source is EventSources.C2 or EventSources.Iap ? "Command (single seat)"
        : e.Source == EventSources.Instructor ? "Instructor"
        : "—";

    private static readonly ConcurrentDictionary<Type, PropertyInfo?> IncidentProperties = new();

    /// <summary>The incident an event is about, from its IncidentId, where it has one.</summary>
    private static Guid? IncidentOf(DomainEvent payload)
    {
        var property = IncidentProperties.GetOrAdd(payload.GetType(), t => t.GetProperty("IncidentId") is { } p && (p.PropertyType == typeof(Guid) || p.PropertyType == typeof(Guid?)) ? p : null);
        return property?.GetValue(payload) as Guid?;
    }
}
