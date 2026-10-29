using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LmChat;

public partial class MainWindow : Window
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"];
    private const int MaxImageSide = 2048;

    private readonly ChatStore _store = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LmChat", "chats.db"));
    private readonly ObservableCollection<ChatInfo> _chats = new();
    private readonly ObservableCollection<ModelInfo> _models = new();
    private readonly List<ChatMessage> _messages = new();

    // _settings = gespeicherter, aktiver Stand; _draft = was gerade auf der Einstellungsseite bearbeitet wird
    private LlmSettings _settings;
    private LlmSettings _draft;
    private ProviderKind? _shownProvider;
    private int _modelLoadId;

    private ChatInfo? _currentChat;
    private byte[]? _pendingImage;
    private CancellationTokenSource? _cts;
    private bool _suppressSelection;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"LM Chat {AppVersion}";
        VersionRun.Text = $"Version {AppVersion}.";
        SidebarVersion.Text = $"Version {AppVersion}";
        _settings = LlmSettings.Load(_store);
        _draft = _settings.Clone();
        foreach (var chat in _store.ListChats())
            _chats.Add(chat);

        // Chatliste: nach Datum gruppiert und über das Suchfeld filterbar
        var chatView = System.Windows.Data.CollectionViewSource.GetDefaultView(_chats);
        chatView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ChatInfo.DateGroup)));
        chatView.Filter = item => item is ChatInfo c &&
            c.Title.Contains(ChatSearchBox.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);
        _chats.CollectionChanged += (_, _) => UpdateTiles();
        ChatList.ItemsSource = _chats;
        ProviderCombo.ItemsSource = ProviderInfo.All;
        RegionCombo.ItemsSource = RegionOption.All;
        ModelCombo.ItemsSource = _models;
        System.Windows.Data.CollectionViewSource.GetDefaultView(_models).Filter = ModelMatchesFilter;
        foreach (var row in ParamRows)
            ParamsPanel.Children.Add(row.Element);
        ShowSettings();
        // Beim Befüllen der Einstellungen kann ein Element dort den Fokus bekommen – immer im Chat starten
        Loaded += (_, _) =>
        {
            NavChat.IsChecked = true;
            MainTabs.SelectedIndex = 0;
            SettingsScroll.ScrollToTop();
            UpdateTiles();
            UpdateEmptyState();
            InputBox.Focus();
        };
    }

    // ---------- Seitenleiste ----------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (MainTabs == null) return;
        MainTabs.SelectedIndex = sender == NavSettings ? 1 : sender == NavUsage ? 2 : sender == NavHelp ? 3 : 0;
        if (sender == NavChat) InputBox.Focus();
        if (sender == NavUsage) _ = ShowUsageAsync();
    }

    // ---------- Verbrauch ----------

    private sealed record UsageLine(string Provider, string Model, string Requests, string InputTokens, string OutputTokens, string Cost);

    private void UsagePeriod_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) _ = ShowUsageAsync();
    }

    private async Task ShowUsageAsync()
    {
        var now = DateTime.Now;
        DateTime? since = UsagePeriodCombo.SelectedIndex switch
        {
            0 => now.Date,
            1 => now.Date.AddDays(-6),
            2 => new DateTime(now.Year, now.Month, 1),
            3 => now.Date.AddDays(-29),
            _ => null
        };
        var rows = _store.UsageByModel(since);

        // Listenpreise für OpenAI/Anthropic nur laden, wenn sie gebraucht werden
        var priceNote = "";
        if (rows.Any(r => !ProviderInfo.Get(r.Provider).IsLocal && r.CostCount < r.Requests))
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await PriceEstimate.LoadAsync(cts.Token);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            {
                priceNote = "Preisliste nicht erreichbar – Kosten ohne Angabe des Anbieters sind unbekannt.";
            }
        }

        double total = 0;
        var anyEstimate = false;
        var anyUnknown = false;
        var lines = rows.Select(r =>
        {
            var info = ProviderInfo.Get(r.Provider);
            string cost;
            if (info.IsLocal)
                cost = "0 $ (lokal)";
            else if (r.CostCount == r.Requests)
            {
                total += r.Cost;
                cost = FormatDollar(r.Cost) + " gemeldet";
            }
            // Ganz oder teilweise ohne gemeldete Kosten: alles nach Listenpreis schätzen
            else if (PriceEstimate.Estimate(r.Provider, r.Model, r.InputTokens, r.OutputTokens) is { } estimate)
            {
                total += estimate;
                anyEstimate = true;
                cost = "≈ " + FormatDollar(estimate) + " geschätzt";
            }
            else if (r.CostCount > 0)
            {
                total += r.Cost;
                anyUnknown = true;
                cost = "mind. " + FormatDollar(r.Cost);
            }
            else
            {
                anyUnknown = true;
                cost = "unbekannt";
            }
            return new UsageLine(info.Name, r.Model, r.Requests.ToString("N0"), r.InputTokens.ToString("N0"),
                r.OutputTokens.ToString("N0"), cost);
        }).ToList();

        UsageList.ItemsSource = lines;
        UsageList.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UsageEmpty.Visibility = lines.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        UsageRequestsTile.Text = rows.Sum(r => r.Requests).ToString("N0");
        UsageTokensTile.Text = $"{rows.Sum(r => r.InputTokens):N0} / {rows.Sum(r => r.OutputTokens):N0}";
        UsageCostTile.Text = "≈ " + FormatDollar(total) + (anyUnknown ? " +" : "");
        UsageStatus.Text = priceNote.Length > 0 ? priceNote
            : anyUnknown ? "„+“: Für manche Antworten ist kein Preis bekannt, die Summe ist also eher höher."
            : anyEstimate ? "Enthält geschätzte Beträge." : "";
    }

    private static string FormatDollar(double d) =>
        d == 0 ? "0 $" : d < 0.0001 ? $"< {0.0001:0.0000} $" : d < 0.01 ? $"{d:0.0000} $" : $"{d:0.00} $";

    private void ClearUsage_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Alle mitgezählten Tokens und Kosten löschen? Die Chats bleiben erhalten.",
                "Zähler zurücksetzen", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _store.ClearUsage();
        _ = ShowUsageAsync();
    }

    private void ModelCard_Click(object sender, MouseButtonEventArgs e) => NavSettings.IsChecked = true;

    private void ShowHelp_Click(object sender, RoutedEventArgs e) => NavHelp.IsChecked = true;

    private void ChatSearch_TextChanged(object sender, TextChangedEventArgs e) =>
        System.Windows.Data.CollectionViewSource.GetDefaultView(_chats)?.Refresh();

    private void UpdateTiles()
    {
        TileChats.Text = _chats.Count.ToString();
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = ChatBox.Document.Blocks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChatTitleText.Text = _currentChat?.Title ?? "Neuer Chat";
        DeleteChatButton.Visibility = _currentChat == null ? Visibility.Hidden : Visibility.Visible;
    }

    private ProviderInfo ActiveProvider => ProviderInfo.Get(_settings.Provider);
    private bool? CurrentVision => _settings.Current.ModelVision;
    private bool CurrentImageGen => _settings.Current.ModelImageGen;

    // ---------- Chat senden ----------

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            Send_Click(sender, e);
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && TryPasteImage())
        {
            e.Handled = true;
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var text = InputBox.Text.Trim();
        var image = _pendingImage;
        if ((text.Length == 0 && image == null) || _cts != null) return;

        var provider = ActiveProvider;
        var config = _settings.Current;
        if (config.Model.Length == 0)
        {
            StatusText.Text = "Bitte zuerst unter „Einstellungen“ ein Modell auswählen und speichern.";
            return;
        }
        if (provider.NeedsKey && config.ApiKey.Length == 0)
        {
            StatusText.Text = $"Für {provider.Name} fehlt der API-Schlüssel – bitte unter „Einstellungen“ eintragen.";
            return;
        }
        if (image != null && CurrentVision == false)
        {
            StatusText.Text = "Das gewählte Modell kann keine Bilder verarbeiten – Bild entfernen oder Modell wechseln.";
            return;
        }
        var imageMode = CurrentImageGen;
        if (imageMode && text.Length == 0)
        {
            StatusText.Text = "Beschreibe in Worten, welches Bild erzeugt bzw. wie das Bild verändert werden soll.";
            return;
        }

        if (_currentChat == null)
        {
            _currentChat = _store.CreateChat(MakeTitle(text));
            _chats.Insert(0, _currentChat);
            SelectChatSilently(_currentChat);
        }
        var chat = _currentChat;

        var userMsg = new ChatMessage("user", text, image);
        userMsg = userMsg with { Id = _store.AddMessage(chat.Id, userMsg) };
        _messages.Add(userMsg);
        InputBox.Clear();
        ClearImage();
        AddMessageBlock(userMsg);
        var answerView = AddMessageBlock(new ChatMessage("assistant", ""), streaming: true);

        List<ChatMessage> toSend = HistoryCheck.IsChecked == true ? _messages.ToList() : [userMsg];
        var request = new ChatRequest(provider, config.Server, config.Region, config.ApiKey, config.Model,
            _settings.SystemPrompt, _settings, toSend);

        _cts = new CancellationTokenSource();
        SetBusy(true);
        var answer = new StringBuilder();
        string? note = null;
        var result = new LlmClient.StreamResult();
        var clock = Stopwatch.StartNew();
        double? firstChunkAt = null;
        byte[]? generated = null;
        double? cost = null;

        // Statuszeile über der Antwort: was gerade passiert und seit wann – verschwindet, sobald Text kommt
        var phase = $"Anfrage an {provider.Name} wird vorbereitet";
        var phaseRun = new Run();
        var phaseLine = new Paragraph(phaseRun) { Margin = new Thickness(0), FontSize = 13 };
        phaseLine.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
        answerView.Body.Blocks.InsertBefore(answerView.Body.Blocks.FirstBlock, phaseLine);
        var ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        void ShowPhase()
        {
            var seconds = (int)clock.Elapsed.TotalSeconds;
            var hint = seconds < 30 ? ""
                : provider.IsLocal && !imageMode ? " – dauert länger, evtl. wird das Modell erst geladen"
                : " – dauert länger als üblich, abbrechen geht jederzeit mit ■";
            phaseRun.Text = $"⏳ {phase} … {seconds} s{hint}";
            StatusText.Text = phase + " …";
        }
        void SetPhase(string text) { phase = text; ShowPhase(); }
        void HidePhase()
        {
            if (!ticker.IsEnabled) return;
            ticker.Stop();
            answerView.Body.Blocks.Remove(phaseLine);
            StatusText.Text = "Antwort wird empfangen …";
        }
        ticker.Tick += (_, _) => ShowPhase();
        ticker.Start();
        ShowPhase();

        try
        {
            if (imageMode)
            {
                // Bildmodell: Nachricht = Bildbeschreibung. Vorlage = angehängtes Bild, sonst (mit Verlauf) das zuletzt erzeugte
                var reference = image ?? (HistoryCheck.IsChecked == true
                    ? _messages.LastOrDefault(m => m.Role == "assistant" && m.Image != null)?.Image
                    : null);
                SetPhase(reference != null
                    ? $"🎨 {provider.Name} bearbeitet das Bild – das kann eine Weile dauern"
                    : $"🎨 {provider.Name} erzeugt das Bild – das kann eine Weile dauern");
                var img = await LlmClient.GenerateImageAsync(request, text, reference, _cts.Token);
                generated = ToPng(img.Image);
                result.InputTokens = img.InputTokens;
                result.OutputTokens = img.OutputTokens;
                cost = img.Cost;
            }
            else
            {
                await LlmClient.StreamAsync(request, chunk =>
                {
                    HidePhase();
                    firstChunkAt ??= clock.Elapsed.TotalSeconds;
                    answer.Append(chunk);
                    answerView.Stream!.Text += chunk;
                    ChatBox.ScrollToEnd();
                }, p =>
                {
                    // Denk-Tokens zählen zur Ausgabe – ab hier wird für Token/s gemessen
                    if (p == LlmClient.Phase.Thinking) firstChunkAt ??= clock.Elapsed.TotalSeconds;
                    if (!ticker.IsEnabled) return;
                    SetPhase(p switch
                    {
                        LlmClient.Phase.FindingHosters => $"Suche Hoster mit Standort {RegionOption.Name(config.Region)}",
                        LlmClient.Phase.Sending => $"Anfrage an {provider.Name} gesendet, warte auf Antwort",
                        LlmClient.Phase.Connected => $"{provider.Name} hat die Anfrage angenommen, warte auf die ersten Wörter",
                        _ => "💭 Das Modell denkt nach"
                    });
                }, result, _cts.Token);
            }
            ticker.Stop();
            StatusText.Text = "Fertig.";
        }
        catch (OperationCanceledException)
        {
            note = "[abgebrochen]";
            StatusText.Text = "Abgebrochen.";
        }
        catch (Exception ex)
        {
            note = $"[Fehler: {ex.Message}]";
            StatusText.Text = "Fehler – Anbieter, Schlüssel und Modell in den Einstellungen prüfen.";
        }
        finally
        {
            ticker.Stop();
            var stats = MakeStats(result, config.Model, clock.Elapsed.TotalSeconds, firstChunkAt, cost ?? result.Cost);
            var success = answer.Length > 0 || generated != null;

            // Fertige Antwort als Markdown darstellen (während des Streamings war es reiner Text)
            answerView.Body.Blocks.Clear();
            if (generated != null)
                answerView.Body.Blocks.Add(new BlockUIContainer(MakeImageView(generated, 420, 560)));
            if (answer.Length > 0)
                MarkdownRenderer.Render(answer.ToString(), answerView.Body.Blocks);
            if (note != null)
                answerView.Body.Blocks.Add(new Paragraph(new Italic(new Run(note))) { Margin = new Thickness(0) });
            if (success)
                AddAnswerFooter(answerView.Body, answer.ToString(), stats, generated);
            ChatBox.ScrollToEnd();

            if (success)
            {
                var reply = new ChatMessage("assistant", answer.ToString(), generated) { Stats = stats };
                _messages.Add(reply with { Id = _store.AddMessage(chat.Id, reply) });
                _store.AddUsage(provider.Kind, stats);
            }
            else
            {
                // Ohne Antwort: Frage nicht speichern, sondern zum erneuten Senden zurück ins Eingabefeld
                _store.DeleteMessage(userMsg.Id);
                _messages.Remove(userMsg);
                InputBox.Text = text;
                if (image != null) SetImage(image);
            }

            if (_messages.Count == 0)
            {
                _store.DeleteChat(chat.Id);
                _chats.Remove(chat);
                _currentChat = null;
            }
            else
            {
                var updated = chat with { Updated = _store.Touch(chat.Id) };
                _chats.Remove(chat);
                _chats.Insert(0, updated);
                _currentChat = updated;
                SelectChatSilently(updated);
            }

            _cts.Dispose();
            _cts = null;
            SetBusy(false);
            UpdateEmptyState();
            InputBox.Focus();
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => _cts?.Cancel();

    // ---------- Chatliste und Anzeige ----------

    private void ChatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || ChatList.SelectedItem is not ChatInfo chat) return;
        _currentChat = chat;
        _messages.Clear();
        _messages.AddRange(_store.LoadMessages(chat.Id));
        RenderChat();
        UpdateEmptyState();
        NavChat.IsChecked = true;
        InputBox.Focus();
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        _currentChat = null;
        _messages.Clear();
        ChatBox.Document.Blocks.Clear();
        SelectChatSilently(null);
        UpdateEmptyState();
        NavChat.IsChecked = true;
        StatusText.Text = "Neuer Chat.";
        InputBox.Focus();
    }

    // Papierkorb oben rechts: den geöffneten Chat löschen
    private void DeleteChat_Click(object sender, RoutedEventArgs e)
    {
        if (_currentChat != null)
            DeleteChat(_currentChat);
    }

    // Papierkorb in der Liste bzw. Rechtsklick → „Chat löschen“: der Chat der jeweiligen Zeile
    private void DeleteChatItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ChatInfo chat)
            DeleteChat(chat);
        e.Handled = true;
    }

    private void ChatList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && ChatList.SelectedItem is ChatInfo chat)
        {
            DeleteChat(chat);
            e.Handled = true;
        }
    }

    private void DeleteChat(ChatInfo chat)
    {
        if (_cts != null)
        {
            StatusText.Text = "Während eine Antwort läuft, kann kein Chat gelöscht werden.";
            return;
        }
        if (MessageBox.Show(this, $"Chat „{chat.Title}“ wirklich löschen?", "Chat löschen",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _store.DeleteChat(chat.Id);
        _chats.Remove(chat);
        if (chat.Id == _currentChat?.Id)
            NewChat_Click(this, new RoutedEventArgs());
        StatusText.Text = $"Chat „{chat.Title}“ gelöscht.";
    }

    private void SelectChatSilently(ChatInfo? chat)
    {
        _suppressSelection = true;
        ChatList.SelectedItem = chat;
        _suppressSelection = false;
    }

    private static string MakeTitle(string text)
    {
        var firstLine = text.Split('\n')[0].Trim();
        if (firstLine.Length == 0) return "Bild";
        return firstLine.Length > 40 ? firstLine[..40] + "…" : firstLine;
    }

    private void RenderChat()
    {
        ChatBox.Document.Blocks.Clear();
        foreach (var m in _messages)
            AddMessageBlock(m);
    }

    // Body = Inhalt der Nachricht; Stream = Text, in den während des Empfangs geschrieben wird
    private sealed record MessageView(Section Body, Run? Stream);

    private MessageView AddMessageBlock(ChatMessage m, bool streaming = false)
    {
        var blocks = ChatBox.Document.Blocks;
        var top = blocks.Count == 0 ? 8 : 22;

        // Eigene Nachrichten: rechtsbündige, abgerundete Blase (Text markier- und kopierbar)
        if (m.Role == "user")
        {
            var content = new StackPanel();
            if (m.Image != null)
                content.Children.Add(MakeThumbnail(m.Image));
            if (m.Content.Length > 0)
                content.Children.Add(new TextBox
                {
                    Text = m.Content,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Padding = new Thickness(0),
                    FontSize = 15,
                    Margin = new Thickness(0, m.Image != null ? 8 : 0, 0, 0),
                    Cursor = Cursors.IBeam
                });
            var bubble = new Border
            {
                Child = content,
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(16, 10, 16, 10),
                MaxWidth = 560,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            bubble.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
            blocks.Add(new BlockUIContainer(bubble) { Margin = new Thickness(80, top, 0, 0) });
            ChatBox.ScrollToEnd();
            UpdateEmptyState();
            return new MessageView(new Section(), null);
        }

        // Antworten: ohne Blase als gut lesbarer Fließtext (Markdown)
        var body = new Section { Margin = new Thickness(0, top, 0, 0) };
        blocks.Add(body);
        Run? stream = null;
        if (streaming)
        {
            stream = new Run();
            body.Blocks.Add(new Paragraph(stream) { Margin = new Thickness(0) });
        }
        else
        {
            if (m.Image != null)
                body.Blocks.Add(new BlockUIContainer(MakeImageView(m.Image, 420, 560)));
            MarkdownRenderer.Render(m.Content, body.Blocks);
            AddAnswerFooter(body, m.Content, m.Stats, m.Image);
        }
        ChatBox.ScrollToEnd();
        UpdateEmptyState();
        return new MessageView(body, stream);
    }

    // ---------- Statistik unter Antworten ----------

    private AnswerStats MakeStats(LlmClient.StreamResult result, string configuredModel, double seconds, double? firstChunkAt,
        double? cost = null)
    {
        var region = RegionOption.Name(_settings.Current.Region);
        var provider = ActiveProvider.Name + (region.Length > 0 ? $" ({region})" : "");

        // Token/s nur für die Schreibphase (ab dem ersten Textstück), sonst verfälscht die Wartezeit den Wert
        double? tokensPerSecond = null;
        if (result.OutputTokens is > 0 && firstChunkAt is { } start)
        {
            var writing = seconds - start;
            tokensPerSecond = result.OutputTokens.Value / (writing > 0.2 ? writing : seconds);
        }
        return new AnswerStats(provider, result.Model ?? configuredModel, result.Hoster,
            result.InputTokens, result.OutputTokens, tokensPerSecond, seconds, cost);
    }

    private static string FormatStats(AnswerStats s)
    {
        var parts = new List<string> { $"{s.Provider} · {s.Model}" + (s.Hoster != null ? $" (über {s.Hoster})" : "") };
        if (s.TokensPerSecond is { } tps)
            parts.Add($"{tps:0.0} Token/s");
        if (s.InputTokens != null || s.OutputTokens != null)
            parts.Add($"{s.InputTokens?.ToString("N0") ?? "–"} in / {s.OutputTokens?.ToString("N0") ?? "–"} out");
        parts.Add($"{s.Seconds:0.0} s");
        if (s.Cost is > 0)
            parts.Add($"{s.Cost:0.####} $");
        return string.Join("   ·   ", parts);
    }

    // Zeile unter einer Antwort: Knöpfe (Kopieren bzw. Bild kopieren/speichern) und Anbieter, Modell, Token/s, Tokens
    private void AddAnswerFooter(Section body, string content, AnswerStats? stats, byte[]? image = null)
    {
        var line = new Paragraph { FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0) };
        line.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");

        if (content.Length > 0)
            line.Inlines.Add(FooterButton("", "Kopieren", "Antwort kopieren",
                "Antwort in die Zwischenablage kopieren (mit Markdown-Formatierung)",
                () => CopyToClipboard(() => Clipboard.SetText(content))));
        if (image != null)
        {
            line.Inlines.Add(FooterButton("", "Bild kopieren", "Bild kopieren", "Bild in die Zwischenablage kopieren",
                () => CopyToClipboard(() => Clipboard.SetImage(LoadBitmap(image)))));
            line.Inlines.Add(FooterButton("", "Speichern", "Bild speichern", "Bild als PNG-Datei speichern",
                () => SaveImage(image)));
        }
        if (stats != null)
            line.Inlines.Add(new Run("   " + FormatStats(stats)));
        body.Blocks.Add(line);
    }

    // Kleiner Knopf in der Zeile unter einer Antwort; zeigt nach dem Klick kurz eine Bestätigung
    private InlineUIContainer FooterButton(string glyph, string text, string automationName, string tooltip, Func<bool> action)
    {
        var label = new TextBlock { Text = text, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var button = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Padding = new Thickness(6, 2, 8, 2),
            ToolTip = tooltip,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock
                    {
                        Text = glyph,
                        FontFamily = (FontFamily)FindResource("IconFont"),
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    label
                }
            }
        };
        System.Windows.Automation.AutomationProperties.SetName(button, automationName);
        button.Click += async (_, _) =>
        {
            if (!action()) return;
            label.Text = text == "Speichern" ? "Gespeichert ✓" : "Kopiert ✓";
            await Task.Delay(1800);
            label.Text = text;
        };
        return new InlineUIContainer(button) { BaselineAlignment = BaselineAlignment.Center };
    }

    // Die Zwischenablage kann kurz von einem anderen Programm gesperrt sein – dann ein paar Mal wiederholen
    private bool CopyToClipboard(Action copy)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                copy();
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(50);
            }
        }
        StatusText.Text = "Die Zwischenablage ist gerade belegt – bitte nochmal versuchen.";
        return false;
    }

    private bool SaveImage(byte[] png)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Bild speichern",
            Filter = "PNG-Bild|*.png",
            FileName = $"LmChat-Bild-{DateTime.Now:yyyy-MM-dd-HHmmss}.png"
        };
        if (dialog.ShowDialog(this) != true) return false;
        File.WriteAllBytes(dialog.FileName, png);
        StatusText.Text = $"Bild gespeichert: {dialog.FileName}";
        return true;
    }

    private static BitmapImage LoadBitmap(byte[] data)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(data);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    // Erzeugte Bilder kommen je nach Anbieter als PNG, JPEG oder WebP – einheitlich als PNG speichern
    private static byte[] ToPng(byte[] data)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(LoadBitmap(data)));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static Image MakeThumbnail(byte[] png) => MakeImageView(png, 180, 320);

    private static Image MakeImageView(byte[] png, double maxHeight, double maxWidth)
    {
        var image = new Image
        {
            Source = LoadBitmap(png),
            MaxHeight = maxHeight,
            MaxWidth = maxWidth,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand,
            ToolTip = "Klicken zum Vergrößern"
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.MouseLeftButtonUp += (_, _) => OpenImage(png);
        return image;
    }

    // Öffnet das Bild in voller Größe mit dem Standard-Bildbetrachter
    private static void OpenImage(byte[] png)
    {
        var dir = Path.Combine(Path.GetTempPath(), "LmChat");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"bild-{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(png))[..12]}.png");
        if (!File.Exists(file))
            File.WriteAllBytes(file, png);
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    private void SetBusy(bool busy)
    {
        SendButton.IsEnabled = !busy;
        StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        NewChatButton.IsEnabled = !busy;
        DeleteChatButton.IsEnabled = !busy;
        ChatList.IsEnabled = !busy;
        UpdateModelUi();
    }

    // ---------- Bilder ----------

    private void ImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Bild anhängen",
            Filter = "Bilder|" + string.Join(";", ImageExtensions.Select(x => "*" + x))
        };
        if (dialog.ShowDialog(this) == true)
            AttachImageFile(dialog.FileName);
    }

    private void RemoveImage_Click(object sender, RoutedEventArgs e) => ClearImage();

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var file = files.FirstOrDefault(IsImageFile);
        if (file != null) AttachImageFile(file);
        e.Handled = true;
    }

    private bool TryPasteImage()
    {
        if (Clipboard.ContainsFileDropList())
        {
            var file = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(IsImageFile);
            if (file == null) return false;
            AttachImageFile(file);
            return true;
        }

        // Screenshots liegen meist zusätzlich als PNG vor – das ist zuverlässiger als GetImage()
        if (Clipboard.GetData("PNG") is MemoryStream png)
        {
            AttachImage(BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad));
            return true;
        }
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bitmap)
        {
            // Zwischenablage-Bitmaps haben oft einen kaputten Alphakanal → ohne Transparenz übernehmen
            AttachImage(new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0));
            return true;
        }
        return false;
    }

    private static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private void AttachImageFile(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            AttachImage(bitmap);
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UriFormatException)
        {
            StatusText.Text = $"Bild konnte nicht geladen werden: {ex.Message}";
        }
    }

    private void AttachImage(BitmapSource source)
    {
        if (CurrentVision == false)
        {
            StatusText.Text = "Das gewählte Modell kann keine Bilder verarbeiten.";
            return;
        }
        if (_cts != null) return;

        // Große Bilder verkleinern, damit die Anfrage nicht unnötig groß wird
        var scale = Math.Min(1.0, (double)MaxImageSide / Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource scaled = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(scaled));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        SetImage(ms.ToArray());
        InputBox.Focus();
    }

    private void SetImage(byte[] png)
    {
        _pendingImage = png;
        var preview = new BitmapImage();
        preview.BeginInit();
        preview.CacheOption = BitmapCacheOption.OnLoad;
        preview.StreamSource = new MemoryStream(png);
        preview.EndInit();
        AttachmentImage.Source = preview;
        AttachmentText.Text = $"Bild angehängt ({preview.PixelWidth} × {preview.PixelHeight}, {png.Length / 1024} KB)";
        AttachmentPanel.Visibility = Visibility.Visible;
    }

    private void ClearImage()
    {
        _pendingImage = null;
        AttachmentImage.Source = null;
        AttachmentPanel.Visibility = Visibility.Collapsed;
    }

    // ---------- Einstellungen ----------

    private readonly ParamRow _temperatureRow = new("Temperatur",
        "Wie frei bzw. zufällig das Modell formuliert. Niedrig (0 bis 0,3): sachlich und bei gleicher Frage fast " +
        "immer gleiche Antwort – gut für Fakten, Code und Bilder auslesen. Mittel (0,7): ausgewogen. " +
        "Hoch (über 1): kreativ, aber zunehmend wirr.",
        0, 2, 0.05, 0.7, "0.00");

    private readonly ParamRow _topPRow = new("Top-P",
        "Das Modell wählt das nächste Wort nur aus den wahrscheinlichsten Kandidaten, die zusammen diesen Anteil " +
        "ausmachen. 0,95 = die oberen 95 %. Kleiner = vorsichtiger. Meist reicht es, nur die Temperatur zu ändern.",
        0.05, 1, 0.01, 0.95, "0.00");

    private readonly ParamRow _topKRow = new("Top-K",
        "Das Modell wählt das nächste Wort nur aus den K wahrscheinlichsten Kandidaten. 40 ist üblich. " +
        "Kleiner = eintöniger, größer = abwechslungsreicher.",
        1, 200, 1, 40, "0");

    private readonly ParamRow _maxTokensRow = new("Maximale Antwortlänge (Tokens)",
        "Begrenzt, wie lang eine Antwort höchstens wird. Ein Token ist ungefähr ein halbes bis ganzes Wort; " +
        "1000 Tokens sind etwa ein bis zwei Seiten Text. Ohne Haken: keine Begrenzung (bei Anthropic: 64000).",
        64, 32768, 64, 2048, "0");

    private readonly ParamRow _repeatPenaltyRow = new("Wiederholungsstrafe",
        "Bremst das Modell, wenn es sich wiederholt. 1,00 = aus, 1,10 = leicht (üblich). " +
        "Über 1,30 kann die Sprache holprig werden.",
        1, 2, 0.01, 1.1, "0.00");

    private IEnumerable<ParamRow> ParamRows => [_temperatureRow, _topPRow, _topKRow, _maxTokensRow, _repeatPenaltyRow];

    private ProviderInfo ShownProvider => ProviderInfo.Get(_shownProvider ?? _draft.Provider);

    private void ShowSettings()
    {
        SystemPromptBox.Text = _draft.SystemPrompt;
        _temperatureRow.Value = _draft.Temperature;
        _topPRow.Value = _draft.TopP;
        _topKRow.Value = _draft.TopK;
        _maxTokensRow.Value = _draft.MaxTokens;
        _repeatPenaltyRow.Value = _draft.RepeatPenalty;
        _shownProvider = null;
        ProviderCombo.SelectedItem = ProviderInfo.Get(_draft.Provider);
        UpdateModelUi();
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderCombo.SelectedItem is not ProviderInfo info) return;
        // Eingaben des bisher gezeigten Anbieters im Entwurf merken, bevor gewechselt wird
        if (_shownProvider is { } previous)
            StoreProviderFields(previous);
        ShowProvider(info);
    }

    private void StoreProviderFields(ProviderKind kind)
    {
        var config = _draft.Config(kind);
        config.Server = ServerBox.Text.Trim().TrimEnd('/');
        config.ApiKey = ApiKeyBox.Password.Trim();
        config.Region = kind == ProviderKind.OpenRouter ? RegionCombo.SelectedValue as string ?? "" : "";
        if (ModelCombo.SelectedItem is ModelInfo model)
        {
            config.Model = model.Id;
            config.ModelVision = model.Vision;
            config.ModelImageGen = model.ImageGen;
        }
    }

    private void ShowProvider(ProviderInfo info)
    {
        _shownProvider = info.Kind;
        var config = _draft.Config(info.Kind);
        ProviderHint.Text = info.Description;
        ProviderLinks.Inlines.Clear();
        ProviderLinks.Inlines.Add(new Run("Links:  ") { FontWeight = FontWeights.SemiBold });
        foreach (var (text, url) in info.Links)
        {
            if (ProviderLinks.Inlines.Count > 1)
                ProviderLinks.Inlines.Add(new Run("   ·   ") { Foreground = Brushes.Gray });
            var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url), ToolTip = url };
            link.RequestNavigate += Link_RequestNavigate;
            ProviderLinks.Inlines.Add(link);
        }
        ServerBox.Text = config.Server;
        ApiKeyPanel.Visibility = info.NeedsKey ? Visibility.Visible : Visibility.Collapsed;
        if (info.KeyUrl != null)
        {
            KeyLink.NavigateUri = new Uri(info.KeyUrl);
            KeyLinkText.Text = info.KeyUrl.Replace("https://", "");
        }
        ApiKeyBox.Password = config.ApiKey;
        RegionPanel.Visibility = info.Kind == ProviderKind.OpenRouter ? Visibility.Visible : Visibility.Collapsed;
        _showingProvider = true;
        RegionCombo.SelectedValue = config.Region;
        _showingProvider = false;

        _temperatureRow.SetSupported(info.SupportsSampling, info.Name);
        _topPRow.SetSupported(info.SupportsSampling, info.Name);
        _topKRow.SetSupported(info.SupportsTopK, info.Name);
        _repeatPenaltyRow.SetSupported(info.SupportsRepeatPenalty, info.Name);

        _selectedModelId = config.Model;
        _models.Clear();
        EnsureModelItem(config.Model, config.ModelVision, config.ModelImageGen);
        ModelCombo.SelectedValue = config.Model;
        _ = LoadModelsAsync();
    }

    private bool _showingProvider;

    private async void RegionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_showingProvider || _shownProvider != ProviderKind.OpenRouter) return;
        await LoadModelsAsync();
    }

    private void DefaultServer_Click(object sender, RoutedEventArgs e) => ServerBox.Text = ShownProvider.DefaultServer;

    private void ResetParams_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in ParamRows)
            row.Value = null;
        SettingsStatus.Text = "Alle Feineinstellungen auf Standard – zum Übernehmen „Speichern“ klicken.";
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var info = ShownProvider;
        var server = ServerBox.Text.Trim().TrimEnd('/');
        if (!server.Contains("://"))
            server = (info.IsLocal ? "http://" : "https://") + server;
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            SettingsStatus.Text = $"Die Server-Adresse ist ungültig. Standard für {info.Name}: {info.DefaultServer}";
            return;
        }
        if (info.NeedsKey && ApiKeyBox.Password.Trim().Length == 0)
        {
            SettingsStatus.Text = $"Für {info.Name} wird ein API-Schlüssel benötigt.";
            return;
        }
        if (ModelCombo.SelectedItem is not ModelInfo)
        {
            SettingsStatus.Text = "Bitte ein Modell auswählen (ggf. erst „Liste aktualisieren“).";
            return;
        }

        ServerBox.Text = server;
        StoreProviderFields(info.Kind);
        _draft.Provider = info.Kind;
        _draft.SystemPrompt = SystemPromptBox.Text.Trim();
        _draft.Temperature = _temperatureRow.Value;
        _draft.TopP = _topPRow.Value;
        _draft.TopK = _topKRow.Value;
        _draft.MaxTokens = _maxTokensRow.Value;
        _draft.RepeatPenalty = _repeatPenaltyRow.Value;
        _draft.Save(_store);
        _settings = _draft.Clone();
        UpdateModelUi();
        SettingsStatus.Text = $"Gespeichert – aktiv: {info.Name}, {_settings.Current.Model} ({DateTime.Now:HH:mm:ss}).";
    }

    private async void ReloadModels_Click(object sender, RoutedEventArgs e) => await LoadModelsAsync();

    // Holt die Modellliste des gerade gezeigten Anbieters (mit Adresse und Schlüssel aus den Eingabefeldern)
    private async Task LoadModelsAsync()
    {
        var info = ShownProvider;
        var key = ApiKeyBox.Password.Trim();
        var region = info.Kind == ProviderKind.OpenRouter ? RegionCombo.SelectedValue as string ?? "" : "";
        // Die OpenRouter-Modellliste ist öffentlich, alle anderen Online-Dienste brauchen dafür den Schlüssel
        if (info.NeedsKey && key.Length == 0 && info.Kind != ProviderKind.OpenRouter)
        {
            SettingsStatus.Text = $"Für die Modellliste von {info.Name} bitte zuerst den API-Schlüssel eintragen.";
            return;
        }

        var selected = ModelCombo.SelectedValue as string ?? _draft.Config(info.Kind).Model;
        var loadId = ++_modelLoadId;
        SettingsStatus.Text = $"Lade Modelle von {info.Name} …";
        try
        {
            // Der Hoster-Filter prüft jedes Modell einzeln – das dauert beim ersten Mal etwas länger
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(RegionOption.IsHosterFilter(region) ? 180 : 20));
            var models = await LlmClient.ListModelsAsync(info, ServerBox.Text, region, key, cts.Token,
                status => { if (loadId == _modelLoadId) SettingsStatus.Text = status; });
            if (loadId != _modelLoadId) return; // inzwischen anderer Anbieter gewählt
            _selectedModelId = selected;
            _models.Clear();
            foreach (var m in models)
                _models.Add(m);
            var where = region.Length > 0 ? $" (nur {RegionOption.Name(region)})" : "";
            SettingsStatus.Text = $"{models.Count} Modelle von {info.Name}{where} gefunden." +
                (selected.Length > 0 && models.All(m => m.Id != selected)
                    ? $" „{selected}“ ist hier nicht verfügbar – bitte ein anderes Modell wählen."
                    : "");
        }
        catch (Exception ex)
        {
            if (loadId != _modelLoadId) return;
            SettingsStatus.Text = $"Modelle konnten nicht geladen werden: {ex.Message}";
            // Liste nicht ladbar: bisherige Auswahl trotzdem anzeigen
            var saved = _draft.Config(info.Kind);
            EnsureModelItem(selected, saved.Model == selected ? saved.ModelVision : null,
                saved.Model == selected && saved.ModelImageGen);
        }
        ModelCombo.SelectedValue = selected;
        ApplyModelFilter();

        // Fähigkeiten des aktiven Modells aktualisieren (z. B. nach dem Programmstart)
        if (info.Kind == _settings.Provider &&
            _models.FirstOrDefault(m => m.Id == _settings.Current.Model) is { } active &&
            ((active.Vision != null && active.Vision != _settings.Current.ModelVision) ||
             active.ImageGen != _settings.Current.ModelImageGen))
        {
            _settings.Current.ModelVision = active.Vision ?? _settings.Current.ModelVision;
            _settings.Current.ModelImageGen = active.ImageGen;
            _draft.Config(info.Kind).ModelVision = _settings.Current.ModelVision;
            _draft.Config(info.Kind).ModelImageGen = active.ImageGen;
            _settings.Save(_store);
            UpdateModelUi();
        }
    }

    // ---------- Filter über der Modellliste ----------

    private void ModelSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyModelFilter();

    private void ModelFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyModelFilter();

    // Das gewählte Modell bleibt immer in der Liste, sonst ginge die Auswahl beim Filtern verloren
    private string _selectedModelId = "";

    private bool ModelMatchesFilter(object item) =>
        item is ModelInfo m && (m.Id == _selectedModelId || MatchesModelSearch(m));

    // Alle Suchwörter müssen im Namen vorkommen, dazu die gewählte Eigenschaft
    private bool MatchesModelSearch(ModelInfo m)
    {
        var words = (ModelSearchBox?.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.All(w => m.Id.Contains(w, StringComparison.OrdinalIgnoreCase)) &&
               (ModelFilterCombo?.SelectedIndex ?? 0) switch { 1 => m.ImageGen, 2 => m.Vision == true, 3 => m.Loaded, _ => true };
    }

    private void ApplyModelFilter()
    {
        if (ModelCombo == null || ModelFilterInfo == null) return; // noch beim Aufbau des Fensters
        System.Windows.Data.CollectionViewSource.GetDefaultView(_models).Refresh();
        var filtered = ModelSearchBox.Text.Trim().Length > 0 || ModelFilterCombo.SelectedIndex > 0;
        ModelFilterInfo.Text = filtered
            ? $"{_models.Count(MatchesModelSearch)} von {_models.Count} Modellen passen – Liste aufklappen zum Auswählen."
            : _models.Count > 0 ? $"{_models.Count} Modelle" : "";
    }

    private void EnsureModelItem(string id, bool? vision, bool imageGen = false)
    {
        if (id.Length > 0 && _models.All(m => m.Id != id))
            _models.Add(new ModelInfo(id, vision, false, imageGen));
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var model = ModelCombo.SelectedItem as ModelInfo;
        if (model != null)
            _selectedModelId = model.Id;
        var visionHint = model?.Vision switch
        {
            true => "🖼 Dieses Modell versteht Bilder.",
            false => "Dieses Modell versteht nur Text.",
            _ => model == null ? "" : "Ob das Modell Bilder versteht, meldet dieser Anbieter nicht – einfach ausprobieren."
        };
        ModelHint.Text = model?.ImageGen == true
            ? "🎨 Dieses Modell erzeugt Bilder: Deine Nachricht ist die Bildbeschreibung. Ein angehängtes Bild (oder mit " +
              "„Verlauf mitsenden“ das zuletzt erzeugte) dient als Vorlage zum Bearbeiten. Bilder kosten meist mehr als Text."
            : visionHint;
    }

    private void UpdateModelUi()
    {
        var vision = CurrentVision;
        var model = _settings.Current.Model;
        var region = RegionOption.Name(_settings.Current.Region);
        var provider = ActiveProvider.Name + (region.Length > 0 ? $" ({region})" : "");
        var modelText = model.Length > 0 ? model : "kein Modell gewählt";
        var visionText = CurrentImageGen ? "erzeugt Bilder" : vision switch { true => "versteht Bilder", false => "nur Text", _ => "" };

        // Karte unten in der Seitenleiste
        ProviderInfoText.Text = provider;
        ModelInfoText.Text = modelText;
        ModelInfoText.ToolTip = $"{provider} · {modelText}" + (visionText.Length > 0 ? $" · {visionText}" : "");

        // Kacheln oben in den Einstellungen
        TileProvider.Text = provider;
        TileModel.Text = modelText.Contains('/') ? modelText[(modelText.LastIndexOf('/') + 1)..] : modelText;
        TileModel.ToolTip = modelText;
        TileModelHint.Text = "Aktives Modell" + (visionText.Length > 0 ? $" · {visionText}" : "");

        EmptyStateHint.Text = CurrentImageGen
            ? $"🎨 {modelText} über {provider} erzeugt Bilder: Beschreibe, was zu sehen sein soll. " +
              "Mit einem angehängten Bild wird dieses bearbeitet."
            : $"Du chattest mit {modelText} über {provider}." +
              (vision == false ? "" : " Bilder kannst du per Strg+V oder „Bild“ anhängen.");
        ImageButton.IsEnabled = vision != false && _cts == null;
        ImageButton.ToolTip = vision == false
            ? "Dieses Modell kann keine Bilder verarbeiten"
            : "Bild anhängen (auch per Strg+V oder Hineinziehen)";
    }

    // ---------- Hilfe ----------

    private static string AppVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
