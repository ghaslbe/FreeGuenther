using System.Globalization;
using System.Reflection;
using System.Text.Json;
#if !MACOS
using System.Windows;
#endif

namespace LmChat;

// Mehrsprachigkeit. Deutsch ist die Ausgangssprache:
// – Oberflächentexte (MainWindow.xaml) haben Schlüssel „ui.…“ und kommen per DynamicResource aus den Anwendungsressourcen,
//   ein Sprachwechsel wirkt dort sofort.
// – Texte im Code sind ihr eigener deutscher Schlüssel: L.T("Fertig.") bzw. L.F("Für {0} fehlt …", name).
// Übersetzungen stehen in Lang/<code>.json (eingebettet). Was dort fehlt, erscheint auf Deutsch.
public static class L
{
    public static readonly (string Code, string Name)[] Languages =
        [("de", "Deutsch"), ("en", "English"), ("fr", "Français"), ("es", "Español"), ("it", "Italiano")];

    private static Dictionary<string, string> _texts = new();

    public static string Current { get; private set; } = "de";

    // Nach einem Sprachwechsel: im Code erzeugte Texte neu aufbauen
    public static event Action? Changed;

    // "" = Sprache von Windows, falls unterstützt – sonst Englisch
    public static string Resolve(string? code)
    {
        if (code is { Length: > 0 } && Languages.Any(l => l.Code == code)) return code;
        var windows = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Languages.Any(l => l.Code == windows) ? windows : "en";
    }

    public static void Load(string? code)
    {
        Current = Resolve(code);
        var german = Read("de");
        _texts = Current == "de" ? new() : Read(Current);
#if MACOS
        _ui = german;
#else
        var resources = Application.Current.Resources;
        foreach (var (key, value) in german)
            resources[key] = _texts.TryGetValue(key, out var translated) && translated.Length > 0 ? translated : value;
#endif

        // Zahlen- und Datumsformat passend zur Sprache
        var culture = CultureInfo.GetCultureInfo(Current);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
        Changed?.Invoke();
    }

    public static string T(string german) => Platform(_texts.TryGetValue(german, out var t) && t.Length > 0 ? t : german);

    public static string F(string german, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(german), args);

#if MACOS
    // Oberflächentexte „ui.…“: unter Windows kommen sie aus den Anwendungsressourcen, hier direkt per Schlüssel
    private static Dictionary<string, string> _ui = new();

    public static string UI(string key) =>
        Platform(_texts.TryGetValue(key, out var t) && t.Length > 0 ? t : _ui.TryGetValue(key, out var german) ? german : key);

    // Die Sprachdateien sind für Windows geschrieben: Tastenkürzel und Systemnamen an den Mac anpassen
    private static string Platform(string text) => text
        .Replace("Strg + V", "⌘ + V").Replace("Ctrl + V", "⌘ + V").Replace("Strg+V", "⌘V").Replace("Ctrl+V", "⌘V")
        .Replace("Windows-Konto", "macOS-Benutzerkonto").Replace("Windows", "macOS")
        .Replace("in der Eingabeaufforderung", "im Terminal").Replace("in the command prompt", "in Terminal")
        .Replace("dans l'invite de commandes", "dans le Terminal").Replace("en el símbolo del sistema", "en Terminal")
        .Replace("nel prompt dei comandi", "nel Terminale");
#else
    private static string Platform(string text) => text;
#endif

    private static Dictionary<string, string> Read(string code)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"lang.{code}.json");
        return stream == null ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
    }
}
