using Emergency_Response_Simulator.Core.Events;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Emergency_Response_Simulator.Tests;

/// <summary>Skips unless ConnectionStrings:ErsTest is configured (see database/Setup-Database.ps1).</summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (PostgresFixture.ConnectionString is null)
            Skip = "ConnectionStrings:ErsTest is not configured; run database/Setup-Database.ps1.";
    }
}

public sealed class PostgresFixture : IAsyncLifetime
{
    public static string? ConnectionString { get; } =
        ErsConfiguration.Load().GetConnectionString("ErsTest") is { Length: > 0 } cs ? cs : null;

    public PooledDbContextFactory<ErsDbContext>? Factory { get; private set; }

    public async Task InitializeAsync()
    {
        if (ConnectionString is null) return;

        var options = new DbContextOptionsBuilder<ErsDbContext>();
        ServiceCollectionExtensions.ConfigureErsDb(options, ConnectionString);
        Factory = new PooledDbContextFactory<ErsDbContext>(options.Options);

        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

public class PostgresIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [DatabaseFact]
    public async Task Event_store_round_trips_events_in_sequence_order()
    {
        var store = new PostgresEventStore(fixture.Factory!);
        var session = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 10, 8, 15, 31, 4, TimeSpan.FromHours(1));
        var notified = new List<long>();
        store.Appended += (_, e) => notified.Add(e.Sequence);

        var call = await store.AppendAsync(new SimEventDraft(session, at, EventVisibility.Perceived, EventSources.Comms,
            new CallReceived(Guid.NewGuid(), "911 caller", "Explosion reported.", TestHarness.Dublin, 150)));
        var truth = await store.AppendAsync(new SimEventDraft(session, at.AddSeconds(14), EventVisibility.Truth, EventSources.Engine,
            new WeatherChanged(45, 6, 12, 0.5)));

        var read = new List<SimEvent>();
        await foreach (var e in store.ReadAsync(session))
            read.Add(e);

        Assert.True(truth.Sequence > call.Sequence);
        Assert.Equal([call.Sequence, truth.Sequence], notified);
        Assert.Equal([call.Sequence, truth.Sequence], read.Select(e => e.Sequence));
        Assert.Equal(at, read[0].SimTime); // same instant, stored as UTC
        Assert.Equal("Explosion reported.", Assert.IsType<CallReceived>(read[0].Payload).Summary);
        Assert.Equal(EventVisibility.Truth, read[1].Visibility);
    }

    [DatabaseFact]
    public async Task Events_table_rejects_updates_and_deletes()
    {
        var store = new PostgresEventStore(fixture.Factory!);
        var appended = await store.AppendAsync(new SimEventDraft(Guid.NewGuid(), DateTimeOffset.UtcNow,
            EventVisibility.Perceived, EventSources.C2, new UnitStatusChanged(Guid.NewGuid(), UnitStatus.EnRoute)));

        await using var db = await fixture.Factory!.CreateDbContextAsync();

        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Events.Where(e => e.Sequence == appended.Sequence).ExecuteUpdateAsync(s => s.SetProperty(e => e.Source, "tampered")));
        var delete = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Events.Where(e => e.Sequence == appended.Sequence).ExecuteDeleteAsync());

        Assert.Contains("append-only", update.MessageText);
        Assert.Contains("append-only", delete.MessageText);
    }

    [DatabaseFact]
    public async Task Gis_service_finds_features_inside_an_area_using_postgis()
    {
        var layerKey = $"schools-{Guid.NewGuid():N}";
        await using (var db = await fixture.Factory!.CreateDbContextAsync())
        {
            db.GisLayers.Add(new GisLayer
            {
                Key = layerKey,
                Name = "Schools",
                Kind = GisLayerKind.Static,
                Features =
                [
                    new GisFeature { Name = "Inside school", Geometry = new GeoPoint(53.345, -6.255).ToPoint(), Properties = { ["pupils"] = "420" } },
                    new GisFeature { Name = "Outside school", Geometry = new GeoPoint(53.400, -6.100).ToPoint() },
                ],
            });
            await db.SaveChangesAsync();
        }

        var gis = new GisService(fixture.Factory!);
        var plume = Wgs84.CreatePolygon([new(53.34, -6.27), new(53.35, -6.27), new(53.35, -6.24), new(53.34, -6.24)]);

        var hits = await gis.FindFeaturesWithinAsync(plume, [layerKey]);

        var school = Assert.Single(hits);
        Assert.Equal("Inside school", school.Name);
        Assert.Equal("420", school.Properties["pupils"]);
    }

    [DatabaseFact]
    public async Task Nearest_features_are_ranked_by_true_distance_in_metres()
    {
        var layerKey = $"hospitals-{Guid.NewGuid():N}";
        var origin = new GeoPoint(53.344, -6.260);
        // At 53°N a degree of longitude is ~0.6 of a degree of latitude, so the point that is nearest
        // in raw degrees (north, 0.009° ≈ 1.0 km) is actually further than the east one (0.012° ≈ 0.8 km).
        var east = new GeoPoint(53.344, -6.248);
        var north = new GeoPoint(53.353, -6.260);
        await SeedLayerAsync(layerKey, ("East", east), ("North", north), ("Far", new GeoPoint(53.40, -6.10)));

        var nearest = await new GisService(fixture.Factory!).FindNearestAsync(origin, [layerKey], maxResults: 2);

        Assert.Equal(["East", "North"], nearest.Select(n => n.Feature.Name));
        Assert.Equal(GeoMath.DistanceMeters(origin, east), nearest[0].DistanceMeters, 3.0);
        Assert.Equal(GeoMath.DistanceMeters(origin, north), nearest[1].DistanceMeters, 3.0);
        Assert.All(nearest, n => Assert.Equal(layerKey, n.LayerKey));
    }

    [DatabaseFact]
    public async Task Elevation_is_interpolated_from_nearby_samples_and_absent_far_away()
    {
        // Samples in an otherwise empty spot (mid-Atlantic) so they don't mix with other elevation data.
        var lat = 45 + Random.Shared.NextDouble();
        var west = new GeoPoint(lat, -30.000);
        var east = new GeoPoint(lat, -29.998); // ~157 m apart
        await SeedLayerAsync(GisLayerKeys.Elevation, ("w", west, "10"), ("e", east, "20"));
        var gis = new GisService(fixture.Factory!);

        var atWest = await gis.GetElevationAsync(west);
        var middle = await gis.GetElevationAsync(new GeoPoint(lat, -29.999));
        // Seeded samples (this run's and earlier runs') all lie at 45–46°N, 30°W; nothing is ever seeded here.
        var nowhere = await gis.GetElevationAsync(new GeoPoint(-60, 150));

        Assert.Equal(10, atWest!.Value, 0.01);
        Assert.Equal(15, middle!.Value, 0.5);
        Assert.Null(nowhere);
    }

    private Task SeedLayerAsync(string layerKey, params (string Name, GeoPoint At)[] points) =>
        SeedLayerAsync(layerKey, points.Select(p => (p.Name, p.At, (string?)null)).ToArray());

    private async Task SeedLayerAsync(string layerKey, params (string Name, GeoPoint At, string? Elevation)[] points)
    {
        await using var db = await fixture.Factory!.CreateDbContextAsync();
        var layer = await db.GisLayers.SingleOrDefaultAsync(l => l.Key == layerKey);
        if (layer is null)
        {
            layer = new GisLayer { Key = layerKey, Name = layerKey, Kind = GisLayerKind.Static };
            db.GisLayers.Add(layer);
        }

        foreach (var (name, at, elevation) in points)
        {
            var feature = new GisFeature { Name = name, Geometry = at.ToPoint(), Layer = layer };
            if (elevation is not null)
                feature.Properties["elevation_m"] = elevation;
            db.GisFeatures.Add(feature);
        }
        await db.SaveChangesAsync();
    }
}
