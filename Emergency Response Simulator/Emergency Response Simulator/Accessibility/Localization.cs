using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace Emergency_Response_Simulator.Accessibility;

/// <summary>A language the interface can show.</summary>
/// <param name="Name">The language's name for itself.</param>
public sealed record Language(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The interface's strings in each language (Phase 21): the header, the main panels' tabs, display and accessibility
/// settings, the shortcuts and the voice-command messages. English is complete and is the fallback for anything missing.
/// The Irish and Polish strings are a first translation, to be checked by a native speaker before an exercise relies on
/// them. What players type and what the simulation says (radio, reports) stays in English, the language of the exercise.
/// </summary>
public static class Localization
{
    public static IReadOnlyList<Language> Languages { get; } = [new("en", "English"), new("ga", "Gaeilge"), new("pl", "Polski")];

    /// <summary>key → (en, ga, pl).</summary>
    private static readonly Dictionary<string, (string En, string Ga, string Pl)> Table = new()
    {
        ["Header.Layers"] = ("☰ Layers", "☰ Sraitheanna", "☰ Warstwy"),
        ["Header.Console"] = ("🎛 Console", "🎛 Consól", "🎛 Konsola"),
        ["Header.Scenarios"] = ("🧩 Scenarios", "🧩 Cásanna", "🧩 Scenariusze"),
        ["Header.Standards"] = ("🔗 Standards", "🔗 Caighdeáin", "🔗 Standardy"),
        ["Header.Forms"] = ("📄 Forms", "📄 Foirmeacha", "📄 Formularze"),
        ["Header.Aar"] = ("📊 AAR", "📊 Athbhreithniú", "📊 Analiza"),
        ["Header.Iap"] = ("📋 IAP Builder", "📋 Plean Gníomhaíochta", "📋 Plan działań"),
        ["Header.Start"] = ("▶ Start", "▶ Tosaigh", "▶ Start"),
        ["Header.Pause"] = ("⏸ Pause", "⏸ Sos", "⏸ Pauza"),
        ["Header.Panels"] = ("Comms / Incident ☰", "Cumarsáid / Teagmhas ☰", "Łączność / Zdarzenie ☰"),
        ["Header.Display"] = ("⚙ Display", "⚙ Taispeáint", "⚙ Wyświetlanie"),
        ["Header.Speed"] = ("Simulation speed", "Luas an insamhalta", "Prędkość symulacji"),

        ["Name.Console"] = ("Instructor console", "Consól an teagascóra", "Konsola instruktora"),
        ["Name.Scenarios"] = ("Scenario editor", "Eagarthóir cásanna", "Edytor scenariuszy"),
        ["Name.PopOut"] = ("Panel in its own window: close the window to bring it back here.", "Painéal ina fhuinneog féin: dún an fhuinneog chun é a thabhairt ar ais.",
            "Panel w osobnym oknie: zamknij okno, aby go tu przywrócić."),

        ["Tab.Comms"] = ("COMMS HUB", "MOL CUMARSÁIDE", "CENTRUM ŁĄCZNOŚCI"),
        ["Tab.Incident"] = ("INCIDENT", "TEAGMHAS", "ZDARZENIE"),
        ["Tab.Unit"] = ("UNIT", "AONAD", "JEDNOSTKA"),
        ["Tab.Ics"] = ("ICS", "ICS", "ICS"),

        ["Settings.Title"] = ("Display & Accessibility", "Taispeáint agus Inrochtaineacht", "Wyświetlanie i dostępność"),
        ["Settings.Display"] = ("Display", "Taispeáint", "Wyświetlanie"),
        ["Settings.Theme"] = ("THEME", "TÉAMA", "MOTYW"),
        ["Settings.Vision"] = ("COLOUR VISION", "FÍS DATHANNA", "WIDZENIE BARW"),
        ["Settings.Font"] = ("READING FONT", "CLÓ LÉITHEOIREACHTA", "CZCIONKA"),
        ["Settings.Scale"] = ("TEXT AND CONTROL SIZE", "MÉID TÉACS AGUS RIALÚCHÁN", "ROZMIAR TEKSTU I ELEMENTÓW"),
        ["Settings.Language"] = ("LANGUAGE", "TEANGA", "JĘZYK"),
        ["Settings.Voice"] = ("Voice commands: say “Simulator”, then the command (English)", "Orduithe gutha: abair “Simulator”, ansin an t-ordú (Béarla)",
            "Polecenia głosowe: powiedz „Simulator”, potem polecenie (po angielsku)"),
        ["Settings.Shortcuts"] = ("Shortcuts", "Aicearraí", "Skróty"),
        ["Settings.ShortcutsHint"] = ("Type a key or combination (F5, Ctrl+Shift+N), then Apply. Clashes and the push-to-talk key are refused.",
            "Clóscríobh eochair nó meascán (F5, Ctrl+Shift+N), ansin Cuir i bhfeidhm. Ní ghlactar le coimhlintí ná le heochair an raidió.",
            "Wpisz klawisz lub kombinację (F5, Ctrl+Shift+N), potem Zastosuj. Konflikty i klawisz nadawania są odrzucane."),
        ["Settings.Workspace"] = ("Workspace", "Spás oibre", "Obszar roboczy"),
        ["Settings.WorkspaceHint"] = ("The panels this workspace shows and the one in front. The desk and the session are always there.",
            "Na painéil a thaispeánann an spás oibre seo agus an ceann chun tosaigh. Bíonn an deasc agus an seisiún ann i gcónaí.",
            "Panele widoczne w tym obszarze i panel na wierzchu. Biurko i sesja są zawsze dostępne."),
        ["Settings.Focus"] = ("IN FRONT", "CHUN TOSAIGH", "NA WIERZCHU"),
        ["Settings.Dashboards"] = ("Pop-out panels", "Painéil i bhfuinneoga", "Panele w oknach"),
        ["Settings.DashboardsHint"] = ("Pop a panel out (Ctrl+Shift+P) and drag it to another screen. Save the layout and it comes back with this workspace.",
            "Oscail painéal i bhfuinneog (Ctrl+Shift+P) agus tarraing chuig scáileán eile é. Sábháil an leagan amach agus filleann sé leis an spás oibre seo.",
            "Otwórz panel w oknie (Ctrl+Shift+P) i przeciągnij na inny ekran. Zapisz układ, a wróci z tym obszarem roboczym."),
        ["Settings.Apply"] = ("Apply", "Cuir i bhfeidhm", "Zastosuj"),
        ["Settings.Reset"] = ("Reset to the role's default", "Athshocraigh go réamhshocrú an róil", "Przywróć domyślne dla roli"),
        ["Settings.SaveLayout"] = ("Save this layout", "Sábháil an leagan amach seo", "Zapisz ten układ"),
        ["Settings.ClearLayout"] = ("Forget the layout", "Déan dearmad ar an leagan amach", "Usuń układ"),
        ["Settings.Close"] = ("Close", "Dún", "Zamknij"),
        ["Settings.Saved"] = ("Saved.", "Sábháilte.", "Zapisano."),

        ["Theme.Dark"] = ("Dark", "Dorcha", "Ciemny"),
        ["Theme.Light"] = ("Light", "Geal", "Jasny"),
        ["Theme.Night"] = ("Night operations (red)", "Oibríochtaí oíche (dearg)", "Działania nocne (czerwony)"),
        ["Theme.HighContrast"] = ("High contrast", "Ardchodarsnacht", "Wysoki kontrast"),
        ["Vision.Standard"] = ("Standard", "Caighdeánach", "Standardowe"),
        ["Vision.RedGreen"] = ("Red–green safe (protan, deutan)", "Sábháilte do dhearg–glas (protan, deutan)", "Dla daltonistów czerwono-zielonych (protan, deutan)"),
        ["Vision.BlueYellow"] = ("Blue–yellow safe (tritan)", "Sábháilte do ghorm–buí (tritan)", "Dla daltonistów niebiesko-żółtych (tritan)"),
        ["Font.Standard"] = ("Standard (Segoe UI)", "Caighdeánach (Segoe UI)", "Standardowa (Segoe UI)"),
        ["Font.DyslexiaFriendly"] = ("Dyslexia-friendly (OpenDyslexic, Lexend or Verdana)", "Oiriúnach don disléicse (OpenDyslexic, Lexend nó Verdana)",
            "Przyjazna dla dyslektyków (OpenDyslexic, Lexend lub Verdana)"),
        ["Font.Hyperlegible"] = ("Hyperlegible, for low vision (Atkinson Hyperlegible or Verdana)", "An-soléite, do lagamharc (Atkinson Hyperlegible nó Verdana)",
            "Bardzo czytelna, dla słabowidzących (Atkinson Hyperlegible lub Verdana)"),

        ["Action.TogglePlay"] = ("Start or pause the clock", "Tosaigh nó cuir an clog ar sos", "Uruchom lub zatrzymaj zegar"),
        ["Action.Faster"] = ("Faster", "Níos tapúla", "Szybciej"),
        ["Action.Slower"] = ("Slower", "Níos moille", "Wolniej"),
        ["Action.ToggleLayers"] = ("Show or hide map layers", "Taispeáin nó folaigh sraitheanna", "Pokaż lub ukryj warstwy mapy"),
        ["Action.TogglePanels"] = ("Show or hide the side panel", "Taispeáin nó folaigh an painéal taoibh", "Pokaż lub ukryj panel boczny"),
        ["Action.NextPanel"] = ("Next panel", "An chéad phainéal eile", "Następny panel"),
        ["Action.PreviousPanel"] = ("Previous panel", "An painéal roimhe", "Poprzedni panel"),
        ["Action.PopOut"] = ("Pop the panel out to its own window", "Oscail an painéal ina fhuinneog féin", "Otwórz panel w osobnym oknie"),
        ["Action.OpenIap"] = ("Incident Action Plan", "Plean Gníomhaíochta Teagmhais", "Plan działań"),
        ["Action.OpenForms"] = ("ICS forms", "Foirmeacha ICS", "Formularze ICS"),
        ["Action.OpenAar"] = ("After-action review", "Athbhreithniú iar-ghnímh", "Analiza po działaniu"),
        ["Action.OpenStandards"] = ("Data standards and NIMS", "Caighdeáin sonraí agus NIMS", "Standardy danych i NIMS"),
        ["Action.OpenSettings"] = ("Display and accessibility", "Taispeáint agus inrochtaineacht", "Wyświetlanie i dostępność"),
        ["Action.Acknowledge"] = ("Acknowledge the oldest alert", "Admhaigh an foláireamh is sine", "Potwierdź najstarszy alarm"),
        ["Action.NightMode"] = ("Night operations on or off", "Oibríochtaí oíche ar siúl nó as", "Tryb nocny włącz/wyłącz"),
        ["Action.Shortcuts"] = ("Show the shortcuts", "Taispeáin na haicearraí", "Pokaż skróty"),

        ["Voice.Off"] = ("Voice commands are off.", "Tá orduithe gutha múchta.", "Polecenia głosowe są wyłączone."),
        ["Voice.Listening"] = ("Listening for “Simulator, …”.", "Ag éisteacht le “Simulator, …”.", "Nasłuchuję „Simulator, …”."),
        ["Voice.Unavailable"] = ("No English speech recogniser on this computer: install one in Windows speech settings.",
            "Níl aitheantóir cainte Béarla ar an ríomhaire seo: suiteáil ceann i socruithe cainte Windows.",
            "Brak rozpoznawania mowy angielskiej na tym komputerze: zainstaluj je w ustawieniach mowy Windows."),
        ["Voice.Heard"] = ("Heard: “{0}”", "Chualathas: “{0}”", "Usłyszano: „{0}”"),
        ["Voice.Paused"] = ("Voice commands pause while you talk on the radio.", "Stopann orduithe gutha agus tú ag caint ar an raidió.",
            "Polecenia głosowe wstrzymują się podczas nadawania przez radio."),
    };

    public static IEnumerable<string> Keys => Table.Keys;

    /// <summary>The string in a language; English if that language has none, the key itself if English has none either.</summary>
    public static string Text(string key, string language)
    {
        if (!Table.TryGetValue(key, out var row)) return key;
        var text = language switch { "ga" => row.Ga, "pl" => row.Pl, _ => row.En };
        return string.IsNullOrEmpty(text) ? row.En : text;
    }
}

/// <summary>The current language, for bindings: <c>{acc:Loc Header.Layers}</c> updates when the language changes.</summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    public static LocalizationManager Instance { get; } = new();

    private string _language = "en";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Language
    {
        get => _language;
        set
        {
            if (_language == value) return;
            _language = value;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        }
    }

    public string this[string key] => Localization.Text(key, _language);

    public string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, this[key], args);
}

/// <summary>XAML: <c>Content="{acc:Loc Header.Layers}"</c>.</summary>
[MarkupExtensionReturnType(typeof(BindingExpression))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = LocalizationManager.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
