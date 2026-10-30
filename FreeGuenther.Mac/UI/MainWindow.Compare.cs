using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace LmChat;

// Seite „Modelle vergleichen“: dieselbe Frage gleichzeitig an zwei Modelle, Antworten nebeneinander mit Dauer, Tokens und Kosten
internal sealed partial class MainWindow
{
    private sealed class CompareSide
    {
        public required NSPopUpButton Providers { get; init; }
        public required NSPopUpButton Models { get; init; }
        public required Col Box { get; init; }
        public required WrapLabel Stats { get; init; }
        public required NSView KeyPanel { get; init; }        // erscheint, wenn für den Anbieter kein Schlüssel hinterlegt ist
        public required Col KeyHint { get; init; }
        public required SecretField KeyBox { get; init; }
        public List<ModelInfo> ModelList { get; } = new();
        public string? Wanted { get; set; }                   // gewünschtes Modell, bis die Liste geladen ist
        public int LoadId { get; set; }

        public ProviderInfo Provider => ProviderInfo.All[Math.Max(0, (int)Providers.IndexOfSelectedItem)];
        public string? Model => Models.IndexOfSelectedItem is var i && i >= 0 && i < ModelList.Count ? ModelList[(int)i].Id : null;
    }

    private CompareSide _left = null!, _right = null!;
    private CancellationTokenSource? _compareCts;
    private bool _compareReady;
    private NSTextField _compareInput = null!, _compareStatus = null!;
    private NSButton _compareSend = null!, _compareStop = null!;

    private NSView BuildComparePage()
    {
        var page = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        var head = new Col(6);
        head.Add(Ui.PageTitle(L.UI("ui.ModelleVergleichen")));
        head.Add(Ui.Hint(L.UI("ui.DieselbeFrageGehtGleichzeitigAn")));
        page.AddSubview(head);
        Ui.Pin(head, page, 40, 32, null, 32);

        (NSView View, CompareSide Side) Column(bool left)
        {
            CompareSide? side = null;
            var providers = Ui.Popup(async () => await LoadCompareModelsAsync(side!, side!.Provider, _settings.Config(side.Provider.Kind).Model));
            foreach (var info in ProviderInfo.All)
                providers.AddItem(info.Label);
            var models = Ui.Popup();

            var keyBox = new SecretField();
            var keyHint = new Col();
            var keyColumn = new Col(6);
            keyColumn.Add(keyHint);
            keyColumn.Add(keyBox);
            var keySave = Ui.Button(L.UI("ui.Speichern"), () => SaveCompareKey(side!));
            keyColumn.Add(keySave, stretch: false);
            var keyPanel = Ui.Card(keyColumn, Ui.WarningFill, Ui.Warning.ColorWithAlphaComponent(0.4f), 10);
            keyPanel.Hidden = true;

            var (scroll, document) = Ui.Scroller();
            var box = new Col(0);
            document.AddSubview(box);
            Ui.Pin(box, document, 8, 8, 8, 8);
            var stats = new WrapLabel("", 11.5f, Ui.Secondary);

            var column = new Col(6);
            column.Add(providers);
            column.Add(models);
            column.Add(keyPanel);
            column.Add(Ui.Card(scroll, NSColor.ControlBackground, NSColor.Separator, 0, 8));
            column.Add(stats);
            scroll.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
            side = new CompareSide
            {
                Providers = providers, Models = models, Box = box, Stats = stats, KeyPanel = keyPanel, KeyHint = keyHint, KeyBox = keyBox
            };
            return (column, side);
        }

        var (leftView, left) = Column(true);
        var (rightView, right) = Column(false);
        (_left, _right) = (left, right);
        var columns = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal, Alignment = NSLayoutAttribute.Top,
            Distribution = NSStackViewDistribution.FillEqually, Spacing = 14, TranslatesAutoresizingMaskIntoConstraints = false
        };
        columns.AddArrangedSubview(leftView);
        columns.AddArrangedSubview(rightView);
        leftView.HeightAnchor.ConstraintEqualTo(columns.HeightAnchor).Active = true;
        rightView.HeightAnchor.ConstraintEqualTo(columns.HeightAnchor).Active = true;
        page.AddSubview(columns);
        columns.TopAnchor.ConstraintEqualTo(head.BottomAnchor, 12).Active = true;
        Ui.Pin(columns, page, null, 32, null, 32);

        _compareInput = Ui.Field(L.UI("ui.FrageAnBeideModelle"));
        _compareInput.Activated += (_, _) => CompareSend();
        _compareSend = Ui.Button(L.UI("ui.AnBeideSenden"), CompareSend, primary: true);
        _compareSend.ToolTip = L.UI("ui.AnBeideModelleSendenEnter");
        _compareStop = Ui.Button(L.UI("ui.Abbrechen"), () => _compareCts?.Cancel());
        _compareStop.ToolTip = L.UI("ui.VergleichAbbrechen");
        _compareStop.Hidden = true;
        _compareStatus = Ui.Label("", 12, Ui.Secondary);
        var foot = new Col(6);
        foot.Add(Ui.Row(8, _compareInput, _compareStop, _compareSend));
        foot.Add(_compareStatus);
        page.AddSubview(foot);
        foot.TopAnchor.ConstraintEqualTo(columns.BottomAnchor, 10).Active = true;
        Ui.Pin(foot, page, null, 32, 14, 32);
        return page;
    }

    private void ShowCompare()
    {
        if (!_compareReady)
        {
            _compareReady = true;
            var current = new CompareSlot(_settings.Provider, _settings.Current.Model);
            foreach (var (side, slot) in new[] { (_left, _settings.CompareLeft ?? current), (_right, _settings.CompareRight ?? current) })
            {
                side.Providers.SelectItem(Array.FindIndex(ProviderInfo.All, p => p.Kind == slot.Provider));
                _ = LoadCompareModelsAsync(side, side.Provider, slot.Model);
            }
        }
        _window.MakeFirstResponder(_compareInput);
    }

    private bool HasKey(ProviderInfo info) => !info.NeedsKey || _settings.Config(info.Kind).ApiKey.Length > 0;

    // Fehlt der Schlüssel, steht über der Antwort ein Feld zum Eintragen (statt erst beim Senden einen Fehler zu bekommen)
    private void UpdateCompareKeyPanel(CompareSide side, ProviderInfo info)
    {
        side.KeyPanel.Hidden = HasKey(info);
        side.KeyHint.Clear();
        if (HasKey(info)) return;
        var text = new Rich(12).Bold(L.F("Für {0} ist noch kein API-Schlüssel hinterlegt.", info.Name) + " ")
            .Text(L.T("Hier eintragen – er wird verschlüsselt gespeichert, der aktive Anbieter im Chat bleibt gleich.") + " ");
        if (info.KeyUrl != null)
            text.Link(L.T("Schlüssel anlegen"), info.KeyUrl);
        if (info.Links.FirstOrDefault(l => l.Item1.Contains("Limit")).Item2 is { } limitUrl)
            text.Text(" · ").Link(L.T("⚠ Ausgabenlimit setzen"), limitUrl);
        side.KeyHint.Add(text.View());
    }

    private async void SaveCompareKey(CompareSide side)
    {
        var info = side.Provider;
        var key = side.KeyBox.Value.Trim();
        if (key.Length == 0)
        {
            _compareStatus.StringValue = L.T("Bitte zuerst den API-Schlüssel einfügen.");
            return;
        }
        // Nur den Schlüssel speichern – auch im Entwurf und im Feld der Einstellungsseite, damit „Speichern“ dort ihn nicht löscht
        _settings.Config(info.Kind).ApiKey = key;
        _draft.Config(info.Kind).ApiKey = key;
        if (_shownProvider == info.Kind)
        {
            _apiKeyBox.Reveal(false);
            _apiKeyBox.Value = key;
        }
        _settings.Save(_store);
        UpdateProviderRows();
        side.KeyBox.Reveal(false);
        side.KeyBox.Value = "";
        _compareStatus.StringValue = L.F("API-Schlüssel für {0} gespeichert.", info.Name);
        foreach (var other in new[] { _left, _right })
            if (other.Provider.Kind == info.Kind)
                await LoadCompareModelsAsync(other, info, other.Model ?? _settings.Config(info.Kind).Model);
    }

    private async Task LoadCompareModelsAsync(CompareSide side, ProviderInfo info, string wanted)
    {
        var config = _settings.Config(info.Kind);
        side.ModelList.Clear();
        side.Models.RemoveAllItems();
        UpdateCompareKeyPanel(side, info);
        // Die OpenRouter-Modellliste ist öffentlich, die anderen Online-Dienste brauchen dafür den Schlüssel
        if (!HasKey(info) && info.Kind != ProviderKind.OpenRouter)
        {
            side.Stats.StringValue = "";
            return;
        }

        var loadId = ++side.LoadId;
        side.Stats.StringValue = L.F("Lade Modelle von {0} …", info.Name);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(RegionOption.IsHosterFilter(config.Region) ? 180 : 30));
            var models = await LlmClient.ListModelsAsync(info, config.Server, config.Region, config.ApiKey, cts.Token);
            if (loadId != side.LoadId) return;
            side.ModelList.AddRange(models.Where(m => !m.ImageGen));
            side.Stats.StringValue = L.F("{0} Modelle von {1}.", side.ModelList.Count, info.Name);
        }
        catch (Exception ex)
        {
            if (loadId != side.LoadId) return;
            side.Stats.StringValue = L.F("Modelle von {0} konnten nicht geladen werden: {1}", info.Name, ex.Message);
        }
        FillModels(side.Models, side.ModelList, wanted);
    }

    // Modelle ins Popup; das gewünschte vorwählen, sonst das erste
    private static void FillModels(NSPopUpButton popup, List<ModelInfo> models, string wanted)
    {
        popup.RemoveAllItems();
        foreach (var model in models)
            popup.Menu!.AddItem(new NSMenuItem(model.Label));
        var index = models.FindIndex(m => m.Id == wanted);
        if (models.Count > 0) popup.SelectItem(Math.Max(0, index));
    }

    private async void CompareSend()
    {
        var question = _compareInput.StringValue.Trim();
        if (question.Length == 0 || _compareCts != null) return;
        var picks = new List<(CompareSide Side, ProviderInfo Provider, string Model)>();
        foreach (var side in new[] { _left, _right })
        {
            if (side.Model is not { Length: > 0 } model)
            {
                _compareStatus.StringValue = L.T("Bitte links und rechts jeweils Anbieter und Modell wählen.");
                return;
            }
            if (!HasKey(side.Provider))
            {
                _compareStatus.StringValue = L.F("Für {0} fehlt der API-Schlüssel – bitte im gelben Feld eintragen.", side.Provider.Name);
                side.KeyBox.Focus();
                return;
            }
            picks.Add((side, side.Provider, model));
        }

        var (allowed, budgetWarning) = await CheckBudgetAsync(picks.Select(p => p.Provider));
        if (!allowed)
        {
            _compareStatus.StringValue = L.T("Nicht gesendet – die Kostenbremse ist erreicht (siehe „Verbrauch“).");
            return;
        }

        // Auswahl merken (auch im Entwurf der Einstellungsseite, damit „Speichern“ dort sie nicht überschreibt)
        foreach (var s in new[] { _settings, _draft })
        {
            s.CompareLeft = new CompareSlot(picks[0].Provider.Kind, picks[0].Model);
            s.CompareRight = new CompareSlot(picks[1].Provider.Kind, picks[1].Model);
        }
        _settings.Save(_store);

        _compareCts = new CancellationTokenSource();
        _compareSend.Enabled = false;
        _compareStop.Hidden = false;
        _compareStatus.StringValue = L.T("Beide Modelle arbeiten …");
        try
        {
            await Task.WhenAll(picks.Select(p => CompareOneAsync(p.Side, p.Provider, p.Model, question, _compareCts.Token)));
            _compareStatus.StringValue = L.T("Fertig.") + (budgetWarning != null ? " " + budgetWarning : "");
        }
        finally
        {
            _compareCts.Dispose();
            _compareCts = null;
            _compareSend.Enabled = true;
            _compareStop.Hidden = true;
            _window.MakeFirstResponder(_compareInput);
        }
    }

    // Eine Seite: streamen, danach als Markdown darstellen und Statistik anzeigen. Fehler bleiben auf dieser Seite.
    private async Task CompareOneAsync(CompareSide side, ProviderInfo provider, string model, string question, CancellationToken ct)
    {
        var config = _settings.Config(provider.Kind);
        var blocks = side.Box;
        blocks.Clear();
        blocks.Add(new WrapLabel(question, 14, bold: true));
        var status = new WrapLabel("⏳ " + L.T("Anfrage gesendet …"), 13, Ui.Secondary);
        blocks.Add(status);
        var stream = new TextBlockView();
        blocks.Add(stream);
        blocks.Gap(blocks.ArrangedSubviews[0], 10);
        side.Stats.StringValue = "";

        var request = new ChatRequest(provider, config.Server, config.Region, config.ApiKey, model,
            _settings.SystemPrompt, _settings, [new ChatMessage("user", question)]);
        var answer = new StringBuilder();
        var result = new LlmClient.StreamResult();
        var clock = Stopwatch.StartNew();
        double? firstChunkAt = null;
        string? error = null;
        var attributes = new NSStringAttributes { Font = NSFont.SystemFontOfSize(15), ForegroundColor = NSColor.Label };
        try
        {
            await LlmClient.StreamAsync(request, chunk => OnMain(() =>
            {
                firstChunkAt ??= clock.Elapsed.TotalSeconds;
                status.Hidden = true;
                answer.Append(chunk);
                stream.Append(new NSAttributedString(chunk, attributes));
            }), phase => OnMain(() =>
            {
                if (phase == LlmClient.Phase.Thinking)
                {
                    firstChunkAt ??= clock.Elapsed.TotalSeconds;
                    if (answer.Length == 0) status.StringValue = "💭 " + L.T("Das Modell denkt nach …");
                }
            }), result, null, ct);
        }
        catch (OperationCanceledException)
        {
            error = L.T("[abgebrochen]");
        }
        catch (Exception ex)
        {
            error = L.F("[Fehler: {0}]", ex.Message);
        }

        var seconds = clock.Elapsed.TotalSeconds;
        var text = Regex.Replace(answer.ToString(), @"^\s*<think>[\s\S]*?(</think>|$)", "").Trim();
        blocks.Clear();
        var heading = blocks.Add(new WrapLabel(question, 14, bold: true));
        blocks.Gap(heading, 10);
        if (text.Length > 0)
            MarkdownRenderer.Render(text, blocks);
        if (error != null)
            blocks.Add(new WrapLabel(error, 13, Ui.Secondary));

        double? tps = null;
        if (result.OutputTokens is > 0 && firstChunkAt is { } start)
            tps = result.OutputTokens.Value / (seconds - start > 0.2 ? seconds - start : seconds);
        var region = RegionOption.Name(config.Region);
        var stats = new AnswerStats(provider.Name + (region.Length > 0 ? $" ({region})" : ""), result.Model ?? model, result.Hoster,
            result.InputTokens, result.OutputTokens, tps, seconds, result.Cost);
        if (text.Length > 0)
            _store.AddUsage(provider.Kind, stats);

        var line = FormatStats(stats);
        // Ohne gemeldete Kosten: nach Listenpreis schätzen, damit beide Seiten vergleichbar sind
        if (stats.Cost == null && !provider.IsLocal && stats.InputTokens is { } input && stats.OutputTokens is { } output)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await PriceEstimate.LoadAsync(cts.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            {
            }
            if (PriceEstimate.Estimate(provider.Kind, stats.Model, input, output) is { } estimate)
                line += "   ·   " + L.F("≈ {0} geschätzt", FormatDollar(estimate));
        }
        else if (provider.IsLocal)
            line += "   ·   " + L.T("0 $ (lokal)");
        side.Stats.StringValue = line;
    }
}
