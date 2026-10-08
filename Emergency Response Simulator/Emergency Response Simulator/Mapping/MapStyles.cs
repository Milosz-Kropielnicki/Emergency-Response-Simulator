using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.ViewModels;
using Mapsui;
using Mapsui.Styles;
using Mapsui.Styles.Thematics;

namespace Emergency_Response_Simulator.Mapping;

/// <summary>Symbology for every map layer, kept in one place so the map reads as one system.</summary>
public static class MapStyles
{
    public const string LabelField = "label";

    /// <summary>Map resolution (metres per pixel) at a web zoom level, for MinVisible/MaxVisible.</summary>
    public static double ResolutionAtZoom(int zoom) => 156543.03392804097 / Math.Pow(2, zoom);

    // ---- Static GIS ----

    public static IStyle ForStaticLayer(string layerKey, bool trafficView) => layerKey switch
    {
        GisLayerKeys.Roads => trafficView ? TrafficRoads : Roads,
        GisLayerKeys.Buildings => new VectorStyle
        {
            Fill = new Brush(Color.FromArgb(70, 120, 128, 140)),
            Outline = new Pen(Color.FromArgb(140, 90, 98, 110), 0.6),
        },
        GisLayerKeys.Water => new VectorStyle
        {
            Fill = new Brush(Color.FromArgb(110, 59, 140, 220)),
            Outline = new Pen(Color.FromArgb(180, 59, 140, 220), 1),
            Line = new Pen(Color.FromArgb(200, 59, 140, 220), 2.5),
        },
        GisLayerKeys.Railways => new VectorStyle
        {
            Line = new Pen(Color.FromArgb(220, 70, 70, 80), 2) { PenStyle = PenStyle.Dash },
            Fill = null,
            Outline = null,
        },
        GisLayerKeys.Elevation => Elevation,
        GisLayerKeys.AreaBoundaries => new StyleCollection
        {
            Styles =
            {
                new VectorStyle { Fill = null, Outline = new Pen(Color.FromArgb(200, 150, 90, 220), 1.5) { PenStyle = PenStyle.Dash } },
                Label(Color.FromArgb(255, 110, 60, 180), minZoom: 13),
            },
        },
        GisLayerKeys.Hospitals => Facility(SymbolType.Rectangle, Color.White, new Color(220, 40, 40), 0.45),
        GisLayerKeys.FireStations => Facility(SymbolType.Triangle, new Color(230, 60, 40), Color.White, 0.45),
        GisLayerKeys.PoliceStations => Facility(SymbolType.Rectangle, new Color(40, 90, 200), Color.White, 0.4),
        GisLayerKeys.AmbulanceStations => Facility(SymbolType.Rectangle, new Color(40, 170, 90), Color.White, 0.4),
        GisLayerKeys.Shelters => Facility(SymbolType.Ellipse, new Color(150, 90, 220), Color.White, 0.35),
        GisLayerKeys.Hydrants => new SymbolStyle
        {
            SymbolType = SymbolType.Ellipse, SymbolScale = 0.22,
            Fill = new Brush(new Color(230, 40, 40)), Outline = new Pen(Color.White, 1),
        },
        GisLayerKeys.Schools => Facility(SymbolType.Ellipse, new Color(240, 190, 30), new Color(60, 50, 10), 0.32),
        GisLayerKeys.CriticalInfrastructure => Facility(SymbolType.Rectangle, new Color(255, 140, 0), new Color(60, 30, 0), 0.32),
        _ => new VectorStyle(),
    };

    /// <summary>Faint fill for facilities mapped as areas; the symbol sits at the interior point.</summary>
    public static IStyle FacilityArea(string layerKey) => new VectorStyle
    {
        Fill = new Brush(layerKey == GisLayerKeys.Hospitals ? Color.FromArgb(50, 220, 40, 40) : Color.FromArgb(40, 255, 140, 0)),
        Outline = new Pen(Color.FromArgb(120, 120, 120, 120), 0.8),
    };

    private static readonly IStyle Roads = RoadTheme(
        major: RoadPen(230, 120, 30, 4), primary: RoadPen(230, 160, 40, 3),
        secondary: RoadPen(200, 180, 90, 2), tertiary: RoadPen(200, 180, 90, 2), minor: RoadPen(90, 150, 150, 1));

    /// <summary>Traffic view: arterial network emphasised so closures and (from Phase 6) congestion read clearly.</summary>
    private static readonly IStyle TrafficRoads = RoadTheme(
        major: TrafficPen(new Color(80, 200, 120), 5), primary: TrafficPen(new Color(80, 200, 120), 4),
        secondary: TrafficPen(new Color(120, 190, 140), 3), tertiary: TrafficPen(new Color(120, 160, 140), 2),
        minor: TrafficPen(new Color(90, 110, 120), 1));

    // Styles are created once and shared: the theme runs for every road on every frame.
    private static ThemeStyle RoadTheme(IStyle major, IStyle primary, IStyle secondary, IStyle tertiary, IStyle minor) =>
        new(f => (f["highway"] as string) switch
        {
            "motorway" or "trunk" or "motorway_link" or "trunk_link" => major,
            "primary" or "primary_link" => primary,
            "secondary" or "secondary_link" => secondary,
            "tertiary" or "tertiary_link" => tertiary,
            _ => minor,
        });

    // Fill and Outline are set explicitly: a few roads are pedestrian areas (polygons), and Mapsui
    // would otherwise paint them with its default white fill.
    private static VectorStyle RoadPen(int alpha, int r, int g, double width) => new()
    {
        Line = new Pen(Color.FromArgb(alpha, r, g, 60), width),
        Fill = new Brush(Color.FromArgb(35, r, g, 60)),
        Outline = new Pen(Color.FromArgb(alpha / 2, r, g, 60), 1),
    };

    private static VectorStyle TrafficPen(Color color, double width) => new()
    {
        Line = new Pen(color, width),
        Fill = new Brush(Color.FromArgb(35, color.R, color.G, color.B)),
        Outline = new Pen(Color.FromArgb(120, color.R, color.G, color.B), 1),
    };

    /// <summary>Elevation samples shaded low (green) to high (brown), with the height labelled when zoomed in.</summary>
    private static readonly IStyle Elevation = new ThemeStyle(f =>
    {
        var meters = f["elevation_m"] is string s && double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        var t = Math.Clamp(meters / 80.0, 0, 1);
        var color = new Color((int)(60 + 140 * t), (int)(170 - 60 * t), (int)(80 - 30 * t), 200);
        return new StyleCollection
        {
            Styles =
            {
                new SymbolStyle { SymbolType = SymbolType.Ellipse, SymbolScale = 0.3, Fill = new Brush(color), Outline = null },
                new LabelStyle
                {
                    Text = $"{meters:F0} m", ForeColor = Color.White, BackColor = null,
                    Font = new Font { Size = 9 }, Offset = new Offset(0, -10),
                    Halo = new Pen(Color.FromArgb(200, 0, 0, 0), 2), MaxVisible = ResolutionAtZoom(15),
                },
            },
        };
    });

    private static StyleCollection Facility(SymbolType symbol, Color fill, Color outline, double scale) => new()
    {
        Styles =
        {
            new SymbolStyle { SymbolType = symbol, SymbolScale = scale, Fill = new Brush(fill), Outline = new Pen(outline, 2) },
            Label(new Color(30, 30, 30), minZoom: 15),
        },
    };

    private static LabelStyle Label(Color color, int minZoom) => new()
    {
        LabelColumn = LabelField,
        ForeColor = color,
        BackColor = null,
        Halo = new Pen(Color.FromArgb(220, 255, 255, 255), 2.5),
        Font = new Font { Size = 11 },
        Offset = new Offset(0, 16),
        CollisionDetection = true,
        MaxVisible = ResolutionAtZoom(minZoom),
    };

    // ---- Operational (COP) ----

    public static IStyle Unit(AgencyType? agency, UnitStatus status)
    {
        var color = agency switch
        {
            AgencyType.Fire => new Color(230, 50, 40),
            AgencyType.Ems => new Color(30, 170, 90),
            AgencyType.Police => new Color(40, 100, 220),
            _ => new Color(120, 120, 130),
        };
        if (status == UnitStatus.OutOfService)
            color = new Color(110, 110, 110);

        // Committed units get an amber ring so availability can be read at a glance.
        var committed = status is not (UnitStatus.Available or UnitStatus.OutOfService);
        return new StyleCollection
        {
            Styles =
            {
                new SymbolStyle
                {
                    SymbolType = SymbolType.Ellipse, SymbolScale = 0.5, Fill = new Brush(color),
                    Outline = new Pen(committed ? new Color(255, 190, 0) : Color.White, committed ? 3.5 : 2),
                },
                new LabelStyle
                {
                    LabelColumn = LabelField, ForeColor = Color.White, BackColor = new Brush(Color.FromArgb(190, 15, 20, 25)),
                    Font = new Font { Size = 10 }, Offset = new Offset(0, -18), CornerRounding = 3,
                    CollisionDetection = false, MaxVisible = ResolutionAtZoom(13),
                },
            },
        };
    }

    public static IStyle Incident(IncidentPriority priority) => new StyleCollection
    {
        Styles =
        {
            new SymbolStyle
            {
                SymbolType = SymbolType.Triangle, SymbolScale = 0.85,
                Fill = new Brush(priority switch
                {
                    IncidentPriority.Critical => new Color(255, 40, 40),
                    IncidentPriority.High => new Color(255, 120, 0),
                    IncidentPriority.Medium => new Color(255, 200, 0),
                    _ => new Color(200, 200, 200),
                }),
                Outline = new Pen(Color.Black, 2),
            },
            new LabelStyle
            {
                LabelColumn = LabelField, ForeColor = Color.White, BackColor = new Brush(Color.FromArgb(220, 160, 20, 20)),
                Font = new Font { Size = 11, Bold = true }, Offset = new Offset(0, 22), CornerRounding = 3,
            },
        },
    };

    public static IStyle Zone(ZoneType type)
    {
        var (color, dashed) = type switch
        {
            ZoneType.HotZone => (new Color(255, 40, 40), false),
            ZoneType.WarmZone => (new Color(255, 150, 0), false),
            ZoneType.ColdZone => (new Color(60, 200, 90), false),
            ZoneType.PlumeHigh => (new Color(200, 0, 120), false),
            ZoneType.PlumeModerate => (new Color(230, 90, 160), false),
            ZoneType.PlumeLow => (new Color(240, 160, 200), false),
            ZoneType.EvacuationZone => (new Color(170, 80, 230), true),
            ZoneType.ShelterInPlace => (new Color(60, 140, 240), true),
            ZoneType.SearchArea => (new Color(250, 220, 40), true),
            ZoneType.PoliceCordon => (new Color(40, 100, 240), true),
            ZoneType.FireExclusion => (new Color(255, 70, 40), true),
            ZoneType.TrafficControl => (new Color(255, 180, 0), true),
            ZoneType.StagingArea => (new Color(0, 190, 190), false),
            ZoneType.CommandPost => (new Color(30, 60, 160), false),
            ZoneType.LandingZone => (new Color(0, 220, 255), false),
            ZoneType.IncidentPerimeter => (new Color(255, 40, 40), true),
            _ => (new Color(200, 200, 200), false),
        };

        if (type == ZoneType.RoadClosure)
        {
            return new StyleCollection
            {
                Styles =
                {
                    new VectorStyle { Line = new Pen(Color.FromArgb(255, 20, 20, 20), 9) },
                    new VectorStyle { Line = new Pen(new Color(255, 40, 40), 6) { PenStyle = PenStyle.Dash } },
                    ZoneLabel(new Color(255, 40, 40)),
                },
            };
        }

        var fillAlpha = type == ZoneType.IncidentPerimeter ? 0 : 55;
        return new StyleCollection
        {
            Styles =
            {
                new VectorStyle
                {
                    Fill = new Brush(Color.FromArgb(fillAlpha, color.R, color.G, color.B)),
                    Outline = new Pen(color, 2.5) { PenStyle = dashed ? PenStyle.Dash : PenStyle.Solid },
                },
                ZoneLabel(color),
            },
        };
    }

    private static LabelStyle ZoneLabel(Color color) => new()
    {
        LabelColumn = LabelField, ForeColor = Color.White, BackColor = new Brush(Color.FromArgb(200, color.R / 2, color.G / 2, color.B / 2)),
        Font = new Font { Size = 10 }, CornerRounding = 3,
    };

    /// <summary>In-progress zone being drawn.</summary>
    public static IStyle DrawingPreview { get; } = new StyleCollection
    {
        Styles =
        {
            new VectorStyle
            {
                Fill = new Brush(Color.FromArgb(50, 59, 167, 255)),
                Outline = new Pen(new Color(59, 167, 255), 2) { PenStyle = PenStyle.Dash },
                Line = new Pen(new Color(59, 167, 255), 3) { PenStyle = PenStyle.Dash },
            },
            new SymbolStyle { SymbolType = SymbolType.Ellipse, SymbolScale = 0.25, Fill = new Brush(Color.White), Outline = new Pen(new Color(59, 167, 255), 2) },
        },
    };

    /// <summary>Wind arrow pointing downwind (where a plume would travel).</summary>
    public static IStyle WindArrow(double windFromDegrees, double speedMps) => new SymbolStyle
    {
        SymbolType = SymbolType.Triangle,
        SymbolScale = 0.35 + Math.Min(speedMps, 20) / 40.0,
        SymbolRotation = (windFromDegrees + 180) % 360,
        RotateWithMap = true,
        Fill = new Brush(Color.FromArgb(200, 120, 200, 255)),
        Outline = new Pen(Color.FromArgb(230, 10, 30, 50), 1.5),
    };

    /// <summary>Which zones toggle each zone layer controls.</summary>
    public static string ZoneToggleKey(ZoneType type) => type switch
    {
        ZoneType.HotZone or ZoneType.WarmZone or ZoneType.ColdZone or ZoneType.FireExclusion
            or ZoneType.PlumeHigh or ZoneType.PlumeModerate or ZoneType.PlumeLow => MapLayerKeys.HazardZones,
        ZoneType.EvacuationZone or ZoneType.ShelterInPlace => MapLayerKeys.EvacuationZones,
        ZoneType.StagingArea or ZoneType.CommandPost or ZoneType.LandingZone => MapLayerKeys.CommandZones,
        ZoneType.RoadClosure or ZoneType.TrafficControl => MapLayerKeys.TrafficZones,
        ZoneType.SearchArea or ZoneType.PoliceCordon => MapLayerKeys.SearchZones,
        _ => MapLayerKeys.PerimeterZones,
    };

    public static string UnitToggleKey(AgencyType? agency) => agency switch
    {
        AgencyType.Fire => MapLayerKeys.FireUnits,
        AgencyType.Ems => MapLayerKeys.EmsUnits,
        AgencyType.Police => MapLayerKeys.PoliceUnits,
        _ => MapLayerKeys.OtherUnits,
    };
}
