using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Standards;

/// <summary>
/// Checks the response against NIMS/ICS (Phase 20) from the COP: what command has set up and said, not the ground
/// truth. Each open incident with resources committed is checked for command, modular organisation, span of control,
/// unified command and facilities; each operational period for an approved IAP with its safety and medical plans; what
/// command said on the radio for plain language; and every unit for its NIMS resource type.
/// </summary>
public static class NimsCompliance
{
    private const string Command = "NIMS 2017, ICS: incident command";
    private const string Modular = "NIMS 2017, ICS: modular organisation";
    private const string Span = "NIMS 2017, ICS: manageable span of control (3–7, ideally 5)";
    private const string Unified = "NIMS 2017, ICS: unified command";
    private const string Facilities = "NIMS 2017, ICS: incident facilities and locations";
    private const string Iap = "NIMS 2017, ICS: incident action planning (ICS 202–208)";
    private const string Plain = "NIMS 2017, Communications: plain language";
    private const string Typing = "NIMS 2017, Resource Management: resource typing";
    private const string Accountability = "NIMS 2017, ICS: accountability (ICS 214 / decision log)";

    /// <summary>How long a period may run without an approved plan before it counts as a gap.</summary>
    public static readonly TimeSpan PlanGrace = TimeSpan.FromMinutes(60);

    /// <summary>How many plain-language slips are listed one by one.</summary>
    public const int PlainLanguageListed = 5;

    /// <param name="now">The simulation time to judge by; the COP's latest information if not given.</param>
    public static NimsReport Check(ICopService cop, Guid? incidentId = null, DateTimeOffset? now = null)
    {
        var at = now ?? cop.AsOf;
        var findings = new List<NimsFinding>();
        var incidents = cop.Incidents.Where(i => i.Status != IncidentStatus.Closed && (incidentId is null || i.Id == incidentId)).ToList();
        foreach (var incident in incidents)
        {
            var units = incident.AssignedUnits.Select(u => cop.FindUnit(u.Id) ?? u).ToList();
            if (units.Count == 0) continue; // nothing committed yet: no organisation to check
            Organisation(cop, incident, units, findings);
            Forms(cop, incident, at, findings);
        }
        Terminology(cop, incidentId, findings);
        ResourceTyping(cop, incidentId, findings);
        return new NimsReport(at, findings);
    }

    private static void Organisation(ICopService cop, Incident incident, List<Unit> units, List<NimsFinding> findings)
    {
        void Add(NimsSeverity severity, string message, string reference) =>
            findings.Add(new(NimsArea.Organisation, severity, $"{incident.Number}: {message}", reference, incident.Id));
        var command = incident.Command;

        // One Incident Commander (or a unified command) for every incident.
        if (string.IsNullOrWhiteSpace(incident.IncidentCommanderName) && !command.IsStaffed(IcsRole.IncidentCommander))
            Add(NimsSeverity.Gap, "no Incident Commander named. Every incident has one, from the first unit on scene.", Command);
        else
            Add(NimsSeverity.Met, $"command established ({incident.IncidentCommanderName ?? command.Positions.GetValueOrDefault(IcsRole.IncidentCommander)}).", Command);

        // Span of control: every supervisor with 3 to 7 reporting to them.
        var spans = SpanOfControl.Assess(command, units.Select(u => u.Id).ToList());
        foreach (var span in spans.Where(s => s.IsOverloaded))
            Add(NimsSeverity.Gap, $"{span.Supervisor} supervises {span.DirectReports}, more than {SpanOfControl.Maximum}. Form groups or divisions.", Span);
        if (!spans.Any(s => s.IsOverloaded))
            Add(NimsSeverity.Met, "every supervisor within span of control.", Span);

        // Modular expansion: Operations once the IC can't run the tactics and the command together.
        if (units.Count > SpanOfControl.Maximum && !command.IsStaffed(IcsRole.Operations))
            Add(NimsSeverity.Advisory, $"{units.Count} resources committed and no Operations Section Chief: the IC is running the tactics too.", Modular);

        // A Safety Officer wherever the work is hazardous.
        var hazardous = incident.Type is IncidentType.HazmatRelease or IncidentType.Explosion or IncidentType.StructuralCollapse or IncidentType.ActiveThreat
                        || cop.Zones.Any(z => z.IncidentId == incident.Id && z.Type == ZoneType.HotZone)
                        || cop.Maydays.Any(m => m.IncidentId == incident.Id);
        if (hazardous && !command.IsStaffed(IcsRole.Safety))
            Add(NimsSeverity.Gap, "hazardous operations and no Safety Officer appointed.", Modular);
        else if (hazardous)
            Add(NimsSeverity.Met, "Safety Officer appointed for hazardous operations.", Modular);

        // Unified command where several agencies have resources at the incident.
        var agencies = units.Select(u => u.AgencyId).Where(a => a is not null).Distinct().Count();
        if (agencies > 1 && !command.IsUnified)
            Add(NimsSeverity.Advisory, $"resources from {agencies} agencies and no unified command: each agency's commander should share it.", Unified);
        else if (agencies > 1)
            Add(NimsSeverity.Met, "unified command with the agencies on scene.", Unified);

        // Facilities: an Incident Command Post, and a Staging Area once the incident is busy.
        var zones = cop.Zones.Where(z => z.IncidentId == incident.Id).ToList();
        if (units.Count > 3 && zones.All(z => z.Type != ZoneType.CommandPost))
            Add(NimsSeverity.Advisory, "no Incident Command Post marked on the map.", Facilities);
        if (units.Count > 10 && zones.All(z => z.Type != ZoneType.StagingArea))
            Add(NimsSeverity.Advisory, "more than ten resources and no Staging Area.", Facilities);
    }

    private static void Forms(ICopService cop, Incident incident, DateTimeOffset now, List<NimsFinding> findings)
    {
        void Add(NimsSeverity severity, string message, string reference) =>
            findings.Add(new(NimsArea.Forms, severity, $"{incident.Number}: {message}", reference, incident.Id));

        foreach (var period in cop.PeriodsOf(incident.Id).Where(p => p.Start <= now))
        {
            var label = $"operational period {period.Number}";
            if (cop.ApprovedPlan(period.Id) is not { } plan)
            {
                if (now - period.Start > PlanGrace)
                    Add(NimsSeverity.Gap, $"{label} has run {(int)(now - period.Start).TotalMinutes} minutes with no approved IAP.", Iap);
                continue;
            }
            var errors = IapCompliance.Check(plan.Content, period, incident, cop.FindUnit).Where(i => i.Severity == IssueSeverity.Error).ToList();
            if (errors.Count == 0)
                Add(NimsSeverity.Met, $"{label}: IAP v{plan.Version} approved and complete.", Iap);
            else
                foreach (var error in errors)
                    Add(NimsSeverity.Gap, $"{label}, IAP v{plan.Version}: {error.Message}", Iap);
            if (string.IsNullOrWhiteSpace(plan.Content.Safety.Message))
                Add(NimsSeverity.Advisory, $"{label}: no safety message (ICS 208).", Iap);
            if (plan.Content.Medical.Hospitals.Count == 0)
                Add(NimsSeverity.Advisory, $"{label}: no hospitals in the medical plan (ICS 206).", Iap);
        }

        // Decisions recorded with their reasons: what the activity log and the review rely on.
        if (now - incident.ReportedAt > TimeSpan.FromMinutes(30) && cop.Public.DecisionLog.Count == 0)
            Add(NimsSeverity.Advisory, "half an hour in and no decision logged with its rationale.", Accountability);
    }

    private static void Terminology(ICopService cop, Guid? incidentId, List<NimsFinding> findings)
    {
        var slips = new List<(CommsEntry Entry, PlainLanguageIssue Issue)>();
        foreach (var entry in cop.CommsLog.Where(c => c.FromControl))
        {
            if (incidentId is { } id && entry.UnitId is { } unit && cop.FindUnit(unit)?.AssignedIncidentId != id) continue;
            slips.AddRange(NimsTerminology.CheckPlainLanguage(entry.Text).Select(i => (entry, i)));
        }
        foreach (var (entry, issue) in slips.Take(PlainLanguageListed))
            findings.Add(new(NimsArea.Terminology, NimsSeverity.Advisory,
                $"{entry.At:HH:mm} to {entry.To ?? entry.ChannelId}: \"{issue.Found}\" is a code. Say {issue.Say}.", Plain));
        if (slips.Count > PlainLanguageListed)
            findings.Add(new(NimsArea.Terminology, NimsSeverity.Advisory, $"{slips.Count - PlainLanguageListed} more codes on the radio.", Plain));
        if (slips.Count == 0 && cop.CommsLog.Any(c => c.FromControl))
            findings.Add(new(NimsArea.Terminology, NimsSeverity.Met, "command's radio traffic in plain language.", Plain));
    }

    private static void ResourceTyping(ICopService cop, Guid? incidentId, List<NimsFinding> findings)
    {
        var units = cop.Units.Where(u => incidentId is null || u.AssignedIncidentId == incidentId).ToList();
        var untyped = units.Where(u => !NimsResourceTypes.IsTyped(u.Type)).ToList();
        foreach (var unit in untyped.Take(5))
            findings.Add(new(NimsArea.ResourceTyping, NimsSeverity.Advisory, $"{unit.Callsign} has no NIMS resource type.", Typing, unit.AssignedIncidentId));
        if (units.Count > 0 && untyped.Count == 0)
            findings.Add(new(NimsArea.ResourceTyping, NimsSeverity.Met, $"all {units.Count} resources typed to the NIMS Resource Typing Library.", Typing));
    }
}
