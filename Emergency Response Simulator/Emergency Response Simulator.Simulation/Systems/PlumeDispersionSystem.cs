using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Engine;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.Simulation.Systems;

/// <summary>
/// Keeps the dispersion model's picture of a real release up to date (Phase 13).
/// <list type="bullet">
/// <item><b>Ground truth</b>: for every live plume, the Python hazard-model service is asked where the cloud is now,
/// from the <i>true</i> weather and the <i>true</i> release. Its answer is recorded as <see cref="PlumePredicted"/>
/// and the plume model adopts it, so the cloud crews walk into is the fuller model's, not the engine's own.</item>
/// <item><b>Dynamic updates</b>: it asks again when the wind backs or freshens, when the release rate is revised or the
/// store runs dry, and otherwise every <see cref="Interval"/> so the cloud's leading edge keeps moving.</item>
/// <item><b>Command's projections</b>: a projection command ran on what it knows is re-run when the <i>reported</i>
/// wind has moved far enough to change the answer, superseding the old one. It reads only the COP, never the truth.</item>
/// </list>
/// The service is called off the engine thread; answers are picked up and recorded on a later tick, so a slow or
/// missing service never holds up the simulation. With no service at all, nothing is recorded and the in-engine
/// Gaussian plume carries the session on its own (<see cref="PlumeModel.InEngineModel"/>). Because the predictions are
/// events, a replay rebuilds the same cloud without calling the service again; a branch re-run, which has no service,
/// falls back to the in-engine model.
/// </summary>
public sealed class PlumeDispersionSystem(IPlumeService? plume, ICopService? cop = null, IGisService? gis = null) : ISimulationSystem
{
    /// <summary>A fresh prediction at least this often (simulation time), so the cloud keeps advancing.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    /// <summary>A wind shift worth re-predicting for.</summary>
    public const double WindShiftDegrees = 15;

    public const double WindSpeedChangeMps = 1.5;

    /// <summary>Command's own projection is re-run when the reported wind has moved this far from the one it used.</summary>
    public const double ProjectionWindShiftDegrees = 30;

    /// <summary>Or its speed has changed this much, which stretches or shrinks the zones.</summary>
    public const double ProjectionWindSpeedChangeMps = 3;

    /// <summary>And no more often than this, however much the wind wanders.</summary>
    public static readonly TimeSpan ProjectionRefreshInterval = TimeSpan.FromMinutes(2);

    private sealed record Asked(double WindFrom, double WindSpeed, double Rate, DateTimeOffset At);

    private readonly Dictionary<Guid, Asked> _asked = [];
    private readonly Dictionary<Guid, Task<PlumePredicted?>> _running = [];
    private readonly Dictionary<Guid, (double Rate, DateTimeOffset StartedAt, DateTimeOffset? StoppedAt)> _releases = [];
    private readonly Dictionary<Guid, Task<PlumeProjected?>> _projecting = [];
    private readonly Dictionary<Guid, DateTimeOffset> _projectedAt = [];

    public int Order => 4; // just before HazardSystem steps the models

    public void Update(SimulationContext context)
    {
        if (plume is null) return;
        Harvest(context);
        TrackReleases(context);
        Request(context);
        RefreshProjections(context);
    }

    // ---- Answers coming back ----

    /// <summary>Records every prediction that has come back since the last tick, on the engine's own thread.</summary>
    private void Harvest(SimulationContext context)
    {
        foreach (var (hazardId, task) in _running.Where(r => r.Value.IsCompleted).ToList())
        {
            _running.Remove(hazardId);
            if (task is { IsCompletedSuccessfully: true, Result: { } prediction } && context.World.Hazards.ContainsKey(hazardId))
                context.EmitTruth(prediction);
        }

        foreach (var (projectionId, task) in _projecting.Where(p => p.Value.IsCompleted).ToList())
        {
            _projecting.Remove(projectionId);
            if (task is { IsCompletedSuccessfully: true, Result: { } projected })
                Publish(context, projected);
        }
    }

    /// <summary>A projection, and an alert for each sensitive place the worse zones cover.</summary>
    private static void Publish(SimulationContext context, PlumeProjected projected)
    {
        context.EmitPerceived(projected, EventSources.Engine);
        foreach (var alert in PlumeImpacts.Alerts(projected))
            context.EmitPerceived(alert, EventSources.Engine);
    }

    // ---- Ground truth ----

    /// <summary>Remembers when each release began and when it stopped, which the model needs to drift the cloud away.</summary>
    private void TrackReleases(SimulationContext context)
    {
        foreach (var hazard in context.World.Hazards.Values.Where(h => h.Kind == HazardKind.Plume))
        {
            var known = _releases.GetValueOrDefault(hazard.Id);
            if (known.StartedAt == default)
            {
                if (hazard.Rate > 0) _releases[hazard.Id] = (hazard.Rate, hazard.StartedAt, null);
                continue;
            }
            if (hazard.Rate > 0) _releases[hazard.Id] = (hazard.Rate, known.StartedAt, null);
            else if (known.StoppedAt is null) _releases[hazard.Id] = (known.Rate, known.StartedAt, context.SimTime);
        }
    }

    private void Request(SimulationContext context)
    {
        var world = context.World;
        var weather = world.Weather;
        var stability = AtmosphericStability.Classify(weather, context.SimTime);

        foreach (var hazard in world.Hazards.Values.Where(h => h.Kind == HazardKind.Plume && !h.Ended).ToList())
        {
            if (_running.ContainsKey(hazard.Id)) continue;
            var release = _releases.GetValueOrDefault(hazard.Id);
            if (release.StartedAt == default) continue; // nothing has come out of it yet
            if (Reason(hazard, release, weather, context.SimTime) is not { } reason) continue;

            _asked[hazard.Id] = new Asked(weather.WindFromDegrees, weather.WindSpeedMps, hazard.Rate, context.SimTime);
            var request = new PlumeRequest(
                hazard.Origin,
                hazard.Substance ?? "toxic gas",
                ReleaseRateKgPerSecond: release.Rate,
                DurationMinutes: Math.Max(0.1, ((release.StoppedAt ?? context.SimTime) - release.StartedAt).TotalMinutes),
                Weather: new WeatherInput(weather.WindFromDegrees, weather.WindSpeedMps, weather.TemperatureC, weather.RelativeHumidity, stability),
                QuantityKg: hazard.Inventory > 0 ? hazard.Inventory : null,
                ElapsedMinutes: Math.Max(0.1, (context.SimTime - release.StartedAt).TotalMinutes),
                Terrain: new TerrainInput());

            var id = hazard.Id;
            _running[id] = PredictAsync(id, request, reason);
        }
    }

    /// <summary>Why this hazard needs asking about now, or null when the last answer still stands.</summary>
    private string? Reason(WorldHazard hazard, (double Rate, DateTimeOffset StartedAt, DateTimeOffset? StoppedAt) release,
        WorldWeather weather, DateTimeOffset now)
    {
        if (_asked.GetValueOrDefault(hazard.Id) is not { } last) return "first prediction";

        var turned = Math.Abs(GeoMath.BearingDifference(weather.WindFromDegrees, last.WindFrom));
        if (turned >= WindShiftDegrees) return $"wind shifted {turned:F0}°";
        if (Math.Abs(weather.WindSpeedMps - last.WindSpeed) >= WindSpeedChangeMps)
            return $"wind {(weather.WindSpeedMps > last.WindSpeed ? "freshened" : "dropped")} to {weather.WindSpeedMps:0.#} m/s";
        if (Math.Abs(hazard.Rate - last.Rate) > 1e-6)
            return hazard.Rate <= 0 ? "the release has stopped" : $"release rate revised to {hazard.Rate:0.##} kg/s";
        return now - last.At >= Interval ? "the cloud has moved on" : null;
    }

    private async Task<PlumePredicted?> PredictAsync(Guid hazardId, PlumeRequest request, string reason)
    {
        try
        {
            var prediction = await plume!.PredictAsync(request);
            return new PlumePredicted(
                hazardId,
                prediction.Model,
                prediction.Substance?.Name ?? request.Substance,
                prediction.Substance?.MaterialClass ?? "toxic-gas",
                prediction.Zones,
                prediction.FrontMeters,
                prediction.RearMeters,
                prediction.EffectiveHeightMeters,
                prediction.DenseGas,
                prediction.StabilityClass,
                prediction.Substance?.Advisory ?? "",
                reason);
        }
        catch (Exception)
        {
            // No service, or it failed: the in-engine model keeps the session going. It will be asked again.
            return null;
        }
    }

    // ---- Command's own projections ----

    /// <summary>
    /// Runs a projection on what command knows, for <see cref="IC2Service.PredictPlumeAsync"/> and for the automatic
    /// refresh. The GIS overlay (what the zones cover) is part of the answer.
    /// </summary>
    public static async Task<PlumeProjected?> ProjectAsync(
        IPlumeService plume,
        IGisService? gis,
        Guid projectionId,
        Guid? incidentId,
        GeoPoint location,
        string substance,
        double rateKgPerSecond,
        double durationMinutes,
        PerceivedWeather? weather,
        DateTimeOffset now,
        bool automatic,
        Guid? supersedes,
        CancellationToken cancellationToken = default)
    {
        var windFrom = weather?.WindFromDegrees ?? 270;
        var windSpeed = weather?.WindSpeedMps ?? 4;
        var stability = AtmosphericStability.Classify(windSpeed, now, weather?.RelativeHumidity ?? 0.7);

        var prediction = await plume.PredictAsync(new PlumeRequest(
            location,
            substance,
            rateKgPerSecond,
            durationMinutes,
            new WeatherInput(windFrom, windSpeed, weather?.TemperatureC ?? 14, weather?.RelativeHumidity ?? 0.7, stability),
            Terrain: new TerrainInput()), cancellationToken);

        var impacts = await PlumeImpacts.FindAsync(gis, prediction.Zones, cancellationToken);
        return new PlumeProjected(
            projectionId,
            incidentId,
            location,
            prediction.Substance?.Name ?? substance,
            prediction.Substance?.MaterialClass ?? "toxic-gas",
            rateKgPerSecond,
            durationMinutes,
            windFrom,
            windSpeed,
            prediction.StabilityClass,
            prediction.Zones,
            impacts,
            prediction.Model,
            prediction.Substance?.Advisory ?? "",
            automatic,
            supersedes);
    }

    /// <summary>
    /// Re-runs a projection when the <i>reported</i> wind has moved far enough to change the answer, so the zones on the
    /// COP are not quietly out of date (Phase 13's dynamic updates). It reads only the COP: the projection is command's,
    /// so it never learns the true wind or the true release rate. The release rate stays command's own estimate; a
    /// revised one is command's to put in, by projecting again.
    /// </summary>
    private void RefreshProjections(SimulationContext context)
    {
        if (cop is null || cop.Weather is not { } weather) return;

        foreach (var projection in cop.PlumeProjections.Where(p => p.IsCurrent))
        {
            if (_projecting.ContainsKey(projection.Id)) continue;
            if (projection.IncidentId is { } incident && cop.Incidents.FirstOrDefault(i => i.Id == incident)?.Status == IncidentStatus.Closed) continue;
            if (_projectedAt.GetValueOrDefault(projection.Id) is var last && last != default && context.SimTime - last < ProjectionRefreshInterval) continue;

            var turned = Math.Abs(GeoMath.BearingDifference(weather.WindFromDegrees, projection.WindFromDegrees));
            var freshened = Math.Abs(weather.WindSpeedMps - projection.WindSpeedMps) >= ProjectionWindSpeedChangeMps;
            if (turned < ProjectionWindShiftDegrees && !freshened) continue;

            // The refresh interval runs from now for both the old projection and the one replacing it.
            var id = Guid.NewGuid();
            _projectedAt[projection.Id] = context.SimTime;
            _projectedAt[id] = context.SimTime;
            _projecting[id] = Refresh(id, projection, projection.ReleaseRateKgPerSecond, weather, context.SimTime);
        }
    }

    private async Task<PlumeProjected?> Refresh(Guid id, PlumeProjection previous, double rate, PerceivedWeather weather, DateTimeOffset now)
    {
        try
        {
            return await ProjectAsync(plume!, gis, id, previous.IncidentId, previous.ReleaseLocation, previous.Substance,
                rate, previous.DurationMinutes, weather, now, automatic: true, supersedes: previous.Id);
        }
        catch (Exception)
        {
            return null; // the projection on the COP stands until the service answers again
        }
    }
}
