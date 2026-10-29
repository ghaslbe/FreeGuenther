using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;

namespace LmChat;

// Seite „Plugins“: installierte Plugins (an/aus, Einstellungen, entfernen), Katalog und Import
public partial class MainWindow
{
    private List<CatalogEntry>? _catalog;
    private readonly HashSet<string> _openPluginSettings = new();

    private void ShowPlugins(bool loadCatalog = false)
    {
        var model = _settings.Current.Model.Length > 0 ? _settings.Current.Model : "kein Modell";
        PluginModelInfo.Text = _settings.Current.ModelTools switch
        {
            true => $"✓ Dein aktives Modell ({model}) kann Plugins nutzen.",
            false => $"✗ Dein aktives Modell ({model}) kann keine Plugins nutzen – wähle in den Einstellungen ein Modell mit 🔧.",
            _ => $"Ob dein aktives Modell ({model}) Plugins nutzen kann, meldet der Anbieter nicht – einfach ausprobieren."
        };
        PluginList.Children.Clear();
        foreach (var m in _plugins.Plugins)
            PluginList.Children.Add(MakePluginCard(m));
        RenderCatalog();
        if (loadCatalog) _ = LoadCatalogAsync();
    }

    private Border MakePluginCard(PluginManifest m)
    {
        var state = _plugins.State(m.Id);
        var card = new Border { Style = (Style)FindResource("Card") };
        var panel = new StackPanel();
        card.Child = panel;

        // Kopfzeile: Name, Version, Art – rechts der Schalter
        var head = new DockPanel();
        var toggle = new CheckBox
        {
            Content = "Aktiv",
            IsChecked = state.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Plugin für das Modell freigeben"
        };
        RoutedEventHandler onToggle = async (_, _) =>
        {
            state.Enabled = toggle.IsChecked == true;
            _plugins.SaveStates();
            if (!state.Enabled) await _plugins.ResetAsync(m.Id);
            // Fehlt eine Pflicht-Einstellung (z. B. Ordner), gleich die Einstellungen öffnen
            if (state.Enabled && m.Settings.Any(s => s.Type == "folder" && _plugins.GetValue(m, s).Length == 0))
                _openPluginSettings.Add(m.Id);
            ShowPlugins();
        };
        toggle.Checked += onToggle;
        toggle.Unchecked += onToggle;
        DockPanel.SetDock(toggle, Dock.Right);
        head.Children.Add(toggle);
        var title = new TextBlock { Style = (Style)FindResource("Heading"), TextWrapping = TextWrapping.Wrap };
        title.Inlines.Add(new Run(m.Name));
        title.Inlines.Add(new Run($"   {m.Version} · {m.TypeLabel}" + (m.IsBuiltIn ? " · mitgeliefert" : ""))
        {
            FontSize = 12, FontWeight = FontWeights.Normal, Foreground = Brushes.Gray
        });
        head.Children.Add(title);
        panel.Children.Add(head);

        panel.Children.Add(new TextBlock { Style = (Style)FindResource("Body"), Text = m.Description });
        var info = new TextBlock { Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 6, 0, 0) };
        info.Inlines.Add(new Run(m.AccessSummary()));
        if (m.Type != "mcp")
            info.Inlines.Add(new Run("   ·   Aktionen: " + string.Join(", ", m.Tools.Select(t => t.Name))));
        if (_plugins.AskBeforeRun(m))
            info.Inlines.Add(new Run("   ·   fragt vor jeder Aktion nach"));
        if (m.Homepage is { } homepage && Uri.TryCreate(homepage, UriKind.Absolute, out _))
        {
            info.Inlines.Add(new Run("   ·   "));
            info.Inlines.Add(MakeLink("Website", homepage));
        }
        panel.Children.Add(info);

        // Knöpfe: Einstellungen auf/zu, Entfernen
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var open = _openPluginSettings.Contains(m.Id);
        var settingsButton = new Button { Content = open ? "Einstellungen ausblenden" : "⚙ Einstellungen", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        settingsButton.Click += (_, _) =>
        {
            if (!_openPluginSettings.Remove(m.Id)) _openPluginSettings.Add(m.Id);
            ShowPlugins();
        };
        buttons.Children.Add(settingsButton);
        if (m.FilePath != null)
        {
            var remove = new Button
            {
                Content = m.IsBuiltIn ? "Update entfernen" : "Entfernen",
                Padding = new Thickness(12, 4, 12, 4),
                ToolTip = m.IsBuiltIn ? "Zurück zur mitgelieferten Version" : "Plugin löschen (Einstellungen gehen verloren)"
            };
            remove.Click += async (_, _) =>
            {
                if (MessageBox.Show(this, $"Plugin „{m.Name}“ entfernen?", "Plugin entfernen", MessageBoxButton.YesNo,
                        MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                await _plugins.RemoveAsync(m);
                ShowPlugins();
            };
            buttons.Children.Add(remove);
        }
        panel.Children.Add(buttons);

        if (open)
            panel.Children.Add(MakePluginSettings(m));
        return card;
    }

    // Einstellungsseite eines Plugins: Felder aus dem Manifest, dazu Rückfrage und (bei MCP) Verbindungstest
    private StackPanel MakePluginSettings(PluginManifest m)
    {
        var state = _plugins.State(m.Id);
        var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var status = new TextBlock { Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 8, 0, 0) };

        async void Changed(PluginSetting s, string value)
        {
            _plugins.SetValue(m, s, value);
            await _plugins.ResetAsync(m.Id);
            status.Text = $"Gespeichert ({DateTime.Now:HH:mm:ss}).";
        }

        if (m.Settings.Count == 0)
            panel.Children.Add(new TextBlock { Style = (Style)FindResource("Hint"), Text = "Dieses Plugin hat keine eigenen Einstellungen." });

        foreach (var s in m.Settings)
        {
            panel.Children.Add(new TextBlock { Text = s.Label.Length > 0 ? s.Label : s.Key, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
            if (s.Description.Length > 0)
                panel.Children.Add(new TextBlock { Style = (Style)FindResource("Hint"), Text = s.Description, Margin = new Thickness(0, 0, 0, 4) });
            var value = _plugins.GetValue(m, s);
            switch (s.Type)
            {
                case "secret":
                {
                    var box = new PasswordBox { Password = value };
                    box.PasswordChanged += (_, _) => Changed(s, box.Password.Trim());
                    panel.Children.Add(box);
                    break;
                }
                case "bool":
                {
                    var box = new CheckBox { Content = "Ein", IsChecked = value is "true" or "1" };
                    box.Checked += (_, _) => Changed(s, "true");
                    box.Unchecked += (_, _) => Changed(s, "false");
                    panel.Children.Add(box);
                    break;
                }
                case "choice":
                {
                    var labels = s.Options.Select((o, i) => i < s.OptionLabels.Count ? s.OptionLabels[i] : o).ToList();
                    var box = new ComboBox { ItemsSource = labels, SelectedIndex = Math.Max(0, s.Options.IndexOf(value)), MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
                    box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) Changed(s, s.Options[box.SelectedIndex]); };
                    panel.Children.Add(box);
                    break;
                }
                case "folder":
                {
                    var row = new DockPanel();
                    var pick = new Button { Content = "Ordner wählen …", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
                    DockPanel.SetDock(pick, Dock.Right);
                    var box = new TextBox { Text = value };
                    box.LostFocus += (_, _) => Changed(s, box.Text.Trim());
                    pick.Click += (_, _) =>
                    {
                        var dialog = new OpenFolderDialog { Title = s.Label };
                        if (dialog.ShowDialog(this) == true)
                        {
                            box.Text = dialog.FolderName;
                            Changed(s, box.Text);
                        }
                    };
                    row.Children.Add(pick);
                    row.Children.Add(box);
                    panel.Children.Add(row);
                    break;
                }
                default:
                {
                    var box = new TextBox { Text = value };
                    box.LostFocus += (_, _) => Changed(s, box.Text.Trim());
                    panel.Children.Add(box);
                    break;
                }
            }
        }

        var ask = new CheckBox
        {
            Content = "Vor jeder Aktion dieses Plugins nachfragen",
            IsChecked = _plugins.AskBeforeRun(m),
            Margin = new Thickness(0, 14, 0, 0)
        };
        RoutedEventHandler onAsk = (_, _) =>
        {
            state.AskBeforeRun = ask.IsChecked == true;
            _plugins.SaveStates();
            status.Text = $"Gespeichert ({DateTime.Now:HH:mm:ss}).";
        };
        ask.Checked += onAsk;
        ask.Unchecked += onAsk;
        panel.Children.Add(ask);

        if (m.Type == "mcp")
        {
            var test = new Button { Content = "Verbindung testen", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left };
            test.Click += async (_, _) =>
            {
                status.Text = "Verbinde …";
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    var problems = new List<string>();
                    await _plugins.ResetAsync(m.Id);
                    var names = await _plugins.ToolNamesAsync(m, cts.Token);
                    status.Text = $"✓ Verbunden – {names.Count} Aktionen: {string.Join(", ", names)}";
                }
                catch (Exception ex)
                {
                    status.Text = "✗ Keine Verbindung: " + ex.Message;
                }
            };
            panel.Children.Add(test);
        }
        panel.Children.Add(status);
        return panel;
    }

    // ---------- Katalog und Import ----------

    private async Task LoadCatalogAsync()
    {
        CatalogStatus.Text = "Lade Katalog …";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            _catalog = await _plugins.LoadCatalogAsync(cts.Token);
            CatalogStatus.Text = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            CatalogStatus.Text = "Der Katalog ist gerade nicht erreichbar (keine Internetverbindung?). Import aus Datei geht trotzdem.";
        }
        RenderCatalog();
    }

    private void RenderCatalog()
    {
        CatalogList.Children.Clear();
        foreach (var entry in _catalog ?? [])
        {
            var installed = _plugins.Plugins.FirstOrDefault(p => p.Id == entry.Id);
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            var button = new Button { Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 120 };
            if (installed == null)
                button.Content = "Installieren";
            else if (PluginManager.CompareVersions(entry.Version, installed.Version) > 0)
                button.Content = $"Aktualisieren ({entry.Version})";
            else
            {
                button.Content = "✓ Installiert";
                button.IsEnabled = false;
            }
            button.Click += async (_, _) => await InstallPluginAsync(ct => PluginManager.DownloadAsync(entry.Url, ct));
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
            text.Inlines.Add(new Run(entry.Name) { FontWeight = FontWeights.SemiBold });
            text.Inlines.Add(new Run($"   {entry.Version} · {(entry.Type switch { "mcp" => "MCP", "builtin" => "eingebaut", _ => "Web" })}")
            {
                FontSize = 12, Foreground = Brushes.Gray
            });
            text.Inlines.Add(new LineBreak());
            text.Inlines.Add(new Run(entry.Description) { FontSize = 13 });
            row.Children.Add(text);
            CatalogList.Children.Add(row);
        }
    }

    private void ReloadCatalog_Click(object sender, RoutedEventArgs e) => _ = LoadCatalogAsync();

    private async void ImportPluginFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Plugin-Datei (*.json)|*.json", Title = "Plugin importieren" };
        if (dialog.ShowDialog(this) == true)
            await InstallPluginAsync(_ => Task.FromResult(PluginManager.ReadFile(dialog.FileName)));
    }

    private async void ImportPluginUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = PluginUrlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
        {
            CatalogStatus.Text = "Bitte eine vollständige https-Adresse einer Plugin-Datei eingeben.";
            return;
        }
        await InstallPluginAsync(ct => PluginManager.DownloadAsync(url, ct));
    }

    // Laden, prüfen, anzeigen was das Plugin tut, nach Bestätigung installieren und einschalten
    private async Task InstallPluginAsync(Func<CancellationToken, Task<PluginManifest>> load)
    {
        PluginManifest m;
        try
        {
            CatalogStatus.Text = "Lade Plugin …";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            m = await load(cts.Token);
            CatalogStatus.Text = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or IOException)
        {
            CatalogStatus.Text = "Das Plugin konnte nicht geladen werden: " + ex.Message;
            return;
        }

        var warning = m.Type == "mcp"
            ? "\n\n⚠ MCP-Plugin: Es verbindet sich mit einem MCP-Server bzw. startet ein Programm auf diesem Rechner. " +
              "Was es darf, bestimmt dieser Server. Vor jeder Aktion wird nachgefragt."
            : "";
        var tools = m.Type == "mcp" ? "" : "\nAktionen: " + string.Join(", ", m.Tools.Select(t => t.Name));
        if (MessageBox.Show(this,
                $"Plugin „{m.Name}“ {m.Version} von {(m.Author.Length > 0 ? m.Author : "unbekannt")} installieren?\n\n{m.Description}\n\n" +
                $"{m.AccessSummary()}{tools}{warning}\n\nNur installieren, wenn du der Quelle vertraust.",
                "Plugin installieren", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        await _plugins.InstallAsync(m);
        var state = _plugins.State(m.Id);
        state.Enabled = true;
        _plugins.SaveStates();
        if (m.Settings.Count > 0) _openPluginSettings.Add(m.Id);
        CatalogStatus.Text = $"✓ „{m.Name}“ installiert und eingeschaltet." + (m.Settings.Count > 0 ? " Bitte die Einstellungen prüfen." : "");
        ShowPlugins();
    }
}
