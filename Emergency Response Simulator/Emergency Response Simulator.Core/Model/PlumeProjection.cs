using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;

namespace Emergency_Response_Simulator.Core.Model;

/// <summary>
/// A plume projection as the COP holds it (Phase 13): the zones command is working from, what the model was told, and
/// what the zones cover on the ground. It is a prediction from reported information, so it may not match the cloud that
/// is really out there — which is the point of it.
/// </summary>
public sealed class PlumeProjection
{
    public Guid Id { get; init; }
    public Guid? IncidentId { get; init; }
    public required GeoPoint ReleaseLocation { get; init; }
    public required string Substance { get; init; }

    /// <summary>"chemical", "smoke", "toxic-gas", "biological" or "radiological".</summary>
    public required string MaterialClass { get; init; }

    public double ReleaseRateKgPerSecond { get; init; }
    public double DurationMinutes { get; init; }

    /// <summary>The weather it was run with, as command had it reported.</summary>
    public double WindFromDegrees { get; init; }

    public double WindSpeedMps { get; init; }
    public required string StabilityClass { get; init; }

    public IReadOnlyList<PlumeZone> Zones { get; init; } = [];

    /// <summary>What the zones cover: schools, hospitals, homes (Phase 13's GIS overlay).</summary>
    public IReadOnlyList<PlumeImpact> Impacts { get; init; } = [];

    /// <summary>Which model produced it, for the record.</summary>
    public required string Model { get; init; }

    /// <summary>What to do about this material, from the model's substance library.</summary>
    public string Advisory { get; init; } = "";

    /// <summary>Re-run by the simulator because the reported wind or rate moved, rather than asked for.</summary>
    public bool Automatic { get; init; }

    public DateTimeOffset RunAt { get; init; }

    /// <summary>Set once a newer projection has replaced this one.</summary>
    public DateTimeOffset? SupersededAt { get; set; }

    /// <summary>The projection command is working from now.</summary>
    public bool IsCurrent => SupersededAt is null;

    public PlumeZone? Zone(string level) => Zones.FirstOrDefault(z => z.Level == level);

    /// <summary>How far the plume reaches, metres: the far edge of its widest zone.</summary>
    public double ReachMeters => Zones.Select(z => z.MaxDistanceMeters).DefaultIfEmpty(0).Max();

    /// <summary>Everything sensitive inside the moderate or high zone, worst first.</summary>
    public IEnumerable<PlumeImpact> Pressing =>
        Impacts.Where(i => i.Level != PlumeLevels.Low).OrderByDescending(i => PlumeLevels.Severity(i.Level));

    /// <summary>"chlorine, 2 kg/s, wind from 270° at 4 m/s (class D)".</summary>
    public string Inputs =>
        $"{Substance}, {ReleaseRateKgPerSecond:0.##} kg/s, wind from {WindFromDegrees:F0}° at {WindSpeedMps:0.#} m/s (class {StabilityClass})";
}
