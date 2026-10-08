using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Core.Model;

/// <summary>A toggleable map layer (Design Document §6.8, §7.1).</summary>
public class GisLayer
{
    public Guid Id { get; set; }

    /// <summary>Stable identifier used by code and the UI, e.g. "roads", "hospitals".</summary>
    public required string Key { get; set; }

    public required string Name { get; set; }
    public GisLayerKind Kind { get; set; }
    public bool VisibleByDefault { get; set; }
    public int DisplayOrder { get; set; }

    public List<GisFeature> Features { get; set; } = [];
}

/// <summary>One geographic object in a layer: a road segment, a hospital, a hydrant.</summary>
public class GisFeature
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public required Geometry Geometry { get; set; }

    /// <summary>Free-form attributes from the source data (stored as jsonb).</summary>
    public Dictionary<string, string> Properties { get; set; } = [];

    public Guid LayerId { get; set; }
    public GisLayer? Layer { get; set; }
}
