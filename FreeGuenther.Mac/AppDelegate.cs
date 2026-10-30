using System.Globalization;
using System.Text.Json;

namespace LmChat;

public sealed class AppDelegate : NSApplicationDelegate
{
    private MainWindow? _main;

    public override void DidFinishLaunching(NSNotification notification)
    {
        var store = new ChatStore(MainWindow.DbPath);
        L.Load(store.GetSetting("language"));   // "" bzw. nicht gesetzt = Sprache von macOS
        BuildMenu();
        NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);

        // Beim ersten Start muss der Nutzer zustimmen, die App auf eigene Verantwortung zu nutzen – sonst wird sie beendet
        if (store.GetSetting("consent") == null)
        {
            if (!AskConsent())
            {
                NSApplication.SharedApplication.Terminate(this);
                return;
            }
            store.SetSetting("consent", JsonSerializer.Serialize(new { accepted = DateTime.Now, version = MainWindow.AppVersion }));
        }
        _main = new MainWindow();
        _main.Show();
#if DEBUG
        if (Environment.GetEnvironmentVariable("FG_TEST_DIR") is { Length: > 0 } dir)
            _main.RunSelfTest(dir);
        if (Environment.GetEnvironmentVariable("FG_SHOT_DIR") is { Length: > 0 } shots)
            _main.RunScreenshots(shots);
#endif
    }

    public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => _rebuilding == false;

    private bool _rebuilding;

    // Sprachwechsel: Menü und Fenster neu aufbauen
    public void RebuildWindow()
    {
        _rebuilding = true;
        _main?.Dispose();
        BuildMenu();
        _main = new MainWindow();
        _main.Show();
        _main.Navigate(Page.Settings);
        _rebuilding = false;
    }

    public override void WillTerminate(NSNotification notification) => _main?.Closing();

    // Nur „ja“ zählt als Zustimmung (bzw. das „Ja“ der gewählten Sprache) – jede andere Eingabe beendet die App
    private static bool AskConsent()
    {
        var answer = new NSTextField(new CGRect(0, 0, 320, 24));
        var alert = new NSAlert
        {
            MessageText = L.T("Willkommen bei FreeGuenther"),
            InformativeText = L.T("FreeGuenther ist kostenlose Software und wird ohne jede Gewährleistung bereitgestellt (MIT-Lizenz). " +
                                  "Antworten von KI-Modellen können falsch sein – prüfe wichtige Informationen selbst. Bei Online-Anbietern " +
                                  "gehen deine Nachrichten an den Anbieter, und es können Kosten entstehen.") + "\n\n" +
                              L.T("Ich stimme zu, dass ich diese Anwendung auf meine eigene Verantwortung nutze.") + "\n\n" +
                              L.T("Gib JA in das folgende Feld ein (Groß- und Kleinschreibung egal):"),
            AccessoryView = answer
        };
        alert.AddButton(L.T("Weiter"));
        alert.AddButton(L.T("Beenden"));
        alert.Window.InitialFirstResponder = answer;
        if (alert.RunModal() != (nint)(long)NSAlertButtonReturn.First) return false;

        static bool Same(string a, string b) => string.Compare(a, b, CultureInfo.CurrentCulture,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0;
        var text = answer.StringValue.Trim();
        if (Same(text, "ja") || Same(text, L.T("ja"))) return true;
        Ui.Info(L.T("Ohne Zustimmung kann FreeGuenther nicht genutzt werden. Die Anwendung wird jetzt beendet."), "FreeGuenther");
        return false;
    }

    private void BuildMenu()
    {
        var bar = new NSMenu();
        NSApplication.SharedApplication.MainMenu = bar;

        var app = Submenu(bar, "FreeGuenther");
        app.AddItem(new NSMenuItem(L.UI("ui.Einstellungen") + " …", ",", (_, _) => _main?.Navigate(Page.Settings)));
        app.AddItem(NSMenuItem.SeparatorItem);
        app.AddItem(new NSMenuItem(L.T("FreeGuenther ausblenden"), new ObjCRuntime.Selector("hide:"), "h"));
        app.AddItem(new NSMenuItem(L.T("Andere ausblenden"), new ObjCRuntime.Selector("hideOtherApplications:"), "h")
            { KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask });
        app.AddItem(NSMenuItem.SeparatorItem);
        app.AddItem(new NSMenuItem(L.T("FreeGuenther beenden"), new ObjCRuntime.Selector("terminate:"), "q"));

        var file = Submenu(bar, L.T("Ablage"));
        file.AddItem(new NSMenuItem(L.UI("ui.NeuerChat"), "n", (_, _) => _main?.NewChat()));

        // Ohne dieses Menü funktionieren ⌘C, ⌘V usw. in Textfeldern nicht
        var edit = Submenu(bar, L.T("Bearbeiten"));
        edit.AddItem(new NSMenuItem(L.T("Widerrufen"), new ObjCRuntime.Selector("undo:"), "z"));
        edit.AddItem(new NSMenuItem(L.T("Wiederholen"), new ObjCRuntime.Selector("redo:"), "Z"));
        edit.AddItem(NSMenuItem.SeparatorItem);
        edit.AddItem(new NSMenuItem(L.T("Ausschneiden"), new ObjCRuntime.Selector("cut:"), "x"));
        edit.AddItem(new NSMenuItem(L.T("Kopieren"), new ObjCRuntime.Selector("copy:"), "c"));
        edit.AddItem(new NSMenuItem(L.T("Einfügen"), new ObjCRuntime.Selector("paste:"), "v"));
        edit.AddItem(new NSMenuItem(L.T("Alles auswählen"), new ObjCRuntime.Selector("selectAll:"), "a"));

        var window = Submenu(bar, L.T("Fenster"));
        window.AddItem(new NSMenuItem(L.T("Im Dock ablegen"), new ObjCRuntime.Selector("performMiniaturize:"), "m"));
        window.AddItem(new NSMenuItem(L.T("Zoomen"), new ObjCRuntime.Selector("performZoom:"), ""));
        NSApplication.SharedApplication.WindowsMenu = window;
    }

    private static NSMenu Submenu(NSMenu bar, string title)
    {
        var item = new NSMenuItem(title);
        var menu = new NSMenu(title);
        item.Submenu = menu;
        bar.AddItem(item);
        return menu;
    }
}
