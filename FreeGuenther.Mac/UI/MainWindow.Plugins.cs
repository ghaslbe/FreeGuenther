using System.Text.Json;

namespace LmChat;

// Seite „Plugins“: installierte Plugins (an/aus, Einstellungen, entfernen), Katalog und Import
internal sealed partial class MainWindow
{
    private List<CatalogEntry>? _catalog;
    private readonly HashSet<string> _openPluginSettings = new();

    private WrapLabel _pluginModelInfo = null!, _catalogStatus = null!;
    private Col _pluginList = null!, _catalogList = null!;
    private NSTextField _pluginUrlBox = null!;

    private NSView BuildPluginsPage()
    {
        var page = new Col(14);
        page.Add(Ui.PageTitle(L.UI("ui.Plugins")));
        page.Add(Ui.Hint(L.UI("ui.PluginsGebenDemModellZusaetzliche")));

        var how = new Col(4);
        how.Add(Ui.Heading(L.UI("ui.SoFunktioniertS")));
        how.Add(Ui.Body(L.UI("ui.IstEinPluginEingeschaltetKann")));
        how.Add(new Rich().Bold(L.UI("ui.NichtJedesModellKannPlugins") + " ").Text(L.UI("ui.InDerModelllisteStehenGeeignete")).View());
        _pluginModelInfo = Ui.Hint();
        how.Add(_pluginModelInfo);
        page.Add(Ui.Card(how));

        page.Add(Ui.Heading(L.UI("ui.InstalliertePlugins")));
        _pluginList = new Col(10);
        page.Add(_pluginList);

        page.Add(Ui.Heading(L.UI("ui.WeiterePlugins")));
        var more = new Col(8);
        more.Add(new Rich().Text(L.UI("ui.AusDemPluginKatalogIm") + " ").Link(L.UI("ui.GitHubProjekt"), "https://github.com/ghaslbe/FreeGuenther/tree/main/plugins")
            .Text(" " + L.UI("ui.HerunterladenOderEinePluginDatei")).View());
        _catalogList = new Col(6);
        more.Add(_catalogList);
        _catalogStatus = Ui.Hint();
        more.Add(_catalogStatus);
        more.Add(Ui.Row(8,
            Ui.Button(L.UI("ui.KatalogNeuLaden"), () => _ = LoadCatalogAsync()),
            Ui.Button(L.UI("ui.PluginDateiImportieren"), ImportPluginFile), Ui.Spacer()));
        _pluginUrlBox = Ui.Field(L.UI("ui.AdresseEinerPluginDateiZ"));
        more.Add(Ui.Row(8, _pluginUrlBox, Ui.Button(L.UI("ui.VonAdresseInstallieren"), ImportPluginUrl)));
        page.Add(Ui.Card(more));

        var safety = new Col(4);
        safety.Add(Ui.Heading(L.UI("ui.Sicherheit")));
        safety.Add(new Rich().Text(L.UI("ui.InstalliereNurPluginsAusQuellen") + "\n")
            .Bold(L.UI("ui.WebPlugins") + " ").Text(L.UI("ui.RufenNurDieAngezeigtenAdressen") + "\n")
            .Bold(L.UI("ui.EingebautePlugins") + " ").Text(L.UI("ui.LaufenInFreeGuentherSelbstDateien") + "\n")
            .Bold(L.UI("ui.MCPPlugins") + " ").Text(L.UI("ui.VerbindenSichMitEinemMCP") + "\n")
            .Text(L.UI("ui.BeiOnlineAnbieternGehenDie")).View());
        page.Add(Ui.Card(safety, Ui.WarningFill, Ui.Warning.ColorWithAlphaComponent(0.4f)));
        return Ui.ScrollPage(page, 860);
    }

    private void ShowPlugins(bool loadCatalog = false)
    {
        var model = _settings.Current.Model.Length > 0 ? _settings.Current.Model : L.T("kein Modell");
        _pluginModelInfo.StringValue = _settings.Current.ModelTools switch
        {
            true => L.F("✓ Dein aktives Modell ({0}) kann Plugins nutzen.", model),
            false => L.F("✗ Dein aktives Modell ({0}) kann keine Plugins nutzen – wähle in den Einstellungen ein Modell mit 🔧.", model),
            _ => L.F("Ob dein aktives Modell ({0}) Plugins nutzen kann, meldet der Anbieter nicht – einfach ausprobieren.", model)
        };
        _pluginList.Clear();
        foreach (var m in _plugins.Plugins)
            _pluginList.Add(MakePluginCard(m));
        RenderCatalog();
        if (loadCatalog) _ = LoadCatalogAsync();
    }

    private NSView MakePluginCard(PluginManifest m)
    {
        var state = _plugins.State(m.Id);
        var panel = new Col(6);

        // Kopfzeile: Name, Version, Art – rechts der Schalter
        var toggle = Ui.Check(L.T("Aktiv"));
        toggle.ToolTip = L.T("Plugin für das Modell freigeben");
        toggle.State = state.Enabled ? NSCellStateValue.On : NSCellStateValue.Off;
        toggle.Activated += async (_, _) =>
        {
            state.Enabled = toggle.State == NSCellStateValue.On;
            _plugins.SaveStates();
            if (!state.Enabled) await _plugins.ResetAsync(m.Id);
            // Fehlt eine Pflicht-Einstellung (z. B. Ordner), gleich die Einstellungen öffnen
            if (state.Enabled && m.Settings.Any(s => s.Type == "folder" && _plugins.GetValue(m, s).Length == 0))
                _openPluginSettings.Add(m.Id);
            ShowPlugins();
            // Knopf „Senden“ unter den Antworten erscheint/verschwindet mit dem Webhook-Plugin
            if (!IsBusy && m.Tools.Any(t => t.Builtin == "webhook_post")) RenderChat();
        };
        var meta = $"   {m.Version} · {m.TypeLabel}" + (m.IsBuiltIn ? " · " + L.T("mitgeliefert") : "");
        panel.Add(Ui.Row(8, Ui.Label(m.Name, 15, bold: true), Ui.Label(meta, 12, Ui.Secondary), Ui.Spacer(), toggle));
        panel.Add(Ui.Body(m.Description));

        var info = new Rich(12, Ui.Secondary).Text(m.AccessSummary());
        if (m.Type != "mcp")
            info.Text("   ·   " + L.T("Aktionen:") + " " + string.Join(", ", m.Tools.Select(t => t.Name)));
        if (_plugins.AskBeforeRun(m))
            info.Text("   ·   " + L.T("fragt vor jeder Aktion nach"));
        if (m.Homepage is { } homepage && Uri.TryCreate(homepage, UriKind.Absolute, out _))
            info.Text("   ·   ").Link(L.T("Website"), homepage);
        panel.Add(info.View());

        // Knöpfe: Einstellungen auf/zu, Entfernen
        var open = _openPluginSettings.Contains(m.Id);
        var settings = Ui.Button(open ? L.T("Einstellungen ausblenden") : "⚙ " + L.T("Einstellungen"), () =>
        {
            if (!_openPluginSettings.Remove(m.Id)) _openPluginSettings.Add(m.Id);
            ShowPlugins();
        });
        var buttons = new List<NSView> { settings };
        if (m.FilePath != null)
        {
            var remove = Ui.Button(L.T(m.IsBuiltIn ? "Update entfernen" : "Entfernen"), async () =>
            {
                if (!Ui.Confirm(L.F("Plugin „{0}“ entfernen?", m.Name), L.T("Plugin entfernen"))) return;
                await _plugins.RemoveAsync(m);
                ShowPlugins();
            });
            remove.ToolTip = L.T(m.IsBuiltIn ? "Zurück zur mitgelieferten Version" : "Plugin löschen (Einstellungen gehen verloren)");
            buttons.Add(remove);
        }
        buttons.Add(Ui.Spacer());
        panel.Add(Ui.Row(8, buttons.ToArray()));

        if (open)
            panel.Add(MakePluginSettings(m));
        return Ui.Card(panel);
    }

    // Einstellungsseite eines Plugins: Felder aus dem Manifest, dazu Rückfrage und (bei MCP) Verbindungstest
    private NSView MakePluginSettings(PluginManifest m)
    {
        var state = _plugins.State(m.Id);
        var panel = new Col(6);
        var status = Ui.Hint();

        async void Changed(PluginSetting s, string value)
        {
            _plugins.SetValue(m, s, value);
            await _plugins.ResetAsync(m.Id);
            status.StringValue = L.F("Gespeichert ({0:HH:mm:ss}).", DateTime.Now);
        }

        if (m.Settings.Count == 0)
            panel.Add(Ui.Hint(L.T("Dieses Plugin hat keine eigenen Einstellungen.")));

        foreach (var s in m.Settings)
        {
            panel.Add(Ui.Label(s.Label.Length > 0 ? s.Label : s.Key, bold: true));
            if (s.Description.Length > 0)
                panel.Add(Ui.Hint(s.Description));
            var value = _plugins.GetValue(m, s);
            switch (s.Type)
            {
                case "secret":
                {
                    var box = new SecretField { Value = value };
                    box.Changed += () => Changed(s, box.Value.Trim());
                    panel.Add(box);
                    break;
                }
                case "bool":
                {
                    NSButton? box = null;
                    box = Ui.Check(L.T("Ein"), () => Changed(s, box!.State == NSCellStateValue.On ? "true" : "false"));
                    box.State = value is "true" or "1" ? NSCellStateValue.On : NSCellStateValue.Off;
                    panel.Add(box, stretch: false);
                    break;
                }
                case "choice":
                {
                    NSPopUpButton? box = null;
                    box = Ui.Popup(() => { if (box!.IndexOfSelectedItem >= 0) Changed(s, s.Options[(int)box.IndexOfSelectedItem]); });
                    for (var i = 0; i < s.Options.Count; i++)
                        box.AddItem(i < s.OptionLabels.Count ? s.OptionLabels[i] : s.Options[i]);
                    box.SelectItem(Math.Max(0, s.Options.IndexOf(value)));
                    panel.Add(box, stretch: false);
                    break;
                }
                case "folder":
                {
                    var box = Ui.Field();
                    box.StringValue = value;
                    box.Changed += (_, _) => Changed(s, box.StringValue.Trim());
                    var pick = Ui.Button(L.T("Ordner wählen …"), () =>
                    {
                        if (Ui.FolderDialog(s.Label, box.StringValue) is { } folder)
                        {
                            box.StringValue = folder;
                            Changed(s, folder);
                        }
                    });
                    panel.Add(Ui.Row(8, box, pick));
                    break;
                }
                default:
                {
                    var box = Ui.Field();
                    box.StringValue = value;
                    box.Changed += (_, _) => Changed(s, box.StringValue.Trim());
                    panel.Add(box);
                    break;
                }
            }
        }

        NSButton? ask = null;
        ask = Ui.Check(L.T("Vor jeder Aktion dieses Plugins nachfragen"), () =>
        {
            state.AskBeforeRun = ask!.State == NSCellStateValue.On;
            _plugins.SaveStates();
            status.StringValue = L.F("Gespeichert ({0:HH:mm:ss}).", DateTime.Now);
        });
        ask.State = _plugins.AskBeforeRun(m) ? NSCellStateValue.On : NSCellStateValue.Off;
        panel.Add(ask);

        if (m.Type == "mcp")
        {
            var test = Ui.Button(L.T("Verbindung testen"), async () =>
            {
                status.StringValue = L.T("Verbinde …");
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    await _plugins.ResetAsync(m.Id);
                    var names = await _plugins.ToolNamesAsync(m, cts.Token);
                    status.StringValue = L.F("✓ Verbunden – {0} Aktionen: {1}", names.Count, string.Join(", ", names));
                }
                catch (Exception ex)
                {
                    status.StringValue = L.T("✗ Keine Verbindung:") + " " + ex.Message;
                }
            });
            panel.Add(test, stretch: false);
        }
        panel.Add(status);
        return Ui.Pad(panel, 8, 0, 0, 0);
    }

    // ---------- Katalog und Import ----------

    private async Task LoadCatalogAsync()
    {
        _catalogStatus.StringValue = L.T("Lade Katalog …");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            _catalog = await _plugins.LoadCatalogAsync(cts.Token);
            _catalogStatus.StringValue = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            _catalogStatus.StringValue = L.T("Der Katalog ist gerade nicht erreichbar (keine Internetverbindung?). Import aus Datei geht trotzdem.");
        }
        RenderCatalog();
    }

    private void RenderCatalog()
    {
        _catalogList.Clear();
        foreach (var entry in _catalog ?? [])
        {
            var installed = _plugins.Plugins.FirstOrDefault(p => p.Id == entry.Id);
            var button = Ui.Button(L.T("Installieren"), async () => await InstallPluginAsync(ct => PluginManager.DownloadAsync(entry.Url, ct)));
            if (installed != null && PluginManager.CompareVersions(entry.Version, installed.Version) > 0)
                button.Title = L.F("Aktualisieren ({0})", entry.Version);
            else if (installed != null)
            {
                button.Title = "✓ " + L.T("Installiert");
                button.Enabled = false;
            }
            var type = entry.Type switch { "mcp" => "MCP", "builtin" => L.T("eingebaut"), _ => "Web" };
            var text = new Col(2);
            text.Add(Ui.Row(6, Ui.Label(entry.Name, 13, bold: true), Ui.Label($"{entry.Version} · {type}", 12, Ui.Secondary), Ui.Spacer()));
            text.Add(Ui.Hint(entry.Description));
            _catalogList.Add(Ui.Row(10, text, button));
        }
    }

    private async void ImportPluginFile()
    {
        var files = Ui.OpenDialog(L.T("Plugin importieren"), ["json"], multiple: false);
        if (files.Length == 1)
            await InstallPluginAsync(_ => Task.FromResult(PluginManager.ReadFile(files[0])));
    }

    private async void ImportPluginUrl()
    {
        var url = _pluginUrlBox.StringValue.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
        {
            _catalogStatus.StringValue = L.T("Bitte eine vollständige https-Adresse einer Plugin-Datei eingeben.");
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
            _catalogStatus.StringValue = L.T("Lade Plugin …");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            m = await load(cts.Token);
            _catalogStatus.StringValue = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or IOException)
        {
            _catalogStatus.StringValue = L.T("Das Plugin konnte nicht geladen werden:") + " " + ex.Message;
            return;
        }

        var warning = m.Type == "mcp"
            ? "\n\n" + L.T("⚠ MCP-Plugin: Es verbindet sich mit einem MCP-Server bzw. startet ein Programm auf diesem Rechner. " +
                           "Was es darf, bestimmt dieser Server. Vor jeder Aktion wird nachgefragt.")
            : "";
        var tools = m.Type == "mcp" ? "" : "\n" + L.T("Aktionen:") + " " + string.Join(", ", m.Tools.Select(t => t.Name));
        if (!Ui.Confirm(L.F("Plugin „{0}“ {1} von {2} installieren?", m.Name, m.Version, m.Author.Length > 0 ? m.Author : L.T("unbekannt")) +
                        $"\n\n{m.Description}\n\n{m.AccessSummary()}{tools}{warning}\n\n" + L.T("Nur installieren, wenn du der Quelle vertraust."),
                L.T("Plugin installieren")))
            return;

        await _plugins.InstallAsync(m);
        var state = _plugins.State(m.Id);
        state.Enabled = true;
        _plugins.SaveStates();
        if (m.Settings.Count > 0) _openPluginSettings.Add(m.Id);
        _catalogStatus.StringValue = L.F("✓ „{0}“ installiert und eingeschaltet.", m.Name) + (m.Settings.Count > 0 ? " " + L.T("Bitte die Einstellungen prüfen.") : "");
        ShowPlugins();
    }
}
