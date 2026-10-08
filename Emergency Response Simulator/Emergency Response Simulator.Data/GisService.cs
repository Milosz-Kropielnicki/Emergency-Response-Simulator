using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Emergency_Response_Simulator.Data;

/// <summary>Static GIS layers served from PostGIS. Spatial filters run in the database on the GiST indexes.</summary>
public sealed class GisService(IDbContextFactory<ErsDbContext> contextFactory) : IGisService
{
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
}
