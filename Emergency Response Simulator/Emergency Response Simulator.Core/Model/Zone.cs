using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Core.Model;

/// <summary>
/// A dynamic GIS object drawn on top of the static map: perimeters, Hot/Warm/Cold zones,
/// evacuation areas, road closures, staging areas (Design Document §6.2, §7.2).
/// </summary>
public class Zone
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public ZoneType Type { get; set; }

    /// <summary>Usually a polygon; a road closure may be a line.</summary>
    public required Geometry Area { get; set; }

    public DateTimeOffset EffectiveFrom { get; set; }
    public DateTimeOffset? EffectiveUntil { get; set; }

    public Guid? IncidentId { get; set; }
    public Incident? Incident { get; set; }
}
