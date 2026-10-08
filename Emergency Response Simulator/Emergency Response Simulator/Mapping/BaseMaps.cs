using BruTile;
using BruTile.Predefined;
using BruTile.Web;
using Mapsui.Tiling;
using Mapsui.Tiling.Layers;

namespace Emergency_Response_Simulator.Mapping;

/// <summary>
/// Background tiles for each map view in the top panel. Operational meaning (wind, closures, terrain)
/// is drawn on top from simulator data; the tiles only provide context.
/// </summary>
public static class BaseMaps
{
    public const string Street = "street";
    public const string Satellite = "satellite";
    public const string Weather = "weather";
    public const string Traffic = "traffic";
    public const string Terrain = "terrain";

    // Tile servers' usage policies require an identifying user agent.
    private const string UserAgent = "EmergencyResponseSimulator/0.1 (training simulator)";

    public static TileLayer Create(string view) => view switch
    {
        Satellite => Layer("Satellite",
            "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}",
            new Attribution("Imagery © Esri, Maxar, Earthstar Geographics", "https://www.esri.com")),

        Terrain => Layer("Terrain",
            "https://{s}.tile.opentopomap.org/{z}/{x}/{y}.png",
            new Attribution("© OpenStreetMap contributors, SRTM | © OpenTopoMap (CC-BY-SA)", "https://opentopomap.org"),
            ["a", "b", "c"], maxZoom: 17),

        // Weather and traffic overlay simulator data on a muted dark background so it stands out.
        Weather or Traffic => Layer(view == Weather ? "Weather" : "Traffic",
            "https://server.arcgisonline.com/ArcGIS/rest/services/Canvas/World_Dark_Gray_Base/MapServer/tile/{z}/{y}/{x}",
            new Attribution("© Esri, HERE, Garmin, © OpenStreetMap contributors", "https://www.esri.com"),
            maxZoom: 16),

        _ => OpenStreetMap.CreateTileLayer(UserAgent),
    };

    private static TileLayer Layer(string name, string url, Attribution attribution, IEnumerable<string>? servers = null, int maxZoom = 19)
    {
        var schema = new GlobalSphericalMercator(YAxis.OSM, minZoomLevel: 0, maxZoomLevel: maxZoom);
        var source = new HttpTileSource(schema, new BasicUrlBuilder(url, servers), name, attribution: attribution,
            configureHttpRequestMessage: request => request.Headers.UserAgent.ParseAdd(UserAgent));
        return new TileLayer(source) { Name = name };
    }
}
