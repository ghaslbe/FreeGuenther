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

    // LMCHAT_DB = andere Datenbank verwenden (z. B. zum Testen mit leerem Stand); Plugins liegen daneben in "plugins"
    private static readonly string DbPath = Environment.GetEnvironmentVariable("LMCHAT_DB") is { Length: > 0 } db ? db
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LmChat", "chats.db");
    private readonly ChatStore _store = new(DbPath);
    private readonly PluginManager _plugins;
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
    private bool _followStream = true;   // false, sobald man während einer Antwort hochscrollt
    private bool _checkingBudget;         // Kostenbremse wird gerade geprüft – kein zweites Senden

    public MainWindow()
    {
        InitializeComponent();
        Title = $"LM Chat {AppVersion}";
        VersionRun.Text = $"Version {AppVersion}.";
        SidebarVersion.Text = $"Version {AppVersion}";
        _settings = LlmSettings.Load(_store);
        _draft = _settings.Clone();
        _plugins = new PluginManager(_store, Path.Combine(Path.GetDirectoryName(DbPath)!, "plugins"))
        {
            // Für das Webhook-Plugin: die letzte Antwort im geöffneten Chat
            LastAnswer = () => _messages.LastOrDefault(m => m.Role == "assistant") is { } last ? ToExport(last) : null
        };
        foreach (var chat in _store.ListChats())
            _chats.Add(chat);

        // Chatliste: nach Datum gruppiert und über das Suchfeld filterbar
        var chatView = System.Windows.Data.CollectionViewSource.GetDefaultView(_chats);
        chatView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ChatInfo.DateGroup)));
        chatView.Filter = item => item is ChatInfo c &&
            c.Title.Contains(ChatSearchBox.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);
        _chats.CollectionChanged += (_, _) => UpdateTiles();
        ChatList.ItemsSource = _chats;
        ProviderList.ItemsSource = _providerRows;
        RegionCombo.ItemsSource = RegionOption.All;
        ModelCombo.ItemsSource = _models;
        System.Windows.Data.CollectionViewSource.GetDefaultView(_models).Filter = ModelMatchesFilter;
        foreach (var row in ParamRows)
            ParamsPanel.Children.Add(row.Element);
        MarkdownRenderer.CopyText = text => CopyToClipboard(() => Clipboard.SetText(text));
        ChatBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ChatBox_ScrollChanged));
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
        MainTabs.SelectedIndex = sender == NavSettings ? 1 : sender == NavUsage ? 2 : sender == NavPlugins ? 3 : sender == NavHelp ? 4
            : sender == NavCompare ? 5 : 0;
        if (sender == NavChat) InputBox.Focus();
        if (sender == NavCompare) ShowCompare();
        if (sender == NavUsage)
        {
            _ = ShowUsageAsync();
            _ = ShowBudgetAsync();
        }
        if (sender == NavPlugins) ShowPlugins(loadCatalog: _catalog == null);
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
        var priceNote = await EnsurePricesAsync(rows, TimeSpan.FromSeconds(20))
            ? "" : "Preisliste nicht erreichbar – Kosten ohne Angabe des Anbieters sind unbekannt.";

        double total = 0;
        var anyEstimate = false;
        var anyUnknown = false;
        var lines = rows.Select(r =>
        {
            var (amount, kind) = CostOf(r);
            total += amount;
            anyEstimate |= kind == CostKind.Estimated;
            anyUnknown |= kind is CostKind.Partial or CostKind.Unknown;
            var cost = kind switch
            {
                CostKind.Local => "0 $ (lokal)",
                CostKind.Reported => FormatDollar(amount) + " gemeldet",
                CostKind.Estimated => "≈ " + FormatDollar(amount) + " geschätzt",
                CostKind.Partial => "mind. " + FormatDollar(amount),
                _ => "unbekannt"
            };
            return new UsageLine(ProviderInfo.Get(r.Provider).Name, r.Model, r.Requests.ToString("N0"), r.InputTokens.ToString("N0"),
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
        EmptyState.Visibility = IsReady && ChatBox.Document.Blocks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChatTitleText.Text = _currentChat?.Title ?? "Neuer Chat";
        DeleteChatButton.Visibility = _currentChat == null ? Visibility.Hidden : Visibility.Visible;
        ExportChatButton.Visibility = _currentChat == null || _messages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- Einrichtung (erster Start) ----------

    // Startklar = Modell gewählt und gespeichert, bei Online-Diensten mit Schlüssel
    private bool IsReady => _settings.Current.Model.Length > 0 && (!ActiveProvider.NeedsKey || _settings.Current.ApiKey.Length > 0);

    private bool _setupJustFinished;
    private ProviderKind? _modelsLoadedFor;

    // Ohne Einrichtung zeigt die Chat-Seite statt Verlauf und Eingabe die Willkommens-Anleitung
    private void UpdateSetupState()
    {
        var ready = IsReady;
        SetupPanel.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        var chat = ready ? Visibility.Visible : Visibility.Collapsed;
        ChatBox.Visibility = chat;
        InputCard.Visibility = chat;
        StatusText.Visibility = chat;
        UpdateEmptyState();
        UpdateSetupGuide();
    }

    private void SetupProvider_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<ProviderKind>(tag, out var kind)) return;
        SelectProviderRow(kind);
        NavSettings.IsChecked = true;
        SettingsScroll.ScrollToTop();
    }

    private void SetupDone_Click(object sender, RoutedEventArgs e)
    {
        _setupJustFinished = false;
        UpdateSetupGuide();
        NavChat.IsChecked = true;
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        UpdateSetupGuide();
        UpdateProviderRows();
    }

    // Schritt-für-Schritt-Karte oben in den Einstellungen: zeigt, was schon erledigt ist und was als Nächstes kommt
    private void UpdateSetupGuide()
    {
        if (SetupGuide == null) return;
        var ready = IsReady;
        SetupGuide.Visibility = !ready || _setupJustFinished ? Visibility.Visible : Visibility.Collapsed;
        if (SetupGuide.Visibility != Visibility.Visible) return;

        var info = ShownProvider;
        var hasKey = ApiKeyBox.Password.Trim().Length > 0;
        var hasModel = ModelCombo.SelectedItem is ModelInfo;
        var saved = ready && _settings.Provider == info.Kind;
        SetupGuideTitle.Text = saved ? "Fertig eingerichtet – du kannst loslegen!" : "So geht's los – in 4 Schritten";
        SetupDoneButton.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;

        SetupSteps.Children.Clear();
        AddStep(true, "1. Anbieter wählen", new Run($"Gewählt: {info.Name}. Ändern kannst du das unten unter „Anbieter“."));

        if (info.NeedsKey)
        {
            var limit = info.Links.FirstOrDefault(l => l.Item1.Contains("Limit"));
            var body = new List<Inline>
            {
                new Run("Beim Anbieter ein Konto anlegen und einen Schlüssel erstellen: "),
                MakeLink(info.KeyUrl!.Replace("https://", ""), info.KeyUrl),
                new Run(". Den Schlüssel kopieren und unten bei „API-Schlüssel“ einfügen. ")
            };
            if (limit.Item2 != null)
            {
                body.Add(new Run("Gleich dabei ein ") { FontWeight = FontWeights.SemiBold });
                body.Add(MakeLink("Ausgabenlimit setzen", limit.Item2));
                body.Add(new Run(", damit nie mehr als gewollt abgebucht wird."));
            }
            AddStep(hasKey, "2. API-Schlüssel eintragen", body.ToArray());
        }
        else if (info.Kind == ProviderKind.LmStudio)
            AddStep(_modelsLoadedFor == info.Kind, "2. LM Studio starten",
                new Run("LM Studio installieren ("), MakeLink("lmstudio.ai", "https://lmstudio.ai"),
                new Run("), dort ein Modell herunterladen (z. B. Gemma oder Qwen) und unter „Developer“ den Server starten. " +
                        "Läuft LM Studio auf einem anderen Rechner: dort „Serve on Local Network“ einschalten und unten dessen Adresse eintragen. " +
                        "Danach unten bei „Modell“ auf „Liste aktualisieren“ klicken."));
        else
            AddStep(_modelsLoadedFor == info.Kind, "2. Ollama starten",
                new Run("Ollama installieren ("), MakeLink("ollama.com", "https://ollama.com"),
                new Run(") und ein Modell laden, z. B. in der Eingabeaufforderung: "),
                new Run("ollama pull gemma3") { FontFamily = new FontFamily("Cascadia Mono, Consolas") },
                new Run(". Danach unten bei „Modell“ auf „Liste aktualisieren“ klicken."));

        AddStep(hasModel, "3. Modell auswählen",
            new Run("Unten unter „Modell“ eins aus der Liste wählen" + info.Kind switch
            {
                ProviderKind.OpenRouter => ". Für den Anfang gut: „openrouter/auto“ – sucht automatisch ein passendes Modell aus.",
                ProviderKind.OpenAI or ProviderKind.Anthropic => ". Die Liste erscheint, sobald der Schlüssel eingetragen ist (sonst „Liste aktualisieren“).",
                _ => ". Ist die Liste leer, läuft der Server noch nicht oder die Adresse stimmt nicht."
            }));
        AddStep(saved, "4. Speichern", new Run("Ganz unten auf „Speichern“ klicken – danach kannst du im Chat loslegen."));
    }

    private void AddStep(bool done, string title, params Inline[] body)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontSize = 14, LineHeight = 21 };
        // Segoe Fluent Icons: erledigt = grüner Haken im Kreis, offen = leerer Kreis
        text.Inlines.Add(new Run(done ? "" : "")
        {
            FontFamily = (FontFamily)FindResource("IconFont"),
            Foreground = done ? new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)) : Brushes.Gray
        });
        text.Inlines.Add(new Run("  " + title) { FontWeight = FontWeights.SemiBold });
        text.Inlines.Add(new LineBreak());
        text.Inlines.AddRange(body);
        if (done) text.Opacity = 0.75;
        SetupSteps.Children.Add(text);
    }

    private Hyperlink MakeLink(string text, string url)
    {
        var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url), ToolTip = url };
        link.RequestNavigate += Link_RequestNavigate;
        return link;
    }

    private ProviderInfo ActiveProvider => ProviderInfo.Get(_settings.Provider);
    private bool? CurrentVision => _settings.Current.ModelVision;
    private bool CurrentImageGen => _settings.Current.ModelImageGen;

    // ---------- Chat senden ----------

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleTemplateKeys(e))
            e.Handled = true;
        else if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
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
        var documents = _pendingDocuments.ToList();
        if ((text.Length == 0 && image == null && documents.Count == 0) || _cts != null || _checkingBudget) return;

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
        if (imageMode && documents.Count > 0)
        {
            StatusText.Text = "Bildmodelle können keine Dateien lesen – Datei entfernen oder ein Text-Modell wählen.";
            return;
        }

        _checkingBudget = true;
        var (allowed, budgetWarning) = await CheckBudgetAsync([provider]);
        _checkingBudget = false;
        if (!allowed)
        {
            StatusText.Text = "Nicht gesendet – die Kostenbremse ist erreicht (siehe „Verbrauch“).";
            return;
        }

        var newChat = _currentChat == null;
        if (_currentChat == null)
        {
            var title = MakeTitle(text.Length == 0 && documents.Count > 0 ? documents[0].Name : text);
            _currentChat = _store.CreateChat(title, _pendingPersona);
            _chats.Insert(0, _currentChat);
            SelectChatSilently(_currentChat);
        }
        var chat = _currentChat;

        var userMsg = new ChatMessage("user", text, image) { Documents = documents.Count > 0 ? documents : null };
        userMsg = userMsg with { Id = _store.AddMessage(chat.Id, userMsg) };
        _messages.Add(userMsg);
        InputBox.Clear();
        ClearImage();
        ClearDocuments();
        _followStream = true;
        AddMessageBlock(userMsg);
        var answerView = AddMessageBlock(new ChatMessage("assistant", ""), streaming: true);

        List<ChatMessage> toSend = HistoryCheck.IsChecked == true ? _messages.ToList() : [userMsg];
        var request = new ChatRequest(provider, config.Server, config.Region, config.ApiKey, config.Model,
            SystemPromptFor(chat.Persona), _settings, toSend);

        _cts = new CancellationTokenSource();
        SetBusy(true);
        var answer = new StringBuilder();
        var reasoning = new StringBuilder();
        string? note = null;
        var result = new LlmClient.StreamResult();
        var clock = Stopwatch.StartNew();
        double? firstChunkAt = null;
        byte[]? generated = null;
        double? cost = null;
        var usedTools = new List<string>();
        var pluginNotes = new List<string>();

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
            // Während der Denkphase das Ende des Gedankengangs mitlaufen lassen
            if (reasoning.Length > 0)
            {
                var tail = reasoning.ToString(Math.Max(0, reasoning.Length - 160), Math.Min(160, reasoning.Length))
                    .ReplaceLineEndings(" ").Trim();
                phaseRun.Text += $"\n💭 … {tail}";
            }
            StatusText.Text = phase + " …";
        }
        void SetPhase(string text)
        {
            phase = text;
            // Nach Text wieder einblenden (z. B. wenn das Modell zwischendurch ein Plugin aufruft) – dann unter dem Text
            if (!ticker.IsEnabled)
            {
                answerView.Body.Blocks.Add(phaseLine);
                ticker.Start();
            }
            ShowPhase();
        }
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
                var toolBox = await PrepareToolsAsync(config, SetPhase, usedTools, pluginNotes, _cts.Token);
                await LlmClient.StreamAsync(request, chunk =>
                {
                    HidePhase();
                    firstChunkAt ??= clock.Elapsed.TotalSeconds;
                    answer.Append(chunk);
                    answerView.Stream!.Text += chunk;
                    if (_followStream) ChatBox.ScrollToEnd();
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
                }, result, toolBox, _cts.Token, piece =>
                {
                    reasoning.Append(piece);
                    if (ticker.IsEnabled) ShowPhase();
                });

                // Modell hat die Werkzeuge abgelehnt: merken, damit es beim nächsten Mal gar nicht erst versucht wird
                if (result.ToolsRejected)
                {
                    config.ModelTools = false;
                    _settings.Save(_store);
                    pluginNotes.Add("Hinweis: Dieses Modell kann keine Plugins nutzen – die Antwort kam ohne Plugins.");
                }
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
            // Manche lokalen Modelle (z. B. Gemma in LM Studio) reichen leere Denk-Markierungen durch – entfernen
            var cleaned = System.Text.RegularExpressions.Regex.Replace(answer.ToString(), @"<\|channel\|?>[\s\S]*?<\|?channel\|>", "").TrimStart();
            // Manche Modelle schreiben die Denkphase als <think>…</think> in den Text – als Gedankengang abtrennen
            var think = System.Text.RegularExpressions.Regex.Match(cleaned, @"^<think>([\s\S]*?)(?:</think>|$)");
            if (think.Success)
            {
                reasoning.Append(think.Groups[1].Value);
                cleaned = cleaned[think.Length..].TrimStart();
            }
            answer.Clear().Append(cleaned);
            var stats = MakeStats(result, config.Model, clock.Elapsed.TotalSeconds, firstChunkAt, cost ?? result.Cost) with
            {
                Tools = usedTools.Count > 0 ? string.Join(", ", usedTools.Distinct()) : null
            };
            var success = answer.Length > 0 || generated != null;

            // Fertige Antwort als Markdown darstellen (während des Streamings war es reiner Text)
            var thoughts = reasoning.ToString().Trim();
            answerView.Body.Blocks.Clear();
            if (thoughts.Length > 0)
                answerView.Body.Blocks.Add(MakeReasoningBlock(thoughts));
            if (generated != null)
                answerView.Body.Blocks.Add(new BlockUIContainer(MakeImageView(generated, 420, 560)));
            if (answer.Length > 0)
                MarkdownRenderer.Render(answer.ToString(), answerView.Body.Blocks);
            if (note != null)
                answerView.Body.Blocks.Add(new Paragraph(new Italic(new Run(note))) { Margin = new Thickness(0) });
            foreach (var pluginNote in pluginNotes)
                answerView.Body.Blocks.Add(new Paragraph(new Italic(new Run(pluginNote)))
                    { Margin = new Thickness(0, 4, 0, 0), FontSize = 12.5, Foreground = Brushes.DarkOrange });
            var reply = new ChatMessage("assistant", answer.ToString(), generated)
            {
                Stats = stats,
                Reasoning = thoughts.Length > 0 ? thoughts : null
            };

            if (success)
            {
                reply = reply with { Id = _store.AddMessage(chat.Id, reply) };
                _messages.Add(reply);
                _store.AddUsage(provider.Kind, stats);
                AddAnswerFooter(answerView.Body, reply);
            }
            else
            {
                // Ohne Antwort: Frage nicht speichern, sondern zum erneuten Senden zurück ins Eingabefeld
                _store.DeleteMessage(userMsg.Id);
                _messages.Remove(userMsg);
                InputBox.Text = text;
                if (image != null) SetImage(image);
                foreach (var document in documents)
                    AddDocument(document);
            }
            if (_followStream) ChatBox.ScrollToEnd();

            // Gerade erst angelegter Chat ohne Antwort: wieder entfernen (ein bestehender Chat bleibt, z. B. nach „Neu erzeugen“)
            if (_messages.Count == 0 && newChat)
            {
                _store.DeleteChat(chat.Id);
                _chats.Remove(chat);
                _currentChat = null;
                _pendingPersona = chat.Persona;
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
            UpdatePersonaChip();
            InputBox.Focus();
            if (success && budgetWarning != null)
                StatusText.Text = "Fertig. " + budgetWarning;

            // Erste Antwort in einem neuen Chat: Titel vom Modell vorschlagen lassen (läuft im Hintergrund)
            if (success && answer.Length > 0 && !imageMode && _settings.AutoTitle && _messages.Count == 2 && _currentChat is { } titled)
                _ = SuggestTitleAsync(titled.Id, request, text, answer.ToString());
        }
    }

    // Fragt dasselbe Modell nach einem kurzen Titel und benennt den Chat um (Fehler werden ignoriert, der alte Titel bleibt)
    private async Task SuggestTitleAsync(long chatId, ChatRequest original, string question, string answer)
    {
        static string Cut(string s, int max) => s.Length > max ? s[..max] + " …" : s;
        var prompt = "Gib diesem Gespräch einen kurzen, treffenden Titel (höchstens 6 Wörter, in der Sprache des Gesprächs). " +
                     "Antworte nur mit dem Titel – ohne Anführungszeichen, ohne Erklärung, ohne Satzzeichen am Ende.\n\n" +
                     $"Frage: {Cut(question, 1000)}\n\nAntwort: {Cut(answer, 1500)}";
        var request = original with { SystemPrompt = "", Messages = [new ChatMessage("user", prompt)] };
        var text = new StringBuilder();
        var result = new LlmClient.StreamResult();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await LlmClient.StreamAsync(request, chunk => text.Append(chunk), _ => { }, result, null, cts.Token);
        }
        catch (Exception)
        {
            return;   // Nur ein Komfort-Extra: bei jedem Fehler (auch aus dem Anthropic-SDK) bleibt einfach der alte Titel
        }
        if (result.InputTokens != null || result.OutputTokens != null || result.Cost != null)
            _store.AddUsage(original.Provider.Kind, new AnswerStats(original.Provider.Name, result.Model ?? original.Model, result.Hoster,
                result.InputTokens, result.OutputTokens, null, 0, result.Cost));

        var raw = System.Text.RegularExpressions.Regex.Replace(text.ToString(), @"<think>[\s\S]*?(</think>|$)|<\|channel\|?>[\s\S]*?<\|?channel\|>", "");
        var title = raw.Split('\n').Select(l => l.Trim().Trim('"', '„', '“', '\'', '*', '#', ' ').TrimEnd('.', ':'))
            .FirstOrDefault(l => l.Length > 0);
        if (title == null) return;
        if (title.Length > 60) title = title[..60] + "…";

        var index = _chats.ToList().FindIndex(c => c.Id == chatId);
        if (index < 0) return;   // inzwischen gelöscht
        _store.RenameChat(chatId, title);
        var renamed = _chats[index] with { Title = title };
        var wasSelected = _currentChat?.Id == chatId;
        _chats[index] = renamed;
        if (wasSelected)
        {
            _currentChat = renamed;
            SelectChatSilently(renamed);
            UpdateEmptyState();
        }
    }

    // Werkzeuge der aktiven Plugins für diese Antwort zusammenstellen (null = keine Plugins nutzen)
    private async Task<ToolBox?> PrepareToolsAsync(ProviderConfig config, Action<string> setPhase, List<string> used,
        List<string> notes, CancellationToken ct)
    {
        if (!_plugins.Enabled.Any()) return null;
        if (config.ModelTools == false)
        {
            notes.Add("Hinweis: Das gewählte Modell kann keine Plugins nutzen – sie wurden nicht angeboten.");
            return null;
        }
        var problems = new List<string>();
        var tools = await _plugins.CollectToolsAsync(setPhase, problems, ct);
        notes.AddRange(problems.Select(p => "Plugin nicht verfügbar – " + p));
        if (tools.Count == 0) return null;

        return new ToolBox
        {
            Specs = tools.Select(t => t.Spec).ToList(),
            Execute = async (name, arguments, token) =>
            {
                var entry = tools.FirstOrDefault(t => t.Spec.Name == name);
                if (entry.Plugin == null) return "Fehler: Dieses Werkzeug gibt es nicht.";
                var summary = ArgumentSummary(arguments);
                if (_plugins.AskBeforeRun(entry.Plugin))
                    setPhase($"❓ Warte auf deine Bestätigung für „{entry.Plugin.Name}“ (Fenster „Plugin ausführen?“)");
                if (_plugins.AskBeforeRun(entry.Plugin) && !ConfirmTool(entry.Plugin, entry.Tool, arguments))
                {
                    used.Add($"{entry.Plugin.Name} abgelehnt");
                    return "Der Nutzer hat diese Aktion nicht erlaubt. Frag nach oder antworte ohne.";
                }
                setPhase($"🔧 {entry.Plugin.Name}: {entry.Tool}{(summary.Length > 0 ? $" ({summary})" : "")}");
                used.Add(entry.Plugin.Name + (summary.Length > 0 ? $" ({summary})" : ""));
                try
                {
                    return await _plugins.ExecuteAsync(entry.Plugin, entry.Tool, arguments, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    return "Fehler beim Ausführen des Plugins: " + ex.Message;
                }
            }
        };
    }

    // Kurzfassung der Argumente für Anzeige: Werte hintereinander, gekürzt
    private static string ArgumentSummary(string json)
    {
        try
        {
            var values = (System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject)?
                .Select(p => p.Value?.ToString() ?? "").Where(v => v.Length > 0) ?? [];
            var text = string.Join(", ", values);
            return text.Length > 40 ? text[..40] + "…" : text;
        }
        catch (System.Text.Json.JsonException)
        {
            return "";
        }
    }

    private bool ConfirmTool(PluginManifest plugin, string tool, string arguments)
    {
        var args = arguments.Length > 600 ? arguments[..600] + " …" : arguments;
        return MessageBox.Show(this,
            $"Das Modell möchte das Plugin „{plugin.Name}“ verwenden:\n\nAktion: {tool}\nAngaben: {args}\n\n" +
            "Erlauben? (Die Rückfrage lässt sich unter Plugins → Einstellungen des Plugins abschalten.)",
            "Plugin ausführen?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _cts?.Cancel();
        _ = _plugins.DisposeAsync().AsTask();   // gestartete MCP-Server beenden
    }

    // ---------- Chatliste und Anzeige ----------

    private void ChatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || ChatList.SelectedItem is not ChatInfo chat) return;
        _currentChat = chat;
        _messages.Clear();
        _messages.AddRange(_store.LoadMessages(chat.Id));
        RenderChat();
        UpdateEmptyState();
        UpdatePersonaChip();
        NavChat.IsChecked = true;
        InputBox.Focus();
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        _currentChat = null;
        _pendingPersona = null;
        _messages.Clear();
        ChatBox.Document.Blocks.Clear();
        SelectChatSilently(null);
        UpdateEmptyState();
        UpdatePersonaChip();
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

    // Autoscroll nur, solange man unten ist: Wer während einer Antwort hochscrollt, wird nicht mehr nach unten gezogen
    private void ChatBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            _followStream = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 40;
    }

    // Gedankengang (Denkphase) als eingeklappter Bereich über der Antwort
    private BlockUIContainer MakeReasoningBlock(string reasoning)
    {
        var text = new TextBox
        {
            Text = reasoning,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            FontSize = 13,
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        text.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
        var words = reasoning.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var expander = new Expander
        {
            Header = $"💭 Gedankengang ({words:N0} Wörter)",
            Content = text,
            IsExpanded = false,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        expander.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
        return new BlockUIContainer(expander) { Margin = new Thickness(0, 0, 0, 8) };
    }

    // ---------- Bearbeiten und neu erzeugen ----------

    // Eigene Nachricht zurück ins Eingabefeld; sie und alles danach wird aus dem Chat entfernt
    private void EditMessage(ChatMessage message)
    {
        if (_cts != null) return;
        var index = _messages.FindIndex(m => m.Id == message.Id);
        if (index < 0 || _currentChat == null) return;
        if (_messages.Count - index > 2 && !ConfirmTruncate(_messages.Count - index - 1)) return;

        TruncateFrom(index);
        InputBox.Text = message.Content;
        InputBox.CaretIndex = InputBox.Text.Length;
        RestoreAttachments(message);
        StatusText.Text = "Nachricht bearbeiten und mit Enter neu senden.";
        InputBox.Focus();
    }

    // Die Frage zu dieser Antwort nochmal senden (mit dem aktuell eingestellten Modell); die alte Antwort wird ersetzt
    private void RegenerateAnswer(ChatMessage answer)
    {
        if (_cts != null) return;
        var index = _messages.FindIndex(m => m.Id == answer.Id);
        if (index < 1 || _messages[index - 1].Role != "user" || _currentChat == null)
        {
            StatusText.Text = "Zu dieser Antwort gibt es keine Frage zum erneuten Senden.";
            return;
        }
        if (index < _messages.Count - 1 && !ConfirmTruncate(_messages.Count - index - 1)) return;

        var question = _messages[index - 1];
        TruncateFrom(index - 1);
        InputBox.Text = question.Content;
        RestoreAttachments(question);
        Send_Click(this, new RoutedEventArgs());
    }

    // Bild und Dateien einer Nachricht wieder ans Eingabefeld hängen
    private void RestoreAttachments(ChatMessage message)
    {
        ClearImage();
        ClearDocuments();
        if (message.Image != null) SetImage(message.Image);
        foreach (var document in message.Documents ?? [])
            AddDocument(document);
    }

    private bool ConfirmTruncate(int later) =>
        MessageBox.Show(this, $"Danach folgen noch {later} weitere Nachricht(en). Sie werden dabei aus dem Chat entfernt. Fortfahren?",
            "Nachrichten ersetzen", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void TruncateFrom(int index)
    {
        _store.DeleteMessagesFrom(_currentChat!.Id, _messages[index].Id);
        _messages.RemoveRange(index, _messages.Count - index);
        RenderChat();
        UpdateEmptyState();
    }

    // ---------- Export ----------

    private void ExportChat_Click(object sender, RoutedEventArgs e)
    {
        if (_currentChat is not { } chat || _messages.Count == 0) return;
        var safeTitle = string.Concat(chat.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('…');
        var dialog = new SaveFileDialog
        {
            Title = "Chat exportieren",
            Filter = "Markdown|*.md|HTML – im Browser lesen, drucken oder als PDF speichern|*.html",
            FileName = $"{(safeTitle.Length > 0 ? safeTitle : "Chat")}.md"
        };
        if (dialog.ShowDialog(this) != true) return;

        var html = Path.GetExtension(dialog.FileName).Equals(".html", StringComparison.OrdinalIgnoreCase)
                   || Path.GetExtension(dialog.FileName).Equals(".htm", StringComparison.OrdinalIgnoreCase);
        try
        {
            File.WriteAllText(dialog.FileName, html ? ChatToHtml(chat) : ChatToMarkdown(chat), new UTF8Encoding(false));
            StatusText.Text = $"Chat exportiert: {dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Export fehlgeschlagen: {ex.Message}";
        }
    }

    private string ChatToMarkdown(ChatInfo chat)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {chat.Title}").AppendLine();
        sb.AppendLine($"*Exportiert aus LM Chat {AppVersion} am {DateTime.Now:dd.MM.yyyy HH:mm}*").AppendLine();
        foreach (var m in _messages)
        {
            sb.AppendLine("---").AppendLine();
            sb.AppendLine(m.Role == "user" ? "### Du" : $"### {m.Stats?.Model ?? "Antwort"}").AppendLine();
            if (m.Image != null)
                sb.AppendLine($"![Bild](data:image/png;base64,{Convert.ToBase64String(m.Image)})").AppendLine();
            foreach (var d in m.Documents ?? [])
                sb.AppendLine($"📄 *Angehängt: {d.Name}*").AppendLine();
            if (m.Content.Length > 0)
                sb.AppendLine(m.Content.TrimEnd()).AppendLine();
            if (m.Stats != null)
                sb.AppendLine($"<sub>{FormatStats(m.Stats)}</sub>").AppendLine();
        }
        return sb.ToString();
    }

    private string ChatToHtml(ChatInfo chat)
    {
        static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html>
            <html lang="de"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{Enc(chat.Title)}}</title>
            <style>
            body { font: 15px/1.6 "Segoe UI", system-ui, sans-serif; max-width: 820px; margin: 32px auto; padding: 0 16px; color: #1f1f1f; background: #fff; }
            h1 { font-size: 24px; margin-bottom: 4px; } .meta { color: #777; font-size: 12px; }
            .user { background: #f0f0f3; border-radius: 16px; padding: 10px 16px; margin: 24px 0 8px 15%; white-space: pre-wrap; }
            .answer { margin: 8px 0 24px; } .stats { color: #888; font-size: 11.5px; }
            pre { background: #f4f4f6; padding: 10px 12px; border-radius: 8px; overflow-x: auto; } code { font-family: "Cascadia Mono", Consolas, monospace; font-size: 13px; }
            table { border-collapse: collapse; } th, td { border: 1px solid #ccc; padding: 3px 8px; } img { max-width: 100%; border-radius: 8px; }
            details { color: #666; font-size: 13px; margin-bottom: 8px; } details div { white-space: pre-wrap; }
            @media print { body { margin: 0; } .user { break-inside: avoid; } }
            </style></head><body>
            <h1>{{Enc(chat.Title)}}</h1>
            <div class="meta">Exportiert aus LM Chat {{AppVersion}} am {{DateTime.Now:dd.MM.yyyy HH:mm}}</div>

            """);
        foreach (var m in _messages)
        {
            var img = m.Image != null ? $"<p><img src=\"data:image/png;base64,{Convert.ToBase64String(m.Image)}\" alt=\"Bild\"></p>" : "";
            var docs = string.Concat((m.Documents ?? []).Select(d => $"<div class=\"meta\">📄 Angehängt: {Enc(d.Name)}</div>"));
            if (m.Role == "user")
                sb.AppendLine($"<div class=\"user\">{img}{docs}{Enc(m.Content)}</div>");
            else
            {
                sb.AppendLine("<div class=\"answer\">");
                if (m.Reasoning is { Length: > 0 } r)
                    sb.AppendLine($"<details><summary>Gedankengang</summary><div>{Enc(r)}</div></details>");
                sb.AppendLine(img).AppendLine(MarkdownRenderer.ToHtml(m.Content));
                if (m.Stats != null)
                    sb.AppendLine($"<div class=\"stats\">{Enc(FormatStats(m.Stats))}</div>");
                sb.AppendLine("</div>");
            }
        }
        sb.AppendLine("</body></html>");
        return sb.ToString();
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
            if (m.Documents is { Count: > 0 } documents)
                content.Children.Add(DocumentList(documents));
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
                    Margin = new Thickness(0, m.Image != null || m.Documents is { Count: > 0 } ? 8 : 0, 0, 0),
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
            // Darunter klein: „Bearbeiten“ – Nachricht zurück ins Eingabefeld, Antworten ab hier werden ersetzt
            var (edit, _) = SmallButton("", "Bearbeiten", "Nachricht bearbeiten und neu senden");
            edit.HorizontalAlignment = HorizontalAlignment.Right;
            edit.Opacity = 0.75;
            edit.Click += (_, _) => EditMessage(m);
            blocks.Add(new BlockUIContainer(new StackPanel { Children = { bubble, edit } }) { Margin = new Thickness(80, top, 0, 0) });
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
            if (m.Reasoning is { Length: > 0 } thoughts)
                body.Blocks.Add(MakeReasoningBlock(thoughts));
            if (m.Image != null)
                body.Blocks.Add(new BlockUIContainer(MakeImageView(m.Image, 420, 560)));
            MarkdownRenderer.Render(m.Content, body.Blocks);
            AddAnswerFooter(body, m);
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
        if (s.Tools is { Length: > 0 })
            parts.Add("🔧 " + s.Tools);
        return string.Join("   ·   ", parts);
    }

    // Zeile unter einer Antwort: Knöpfe (Kopieren bzw. Bild kopieren/speichern) und Anbieter, Modell, Token/s, Tokens
    private void AddAnswerFooter(Section body, ChatMessage answer)
    {
        var (content, stats, image) = (answer.Content, answer.Stats, answer.Image);
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
        if (answer.Id != 0)
        {
            var (regenerate, _) = SmallButton("", "Neu erzeugen", "Dieselbe Frage nochmal stellen – mit dem aktuell eingestellten Modell");
            regenerate.Click += (_, _) => RegenerateAnswer(answer);
            line.Inlines.Add(new InlineUIContainer(regenerate) { BaselineAlignment = BaselineAlignment.Center });
        }
        if (_plugins.ActiveWebhook != null)
            line.Inlines.Add(WebhookButton(answer));
        if (stats != null)
            line.Inlines.Add(new Run("   " + FormatStats(stats)));
        body.Blocks.Add(line);
    }

    // Kleiner Knopf mit Symbol und Text (unter Nachrichten)
    private (Button Button, TextBlock Label) SmallButton(string glyph, string text, string tooltip, string? automationName = null)
    {
        var label = new TextBlock { Text = text, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var button = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Padding = new Thickness(6, 2, 8, 2),
            ToolTip = tooltip,
            FontSize = 11.5,
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
        System.Windows.Automation.AutomationProperties.SetName(button, automationName ?? text);
        return (button, label);
    }

    // Kleiner Knopf in der Zeile unter einer Antwort; zeigt nach dem Klick kurz eine Bestätigung
    private InlineUIContainer FooterButton(string glyph, string text, string automationName, string tooltip, Func<bool> action)
    {
        var (button, label) = SmallButton(glyph, text, tooltip, automationName);
        button.Click += async (_, _) =>
        {
            if (!action()) return;
            label.Text = text == "Speichern" ? "Gespeichert ✓" : "Kopiert ✓";
            await Task.Delay(1800);
            label.Text = text;
        };
        return new InlineUIContainer(button) { BaselineAlignment = BaselineAlignment.Center };
    }

    // Knopf „Senden“ (Plugin Webhook): schickt diese Antwort als JSON an die eingestellte Adresse
    private InlineUIContainer WebhookButton(ChatMessage answer)
    {
        var label = new TextBlock { Text = "Senden", Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var button = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Padding = new Thickness(6, 2, 8, 2),
            ToolTip = "Diese Antwort als JSON an den Webhook senden (Plugin „Webhook“)",
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                    label
                }
            }
        };
        System.Windows.Automation.AutomationProperties.SetName(button, "Antwort senden");
        button.Click += async (_, _) =>
        {
            if (_plugins.ActiveWebhook is not { } webhook)
            {
                StatusText.Text = "Das Plugin „Webhook“ ist ausgeschaltet (Plugins → Webhook).";
                return;
            }
            button.IsEnabled = false;
            label.Text = "Sende …";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string result;
            try
            {
                result = await _plugins.PostAnswerAsync(webhook, ToExport(answer), null, cts.Token);
            }
            catch (OperationCanceledException)
            {
                result = "Fehler: Zeitüberschreitung beim Senden.";
            }
            StatusText.Text = result;
            label.Text = result.StartsWith("Fehler") ? "Fehler ✗" : "Gesendet ✓";
            await Task.Delay(2500);
            label.Text = "Senden";
            button.IsEnabled = true;
        };
        return new InlineUIContainer(button) { BaselineAlignment = BaselineAlignment.Center };
    }

    // Antwort samt zugehöriger Frage für den Webhook
    private ExportAnswer ToExport(ChatMessage answer)
    {
        var index = _messages.FindLastIndex(m => m.Role == "assistant" && m.Content == answer.Content && m.Image == answer.Image);
        var question = index > 0 && _messages[index - 1].Role == "user" ? _messages[index - 1].Content : null;
        return new ExportAnswer(_currentChat?.Title ?? "", question, answer);
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
        ExportChatButton.IsEnabled = !busy;
        ChatList.IsEnabled = !busy;
        UpdateModelUi();
    }

    // ---------- Bilder ----------

    private void ImageButton_Click(object sender, RoutedEventArgs e)
    {
        var images = string.Join(";", ImageExtensions.Select(x => "*" + x));
        var documents = string.Join(";", DocumentReader.Extensions.Select(x => "*" + x));
        var dialog = new OpenFileDialog
        {
            Title = "Datei anhängen",
            Filter = $"Bilder und Dokumente|{images};{documents}|Bilder|{images}|PDF, Word und Text|{documents}",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true)
            AttachFiles(dialog.FileNames);
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
        AttachFiles(files);
        e.Handled = true;
    }

    private bool TryPasteImage()
    {
        if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList().Cast<string>().Where(f => IsImageFile(f) || DocumentReader.IsSupported(f)).ToList();
            if (files.Count == 0) return false;
            AttachFiles(files);
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
        AutoTitleCheck.IsChecked = _draft.AutoTitle;
        ShowTemplates();
        _temperatureRow.Value = _draft.Temperature;
        _topPRow.Value = _draft.TopP;
        _topKRow.Value = _draft.TopK;
        _maxTokensRow.Value = _draft.MaxTokens;
        _repeatPenaltyRow.Value = _draft.RepeatPenalty;
        _shownProvider = null;
        SelectProviderRow(_draft.Provider);
        UpdateModelUi();
    }

    // Eine Zeile der Anbieterliste: Name, ob er im Chat aktiv ist, und kurz sein Stand (Schlüssel bzw. Adresse, Modell)
    public sealed class ProviderRow(ProviderInfo info) : System.ComponentModel.INotifyPropertyChanged
    {
        private string _status = "", _activeMark = "";
        public ProviderInfo Info { get; } = info;
        public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }
        public string ActiveMark { get => _activeMark; set { _activeMark = value; Changed(nameof(ActiveMark)); } }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    }

    private readonly List<ProviderRow> _providerRows = ProviderInfo.All.Select(p => new ProviderRow(p)).ToList();

    private void SelectProviderRow(ProviderKind kind) =>
        ProviderList.SelectedItem = _providerRows.First(r => r.Info.Kind == kind);

    // Für den gerade gezeigten Anbieter zählen die Eingabefelder, für alle anderen der Entwurf
    private void UpdateProviderRows()
    {
        foreach (var row in _providerRows)
        {
            var kind = row.Info.Kind;
            var config = _draft.Config(kind);
            var shown = kind == _shownProvider;
            var key = shown ? ApiKeyBox.Password.Trim() : config.ApiKey;
            var server = shown ? ServerBox.Text.Trim() : config.Server;
            var model = shown ? ModelCombo.SelectedValue as string ?? config.Model : config.Model;
            var where = row.Info.NeedsKey ? (key.Length > 0 ? "Schlüssel ✓" : "kein Schlüssel")
                : server.Replace("http://", "").Replace("https://", "");
            row.Status = $"{where} · {(model.Length > 0 ? model : "kein Modell")}";
            row.ActiveMark = kind == _settings.Provider ? "  ● im Chat" : "";
        }
        var info = ShownProvider;
        var active = info.Kind == _settings.Provider;
        ProviderTitle.Text = info.Name;
        ModelHeading.Text = $"Modell – {info.Name}";
        UseProviderButton.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        ProviderActiveNote.Text = active ? "● Wird gerade im Chat verwendet." : $"Im Chat antwortet gerade {ActiveProvider.Name}.";
    }

    private void ProviderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderList.SelectedItem is not ProviderRow { Info: var info }) return;
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
            config.ModelTools = model.Tools;
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
        _modelsLoadedFor = null;
        _models.Clear();
        EnsureModelItem(config.Model, config.ModelVision, config.ModelImageGen, config.ModelTools);
        ModelCombo.SelectedValue = config.Model;
        UpdateSetupGuide();
        UpdateProviderRows();
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

    private void SaveSettings_Click(object sender, RoutedEventArgs e) => SaveSettings(activate: false);

    private void UseProvider_Click(object sender, RoutedEventArgs e) => SaveSettings(activate: true);

    // Speichert alle Einstellungen. Der Chat-Anbieter wechselt nur mit activate („Im Chat verwenden“) –
    // oder beim ersten Einrichten, sobald der gezeigte Anbieter vollständig ist (Schlüssel und Modell)
    private void SaveSettings(bool activate)
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
        var complete = (!info.NeedsKey || ApiKeyBox.Password.Trim().Length > 0) && ModelCombo.SelectedItem is ModelInfo;
        activate |= info.Kind == _settings.Provider || (!IsReady && complete);
        if (activate && info.NeedsKey && ApiKeyBox.Password.Trim().Length == 0)
        {
            SettingsStatus.Text = $"Für {info.Name} wird ein API-Schlüssel benötigt.";
            return;
        }
        if (activate && ModelCombo.SelectedItem is not ModelInfo)
        {
            SettingsStatus.Text = "Bitte ein Modell auswählen (ggf. erst „Liste aktualisieren“).";
            return;
        }

        ServerBox.Text = server;
        StoreProviderFields(info.Kind);
        if (activate)
            _draft.Provider = info.Kind;
        _draft.SystemPrompt = SystemPromptBox.Text.Trim();
        _draft.AutoTitle = AutoTitleCheck.IsChecked == true;
        StoreTemplates();
        _draft.Temperature = _temperatureRow.Value;
        _draft.TopP = _topPRow.Value;
        _draft.TopK = _topKRow.Value;
        _draft.MaxTokens = _maxTokensRow.Value;
        _draft.RepeatPenalty = _repeatPenaltyRow.Value;
        var wasReady = IsReady;
        _draft.Save(_store);
        _settings = _draft.Clone();
        _setupJustFinished |= !wasReady && IsReady;
        UpdateModelUi();
        UpdateProviderRows();
        SettingsStatus.Text = activate
            ? $"Gespeichert – im Chat aktiv: {info.Name}, {_settings.Current.Model} ({DateTime.Now:HH:mm:ss})."
            : $"Gespeichert – {info.Name} ist eingerichtet, im Chat antwortet weiter {ActiveProvider.Name} ({DateTime.Now:HH:mm:ss}).";
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
            _modelsLoadedFor = models.Count > 0 ? info.Kind : null;
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
        UpdateSetupGuide();

        // Fähigkeiten des aktiven Modells aktualisieren (z. B. nach dem Programmstart)
        if (info.Kind == _settings.Provider &&
            _models.FirstOrDefault(m => m.Id == _settings.Current.Model) is { } active &&
            ((active.Vision != null && active.Vision != _settings.Current.ModelVision) ||
             active.ImageGen != _settings.Current.ModelImageGen ||
             (active.Tools != null && active.Tools != _settings.Current.ModelTools)))
        {
            _settings.Current.ModelVision = active.Vision ?? _settings.Current.ModelVision;
            _settings.Current.ModelImageGen = active.ImageGen;
            _settings.Current.ModelTools = active.Tools ?? _settings.Current.ModelTools;
            _draft.Config(info.Kind).ModelVision = _settings.Current.ModelVision;
            _draft.Config(info.Kind).ModelImageGen = active.ImageGen;
            _draft.Config(info.Kind).ModelTools = _settings.Current.ModelTools;
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

    private void EnsureModelItem(string id, bool? vision, bool imageGen = false, bool? tools = null)
    {
        if (id.Length > 0 && _models.All(m => m.Id != id))
            _models.Add(new ModelInfo(id, vision, false, imageGen, tools));
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var model = ModelCombo.SelectedItem as ModelInfo;
        if (model != null)
            _selectedModelId = model.Id;
        UpdateSetupGuide();
        if (ProviderTitle != null) UpdateProviderRows();
        var visionHint = model?.Vision switch
        {
            true => "🖼 Dieses Modell versteht Bilder.",
            false => "Dieses Modell versteht nur Text.",
            _ => model == null ? "" : "Ob das Modell Bilder versteht, meldet dieser Anbieter nicht – einfach ausprobieren."
        };
        ModelHint.Text = model?.ImageGen == true
            ? "🎨 Dieses Modell erzeugt Bilder: Deine Nachricht ist die Bildbeschreibung. Ein angehängtes Bild (oder mit " +
              "„Verlauf mitsenden“ das zuletzt erzeugte) dient als Vorlage zum Bearbeiten. Bilder kosten meist mehr als Text."
            : visionHint + model?.Tools switch
            {
                true => " 🔧 Kann Plugins nutzen.",
                false => " Kann keine Plugins nutzen.",
                _ => ""
            };
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
              (vision == false ? " PDF-, Word- und Textdateien" : " Bilder sowie PDF-, Word- und Textdateien") +
              " kannst du per „Datei“, Strg+V oder Hineinziehen anhängen. „/“ öffnet Vorlagen und Personas.";
        UpdateSetupState();
        // Dateien gehen immer; Bilder nur, wenn das Modell sie versteht (Bildmodelle: nur Bilder als Vorlage)
        ImageButton.IsEnabled = _cts == null && !(CurrentImageGen && vision == false);
        ImageButton.ToolTip = CurrentImageGen ? "Bild als Vorlage anhängen (auch per Strg+V oder Hineinziehen)"
            : vision == false ? "PDF-, Word- oder Textdatei anhängen – Bilder versteht dieses Modell nicht"
            : "Bild, PDF-, Word- oder Textdatei anhängen (auch per Strg+V oder Hineinziehen)";
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
