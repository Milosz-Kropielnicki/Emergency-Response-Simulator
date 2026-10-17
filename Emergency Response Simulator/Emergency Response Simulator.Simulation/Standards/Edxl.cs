using System.Globalization;
using System.Xml.Linq;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Documentation;

namespace Emergency_Response_Simulator.Simulation.Standards;

// ---- OASIS Emergency Data Exchange Language: DE (the envelope), HAVE (hospitals), RM (resources), SitRep ----

/// <summary>EDXL Distribution Element 1.0: an envelope that routes any XML content (CAP, HAVE, RM, SitRep) to its recipients.</summary>
public static class EdxlDe
{
    public static readonly XNamespace Ns = "urn:oasis:names:tc:emergency:EDXL:DE:1.0";

    /// <summary>A content object: its description and the XML it carries.</summary>
    public sealed record Content(string Description, XElement Xml);

    public static XElement Wrap(string distributionId, string senderId, DateTimeOffset sent, IEnumerable<Content> contents,
        string distributionType = "Report") =>
        new(Ns + "EDXLDistribution",
            new XElement(Ns + "distributionID", distributionId),
            new XElement(Ns + "senderID", senderId),
            new XElement(Ns + "dateTimeSent", Cap.Time(sent)),
            new XElement(Ns + "distributionStatus", Cap.ExerciseStatus),
            new XElement(Ns + "distributionType", distributionType),
            new XElement(Ns + "combinedConfidentiality", "UNCLASSIFIED AND NOT SENSITIVE"),
            contents.Select(c => new XElement(Ns + "contentObject",
                new XElement(Ns + "contentDescription", c.Description),
                new XElement(Ns + "xmlContent", new XElement(Ns + "embeddedXMLContent", c.Xml)))));

    /// <summary>The XML each content object carries, in order; throws <see cref="FormatException"/> when it isn't an EDXL-DE envelope.</summary>
    public static IReadOnlyList<XElement> Unwrap(XElement root)
    {
        if (root.Name != Ns + "EDXLDistribution") throw new FormatException($"Not an EDXL-DE 1.0 envelope: the root is <{root.Name.LocalName}>.");
        return root.Elements(Ns + "contentObject")
            .Select(o => o.Element(Ns + "xmlContent")?.Element(Ns + "embeddedXMLContent")?.Elements().FirstOrDefault())
            .OfType<XElement>().ToList();
    }

    public static string SenderOf(XElement root) => root.Element(Ns + "senderID")?.Value.Trim() ?? "";
}

/// <summary>A hospital's status as HAVE reports it.</summary>
/// <param name="EmsTraffic">Normal, Advisory or Closed (on diversion).</param>
/// <param name="Beds">Available and total by bed type ("EmergencyDepartment", "Burn", "Pediatrics", …).</param>
public sealed record HaveHospital(string Id, string Name, GeoPoint? Location, string EmsTraffic, string? Reason,
    IReadOnlyDictionary<string, (int Available, int Baseline)> Beds, DateTimeOffset Updated);

/// <summary>
/// EDXL Hospital AVailability Exchange 1.0: each hospital's emergency department traffic status (normal, advisory, closed
/// to ambulances) and its bed capacity by type. The emergency department's places travel as a bed type of their own.
/// </summary>
public static class EdxlHave
{
    public static readonly XNamespace Ns = "urn:oasis:names:tc:emergency:EDXL:HAVE:1.0";
    public static readonly XNamespace Gml = "http://www.opengis.net/gml";

    public const string EmergencyDepartment = "EmergencyDepartment";

    /// <summary>HAVE bed types for the simulator's specialist beds; major trauma travels as adult critical care.</summary>
    public static string BedType(Specialty specialty) => specialty switch
    {
        Specialty.Burns => "Burn",
        Specialty.Paediatric => "Pediatrics",
        Specialty.Psychiatric => "Psychiatric",
        _ => "AdultICU",
    };

    public static Specialty? SpecialtyOf(string bedType) => bedType switch
    {
        "Burn" => Specialty.Burns,
        "Pediatrics" => Specialty.Paediatric,
        "Psychiatric" => Specialty.Psychiatric,
        "AdultICU" => Specialty.Trauma,
        _ => null,
    };

    public static IReadOnlyList<HaveHospital> FromCop(ICopService cop) =>
        cop.Hospitals.Select(h =>
        {
            var beds = new Dictionary<string, (int, int)> { [EmergencyDepartment] = (h.Available, h.Capacity) };
            if (cop.Health.Hospitals.GetValueOrDefault(h.Id) is { } resources)
                foreach (var s in resources.Specialties)
                    beds[BedType(s.Specialty)] = (s.Free, s.Beds);
            var traffic = h.OnDiversion ? "Closed" : h.Load >= 0.9 ? "Advisory" : "Normal";
            return new HaveHospital(h.Id.ToString(), h.Name, h.Location, traffic, h.Note, beds, h.ReportedAt);
        }).ToList();

    public static XElement Write(IEnumerable<HaveHospital> hospitals) =>
        new(Ns + "HospitalStatus",
            new XAttribute(XNamespace.Xmlns + "gml", Gml),
            hospitals.Select(h => new XElement(Ns + "Hospital",
                new XElement(Ns + "Organization",
                    new XElement(Ns + "OrganizationInformation",
                        new XElement(Ns + "OrganizationID", h.Id),
                        new XElement(Ns + "OrganizationName", h.Name)),
                    h.Location is { } at
                        ? new XElement(Ns + "OrganizationGeoLocation", new XElement(Gml + "Point", new XElement(Gml + "pos",
                            string.Create(CultureInfo.InvariantCulture, $"{at.Latitude:0.######} {at.Longitude:0.######}"))))
                        : null),
                new XElement(Ns + "EmergencyDepartmentStatus",
                    new XElement(Ns + "EMSTraffic",
                        new XElement(Ns + "EMSTrafficStatus", h.EmsTraffic),
                        h.Reason is null ? null : new XElement(Ns + "EMSTrafficReason", h.Reason))),
                new XElement(Ns + "HospitalBedCapacityStatus",
                    h.Beds.Select(b => new XElement(Ns + "BedCapacity",
                        new XElement(Ns + "BedType", b.Key),
                        new XElement(Ns + "Capacity",
                            new XElement(Ns + "CapacityStatus", b.Value.Available > 0 ? "VacantAvailable" : "NotAvailable"),
                            new XElement(Ns + "AvailableCount", b.Value.Available),
                            new XElement(Ns + "BaselineCount", b.Value.Baseline))))),
                new XElement(Ns + "LastUpdateTime", Cap.Time(h.Updated)))));

    public static IReadOnlyList<HaveHospital> Read(XElement root)
    {
        if (root.Name != Ns + "HospitalStatus") throw new FormatException($"Not an EDXL-HAVE 1.0 hospital status: the root is <{root.Name.LocalName}>.");
        return root.Elements(Ns + "Hospital").Select(h =>
        {
            var info = h.Element(Ns + "Organization")?.Element(Ns + "OrganizationInformation");
            var name = info?.Element(Ns + "OrganizationName")?.Value.Trim() ?? throw new FormatException("A hospital has no <OrganizationName>.");
            var pos = h.Element(Ns + "Organization")?.Element(Ns + "OrganizationGeoLocation")?.Descendants(Gml + "pos").FirstOrDefault()?.Value;
            var location = pos?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var lat, var lon]
                ? new GeoPoint(double.Parse(lat, CultureInfo.InvariantCulture), double.Parse(lon, CultureInfo.InvariantCulture))
                : (GeoPoint?)null;
            var traffic = h.Element(Ns + "EmergencyDepartmentStatus")?.Element(Ns + "EMSTraffic");
            var beds = new Dictionary<string, (int, int)>();
            foreach (var bed in h.Element(Ns + "HospitalBedCapacityStatus")?.Elements(Ns + "BedCapacity") ?? [])
            {
                var capacity = bed.Element(Ns + "Capacity");
                var available = int.TryParse(capacity?.Element(Ns + "AvailableCount")?.Value, out var a) ? a : 0;
                var baseline = int.TryParse(capacity?.Element(Ns + "BaselineCount")?.Value, out var b) ? b : available;
                beds[bed.Element(Ns + "BedType")?.Value.Trim() ?? ""] = (available, Math.Max(baseline, available));
            }
            var updated = h.Element(Ns + "LastUpdateTime")?.Value is { } u ? DateTimeOffset.Parse(u, CultureInfo.InvariantCulture) : default;
            return new HaveHospital(info?.Element(Ns + "OrganizationID")?.Value.Trim() ?? "", name, location,
                traffic?.Element(Ns + "EMSTrafficStatus")?.Value.Trim() ?? "Normal", traffic?.Element(Ns + "EMSTrafficReason")?.Value.Trim(), beds, updated);
        }).ToList();
    }
}

/// <summary>EDXL Resource Messaging 1.0: command's resource requests, each resource described by its NIMS type.</summary>
public static class EdxlRm
{
    public static readonly XNamespace Msg = "urn:oasis:names:tc:emergency:EDXL:RM:1.0:msg";
    public static readonly XNamespace Rm = "urn:oasis:names:tc:emergency:EDXL:RM:1.0";

    /// <summary>The value list the resource types come from.</summary>
    public const string NimsTypeList = "urn:x-nims:resource-typing-library";

    /// <summary>One RequestResource message for each request still open (requested or approved, not yet fulfilled).</summary>
    public static IReadOnlyList<XElement> FromCop(ICopService cop, Guid? incidentId = null) =>
        cop.ResourceRequests
            .Where(r => r.Status is ResourceRequestStatus.Requested or ResourceRequestStatus.Approved && (incidentId is null || r.IncidentId == incidentId))
            .OrderBy(r => r.RequestedAt)
            .Select(r =>
            {
                var incident = cop.FindIncident(r.IncidentId);
                var type = r.UnitType is { } t ? NimsResourceTypes.Of(t) : null;
                return new XElement(Msg + "RequestResource",
                    new XAttribute(XNamespace.Xmlns + "rm", Rm),
                    new XElement(Rm + "MessageID", $"ERS-RM-{r.Id:N}"),
                    new XElement(Rm + "SentDateTime", Cap.Time(r.RequestedAt)),
                    new XElement(Rm + "MessageContentType", "RequestResource"),
                    new XElement(Rm + "MessageDescription", r.Description),
                    new XElement(Rm + "OriginatingMessageID", $"ERS-RM-{r.Id:N}"),
                    new XElement(Rm + "IncidentInformation",
                        new XElement(Rm + "IncidentDescription", incident is null ? "" : $"{incident.Number} {incident.Name ?? incident.Type.ToString()}".Trim()),
                        new XElement(Rm + "IncidentID", incident?.Number ?? r.IncidentId.ToString())),
                    new XElement(Msg + "ResourceInformation",
                        new XElement(Rm + "ResourceInfoElementID", r.Id.ToString()),
                        new XElement(Rm + "ResponseInformation",
                            new XElement(Rm + "PrecedingResourceInfoElementID", r.Id.ToString()),
                            new XElement(Rm + "ResponseType", r.Status == ResourceRequestStatus.Approved ? "Accepted" : "Requested")),
                        new XElement(Rm + "Resource",
                            new XElement(Rm + "Name", type?.Label ?? r.Description),
                            new XElement(Rm + "TypeStructure",
                                new XElement(Rm + "Value", type?.Label ?? r.Kind.ToString()),
                                new XElement(Rm + "ValueListURN", type is null ? "urn:x-ers:resource-request-kind" : NimsTypeList)),
                            new XElement(Rm + "Description", r.Description)),
                        new XElement(Rm + "AssignmentInformation",
                            new XElement(Rm + "Quantity", new XElement(Rm + "MeasuredQuantity", new XElement(Rm + "Amount", r.Granted ?? r.Quantity))),
                            r.Justification is null ? null : new XElement(Rm + "AnticipatedFunction", r.Justification))));
            }).ToList();
}

/// <summary>EDXL Situation Reporting 1.0: the incident, its casualties and resources, and the narrative, for the EOC.</summary>
public static class EdxlSitRep
{
    public static readonly XNamespace Ns = "urn:oasis:names:tc:emergency:EDXL:SitRep:1.0";

    public static XElement FromCop(ICopService cop, int reportNumber, string preparedBy, Guid? incidentId = null)
    {
        var incidents = cop.Incidents.Where(i => incidentId is null || i.Id == incidentId).OrderByDescending(i => i.Priority).ToList();
        var primary = incidents.FirstOrDefault();
        var units = cop.Units.Where(u => incidentId is null ? u.AssignedIncidentId is not null : u.AssignedIncidentId == incidentId).ToList();
        var deaths = cop.Health.Patients.Count(p => p.State == CasualtyState.Deceased && (incidentId is null || p.IncidentId == incidentId))
                     + cop.Health.Fatalities.AtScene.Where(f => incidentId is null || f.Key == incidentId).Sum(f => f.Value);
        var severity = primary?.Priority switch { IncidentPriority.Critical => "Extreme", IncidentPriority.High => "Severe", IncidentPriority.Medium => "Moderate", _ => "Minor" };
        var phase = primary?.Status switch { IncidentStatus.Closed => "Recovery", IncidentStatus.Controlled => "Recovery", _ => "Response" };

        return new XElement(Ns + "SitRep",
            new XElement(Ns + "MessageID", $"ERS-SITREP-{reportNumber:D3}-{cop.LastSequence}"),
            new XElement(Ns + "PreparedBy", new XElement(Ns + "PersonName", preparedBy), new XElement(Ns + "Role", "Planning Section")),
            new XElement(Ns + "ReportPurpose", "Situation update to the EOC"),
            new XElement(Ns + "ReportNumber", reportNumber),
            new XElement(Ns + "ReportVersion", reportNumber == 1 ? "Initial" : "Update"),
            new XElement(Ns + "ForTimePeriod",
                new XElement(Ns + "FromDateTime", Cap.Time(primary?.ReportedAt ?? cop.AsOf)),
                new XElement(Ns + "ToDateTime", Cap.Time(cop.AsOf))),
            new XElement(Ns + "ReportTitle", primary is null ? "Situation report" : $"Situation report: {primary.Number} {primary.Name ?? primary.Type.ToString()}".Trim()),
            new XElement(Ns + "IncidentID", primary?.Number ?? ""),
            new XElement(Ns + "IncidentLifecyclePhase", phase),
            new XElement(Ns + "Urgency", "Immediate"),
            new XElement(Ns + "ReportConfidence", "HighlyConfident"),
            new XElement(Ns + "Severity", severity),
            new XElement(Ns + "ReportingLevel", "Incident"),
            new XElement(Ns + "Report",
                new XElement(Ns + "SituationInformation",
                    incidents.Select(i => new XElement(Ns + "IncidentInformation",
                        new XElement(Ns + "IncidentName", i.Name ?? i.Type.ToString()),
                        new XElement(Ns + "IncidentNumber", i.Number),
                        new XElement(Ns + "IncidentType", i.Type.ToString()),
                        new XElement(Ns + "IncidentStatus", i.Status.ToString()),
                        new XElement(Ns + "IncidentLocation", string.Create(CultureInfo.InvariantCulture, $"{i.Location.Y:0.######} {i.Location.X:0.######}")),
                        i.Address is null ? null : new XElement(Ns + "IncidentAddress", i.Address),
                        new XElement(Ns + "IncidentStartDateTime", Cap.Time(i.ReportedAt)),
                        new XElement(Ns + "IncidentCommander", i.IncidentCommanderName ?? ""))),
                    new XElement(Ns + "SituationSummary", FormBuilder.DraftSitrep(cop)),
                    new XElement(Ns + "CasualtyAndIllnessSummary",
                        new XElement(Ns + "Injured", incidents.Sum(i => i.CasualtiesReported)),
                        new XElement(Ns + "Fatalities", deaths),
                        new XElement(Ns + "Hospitalised", cop.Health.Patients.Count(p => p.State == CasualtyState.AtHospital && (incidentId is null || p.IncidentId == incidentId)))),
                    new XElement(Ns + "ResourceSummary",
                        new XElement(Ns + "Committed", units.Count),
                        new XElement(Ns + "OnScene", units.Count(u => u.Status is UnitStatus.OnScene or UnitStatus.Operating)),
                        new XElement(Ns + "OpenRequests", cop.ResourceRequests.Count(r => r.Status == ResourceRequestStatus.Requested && (incidentId is null || r.IncidentId == incidentId)))))));
    }
}
