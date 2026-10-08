using System.Diagnostics;
using System.Globalization;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Data;
using Microsoft.EntityFrameworkCore;

namespace Emergency_Response_Simulator.GisImport;

/// <summary>Replaces the contents of static GIS layers with freshly converted features.</summary>
public sealed class GisImporter(IDbContextFactory<ErsDbContext> contextFactory, DataSources sources, TextWriter log)
{
    public const string OsmSource = "© OpenStreetMap contributors (ODbL)";
    public const string ElevationSource = "Copernicus DEM GLO-90 via Open-Meteo (CC BY 4.0)";

    private const int InsertBatchSize = 2000;

    public async Task ImportOsmLayerAsync(OsmLayerQuery query, BoundingBox box, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        log.WriteLine($"{query.LayerKey}");

        using var response = await sources.GetOverpassAsync(query.LayerKey, query.ToOverpassQl(box.ToString()), cancellationToken);
        var (osmFeatures, skipped) = OsmGeometryBuilder.Build(response);

        var features = osmFeatures.Select(f => ToFeature(f, query)).ToList();
        await ReplaceLayerAsync(query.LayerKey, OsmSource, features, cancellationToken);

        log.WriteLine($"  stored   {features.Count:N0} features" +
                      (skipped > 0 ? $" ({skipped} skipped: incomplete geometry)" : "") +
                      $" in {stopwatch.Elapsed.TotalSeconds:F1}s");
    }

    public async Task ImportElevationAsync(BoundingBox box, double spacingMeters, CancellationToken cancellationToken)
    {
        log.WriteLine(GisLayerKeys.Elevation);
        var samples = await sources.GetElevationGridAsync(box, spacingMeters, cancellationToken);

        var features = samples.Select(s => new GisFeature
        {
            Id = Guid.NewGuid(),
            Geometry = s.Point.ToPoint(),
            Properties = { ["elevation_m"] = s.ElevationMeters.ToString("F1", CultureInfo.InvariantCulture) },
        }).ToList();

        await ReplaceLayerAsync(GisLayerKeys.Elevation, ElevationSource, features, cancellationToken);
        log.WriteLine($"  stored   {features.Count:N0} samples, {samples.Min(s => s.ElevationMeters):F0}–{samples.Max(s => s.ElevationMeters):F0} m");
    }

    internal static GisFeature ToFeature(OsmFeature osm, OsmLayerQuery query)
    {
        var properties = new Dictionary<string, string> { ["osm_id"] = osm.OsmId };
        foreach (var tag in query.KeepTags)
        {
            if (osm.Tags.TryGetValue(tag, out var value) && !string.IsNullOrWhiteSpace(value))
                properties[tag] = value;
        }

        if (query.LayerKey == GisLayerKeys.CriticalInfrastructure
            && OsmLayerQueries.InfrastructureCategory(osm.Tags) is { } category)
        {
            properties["category"] = category;
        }

        return new GisFeature
        {
            Id = Guid.NewGuid(),
            Name = osm.Tags.TryGetValue("name", out var name) ? Truncate(name, 200) : null,
            Geometry = osm.Geometry,
            Properties = properties,
        };
    }

    private async Task ReplaceLayerAsync(
        string layerKey, string source, List<GisFeature> features, CancellationToken cancellationToken)
    {
        var definition = GisLayerKeys.Find(layerKey)
            ?? throw new ArgumentException($"Unknown layer \"{layerKey}\".", nameof(layerKey));

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var layer = await db.GisLayers.SingleOrDefaultAsync(l => l.Key == layerKey, cancellationToken);
        if (layer is null)
        {
            layer = new GisLayer { Id = Guid.NewGuid(), Key = layerKey, Name = definition.Name };
            db.GisLayers.Add(layer);
        }

        layer.Name = definition.Name;
        layer.Kind = GisLayerKind.Static;
        layer.Group = definition.Group;
        layer.DisplayOrder = definition.DisplayOrder;
        layer.VisibleByDefault = definition.VisibleByDefault;
        layer.MinZoom = definition.MinZoom;
        layer.Source = source;
        layer.ImportedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await db.GisFeatures.Where(f => f.LayerId == layer.Id).ExecuteDeleteAsync(cancellationToken);

        db.ChangeTracker.AutoDetectChangesEnabled = false;
        foreach (var batch in features.Chunk(InsertBatchSize))
        {
            foreach (var feature in batch)
                feature.LayerId = layer.Id;
            db.GisFeatures.AddRange(batch);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
