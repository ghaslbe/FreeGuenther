using System.Text.Json;
using System.Windows;

namespace LmChat;

public partial class App : Application
{
    // Beim ersten Start muss der Nutzer zustimmen, die App auf eigene Verantwortung zu nutzen – sonst wird sie beendet
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Solange nur der Zustimmungs-Dialog offen ist, darf sein Schließen die App nicht beenden
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var store = new ChatStore(LmChat.MainWindow.DbPath);
        L.Load(store.GetSetting("language"));   // "" bzw. nicht gesetzt = Sprache von Windows
        if (store.GetSetting("consent") == null)
        {
            if (new ConsentWindow().ShowDialog() != true)
            {
                Shutdown();
                return;
            }
            store.SetSetting("consent", JsonSerializer.Serialize(new { accepted = DateTime.Now, version = LmChat.MainWindow.AppVersion }));
        }

        var window = new MainWindow();
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }
}
