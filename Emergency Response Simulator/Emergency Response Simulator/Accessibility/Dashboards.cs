namespace Emergency_Response_Simulator.Accessibility;

/// <summary>The desktop: every monitor together, in device-independent pixels (WPF's virtual screen).</summary>
public readonly record struct DesktopArea(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>
/// Multi-monitor dashboards (Phase 21): any right-panel tab can be popped out into its own window and moved to another
/// screen, and the arrangement saved with the workspace. A saved place is brought back onto the desktop as it is now, so a
/// layout saved with a second monitor never opens a window off-screen when that monitor is gone.
/// </summary>
public static class Dashboards
{
    public const double MinWidth = 320;
    public const double MinHeight = 240;

    /// <summary>How much of a window's title bar must be on the desktop to be reachable.</summary>
    public const double Reachable = 120;

    public static PanelPlacement Fit(PanelPlacement placement, DesktopArea desktop)
    {
        var width = Math.Clamp(Finite(placement.Width, 480), MinWidth, Math.Max(MinWidth, desktop.Width));
        var height = Math.Clamp(Finite(placement.Height, 600), MinHeight, Math.Max(MinHeight, desktop.Height));
        var left = Math.Clamp(Finite(placement.Left, desktop.Left), desktop.Left - width + Reachable, desktop.Right - Reachable);
        var top = Math.Clamp(Finite(placement.Top, desktop.Top), desktop.Top, desktop.Bottom - 40);
        return placement with { Left = left, Top = top, Width = width, Height = height };
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}
