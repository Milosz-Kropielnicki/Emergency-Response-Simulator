using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.State;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Simulation.Hazards;

/// <summary>
/// A basic physical hazard model (Design Document §10.4) advanced by <c>HazardSystem</c> every tick.
/// Models are ground truth: what they say is never shown to the trainee except through what crews,
/// callers and sensors later report.
/// </summary>
public interface IHazardModel
{
    HazardKind Kind { get; }

    /// <summary>Still burning, flooding or releasing (or a cloud still lingering).</summary>
    bool IsActive { get; }

    /// <summary>Area affected so far, m².</summary>
    double AreaSquareMeters { get; }

    /// <summary>Fire: area burning now (m²). Flood: deepest water (m). Plume: concentration at 100 m downwind (mg/m³).</summary>
    double Intensity { get; }

    void Step(HazardStepInput input, TimeSpan delta);

    /// <summary>The affected area as WGS84 polygons (empty when there is nothing to show).</summary>
    IReadOnlyList<Polygon> Footprint();

    /// <summary>How dangerous it is to be at <paramref name="point"/> right now.</summary>
    HazardExposure ExposureAt(GeoPoint point);
}

/// <param name="Rate">Flood inflow (m³/s) or plume release rate (kg/s).</param>
/// <param name="Suppression">Crews putting water on a fire.</param>
public sealed record HazardStepInput(
    DateTimeOffset SimTime,
    WorldWeather Weather,
    double Rate = 0,
    IReadOnlyList<Suppression>? Suppression = null);

/// <summary>A fire crew's water: it can put out <paramref name="CellsPerMinute"/> burning cells within reach.</summary>
public readonly record struct Suppression(GeoPoint Location, double ReachMeters, double CellsPerMinute);

public enum HazardLevel
{
    None,

    /// <summary>Noticeable: smoke, a smell, ankle-deep water.</summary>
    Low,

    /// <summary>Harmful with time: heat, irritating concentrations, knee-deep water.</summary>
    Moderate,

    /// <summary>Immediately dangerous: in the fire, lethal concentrations, deep water.</summary>
    High,
}

/// <param name="Value">Fire: distance to the nearest burning area (m). Flood: depth (m). Plume: concentration (mg/m³).</param>
public readonly record struct HazardExposure(HazardLevel Level, double Value)
{
    public static readonly HazardExposure None = new(HazardLevel.None, 0);
}

/// <summary>Stable pseudo-random helpers so a session behaves the same way every run (and in tests).</summary>
public static class SimRandom
{
    /// <summary>A generator seeded from an id; Guid hash codes are computed from their bytes, so this is stable.</summary>
    public static Random For(Guid id, int salt = 0) => new(id.GetHashCode() ^ (salt * 7919));

    /// <summary>A stable id derived from another id and an index, e.g. one flooded cell of a flood.</summary>
    public static Guid Derive(Guid id, int index)
    {
        var bytes = id.ToByteArray();
        var extra = BitConverter.GetBytes(index * 2654435761u);
        for (var i = 0; i < 4; i++)
            bytes[i] ^= extra[i];
        bytes[15] ^= 0x5A;
        return new Guid(bytes);
    }
}
