using System.Globalization;

namespace Emergency_Response_Simulator.Accessibility;

/// <summary>An sRGB colour, with the WCAG 2 luminance and contrast arithmetic.</summary>
public readonly record struct Rgb(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>"#RRGGBB" or "#AARRGGBB".</summary>
    public static Rgb Parse(string hex)
    {
        var h = hex.TrimStart('#');
        byte At(int i) => byte.Parse(h.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return h.Length switch
        {
            6 => new(At(0), At(2), At(4)),
            8 => new(At(2), At(4), At(6), At(0)),
            _ => throw new FormatException($"Not a colour: {hex}"),
        };
    }

    public Rgb WithAlpha(byte alpha) => this with { A = alpha };

    /// <summary>WCAG 2 relative luminance, 0 (black) to 1 (white).</summary>
    public double Luminance
    {
        get
        {
            static double Channel(byte c)
            {
                var s = c / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);
        }
    }

    /// <summary>WCAG 2 contrast ratio, 1 to 21. Normal text needs 4.5 (AA); large text and graphics 3.</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        var (light, dark) = a.Luminance >= b.Luminance ? (a.Luminance, b.Luminance) : (b.Luminance, a.Luminance);
        return (light + 0.05) / (dark + 0.05);
    }

    public override string ToString() => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

/// <summary>The interface colours: one per brush the windows use (App.xaml keys without "Brush").</summary>
public sealed record UiPalette(Rgb Background, Rgb Panel, Rgb PanelRaised, Rgb Border, Rgb Text, Rgb MutedText, Rgb Accent, Rgb Critical,
    Rgb Warning, Rgb Ok, Rgb Selection, Rgb AlternateRow, Rgb Overlay, Rgb Banner, Rgb Truth)
{
    /// <summary>Text-bearing colours, each checked against every surface it sits on.</summary>
    public IEnumerable<(string Name, Rgb Colour)> Foregrounds =>
        [("Text", Text), ("MutedText", MutedText), ("Accent", Accent), ("Critical", Critical), ("Warning", Warning), ("Ok", Ok), ("Truth", Truth)];

    public IEnumerable<(string Name, Rgb Colour)> Surfaces =>
        [("Background", Background), ("Panel", Panel), ("PanelRaised", PanelRaised)];

    /// <summary>Brush key → colour, for applying to the application's resources.</summary>
    public IReadOnlyDictionary<string, Rgb> Brushes => new Dictionary<string, Rgb>
    {
        ["BackgroundBrush"] = Background, ["PanelBrush"] = Panel, ["PanelRaisedBrush"] = PanelRaised, ["BorderBrush"] = Border,
        ["TextBrush"] = Text, ["MutedTextBrush"] = MutedText, ["AccentBrush"] = Accent, ["CriticalBrush"] = Critical,
        ["WarningBrush"] = Warning, ["OkBrush"] = Ok, ["SelectionBrush"] = Selection, ["AlternateRowBrush"] = AlternateRow,
        ["OverlayBrush"] = Overlay, ["BannerBrush"] = Banner, ["TruthBrush"] = Truth,
    };
}

/// <summary>The map's meaningful colours: agencies, zones, triage, plume, congestion, unit rings.</summary>
public sealed record MapPalette(Rgb Fire, Rgb Ems, Rgb Police, Rgb OtherAgency, Rgb Hot, Rgb Warm, Rgb Cold, Rgb PlumeHigh, Rgb PlumeModerate,
    Rgb Immediate, Rgb Urgent, Rgb Delayed, Rgb CommsLost, Rgb Committed, Rgb Gridlock, Rgb Heavy, Rgb Slow);

/// <summary>
/// The palettes (Phase 21). Every theme and colour-vision combination keeps text, muted text and the status colours at
/// WCAG AA contrast (4.5:1) on every surface; <c>AccessibilityTests</c> holds them to it. Night operations is one red
/// palette whatever the colour vision: it carries status by brightness and words, not hue.
/// </summary>
public static class Palettes
{
    private static Rgb C(string hex) => Rgb.Parse(hex);

    public static UiPalette Ui(Theme theme, ColourVision vision)
    {
        var basis = theme switch
        {
            Theme.Light => new UiPalette(C("#F4F6F9"), C("#FFFFFF"), C("#E9EDF2"), C("#C3CCD6"), C("#17202B"), C("#4A5560"), C("#0A5BB5"),
                C("#B3261E"), C("#8A5300"), C("#1B6E31"), C("#CFE2F7"), C("#F0F3F7"), C("#EEFFFFFF"), C("#F8D7D5"), C("#6B3FA0")),
            Theme.Night => new UiPalette(C("#000000"), C("#080202"), C("#120404"), C("#3A1008"), C("#E8643C"), C("#D0563A"), C("#F07850"),
                C("#FF8060"), C("#D88A3A"), C("#B07040"), C("#2A0A04"), C("#0A0302"), C("#EE080202"), C("#3A0A04"), C("#D0708A")),
            Theme.HighContrast => new UiPalette(C("#000000"), C("#000000"), C("#0A0A0A"), C("#FFFFFF"), C("#FFFFFF"), C("#E6E6E6"), C("#5CC8FF"),
                C("#FF7070"), C("#FFD000"), C("#50FF80"), C("#003A66"), C("#141414"), C("#F0000000"), C("#5A0000"), C("#E0B0FF")),
            _ => new UiPalette(C("#0F1419"), C("#161D26"), C("#1C2530"), C("#2A3644"), C("#E6EDF3"), C("#8B98A5"), C("#3BA7FF"),
                C("#FF5C5C"), C("#FFB020"), C("#3FB950"), C("#1F3A56"), C("#121922"), C("#EE161D26"), C("#7A1F1F"), C("#C792EA")),
        };
        if (theme == Theme.Night || vision == ColourVision.Standard) return basis;

        // Status colours a person with that colour vision can tell apart, at the same contrast.
        var (critical, warning, ok, accent) = (theme, vision) switch
        {
            (Theme.Light, ColourVision.RedGreen) => ("#8C2D00", "#806600", "#005A9E", "#6A3D9A"), // OK in blue, as Okabe–Ito does
            (Theme.Light, _) => ("#B0123A", "#8F4A00", "#1F6B2E", "#A3155F"),
            (Theme.HighContrast, ColourVision.RedGreen) => ("#FF8A3D", "#F0E442", "#33C3FF", "#D9A6FF"),
            (Theme.HighContrast, _) => ("#FF6B8A", "#FFA040", "#7DFF7D", "#FF7AC6"),
            (_, ColourVision.RedGreen) => ("#F0703A", "#E69F00", "#1AB394", "#56B4E9"),
            _ => ("#FF5C7A", "#FF9A3C", "#4CC36A", "#F06AA8"),
        };
        return basis with { Critical = C(critical), Warning = C(warning), Ok = C(ok), Accent = C(accent) };
    }

    /// <summary>The map keeps its hues in every theme (a basemap can't go red-only); only colour vision changes them.</summary>
    public static MapPalette Map(ColourVision vision) => vision switch
    {
        ColourVision.RedGreen => new MapPalette(C("#D55E00"), C("#009E73"), C("#0072B2"), C("#7A7A82"), C("#D55E00"), C("#E69F00"),
            C("#56B4E9"), C("#CC79A7"), C("#F0A8CF"), C("#D55E00"), C("#F0E442"), C("#56B4E9"), C("#D55E00"), C("#F0E442"),
            C("#7F2F00"), C("#D55E00"), C("#E69F00")),
        ColourVision.BlueYellow => new MapPalette(C("#D81B60"), C("#2E7D32"), C("#6A1B9A"), C("#7A7A82"), C("#D81B60"), C("#FB8C00"),
            C("#43A047"), C("#AD1457"), C("#F48FB1"), C("#D81B60"), C("#FB8C00"), C("#43A047"), C("#D81B60"), C("#FB8C00"),
            C("#880E4F"), C("#D81B60"), C("#FB8C00")),
        _ => new MapPalette(C("#E63228"), C("#1EAA5A"), C("#2864DC"), C("#787882"), C("#FF2828"), C("#FF9600"), C("#3CC85A"),
            C("#DC1428"), C("#FF7800"), C("#E61414"), C("#FFBE00"), C("#28B446"), C("#FF2828"), C("#FFBE00"),
            C("#960014"), C("#EB2828"), C("#FFAA00")),
    };
}
