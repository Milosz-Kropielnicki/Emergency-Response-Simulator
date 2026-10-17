using System.Globalization;
using System.Xml.Linq;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Standards;

/// <summary>An incident as a NIEM exchange carries it.</summary>
public sealed record NiemIncident(string Id, string? Name, string Category, string? Status, DateTimeOffset? Date, GeoPoint? Location,
    string? Address, string? Description, string? Priority);

/// <summary>
/// The simulator's NIEM incident exchange (Phase 20), in the manner of an IEPD: NIEM Core 5.0 for the incident (activity
/// identification, name, category, status, date, location and address) and for the units (item identification and
/// category, as their NIMS resource type), with the few things NIEM Core has no element for (priority, unit status, the
/// incident a unit is assigned to) in the exchange's own namespace. A CAD or records system reads it, or sends its own
/// incidents in.
/// </summary>
public static class Niem
{
    public static readonly XNamespace Nc = "http://release.niem.gov/niem/niem-core/5.0/";
    public static readonly XNamespace Structures = "http://release.niem.gov/niem/structures/5.0/";
    public static readonly XNamespace Exchange = "urn:ers:niem:incident-exchange:1.0";

    public static XElement FromCop(ICopService cop, string sender, Guid? incidentId = null)
    {
        var incidents = cop.Incidents.Where(i => incidentId is null || i.Id == incidentId).OrderBy(i => i.ReportedAt).ToList();
        var units = cop.Units.Where(u => incidentId is null || u.AssignedIncidentId == incidentId).OrderBy(u => u.Callsign).ToList();
        return new XElement(Exchange + "IncidentExchange",
            new XAttribute(XNamespace.Xmlns + "nc", Nc),
            new XAttribute(XNamespace.Xmlns + "structures", Structures),
            new XAttribute(XNamespace.Xmlns + "ers", Exchange),
            new XElement(Nc + "DocumentCreationDate", new XElement(Nc + "DateTime", Cap.Time(cop.AsOf))),
            new XElement(Exchange + "ExchangeSender", sender),
            incidents.Select(i => new XElement(Nc + "Incident",
                new XAttribute(Structures + "id", $"incident-{i.Id:N}"),
                new XElement(Nc + "ActivityIdentification", new XElement(Nc + "IdentificationID", i.Number)),
                i.Name is null ? null : new XElement(Nc + "ActivityName", i.Name),
                new XElement(Nc + "ActivityCategoryText", i.Type.ToString()),
                new XElement(Nc + "ActivityStatus", new XElement(Nc + "StatusText", i.Status.ToString())),
                new XElement(Nc + "ActivityDate", new XElement(Nc + "DateTime", Cap.Time(i.ReportedAt))),
                i.Threats.Count == 0 ? null : new XElement(Nc + "ActivityDescriptionText", string.Join("; ", i.Threats)),
                Location(GeoPoint.FromPoint(i.Location), i.Address, "IncidentLocation"),
                new XElement(Exchange + "IncidentPriorityText", i.Priority.ToString()),
                new XElement(Exchange + "IncidentCasualtyQuantity", i.CasualtiesReported),
                i.IncidentCommanderName is null ? null : new XElement(Exchange + "IncidentCommanderName", i.IncidentCommanderName))),
            units.Select(u => new XElement(Exchange + "Unit",
                new XAttribute(Structures + "id", $"unit-{u.Id:N}"),
                new XElement(Nc + "ItemIdentification", new XElement(Nc + "IdentificationID", u.Callsign)),
                new XElement(Nc + "ItemCategoryText", NimsResourceTypes.Of(u.Type).Label),
                u.Agency is null ? null : new XElement(Nc + "ItemOwner", new XElement(Nc + "EntityOrganization",
                    new XElement(Nc + "OrganizationName", u.Agency.Name))),
                u.Location is null ? null : Location(GeoPoint.FromPoint(u.Location), null, "ItemLocation"),
                new XElement(Exchange + "UnitStatusText", u.Status.ToString()),
                u.AssignedIncidentId is { } assigned && cop.FindIncident(assigned) is { } incident
                    ? new XElement(Exchange + "AssignedIncidentID", incident.Number)
                    : null)));
    }

    private static XElement Location(GeoPoint at, string? address, string name) =>
        new(name == "IncidentLocation" ? Nc + "IncidentLocation" : Exchange + name,
            address is null ? null : new XElement(Nc + "LocationAddress", new XElement(Nc + "AddressFullText", address)),
            new XElement(Nc + "Location2DGeospatialCoordinate",
                new XElement(Nc + "GeographicCoordinateLatitude", new XElement(Nc + "LatitudeDegreeValue", at.Latitude.ToString("0.######", CultureInfo.InvariantCulture))),
                new XElement(Nc + "GeographicCoordinateLongitude", new XElement(Nc + "LongitudeDegreeValue", at.Longitude.ToString("0.######", CultureInfo.InvariantCulture)))));

    /// <summary>The incidents in an exchange; throws <see cref="FormatException"/> when there is no NIEM incident in it.</summary>
    public static (string Sender, IReadOnlyList<NiemIncident> Incidents) Read(XElement root)
    {
        var incidents = root.DescendantsAndSelf(Nc + "Incident").Select(i =>
        {
            string? Text(params string[] path)
            {
                XElement? at = i;
                foreach (var step in path) at = at?.Element(Nc + step);
                return at?.Value.Trim() is { Length: > 0 } value ? value : null;
            }
            var coordinate = i.Element(Nc + "IncidentLocation")?.Element(Nc + "Location2DGeospatialCoordinate");
            var lat = coordinate?.Element(Nc + "GeographicCoordinateLatitude")?.Element(Nc + "LatitudeDegreeValue")?.Value;
            var lon = coordinate?.Element(Nc + "GeographicCoordinateLongitude")?.Element(Nc + "LongitudeDegreeValue")?.Value;
            var id = Text("ActivityIdentification", "IdentificationID") ?? throw new FormatException("A NIEM incident has no ActivityIdentification/IdentificationID.");
            return new NiemIncident(id, Text("ActivityName"), Text("ActivityCategoryText") ?? "Other", Text("ActivityStatus", "StatusText"),
                Text("ActivityDate", "DateTime") is { } date ? DateTimeOffset.Parse(date, CultureInfo.InvariantCulture) : null,
                lat is not null && lon is not null ? new GeoPoint(double.Parse(lat, CultureInfo.InvariantCulture), double.Parse(lon, CultureInfo.InvariantCulture)) : null,
                Text("IncidentLocation", "LocationAddress", "AddressFullText"), Text("ActivityDescriptionText"),
                i.Element(Exchange + "IncidentPriorityText")?.Value.Trim());
        }).ToList();
        if (incidents.Count == 0) throw new FormatException("No NIEM Core <nc:Incident> in the document.");
        return (root.Element(Exchange + "ExchangeSender")?.Value.Trim() ?? "External CAD", incidents);
    }
}
