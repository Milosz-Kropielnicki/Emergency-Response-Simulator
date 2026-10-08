using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Geo;

namespace Emergency_Response_Simulator.GisImport;

/// <summary>
/// Downloads raw source data and keeps it in a local cache, so re-importing (or importing into the test
/// database) does not hit public servers again. The cache folder is git-ignored.
/// </summary>
public sealed class DataSources(HttpClient http, string cacheDirectory, bool refresh, TextWriter log)
{
    public const string UserAgent = "EmergencyResponseSimulator/0.1 (training simulator GIS import)";

    /// <summary>Public Overpass instances, tried in order when one is overloaded or down.</summary>
    private static readonly string[] OverpassEndpoints =
    [
        "https://overpass-api.de/api/interpreter",
        "https://overpass.kumi.systems/api/interpreter",
        "https://overpass.private.coffee/api/interpreter",
    ];

    /// <summary>Rounds over all endpoints before giving up; delays grow 20 s, 40 s, … between rounds.</summary>
    private const int MaxOverpassRounds = 6;

    private const string ElevationEndpoint = "https://api.open-meteo.com/v1/elevation";
    private const int ElevationBatchSize = 100;

    public async Task<JsonDocument> GetOverpassAsync(string layerKey, string query, CancellationToken cancellationToken)
    {
        var cachePath = CachePath($"osm-{layerKey}", query);
        if (!refresh && File.Exists(cachePath))
        {
            log.WriteLine($"  cached   {Path.GetFileName(cachePath)}");
            return await ReadJsonAsync(cachePath, cancellationToken);
        }

        Exception? lastError = null;
        for (var attempt = 0; attempt < MaxOverpassRounds; attempt++)
        {
            foreach (var endpoint in OverpassEndpoints)
            {
                try
                {
                    log.WriteLine($"  download {new Uri(endpoint).Host} ...");
                    using var content = new FormUrlEncodedContent([new("data", query)]);
                    using var response = await http.PostAsync(endpoint, content, cancellationToken);

                    if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.GatewayTimeout
                        or HttpStatusCode.ServiceUnavailable or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway)
                    {
                        lastError = new HttpRequestException($"{endpoint} returned {(int)response.StatusCode}");
                        log.WriteLine($"           busy ({(int)response.StatusCode})");
                        continue;
                    }
                    response.EnsureSuccessStatusCode();

                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    // Overpass reports query errors as HTML/XML with status 200.
                    if (!body.TrimStart().StartsWith('{'))
                    {
                        lastError = new InvalidDataException($"{endpoint} returned a non-JSON response: {Truncate(body)}");
                        continue;
                    }

                    var document = JsonDocument.Parse(body);
                    if (document.RootElement.TryGetProperty("remark", out var remark))
                        throw new InvalidDataException($"Overpass remark: {remark.GetString()}");

                    await File.WriteAllTextAsync(cachePath, body, cancellationToken);
                    log.WriteLine($"           {body.Length / 1024.0 / 1024.0:F1} MB");
                    return document;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    lastError = ex;
                }
            }

            if (attempt == MaxOverpassRounds - 1) break;
            var delay = TimeSpan.FromSeconds(20 * (attempt + 1));
            log.WriteLine($"  all Overpass servers busy; retrying in {delay.TotalSeconds:F0}s ({lastError?.Message})");
            await Task.Delay(delay, cancellationToken);
        }

        throw new InvalidOperationException($"Could not download {layerKey} from Overpass.", lastError);
    }

    /// <summary>
    /// Ground elevation (Copernicus DEM, ~90 m) on a regular grid covering the box, via the Open-Meteo API.
    /// </summary>
    public async Task<IReadOnlyList<(GeoPoint Point, double ElevationMeters)>> GetElevationGridAsync(
        BoundingBox box, double spacingMeters, CancellationToken cancellationToken)
    {
        var grid = ElevationGrid(box, spacingMeters);
        var key = $"{box}|{spacingMeters}";
        var cachePath = CachePath("elevation", key);

        double[] elevations;
        if (!refresh && File.Exists(cachePath))
        {
            log.WriteLine($"  cached   {Path.GetFileName(cachePath)}");
            elevations = JsonSerializer.Deserialize<double[]>(await File.ReadAllTextAsync(cachePath, cancellationToken))!;
        }
        else
        {
            log.WriteLine($"  download {new Uri(ElevationEndpoint).Host} ({grid.Count} points)");
            var results = new List<double>(grid.Count);
            foreach (var batch in grid.Chunk(ElevationBatchSize))
            {
                var lat = string.Join(',', batch.Select(p => p.Latitude.ToString("F6", CultureInfo.InvariantCulture)));
                var lon = string.Join(',', batch.Select(p => p.Longitude.ToString("F6", CultureInfo.InvariantCulture)));
                using var doc = JsonDocument.Parse(
                    await http.GetStringAsync($"{ElevationEndpoint}?latitude={lat}&longitude={lon}", cancellationToken));
                results.AddRange(doc.RootElement.GetProperty("elevation").EnumerateArray().Select(e => e.GetDouble()));
            }
            elevations = [.. results];
            await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(elevations), cancellationToken);
        }

        if (elevations.Length != grid.Count)
            throw new InvalidDataException($"Expected {grid.Count} elevations, got {elevations.Length}.");

        return grid.Zip(elevations, (p, e) => (p, e)).ToList();
    }

    /// <summary>Points spaced roughly <paramref name="spacingMeters"/> apart across the box.</summary>
    public static List<GeoPoint> ElevationGrid(BoundingBox box, double spacingMeters)
    {
        var latStep = spacingMeters / 111_320.0;
        var lonStep = spacingMeters / (111_320.0 * Math.Cos(box.Center.Latitude * Math.PI / 180.0));

        var points = new List<GeoPoint>();
        for (var lat = box.MinLatitude; lat <= box.MaxLatitude + 1e-9; lat += latStep)
            for (var lon = box.MinLongitude; lon <= box.MaxLongitude + 1e-9; lon += lonStep)
                points.Add(new GeoPoint(Math.Round(lat, 6), Math.Round(lon, 6)));
        return points;
    }

    public static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(6) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    private string CachePath(string prefix, string key)
    {
        Directory.CreateDirectory(cacheDirectory);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();
        return Path.Combine(cacheDirectory, $"{prefix}-{hash}.json");
    }

    private static async Task<JsonDocument> ReadJsonAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "…";
}
