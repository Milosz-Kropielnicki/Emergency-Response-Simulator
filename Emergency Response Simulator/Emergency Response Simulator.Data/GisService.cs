using System.Data.Common;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Npgsql;

namespace Emergency_Response_Simulator.Data;

/// <summary>Static GIS layers served from PostGIS. Spatial filters run in the database on the GiST indexes.</summary>
public sealed class GisService(IDbContextFactory<ErsDbContext> contextFactory) : IGisService
{
    /// <summary>Elevation samples further away than this are not used for interpolation.</summary>
    private const double MaxElevationSampleDistanceMeters = 1000;

    public async Task<IReadOnlyList<GisLayer>> GetLayersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.GisLayers.AsNoTracking()
            .OrderBy(l => l.DisplayOrder)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GisFeature>> GetFeaturesAsync(
        string layerKey, BoundingBox bounds, CancellationToken cancellationToken = default)
    {
        var envelope = Wgs84.Factory.ToGeometry(
            new Envelope(bounds.MinLongitude, bounds.MaxLongitude, bounds.MinLatitude, bounds.MaxLatitude));

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.GisFeatures.AsNoTracking()
            .Where(f => f.Layer!.Key == layerKey && f.Geometry.Intersects(envelope))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GisFeature>> GetAllFeaturesAsync(
        string layerKey, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.GisFeatures.AsNoTracking()
            .Where(f => f.Layer!.Key == layerKey)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GisFeature>> FindFeaturesWithinAsync(
        Geometry area, IReadOnlyCollection<string> layerKeys, CancellationToken cancellationToken = default)
    {
        if (area.SRID == 0)
            area.SRID = Wgs84.Srid;

        var keys = layerKeys.ToList();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.GisFeatures.AsNoTracking()
            .Include(f => f.Layer)
            .Where(f => keys.Contains(f.Layer!.Key) && f.Geometry.Intersects(area))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NearestFeature>> FindNearestAsync(
        GeoPoint point, IReadOnlyCollection<string> layerKeys, int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        if (maxResults < 1) return [];

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The <-> operator walks the GiST index in planar degrees, which is only approximately
        // nearest at this latitude, so over-fetch and re-rank by true distance.
        var candidates = await QueryNearestAsync(db, point, layerKeys, maxResults * 4, cancellationToken);
        var nearest = candidates.OrderBy(c => c.DistanceMeters).Take(maxResults).ToList();

        var ids = nearest.Select(c => c.FeatureId).ToList();
        var features = await db.GisFeatures.AsNoTracking()
            .Where(f => ids.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, cancellationToken);

        return nearest
            .Select(c => new NearestFeature(features[c.FeatureId], c.LayerKey, c.DistanceMeters))
            .ToList();
    }

    public async Task<double?> GetElevationAsync(GeoPoint point, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var samples = (await QueryNearestAsync(db, point, [GisLayerKeys.Elevation], 4, cancellationToken))
            .Where(s => s.Elevation is not null && s.DistanceMeters <= MaxElevationSampleDistanceMeters)
            .ToList();

        if (samples.Count == 0) return null;

        // Inverse-distance weighting of the nearest grid samples.
        var exact = samples.FirstOrDefault(s => s.DistanceMeters < 1);
        if (exact is not null) return exact.Elevation;

        var weights = samples.Select(s => 1.0 / (s.DistanceMeters * s.DistanceMeters)).ToList();
        return samples.Zip(weights, (s, w) => s.Elevation!.Value * w).Sum() / weights.Sum();
    }

    private sealed record Candidate(Guid FeatureId, string LayerKey, double DistanceMeters, double? Elevation);

    private static async Task<List<Candidate>> QueryNearestAsync(
        ErsDbContext db, GeoPoint point, IReadOnlyCollection<string> layerKeys, int limit,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT f.id,
                   l.key,
                   ST_Distance(f.geometry::geography, p.geom::geography) AS distance_m,
                   (f.properties ->> 'elevation_m')::double precision AS elevation_m
            FROM gis_features f
            JOIN gis_layers l ON l.id = f.layer_id,
                 (SELECT ST_SetSRID(ST_MakePoint(@lon, @lat), 4326) AS geom) p
            WHERE f.layer_id IN (SELECT id FROM gis_layers WHERE key = ANY(@keys))
            ORDER BY f.geometry <-> p.geom
            LIMIT @limit
            """;

        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter("lon", point.Longitude));
        command.Parameters.Add(new NpgsqlParameter("lat", point.Latitude));
        command.Parameters.Add(new NpgsqlParameter("keys", layerKeys.ToArray()));
        command.Parameters.Add(new NpgsqlParameter("limit", limit));

        var results = new List<Candidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new Candidate(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3)));
        }
        return results;
    }
}
