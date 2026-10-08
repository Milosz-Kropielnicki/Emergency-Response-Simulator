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
}
