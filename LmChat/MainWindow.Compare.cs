using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace LmChat;

// Seite „Modelle vergleichen“: dieselbe Frage gleichzeitig an zwei Modelle, Antworten nebeneinander mit Dauer, Tokens und Kosten
public partial class MainWindow
{
    private sealed class CompareSide
    {
        public required ComboBox Providers { get; init; }
        public required ComboBox Models { get; init; }
        public required RichTextBox Box { get; init; }
        public required TextBlock Stats { get; init; }
        public required Border KeyPanel { get; init; }        // erscheint, wenn für den Anbieter kein Schlüssel hinterlegt ist
        public required TextBlock KeyHint { get; init; }
        public required PasswordBox KeyBox { get; init; }
        public ObservableCollection<ModelInfo> ModelList { get; } = new();
        public int LoadId { get; set; }
    }

    private CompareSide? _left, _right;
    private CancellationTokenSource? _compareCts;

    private void ShowCompare()
    {
        if (_left == null)
        {
            _left = new CompareSide
            {
                Providers = CompareProviderLeft, Models = CompareModelLeft, Box = CompareBoxLeft, Stats = CompareStatsLeft,
                KeyPanel = CompareKeyLeft, KeyHint = CompareKeyHintLeft, KeyBox = CompareKeyBoxLeft
            };
            _right = new CompareSide
            {
                Providers = CompareProviderRight, Models = CompareModelRight, Box = CompareBoxRight, Stats = CompareStatsRight,
                KeyPanel = CompareKeyRight, KeyHint = CompareKeyHintRight, KeyBox = CompareKeyBoxRight
            };
            var current = new CompareSlot(_settings.Provider, _settings.Current.Model);
            foreach (var (side, slot) in new[] { (_left, _settings.CompareLeft ?? current), (_right!, _settings.CompareRight ?? current) })
            {
                side.Models.ItemsSource = side.ModelList;
                side.Providers.ItemsSource = ProviderInfo.All;
                side.Models.Tag = slot.Model;   // gewünschtes Modell, bis die Liste geladen ist
                side.Providers.SelectedItem = ProviderInfo.Get(slot.Provider);
            }
        }
        CompareInput.Focus();
    }

    private CompareSide? SideOf(object sender) => sender == CompareProviderLeft ? _left : sender == CompareProviderRight ? _right : null;

    private async void CompareProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SideOf(sender) is not { } side || side.Providers.SelectedItem is not ProviderInfo info) return;
        var wanted = side.Models.Tag as string is { Length: > 0 } tagged && e.RemovedItems.Count == 0 ? tagged : _settings.Config(info.Kind).Model;
        await LoadCompareModelsAsync(side, info, wanted);
    }

    private bool HasKey(ProviderInfo info) => !info.NeedsKey || _settings.Config(info.Kind).ApiKey.Length > 0;

    // Fehlt der Schlüssel, steht über der Antwort ein Feld zum Eintragen (statt erst beim Senden einen Fehler zu bekommen)
    private void UpdateCompareKeyPanel(CompareSide side, ProviderInfo info)
    {
        side.KeyPanel.Visibility = HasKey(info) ? Visibility.Collapsed : Visibility.Visible;
        side.KeyHint.Inlines.Clear();
        if (HasKey(info)) return;
        side.KeyHint.Inlines.Add(new Run($"Für {info.Name} ist noch kein API-Schlüssel hinterlegt. ") { FontWeight = FontWeights.SemiBold });
        side.KeyHint.Inlines.Add(new Run("Hier eintragen – er wird verschlüsselt gespeichert, der aktive Anbieter im Chat bleibt gleich. "));
        if (info.KeyUrl != null)
            side.KeyHint.Inlines.Add(MakeLink("Schlüssel anlegen", info.KeyUrl));
        if (info.Links.FirstOrDefault(l => l.Item1.Contains("Limit")).Item2 is { } limitUrl)
        {
            side.KeyHint.Inlines.Add(new Run(" · "));
            side.KeyHint.Inlines.Add(MakeLink("⚠ Ausgabenlimit setzen", limitUrl));
        }
    }

    private async void SaveCompareKey_Click(object sender, RoutedEventArgs e)
    {
        var side = (sender as FrameworkElement)?.Tag as string == "left" ? _left : _right;
        if (side?.Providers.SelectedItem is not ProviderInfo info) return;
        var key = side.KeyBox.Password.Trim();
        if (key.Length == 0)
        {
            CompareStatus.Text = "Bitte zuerst den API-Schlüssel einfügen.";
            return;
        }
        // Nur den Schlüssel speichern – auch im Entwurf und im Feld der Einstellungsseite, damit „Speichern“ dort ihn nicht löscht
        _settings.Config(info.Kind).ApiKey = key;
        _draft.Config(info.Kind).ApiKey = key;
        if (_shownProvider == info.Kind)
            ApiKeyBox.Password = key;
        _settings.Save(_store);
        side.KeyBox.Clear();
        CompareStatus.Text = $"API-Schlüssel für {info.Name} gespeichert.";
        foreach (var other in new[] { _left, _right })
            if (other?.Providers.SelectedItem is ProviderInfo shown && shown.Kind == info.Kind)
                await LoadCompareModelsAsync(other, shown, other.Models.SelectedValue as string ?? _settings.Config(info.Kind).Model);
    }

    private async Task LoadCompareModelsAsync(CompareSide side, ProviderInfo info, string wanted)
    {
        var config = _settings.Config(info.Kind);
        side.ModelList.Clear();
        UpdateCompareKeyPanel(side, info);
        // Die OpenRouter-Modellliste ist öffentlich, die anderen Online-Dienste brauchen dafür den Schlüssel
        if (!HasKey(info) && info.Kind != ProviderKind.OpenRouter)
        {
            side.Stats.Text = "";
            return;
        }

        var loadId = ++side.LoadId;
        side.Stats.Text = $"Lade Modelle von {info.Name} …";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(RegionOption.IsHosterFilter(config.Region) ? 180 : 30));
            var models = await LlmClient.ListModelsAsync(info, config.Server, config.Region, config.ApiKey, cts.Token);
            if (loadId != side.LoadId) return;
            foreach (var m in models.Where(m => !m.ImageGen))
                side.ModelList.Add(m);
            side.Stats.Text = $"{side.ModelList.Count} Modelle von {info.Name}.";
        }
        catch (Exception ex)
        {
            if (loadId != side.LoadId) return;
            side.Stats.Text = $"Modelle von {info.Name} konnten nicht geladen werden: {ex.Message}";
        }
        side.Models.SelectedValue = side.ModelList.Any(m => m.Id == wanted) ? wanted : side.ModelList.FirstOrDefault()?.Id;
    }

    private void CompareInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers == ModifierKeys.Shift) return;
        e.Handled = true;
        CompareSend_Click(sender, e);
    }

    private void CompareStop_Click(object sender, RoutedEventArgs e) => _compareCts?.Cancel();

    private async void CompareSend_Click(object sender, RoutedEventArgs e)
    {
        var question = CompareInput.Text.Trim();
        if (question.Length == 0 || _compareCts != null || _left == null || _right == null) return;
        var picks = new List<(CompareSide Side, ProviderInfo Provider, string Model)>();
        foreach (var side in new[] { _left, _right })
        {
            if (side.Providers.SelectedItem is not ProviderInfo info || side.Models.SelectedValue is not string { Length: > 0 } model)
            {
                CompareStatus.Text = "Bitte links und rechts jeweils Anbieter und Modell wählen.";
                return;
            }
            if (!HasKey(info))
            {
                CompareStatus.Text = $"Für {info.Name} fehlt der API-Schlüssel – bitte im gelben Feld eintragen.";
                side.KeyBox.Focus();
                return;
            }
            picks.Add((side, info, model));
        }

        var (allowed, budgetWarning) = await CheckBudgetAsync(picks.Select(p => p.Provider));
        if (!allowed)
        {
            CompareStatus.Text = "Nicht gesendet – die Kostenbremse ist erreicht (siehe „Verbrauch“).";
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
        CompareSendButton.IsEnabled = false;
        CompareStopButton.Visibility = Visibility.Visible;
        CompareStatus.Text = "Beide Modelle arbeiten …";
        try
        {
            await Task.WhenAll(picks.Select(p => CompareOneAsync(p.Side, p.Provider, p.Model, question, _compareCts.Token)));
            CompareStatus.Text = "Fertig." + (budgetWarning != null ? " " + budgetWarning : "");
        }
        finally
        {
            _compareCts.Dispose();
            _compareCts = null;
            CompareSendButton.IsEnabled = true;
            CompareStopButton.Visibility = Visibility.Collapsed;
            CompareInput.Focus();
        }
    }

    // Eine Seite: streamen, danach als Markdown darstellen und Statistik anzeigen. Fehler bleiben auf dieser Seite.
    private async Task CompareOneAsync(CompareSide side, ProviderInfo provider, string model, string question, CancellationToken ct)
    {
        var config = _settings.Config(provider.Kind);
        var blocks = side.Box.Document.Blocks;
        blocks.Clear();
        blocks.Add(new Paragraph(new Run(question)) { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        var status = new Run("⏳ Anfrage gesendet …");
        var stream = new Run();
        var live = new Paragraph { Margin = new Thickness(0) };
        live.Inlines.Add(status);
        live.Inlines.Add(stream);
        blocks.Add(live);
        side.Stats.Text = "";

        var request = new ChatRequest(provider, config.Server, config.Region, config.ApiKey, model,
            _settings.SystemPrompt, _settings, [new ChatMessage("user", question)]);
        var answer = new StringBuilder();
        var result = new LlmClient.StreamResult();
        var clock = Stopwatch.StartNew();
        double? firstChunkAt = null;
        string? error = null;
        try
        {
            await LlmClient.StreamAsync(request, chunk =>
            {
                firstChunkAt ??= clock.Elapsed.TotalSeconds;
                status.Text = "";
                answer.Append(chunk);
                stream.Text += chunk;
            }, phase =>
            {
                if (phase == LlmClient.Phase.Thinking)
                {
                    firstChunkAt ??= clock.Elapsed.TotalSeconds;
                    if (answer.Length == 0) status.Text = "💭 Das Modell denkt nach …";
                }
            }, result, null, ct);
        }
        catch (OperationCanceledException)
        {
            error = "[abgebrochen]";
        }
        catch (Exception ex)
        {
            error = $"[Fehler: {ex.Message}]";
        }

        var seconds = clock.Elapsed.TotalSeconds;
        var text = System.Text.RegularExpressions.Regex.Replace(answer.ToString(), @"^\s*<think>[\s\S]*?(</think>|$)", "").Trim();
        blocks.Remove(live);
        if (text.Length > 0)
            MarkdownRenderer.Render(text, blocks);
        if (error != null)
            blocks.Add(new Paragraph(new Italic(new Run(error))) { Margin = new Thickness(0) });

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
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            {
            }
            if (PriceEstimate.Estimate(provider.Kind, stats.Model, input, output) is { } estimate)
                line += $"   ·   ≈ {FormatDollar(estimate)} geschätzt";
        }
        else if (provider.IsLocal)
            line += "   ·   0 $ (lokal)";
        side.Stats.Text = line;
    }
}
