using System.Xml.Linq;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Standards;

namespace Emergency_Response_Simulator.Simulation.Services;

/// <summary>
/// Data standards and interoperability (Phase 20). Exports read the COP, so a trainee's export holds what command knows,
/// never the ground truth. Imports publish what the outside system said as perceived events from the integration source,
/// stamped with whoever sent them in: a warning becomes a report and an alert, a hospital's status updates its entry, a
/// CAD's incident arrives as a report for command to act on, an HL7 message moves a patient on the tracking board.
/// </summary>
/// <param name="clock">The simulation clock, so the NIMS check judges by the time now and not the COP's latest event.</param>
public sealed class IntegrationService(ICopService cop, IEventPublisher publisher, ISimulationControl? clock = null,
    string sender = IntegrationService.DefaultSender)
    : IIntegrationService
{
    public const string DefaultSender = "ers.simulator";

    private readonly HashSet<string> _seenAlerts = [];
    private readonly HashSet<string> _seenIncidents = [];
    private int _sitreps;

    public Task<IReadOnlyList<DataStandard>> GetStandardsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(DataStandards.All);

    public Task<NimsReport> CheckNimsAsync(Guid? incidentId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(NimsCompliance.Check(cop, incidentId, clock?.SimTime));

    // ---- Out ----

    public Task<StandardDocument> ExportAsync(string standard, Guid? incidentId = null, CancellationToken cancellationToken = default)
    {
        var key = DataStandards.Find(standard)?.Key ?? throw new ArgumentException($"Unknown standard '{standard}'. Known: {string.Join(", ", DataStandards.All.Select(s => s.Key))}.");
        var stamp = $"{cop.AsOf:yyyyMMdd-HHmm}";
        StandardDocument Xml(XElement root, int items, string mediaType = "application/xml") =>
            new(key, mediaType, $"ers-{key}-{stamp}.xml", Document(root), items);
        XElement Envelope(IEnumerable<EdxlDe.Content> contents) =>
            EdxlDe.Wrap($"ERS-DE-{key}-{cop.LastSequence}", sender, cop.AsOf, contents);

        StandardDocument document;
        switch (key)
        {
            case DataStandards.Cap:
                // One alert stands alone; several travel in a distribution envelope, as CAP feeds do.
                var alerts = Cap.FromCop(cop, sender, incidentId);
                document = alerts is [var only]
                    ? Xml(Cap.ToElement(only), 1, "application/cap+xml")
                    : Xml(Envelope(alerts.Select(a => new EdxlDe.Content($"CAP: {a.Info[0].Event}", Cap.ToElement(a)))), alerts.Count);
                break;
            case DataStandards.EdxlHave:
                document = Xml(EdxlHave.Write(EdxlHave.FromCop(cop)), cop.Hospitals.Count);
                break;
            case DataStandards.EdxlRm:
                var requests = EdxlRm.FromCop(cop, incidentId);
                document = requests is [var request]
                    ? Xml(request, 1)
                    : Xml(Envelope(requests.Select(r => new EdxlDe.Content("EDXL-RM: resource request", r))), requests.Count);
                break;
            case DataStandards.EdxlSitRep:
                document = Xml(EdxlSitRep.FromCop(cop, ++_sitreps, "Planning Section", incidentId), 1);
                break;
            case DataStandards.Niem:
                document = Xml(Niem.FromCop(cop, sender, incidentId), cop.Incidents.Count(i => incidentId is null || i.Id == incidentId));
                break;
            case DataStandards.Hl7:
                var messages = Hl7.FromCop(cop, "National Ambulance Service", incidentId);
                document = new StandardDocument(key, "application/hl7-v2", $"ers-{key}-{stamp}.hl7", string.Concat(messages), messages.Count);
                break;
            default:
                document = Everything();
                break;
        }
        return Task.FromResult(document);

        // EDXL-DE: the whole picture in one envelope.
        StandardDocument Everything()
        {
            var contents = new List<EdxlDe.Content>();
            contents.AddRange(Cap.FromCop(cop, sender, incidentId).Select(a => new EdxlDe.Content($"CAP: {a.Info[0].Event}", Cap.ToElement(a))));
            contents.Add(new("EDXL-HAVE: hospital availability", EdxlHave.Write(EdxlHave.FromCop(cop))));
            contents.AddRange(EdxlRm.FromCop(cop, incidentId).Select(r => new EdxlDe.Content("EDXL-RM: resource request", r)));
            contents.Add(new("EDXL-SitRep: situation report", EdxlSitRep.FromCop(cop, ++_sitreps, "Planning Section", incidentId)));
            return Xml(Envelope(contents), contents.Count);
        }
    }

    private static string Document(XElement root) =>
        new XDeclaration("1.0", "utf-8", null) + Environment.NewLine + root;

    // ---- In ----

    public async Task<ImportResult> ImportAsync(string standard, string content, CancellationToken cancellationToken = default)
    {
        var known = DataStandards.Find(standard);
        if (known is null) return ImportResult.Failed($"Unknown standard '{standard}'.");
        if (!known.Imports) return ImportResult.Failed($"{known.Label} is export only: the simulator sends it but doesn't take it in.");
        if (string.IsNullOrWhiteSpace(content)) return ImportResult.Failed("The document is empty.");

        try
        {
            var notes = new List<string>();
            if (known.Key == DataStandards.Hl7)
                return await Hl7InAsync(content, notes, cancellationToken);

            var root = XElement.Parse(content);
            var accepted = known.Key switch
            {
                DataStandards.Cap => await CapInAsync(root, notes, cancellationToken),
                DataStandards.EdxlHave => await HaveInAsync(root, notes, cancellationToken),
                DataStandards.Niem => await NiemInAsync(root, notes, cancellationToken),
                _ => await EnvelopeInAsync(root, notes, cancellationToken),
            };
            return new ImportResult(accepted > 0, accepted > 0 ? $"{known.Label}: {accepted} item(s) taken in." : $"{known.Label}: nothing taken in.", accepted, notes);
        }
        catch (Exception ex) when (ex is FormatException or System.Xml.XmlException or OverflowException)
        {
            return ImportResult.Failed($"Not a valid {known.Label} document: {ex.Message}");
        }
    }

    private async Task<int> EnvelopeInAsync(XElement root, List<string> notes, CancellationToken cancellationToken)
    {
        var accepted = 0;
        foreach (var content in EdxlDe.Unwrap(root))
        {
            if (content.Name == Cap.Ns + "alert") accepted += await CapInAsync(content, notes, cancellationToken);
            else if (content.Name == EdxlHave.Ns + "HospitalStatus") accepted += await HaveInAsync(content, notes, cancellationToken);
            else if (content.DescendantsAndSelf(Niem.Nc + "Incident").Any()) accepted += await NiemInAsync(content, notes, cancellationToken);
            else notes.Add($"Skipped <{content.Name.LocalName}>: the simulator doesn't take that content in.");
        }
        return accepted;
    }

    /// <summary>An outside warning (the met service, another agency): a report from that agency and an alert to command.</summary>
    private async Task<int> CapInAsync(XElement root, List<string> notes, CancellationToken cancellationToken)
    {
        var alert = Cap.Read(root);
        if (!_seenAlerts.Add($"{alert.Sender}|{alert.Identifier}|{alert.MsgType}"))
        {
            notes.Add($"CAP {alert.Identifier} from {alert.Sender} already received.");
            return 0;
        }
        var accepted = 0;
        foreach (var info in alert.Info)
        {
            var from = info.SenderName ?? alert.Sender;
            var where = info.Areas.Select(a => a.AreaDesc).FirstOrDefault(a => a.Length > 0);
            var claim = $"{(alert.MsgType == "Cancel" ? "CANCELLED: " : "")}{info.Event}: {info.Headline ?? info.Description ?? ""}"
                        + (where is null ? "" : $" ({where})") + (info.Instruction is null ? "" : $" Advice: {info.Instruction}");
            var confidence = info.Certainty switch { "Observed" => Confidence.High, "Likely" => Confidence.Medium, _ => Confidence.Low };
            await PerceivedAsync(new ReportReceived(Guid.NewGuid(), null, ReportSource.Agency, from, claim, confidence, VerificationStatus.Reported,
                info.Areas.Select(a => a.Centre()).FirstOrDefault(c => c is not null), null), cancellationToken);
            var category = info.Category is "Met" or "Env" or "CBRNE" or "Fire" or "Geo" ? AlertCategory.Hazard
                : info.Category is "Safety" or "Security" ? AlertCategory.Safety
                : AlertCategory.SituationChange;
            await PerceivedAsync(new AlertRaised(Guid.NewGuid(), category, alert.MsgType == "Cancel" ? AlertSeverity.Info : Cap.SeverityOf(info.Severity),
                $"CAP {alert.MsgType.ToLowerInvariant()} from {from}: {info.Event}", info.Headline ?? info.Description ?? info.Event, null, null), cancellationToken);
            notes.Add($"CAP {info.Event} ({info.Severity}, {info.Urgency}) from {from}.");
            accepted++;
        }
        if (alert.Status is not ("Actual" or "Exercise"))
            notes.Add($"Note: status {alert.Status}, not Actual or Exercise.");
        return accepted;
    }

    /// <summary>Hospitals' own availability: the emergency department, diversion and specialist beds.</summary>
    private async Task<int> HaveInAsync(XElement root, List<string> notes, CancellationToken cancellationToken)
    {
        var accepted = 0;
        foreach (var reported in EdxlHave.Read(root))
        {
            var hospital = cop.Hospitals.FirstOrDefault(h => Guid.TryParse(reported.Id, out var id) && h.Id == id)
                           ?? cop.Hospitals.FirstOrDefault(h => string.Equals(h.Name, reported.Name, StringComparison.OrdinalIgnoreCase));
            var ed = reported.Beds.TryGetValue(EdxlHave.EmergencyDepartment, out var places) ? places : ((int, int)?)null;
            if (hospital is null)
            {
                if (reported.Location is not { } location || ed is null)
                {
                    notes.Add($"Skipped {reported.Name}: not a receiving hospital on the picture, and no location and ED capacity to add it with.");
                    continue;
                }
                var id = Guid.NewGuid();
                await PerceivedAsync(new HospitalRegistered(id, reported.Name, location, ed.Value.Item2, ed.Value.Item2 - ed.Value.Item1), cancellationToken);
                hospital = cop.Hospitals.FirstOrDefault(h => h.Id == id);
                notes.Add($"{reported.Name} added as a receiving hospital.");
                if (hospital is null) continue;
            }

            var (available, baseline) = ed ?? (hospital.Available, hospital.Capacity);
            await PerceivedAsync(new HospitalStatusReported(hospital.Id, hospital.Name, baseline - available, baseline, reported.EmsTraffic == "Closed",
                reported.Reason ?? (reported.EmsTraffic == "Advisory" ? "EMS traffic advisory" : null)), cancellationToken);

            var specialties = reported.Beds
                .Select(b => (Specialty: EdxlHave.SpecialtyOf(b.Key), b.Value))
                .Where(b => b.Specialty is not null)
                .Select(b => new SpecialtyBeds(b.Specialty!.Value, b.Value.Baseline, b.Value.Baseline - b.Value.Available))
                .ToList();
            if (specialties.Count > 0)
            {
                var current = cop.Health.Hospitals.GetValueOrDefault(hospital.Id);
                await PerceivedAsync(new HospitalResourcesReported(hospital.Id, specialties, current?.BloodUnits ?? 0, current?.MorgueOccupied ?? 0,
                    cop.Health.WorriedWell.GetValueOrDefault(hospital.Id)), cancellationToken);
            }
            notes.Add($"{hospital.Name}: {available} of {baseline} ED places, {reported.EmsTraffic.ToLowerInvariant()}"
                      + (specialties.Count > 0 ? $", {specialties.Count} specialist bed type(s)." : "."));
            accepted++;
        }
        return accepted;
    }

    /// <summary>A CAD or records system's incidents: each arrives as a report from that system, for command to act on.</summary>
    private async Task<int> NiemInAsync(XElement root, List<string> notes, CancellationToken cancellationToken)
    {
        var (from, incidents) = Niem.Read(root);
        var accepted = 0;
        foreach (var incident in incidents)
        {
            if (!_seenIncidents.Add($"{from}|{incident.Id}"))
            {
                notes.Add($"{incident.Id} from {from} already received.");
                continue;
            }
            var claim = $"{from} incident {incident.Id}: {incident.Category}"
                        + (incident.Name is null ? "" : $", {incident.Name}")
                        + (incident.Address is null ? "" : $" at {incident.Address}")
                        + (incident.Description is null ? "" : $". {incident.Description}")
                        + (incident.Priority is null ? "" : $" Priority {incident.Priority}.");
            await PerceivedAsync(new ReportReceived(Guid.NewGuid(), null, ReportSource.Agency, from, claim, Confidence.Medium, VerificationStatus.Reported,
                incident.Location, incident.Location is null ? null : 50), cancellationToken);
            notes.Add($"{incident.Id} ({incident.Category}) as a report from {from}.");
            accepted++;
        }
        return accepted;
    }

    /// <summary>Hospitals' ADT messages move patients on the tracking board; each gets an ACK.</summary>
    private async Task<ImportResult> Hl7InAsync(string content, List<string> notes, CancellationToken cancellationToken)
    {
        var acks = new List<string>();
        var accepted = 0;
        foreach (var message in Hl7Message.ParseBatch(content))
        {
            var (ok, text) = await AdtInAsync(message, cancellationToken);
            notes.Add($"{message.Type} {message.ControlId}: {text}");
            acks.Add(Hl7.Ack(message, ok, ok ? null : text, clock?.SimTime ?? cop.AsOf));
            if (ok) accepted++;
        }
        return new ImportResult(accepted > 0, $"HL7 v2: {accepted} of {acks.Count} message(s) accepted.", accepted, notes, string.Concat(acks));
    }

    private async Task<(bool Accepted, string Text)> AdtInAsync(Hl7Message message, CancellationToken cancellationToken)
    {
        if (message.Get("MSH", 9, 1) != "ADT") return (false, "only ADT messages are taken in.");
        var tag = message.Get("PID", 3, 1);
        var patient = cop.Health.Patients.FirstOrDefault(p => string.Equals(p.Tag, tag, StringComparison.OrdinalIgnoreCase));
        if (patient is null) return (false, $"no patient tagged {(tag.Length == 0 ? "(none given)" : tag)} on the tracking board.");

        var facility = message.Get("PV1", 3, 4) is { Length: > 0 } f ? f : message.Get("MSH", 4, 1);
        var hospital = cop.Hospitals.FirstOrDefault(h => string.Equals(h.Name, facility, StringComparison.OrdinalIgnoreCase))
                       ?? cop.Hospitals.FirstOrDefault(h => h.Id == patient.HospitalId);

        switch (message.Event)
        {
            case "A01" or "A04" when hospital is null:
                return (false, $"no receiving hospital called {facility}.");
            case "A01" or "A04" when patient.State == CasualtyState.AtHospital && patient.HospitalId == hospital.Id:
                return (true, $"{tag} already at {hospital.Name}.");
            case "A01" or "A04":
                var beds = patient.Need is { } need ? cop.Health.Hospitals.GetValueOrDefault(hospital.Id)?.Beds(need) : null;
                await PerceivedAsync(new PatientArrived(patient.Id, hospital.Id, patient.Need is null || beds is { Beds: > 0 }), cancellationToken);
                return (true, $"{tag} arrived at {hospital.Name}.");
            case "A02" when hospital is null || patient.HospitalId is null:
                return (false, $"transfer of {tag} names no hospital it could go to.");
            case "A02" when hospital.Id == patient.HospitalId:
                return (false, $"{tag} is already at {hospital.Name}.");
            case "A02":
                await PerceivedAsync(new PatientTransferred(patient.Id, patient.HospitalId.Value, hospital.Id,
                    message.Get("EVN", 4, 2) is { Length: > 0 } reason ? reason : "Transferred (HL7 A02)"), cancellationToken);
                return (true, $"{tag} transferred to {hospital.Name}.");
            case "A03" when message.Get("PV1", 36) == Hl7.Expired:
                if (patient.State == CasualtyState.Deceased) return (true, $"{tag} already recorded as dead.");
                await PerceivedAsync(new PatientDied(patient.Id, hospital?.Name ?? "hospital"), cancellationToken);
                return (true, $"{tag} died{(hospital is null ? "" : $" at {hospital.Name}")}.");
            case "A03":
                return (true, $"{tag} discharged (noted; the board tracks them to hospital).");
            case "A08" when Hl7.TriageOf(message.Get("OBX", 5, 1)) is { } triage:
                if (triage == patient.Triage) return (true, $"{tag} still {triage}.");
                await PerceivedAsync(new PatientRetriaged(patient.Id, triage), cancellationToken);
                return (true, $"{tag} re-triaged {triage}.");
            default:
                return (false, $"ADT^{message.Event} isn't handled.");
        }
    }

    public async Task<ImportResult> FeedPositionsAsync(IReadOnlyList<ExternalPosition> positions, CancellationToken cancellationToken = default)
    {
        var notes = new List<string>();
        var accepted = 0;
        foreach (var fix in positions)
        {
            var unit = cop.Units.FirstOrDefault(u => string.Equals(u.Callsign, fix.Unit?.Trim(), StringComparison.OrdinalIgnoreCase)
                                                     || (Guid.TryParse(fix.Unit, out var id) && u.Id == id));
            if (unit is null)
            {
                notes.Add($"No unit '{fix.Unit}'.");
                continue;
            }
            if (fix.Latitude is < -90 or > 90 || fix.Longitude is < -180 or > 180 || double.IsNaN(fix.Latitude) || double.IsNaN(fix.Longitude))
            {
                notes.Add($"{unit.Callsign}: position out of range.");
                continue;
            }
            await PerceivedAsync(new UnitPositionReported(unit.Id, new GeoPoint(fix.Latitude, fix.Longitude), Math.Max(0, fix.SpeedKph), fix.Heading, null),
                cancellationToken, EventSources.Avl);
            if (fix.Status is { } status && status != unit.Status)
                await PerceivedAsync(new UnitStatusChanged(unit.Id, status), cancellationToken, EventSources.Avl);
            accepted++;
        }
        return new ImportResult(accepted > 0, $"{accepted} of {positions.Count} position(s) taken in.", accepted, notes);
    }

    private Task<SimEvent> PerceivedAsync(DomainEvent payload, CancellationToken cancellationToken, string source = EventSources.Integration) =>
        publisher.PublishAsync(payload, EventVisibility.Perceived, source, cancellationToken);
}
