using System.IO;
using System.Windows;
using System.Windows.Media;
using Emergency_Response_Simulator.Mapping;

namespace Emergency_Response_Simulator.Accessibility;

/// <summary>
/// Applies the display settings (Phase 21) to the whole application: the theme and colour vision to the shared brushes
/// (recoloured in place, so every window follows at once), the reading font and size to every window as it opens, the
/// language to every localised string, and the colour vision to the map. Saves them for next time.
/// </summary>
public sealed class DisplayService
{
    private bool _registered;

    public DisplayService(DisplaySettings initial) => Current = initial.Sanitised();

    public DisplaySettings Current { get; private set; }

    /// <summary>Raised after settings are applied (on the UI thread): the map redraws, the shortcuts re-bind.</summary>
    public event EventHandler? Changed;

    public static FontFamily FontFor(ReadingFont font) => new(font switch
    {
        ReadingFont.DyslexiaFriendly => "OpenDyslexic, Lexend, Verdana",
        ReadingFont.Hyperlegible => "Atkinson Hyperlegible, Verdana",
        _ => "Segoe UI",
    });

    /// <summary>Applies settings everywhere, and saves them unless told not to.</summary>
    public string? Apply(DisplaySettings settings, bool save = true)
    {
        Current = settings.Sanitised();
        if (Application.Current is { } app)
        {
            if (!_registered)
            {
                // Every window opened from now on gets the font and size as it loads.
                EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => ApplyTo((Window)sender)));
                _registered = true;
            }
            Recolour(app.Resources, Palettes.Ui(Current.Theme, Current.Vision));
            foreach (Window window in app.Windows) ApplyTo(window);
        }
        LocalizationManager.Instance.Language = Current.Language;
        MapStyles.Palette = Palettes.Map(Current.Vision);

        string? problem = null;
        if (save)
        {
            try
            {
                Current.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = $"Applied, but couldn't be saved: {ex.Message}";
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return problem;
    }

    /// <summary>
    /// The theme's colours into the application's brushes. The windows refer to them as DynamicResource, so replacing a
    /// brush reaches every element (a brush held by a sealed style is frozen and couldn't be recoloured in place).
    /// </summary>
    public static void Recolour(ResourceDictionary resources, UiPalette palette)
    {
        foreach (var (key, rgb) in palette.Brushes)
        {
            var colour = Color.FromArgb(rgb.A, rgb.R, rgb.G, rgb.B);
            resources[key] = new SolidColorBrush(colour);
            var colourKey = key.Replace("Brush", "Color");
            if (resources.Contains(colourKey)) resources[colourKey] = colour;
        }
    }

    public void ApplyTo(Window window)
    {
        window.FontFamily = FontFor(Current.Font);
        if (window.Content is FrameworkElement content)
            content.LayoutTransform = Current.Scale <= 1.0 ? Transform.Identity : new ScaleTransform(Current.Scale, Current.Scale);
    }
}
