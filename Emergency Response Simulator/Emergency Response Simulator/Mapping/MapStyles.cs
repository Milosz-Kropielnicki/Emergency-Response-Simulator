using Emergency_Response_Simulator.Core.Geo;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.Hazards;
using Emergency_Response_Simulator.ViewModels;
using Mapsui;
using Mapsui.Styles;
using Mapsui.Styles.Thematics;

namespace Emergency_Response_Simulator.Mapping;

/// <summary>Symbology for every map layer, kept in one place so the map reads as one system.</summary>
public static class MapStyles
{
    public const string LabelField = "label";

    /// <summary>
    /// The meaningful colours (Phase 21): agencies, zones, triage, plume, congestion and unit rings, for the user's colour
    /// vision. Styles read it as they are built, so the map follows a change at its next refresh.
    /// </summary>
    public static Accessibility.MapPalette Palette { get; set; } = Accessibility.Palettes.Map(Accessibility.ColourVision.Standard);

    private static Color M(Accessibility.Rgb c, int alpha = 255) => Color.FromArgb(alpha, c.R, c.G, c.B);

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

    public static Color AgencyColor(AgencyType? agency) => agency switch
    {
        AgencyType.Fire => M(Palette.Fire),
        AgencyType.Ems => M(Palette.Ems),
        AgencyType.Police => M(Palette.Police),
        _ => M(Palette.OtherAgency),
    };

    /// <param name="heading">Degrees clockwise from north when moving; draws a direction arrow.</param>
    public static IStyle Unit(AgencyType? agency, UnitStatus status, double? heading = null, bool selected = false, bool commsOk = true)
    {
        var color = AgencyColor(agency);
        if (status == UnitStatus.OutOfService)
            color = new Color(110, 110, 110);

        // Committed units get an amber ring so availability can be read at a glance; lost comms show red.
        var committed = status is not (UnitStatus.Available or UnitStatus.OutOfService);
        var ring = !commsOk ? M(Palette.CommsLost) : committed ? M(Palette.Committed) : Color.White;
        var styles = new StyleCollection();
        if (selected)
        {
            styles.Styles.Add(new SymbolStyle
            {
                SymbolType = SymbolType.Ellipse, SymbolScale = 0.95,
                Fill = new Brush(Color.FromArgb(60, 59, 167, 255)), Outline = new Pen(new Color(59, 167, 255), 2.5),
            });
        }
        if (heading is { } direction)
        {
            // Direction arrow, offset ahead of the vehicle along its heading.
            var radians = direction * Math.PI / 180;
            styles.Styles.Add(new SymbolStyle
            {
                SymbolType = SymbolType.Triangle, SymbolScale = 0.32, SymbolRotation = direction, RotateWithMap = true,
                Offset = new Offset(Math.Sin(radians) * 15, -Math.Cos(radians) * 15),
                Fill = new Brush(color), Outline = new Pen(Color.White, 1.5),
            });
        }
        return new StyleCollection
        {
            Styles =
            {
                styles,
                new SymbolStyle
                {
                    SymbolType = SymbolType.Ellipse, SymbolScale = 0.5, Fill = new Brush(color),
                    Outline = new Pen(ring, committed || !commsOk ? 3.5 : 2),
                },
                new LabelStyle
                {
                    LabelColumn = LabelField, ForeColor = Color.White, BackColor = new Brush(Color.FromArgb(190, 15, 20, 25)),
                    Font = new Font { Size = 10 }, Offset = new Offset(0, -18), CornerRounding = 3,
                    // Units converge on a scene; overlapping labels become unreadable, so drop the clashing ones.
                    CollisionDetection = true, MaxVisible = ResolutionAtZoom(13),
                },
            },
        };
    }

    public static IStyle Route(AgencyType? agency, bool selected)
    {
        var color = AgencyColor(agency);
        return new VectorStyle
        {
            Line = new Pen(Color.FromArgb(selected ? 230 : 150, color.R, color.G, color.B), selected ? 5 : 3) { PenStyle = PenStyle.Dash },
            Fill = null,
            Outline = null,
        };
    }

    /// <summary>Breadcrumb trail of recent AVL fixes.</summary>
    public static IStyle Trail { get; } = new VectorStyle
    {
        Line = new Pen(Color.FromArgb(150, 255, 255, 255), 2) { PenStyle = PenStyle.Dot },
        Fill = null,
        Outline = null,
    };

    public static IStyle Report(Confidence confidence) => new SymbolStyle
    {
        SymbolType = SymbolType.Ellipse,
        SymbolScale = 0.32,
        Fill = new Brush(ConfidenceColor(confidence)),
        Outline = new Pen(Color.Black, 1.5),
    };

    /// <summary>Faint circle for a report's stated location accuracy.</summary>
    public static IStyle ReportAccuracy(Confidence confidence)
    {
        var color = ConfidenceColor(confidence);
        return new VectorStyle
        {
            Fill = new Brush(Color.FromArgb(35, color.R, color.G, color.B)),
            Outline = new Pen(Color.FromArgb(160, color.R, color.G, color.B), 1) { PenStyle = PenStyle.Dot },
        };
    }

    private static Color ConfidenceColor(Confidence confidence) => confidence switch
    {
        Confidence.High => new Color(80, 220, 120),
        Confidence.Medium => new Color(255, 200, 60),
        _ => new Color(255, 120, 200),
    };

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
            ZoneType.HotZone => (M(Palette.Hot), false),
            ZoneType.WarmZone => (M(Palette.Warm), false),
            ZoneType.ColdZone => (M(Palette.Cold), false),
            ZoneType.PlumeHigh => (M(Palette.PlumeHigh), false),
            ZoneType.PlumeModerate => (M(Palette.PlumeModerate), false),
            ZoneType.PlumeLow => (new Color(240, 160, 200), false),
            ZoneType.EvacuationZone => (new Color(170, 80, 230), true),
            ZoneType.ShelterInPlace => (new Color(60, 140, 240), true),
            ZoneType.SearchArea => (new Color(250, 220, 40), true),
            ZoneType.PoliceCordon => (new Color(40, 100, 240), true),
            ZoneType.FireExclusion => (new Color(255, 70, 40), true),
            ZoneType.TrafficControl => (new Color(255, 180, 0), true),
            ZoneType.StagingArea => (new Color(0, 190, 190), false),
            ZoneType.BaseCamp => (new Color(0, 150, 110), false),
            ZoneType.CommandPost => (new Color(30, 60, 160), false),
            ZoneType.LandingZone => (new Color(0, 220, 255), false),
            ZoneType.IncidentPerimeter => (M(Palette.Hot), true),
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

    /// <summary>
    /// A jurisdiction boundary (Phase 10): dashed outline by tier; estates whose authority must agree measures inside are
    /// lightly filled so a zone reaching into one stands out.
    /// </summary>
    public static IStyle Jurisdiction(JurisdictionLevel level, bool requiresConsent)
    {
        var color = requiresConsent ? new Color(210, 60, 200) : level switch
        {
            JurisdictionLevel.City => new Color(240, 140, 30),
            JurisdictionLevel.County => new Color(30, 170, 170),
            JurisdictionLevel.Regional => new Color(110, 120, 170),
            _ => new Color(150, 150, 150),
        };
        var styles = new StyleCollection
        {
            Styles =
            {
                new VectorStyle
                {
                    Fill = new Brush(Color.FromArgb(requiresConsent ? 40 : 0, color.R, color.G, color.B)),
                    Outline = new Pen(color, requiresConsent ? 2.5 : 2) { PenStyle = PenStyle.DashDot },
                },
            },
        };
        if (requiresConsent || level is JurisdictionLevel.City or JurisdictionLevel.County)
            styles.Styles.Add(ZoneLabel(color));
        return styles;
    }

    private static LabelStyle ZoneLabel(Color color) => new()
    {
        LabelColumn = LabelField, ForeColor = Color.White, BackColor = new Brush(Color.FromArgb(200, color.R / 2, color.G / 2, color.B / 2)),
        Font = new Font { Size = 10 }, CornerRounding = 3,
    };

    /// <summary>
    /// A projected plume zone (Phase 13): amber to red by concentration, dashed because it is a prediction, not a
    /// declared zone. A superseded projection is not drawn at all.
    /// </summary>
    public static IStyle PlumeProjection(string level)
    {
        var (color, alpha) = level switch
        {
            Core.Contracts.PlumeLevels.High => (M(Palette.PlumeHigh), 110),
            Core.Contracts.PlumeLevels.Moderate => (M(Palette.Warm), 80),
            _ => (new Color(255, 205, 0), 50),
        };
        return new StyleCollection
        {
            Styles =
            {
                new VectorStyle
                {
                    Fill = new Brush(Color.FromArgb(alpha, color.R, color.G, color.B)),
                    Outline = new Pen(color, level == Core.Contracts.PlumeLevels.High ? 2.5 : 1.5) { PenStyle = PenStyle.Dash },
                },
                ZoneLabel(color),
            },
        };
    }

    /// <summary>A sensitive place inside a projected plume: a school, hospital or shelter, ringed in the zone's colour.</summary>
    public static IStyle PlumeRelease { get; } = new SymbolStyle
    {
        SymbolType = SymbolType.Triangle,
        SymbolScale = 0.5,
        Fill = new Brush(new Color(220, 20, 40)),
        Outline = new Pen(Color.White, 2),
    };

    // ---- Phase 17: population protection and critical systems ----

    private static readonly Color EvacuationColor = new(170, 80, 230);

    private static LabelStyle SmallLabel(Color color, int dy = -18) => new()
    {
        LabelColumn = LabelField, ForeColor = Color.White, BackColor = new Brush(Color.FromArgb(210, color.R / 2, color.G / 2, color.B / 2)),
        Font = new Font { Size = 9 }, Offset = new Offset(0, dy), CornerRounding = 3, MaxVisible = ResolutionAtZoom(13),
    };

    /// <summary>A rest centre: green when open and fine, amber while it sets up, red when it has run out of something, is over capacity or has had trouble.</summary>
    public static IStyle Shelter(bool ready, bool alarm)
    {
        var color = alarm ? new Color(230, 30, 30) : ready ? new Color(40, 170, 90) : new Color(255, 170, 0);
        return new StyleCollection
        {
            Styles =
            {
                new SymbolStyle { SymbolType = SymbolType.Rectangle, SymbolScale = 0.6, Fill = new Brush(color), Outline = new Pen(Color.White, 3) },
                SmallLabel(color, -20),
            },
        };
    }

    /// <summary>An evacuation route: a broad dashed purple line, the colour of evacuation zones.</summary>
    public static IStyle EvacuationRoute { get; } = new StyleCollection
    {
        Styles =
        {
            new VectorStyle { Line = new Pen(Color.FromArgb(230, EvacuationColor.R, EvacuationColor.G, EvacuationColor.B), 5) { PenStyle = PenStyle.Dash } },
            new LabelStyle
            {
                LabelColumn = LabelField, ForeColor = Color.White, BackColor = new Brush(Color.FromArgb(200, 85, 40, 115)),
                Font = new Font { Size = 9 }, CornerRounding = 3, MaxVisible = ResolutionAtZoom(13),
            },
        },
    };

    /// <summary>A traffic control point: a blue triangle when gardaí are there, grey while they are on their way.</summary>
    public static IStyle ControlPoint(bool staffed)
    {
        var color = staffed ? new Color(30, 90, 220) : new Color(130, 130, 140);
        return new StyleCollection
        {
            Styles =
            {
                new SymbolStyle { SymbolType = SymbolType.Triangle, SymbolScale = 0.5, Fill = new Brush(color), Outline = new Pen(Color.White, 2) },
                SmallLabel(color),
            },
        };
    }

    public static Color InfrastructureColor(InfrastructureStatus status) => status switch
    {
        InfrastructureStatus.Failed or InfrastructureStatus.Leaking => new Color(230, 30, 30),
        InfrastructureStatus.Normal => new Color(40, 170, 90),
        _ => new Color(255, 170, 0),
    };

    /// <summary>An infrastructure asset, coloured by its operator's last report.</summary>
    public static IStyle InfrastructureAsset(InfrastructureStatus status)
    {
        var color = InfrastructureColor(status);
        return new StyleCollection
        {
            Styles =
            {
                new SymbolStyle { SymbolType = SymbolType.Ellipse, SymbolScale = 0.45, Fill = new Brush(Color.White), Outline = new Pen(color, 4) },
                SmallLabel(color),
            },
        };
    }

    /// <summary>The area a degraded asset serves: who is without water, gas, mobile service or trains.</summary>
    public static IStyle ServiceArea(InfrastructureStatus status)
    {
        var color = InfrastructureColor(status);
        return new VectorStyle
        {
            Fill = new Brush(Color.FromArgb(25, color.R, color.G, color.B)),
            Outline = new Pen(Color.FromArgb(170, color.R, color.G, color.B), 2) { PenStyle = PenStyle.Dash },
        };
    }

    /// <summary>GPS interference: orange where it is jammed, magenta where it is spoofed (worse: nothing looks wrong).</summary>
    public static IStyle GnssArea(GnssInterferenceKind kind)
    {
        var color = kind == GnssInterferenceKind.Jamming ? new Color(255, 140, 0) : new Color(220, 40, 200);
        return new StyleCollection
        {
            Styles =
            {
                new VectorStyle
                {
                    Fill = new Brush(Color.FromArgb(30, color.R, color.G, color.B)),
                    Outline = new Pen(Color.FromArgb(200, color.R, color.G, color.B), 2) { PenStyle = PenStyle.DashDot },
                },
                new LabelStyle
                {
                    LabelColumn = LabelField, ForeColor = Color.White, BackColor = new Brush(Color.FromArgb(200, color.R / 2, color.G / 2, color.B / 2)),
                    Font = new Font { Size = 10 }, CornerRounding = 3,
                },
            },
        };
    }

    /// <summary>A monitoring team, coloured by its last reading (grey before the first).</summary>
    public static IStyle Monitor(PollutionLevel? level)
    {
        var color = level switch
        {
            PollutionLevel.High => new Color(230, 30, 30),
            PollutionLevel.Moderate => new Color(255, 140, 0),
            PollutionLevel.Low => new Color(220, 200, 0),
            PollutionLevel.Clear => new Color(40, 170, 90),
            _ => new Color(130, 130, 140),
        };
        return new StyleCollection
        {
            Styles =
            {
                new SymbolStyle
                {
                    SymbolType = SymbolType.Rectangle, SymbolScale = 0.4, SymbolRotation = 45, Fill = new Brush(color), Outline = new Pen(Color.White, 2),
                },
                SmallLabel(color),
            },
        };
    }

    /// <summary>Harm to the environment someone has reported: a fish kill, dead birds.</summary>
    public static IStyle EnvironmentalImpact { get; } = new StyleCollection
    {
        Styles =
        {
            new SymbolStyle
            {
                SymbolType = SymbolType.Triangle, SymbolScale = 0.55, SymbolRotation = 180,
                Fill = new Brush(new Color(120, 60, 20)), Outline = new Pen(new Color(255, 200, 0), 3),
            },
            SmallLabel(new Color(120, 60, 20)),
        },
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

    /// <summary>A receiving hospital with its last reported load: red when diverting, amber when nearly full.</summary>
    public static IStyle HospitalStatus(bool onDiversion, bool nearFull)
    {
        var color = onDiversion ? new Color(230, 30, 30) : nearFull ? new Color(255, 170, 0) : new Color(40, 180, 90);
        return new StyleCollection
        {
            Styles =
            {
                new SymbolStyle
                {
                    SymbolType = SymbolType.Rectangle, SymbolScale = 0.55, Fill = new Brush(Color.White), Outline = new Pen(color, 4),
                },
                new LabelStyle
                {
                    LabelColumn = LabelField, ForeColor = Color.White, BackColor = new Brush(Color.FromArgb(210, color.R / 2, color.G / 2, color.B / 2)),
                    Font = new Font { Size = 10 }, Offset = new Offset(0, -20), CornerRounding = 3, MaxVisible = ResolutionAtZoom(12),
                },
            },
        };
    }

    /// <summary>
    /// Congestion on a stretch of road: amber when slow, red when heavy, dark red when gridlocked. Used for the
    /// trainee's traffic feed and the instructor's live traffic alike.
    /// </summary>
    public static IStyle Congestion(double factor) => factor switch
    {
        < 0.2 => Gridlock,
        < 0.5 => Heavy,
        _ => Slow,
    };

    private static IStyle Gridlock => CongestionPen(M(Palette.Gridlock), 7);
    private static IStyle Heavy => CongestionPen(M(Palette.Heavy), 6);
    private static IStyle Slow => CongestionPen(M(Palette.Slow), 5);

    private static VectorStyle CongestionPen(Color color, double width) => new()
    {
        Line = new Pen(Color.FromArgb(230, color.R, color.G, color.B), width), Fill = null, Outline = null,
    };

    // ---- Ground truth (instructor only, drawn in purple so it is never mistaken for the COP) ----

    public static readonly Color TruthColor = new(199, 146, 234);

    public static IStyle TruthHazard(HazardKind kind, HazardLevel level = HazardLevel.Low)
    {
        var (fill, alpha) = kind switch
        {
            HazardKind.Fire => (new Color(255, 80, 20), 110),
            HazardKind.Flood => (new Color(40, 110, 230), 110),
            _ => (new Color(200, 40, 160), level switch { HazardLevel.High => 120, HazardLevel.Moderate => 80, _ => 45 }),
        };
        return new StyleCollection
        {
            Styles =
            {
                new VectorStyle
                {
                    Fill = new Brush(Color.FromArgb(alpha, fill.R, fill.G, fill.B)),
                    Outline = new Pen(TruthColor, 2) { PenStyle = PenStyle.Dash },
                },
                TruthLabel,
            },
        };
    }

    public static IStyle Outage { get; } = new StyleCollection
    {
        Styles =
        {
            new VectorStyle
            {
                Fill = new Brush(Color.FromArgb(30, 20, 20, 20)),
                Outline = new Pen(new Color(255, 220, 0), 2.5) { PenStyle = PenStyle.LongDash },
            },
            TruthLabelStyle(),
        },
    };

    public static IStyle Obstruction { get; } = new StyleCollection
    {
        Styles =
        {
            new VectorStyle
            {
                Line = new Pen(new Color(255, 220, 0), 6) { PenStyle = PenStyle.Dash },
                Fill = new Brush(Color.FromArgb(90, 255, 220, 0)),
                Outline = new Pen(new Color(30, 30, 30), 1.5),
            },
        },
    };

    public static IStyle BlackSpot { get; } = new StyleCollection
    {
        Styles =
        {
            new VectorStyle
            {
                Fill = new Brush(Color.FromArgb(60, 60, 60, 60)),
                Outline = new Pen(TruthColor, 2) { PenStyle = PenStyle.Dot },
            },
            TruthLabelStyle(),
        },
    };

    /// <summary>A firefighter in distress (truth): a red diamond.</summary>
    public static IStyle Distress { get; } = new StyleCollection
    {
        Styles =
        {
            new SymbolStyle
            {
                SymbolType = SymbolType.Rectangle, SymbolScale = 0.5, SymbolRotation = 45,
                Fill = new Brush(new Color(255, 30, 30)), Outline = new Pen(TruthColor, 3),
            },
            TruthLabelStyle(),
        },
    };

    public static IStyle Site(bool triggered) => new StyleCollection
    {
        Styles =
        {
            new SymbolStyle
            {
                SymbolType = SymbolType.Rectangle, SymbolScale = 0.45,
                Fill = new Brush(triggered ? new Color(255, 40, 40) : new Color(255, 220, 0)), Outline = new Pen(TruthColor, 3),
            },
            TruthLabelStyle(),
        },
    };

    public static IStyle Civilian(Simulation.State.CivilianState state) => CivilianStyles[(int)state];

    private static readonly IStyle[] CivilianStyles = Enum.GetValues<Simulation.State.CivilianState>().Select(state => (IStyle)new SymbolStyle
    {
        SymbolType = SymbolType.Ellipse, SymbolScale = 0.13, Outline = null,
        Fill = new Brush(state switch
        {
            Simulation.State.CivilianState.Normal => new Color(170, 170, 170),
            Simulation.State.CivilianState.Aware => new Color(255, 230, 60),
            Simulation.State.CivilianState.Evacuating => new Color(60, 220, 110),
            Simulation.State.CivilianState.Converging => new Color(255, 140, 0),
            Simulation.State.CivilianState.Sheltering => new Color(80, 150, 255),
            Simulation.State.CivilianState.Injured => new Color(255, 30, 30),
            _ => new Color(60, 120, 80),
        }),
    }).ToArray();

    public static IStyle Casualty(Triage triage) => new SymbolStyle
    {
        SymbolType = SymbolType.Triangle, SymbolScale = 0.28, Outline = new Pen(Color.White, 1),
        Fill = new Brush(triage switch
        {
            Triage.Immediate => M(Palette.Immediate),
            Triage.Urgent => M(Palette.Urgent),
            Triage.Delayed => M(Palette.Delayed),
            _ => new Color(20, 20, 20),
        }),
    };

    /// <summary>Where a unit really is, when that differs from what the COP shows.</summary>
    public static IStyle TrueUnit { get; } = new StyleCollection
    {
        Styles =
        {
            new SymbolStyle { SymbolType = SymbolType.Ellipse, SymbolScale = 0.55, Fill = null, Outline = new Pen(TruthColor, 3) },
            TruthLabelStyle(offsetY: 18),
        },
    };

    private static readonly LabelStyle TruthLabel = TruthLabelStyle();

    private static LabelStyle TruthLabelStyle(double offsetY = 0) => new()
    {
        LabelColumn = LabelField, ForeColor = Color.White, BackColor = new Brush(Color.FromArgb(210, 90, 50, 120)),
        Font = new Font { Size = 10, Italic = true }, Offset = new Offset(0, offsetY), CornerRounding = 3,
    };

    /// <summary>Which zones toggle each zone layer controls.</summary>
    public static string ZoneToggleKey(ZoneType type) => type switch
    {
        ZoneType.HotZone or ZoneType.WarmZone or ZoneType.ColdZone or ZoneType.FireExclusion
            or ZoneType.PlumeHigh or ZoneType.PlumeModerate or ZoneType.PlumeLow => MapLayerKeys.HazardZones,
        ZoneType.EvacuationZone or ZoneType.ShelterInPlace => MapLayerKeys.EvacuationZones,
        ZoneType.StagingArea or ZoneType.BaseCamp or ZoneType.CommandPost or ZoneType.LandingZone => MapLayerKeys.CommandZones,
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
