using System.Globalization;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Data;
using Emergency_Response_Simulator.GisImport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;

const string usage = """
    Imports static GIS layers (Design Document §7.1) into PostGIS.

    Usage:
      dotnet run --project "Emergency Response Simulator.GisImport" -- [options]

    Options:
      --bbox south,west,north,east   Area to import (default: central Dublin, 53.325,-6.300,53.365,-6.215)
      --layers a,b,c                 Only these layers (default: all). Keys: {0}
      --elevation-spacing <metres>   Elevation grid spacing (default: 250)
      --connection <name>            Connection string name in appsettings.Local.json (default: Ers)
      --refresh                      Ignore the download cache and fetch again
    """;

var bbox = BoundingBox.Parse("53.325,-6.300,53.365,-6.215");
var layerKeys = GisLayerKeys.All.Select(l => l.Key).ToList();
var elevationSpacing = 250.0;
var connectionName = "Ers";
var refresh = false;

try
{
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--bbox": bbox = BoundingBox.Parse(args[++i]); break;
            case "--layers": layerKeys = [.. args[++i].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)]; break;
            case "--elevation-spacing": elevationSpacing = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--connection": connectionName = args[++i]; break;
            case "--refresh": refresh = true; break;
            case "-h" or "--help":
                Console.WriteLine(usage, string.Join(", ", GisLayerKeys.All.Select(l => l.Key)));
                return 0;
            default: throw new ArgumentException($"Unknown option \"{args[i]}\".");
        }
    }

    var unknown = layerKeys.Where(k => GisLayerKeys.Find(k) is null).ToList();
    if (unknown.Count > 0)
        throw new ArgumentException($"Unknown layer(s): {string.Join(", ", unknown)}.");
    if (elevationSpacing < 30)
        throw new ArgumentException("--elevation-spacing must be at least 30 metres.");
}
catch (Exception ex) when (ex is ArgumentException or FormatException or IndexOutOfRangeException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(string.Format(usage, string.Join(", ", GisLayerKeys.All.Select(l => l.Key))));
    return 2;
}

var connectionString = ErsConfiguration.Load().GetConnectionString(connectionName);
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine($"ConnectionStrings:{connectionName} is not configured. Run database\\Setup-Database.ps1 first.");
    return 1;
}

var options = new DbContextOptionsBuilder<ErsDbContext>();
ServiceCollectionExtensions.ConfigureErsDb(options, connectionString);
var contextFactory = new PooledDbContextFactory<ErsDbContext>(options.Options);

await using (var db = await contextFactory.CreateDbContextAsync())
{
    var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
    if (pending.Count > 0)
    {
        Console.WriteLine($"Applying {pending.Count} pending migration(s)...");
        await db.Database.MigrateAsync();
    }
}

var cacheDirectory = Path.Combine(FindRepositoryRoot(), "gis-data", "cache");
using var http = DataSources.CreateHttpClient();
var importer = new GisImporter(contextFactory, new DataSources(http, cacheDirectory, refresh, Console.Out), Console.Out);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

Console.WriteLine($"Importing {layerKeys.Count} layer(s) for {bbox} into \"{connectionName}\"");
Console.WriteLine($"Cache: {cacheDirectory}");
Console.WriteLine();

var failures = 0;
var first = true;
foreach (var key in layerKeys)
{
    // Public Overpass servers throttle bursts; a short pause between layers avoids most rejections.
    if (!first && key != GisLayerKeys.Elevation)
        await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token);
    first = false;

    try
    {
        if (key == GisLayerKeys.Elevation)
            await importer.ImportElevationAsync(bbox, elevationSpacing, cancellation.Token);
        else
            await importer.ImportOsmLayerAsync(OsmLayerQueries.Find(key)!, bbox, cancellation.Token);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        failures++;
        Console.Error.WriteLine($"  FAILED   {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "Import complete." : $"Import finished with {failures} failed layer(s).");
return failures == 0 ? 0 : 1;

static string FindRepositoryRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            return dir.FullName;
    }
    return Directory.GetCurrentDirectory();
}
