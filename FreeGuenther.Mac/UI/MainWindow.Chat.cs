using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace LmChat;

// Chat: Liste, Verlauf, Eingabe, Senden, Bearbeiten, Export
internal sealed partial class MainWindow
{
    private readonly List<ChatInfo> _chats = new();
    private readonly List<ChatMessage> _messages = new();
    private ChatInfo? _currentChat;
    private byte[]? _pendingImage;
    private CancellationTokenSource? _cts;
    private bool _followStream = true;   // false, sobald man während einer Antwort hochscrollt
    private bool _checkingBudget;         // Kostenbremse wird gerade geprüft – kein zweites Senden
    private bool _suppressSelection;
    private bool _scrollQueued;

    private NSButton _newChatButton = null!;
    private NSSearchField _chatSearch = null!;
    private NSTextField _modelCardProvider = null!, _modelCardModel = null!;
    private ChatTable _chatTable = null!;
    private ChatListSource _chatSource = null!;

    private NSTextField _chatTitle = null!;
    private NSButton _deleteChatButton = null!, _exportChatButton = null!, _sendButton = null!, _stopButton = null!, _imageButton = null!, _historyCheck = null!;
    private NSScrollView _chatScroll = null!;
    private Col _chatColumn = null!;
    private NSView _emptyState = null!, _setupPanel = null!, _inputArea = null!;
    private WrapLabel _emptyHint = null!;
    private NSTextField _status = null!, _personaText = null!, _attachmentText = null!;
    private NSView _personaPanel = null!, _attachmentPanel = null!;
    private NSImageView _attachmentImage = null!;
    private Col _documentsPanel = null!;
    private InputView _input = null!;

    private bool IsBusy => _cts != null;
    private ProviderInfo ActiveProvider => ProviderInfo.Get(_settings.Provider);
    private bool? CurrentVision => _settings.Current.ModelVision;
    private bool CurrentImageGen => _settings.Current.ModelImageGen;

    // Startklar = Modell gewählt und gespeichert, bei Online-Diensten mit Schlüssel
    private bool IsReady => _settings.Current.Model.Length > 0 && (!ActiveProvider.NeedsKey || _settings.Current.ApiKey.Length > 0);

    // ---------- Chatliste ----------

    private sealed class ChatTable : NSTableView
    {
        public Action? DeletePressed;

        public override void KeyDown(NSEvent theEvent)
        {
            if (theEvent.KeyCode is 51 or 117) DeletePressed?.Invoke();
            else base.KeyDown(theEvent);
        }
    }

    // Zeilen der Liste: Datumsüberschrift (string) oder Chat
    private sealed class ChatListSource(MainWindow owner) : NSTableViewSource
    {
        public List<object> Rows { get; } = new();

        public override nint GetRowCount(NSTableView tableView) => Rows.Count;

        public override bool IsGroupRow(NSTableView tableView, nint row) => Rows[(int)row] is string;

        public override bool ShouldSelectRow(NSTableView tableView, nint row) => Rows[(int)row] is ChatInfo;

        public override nfloat GetRowHeight(NSTableView tableView, nint row) => Rows[(int)row] is string ? 24 : 28;

        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
        {
            var item = Rows[(int)row];
            var label = item is ChatInfo chat ? Ui.Label(chat.Title) : Ui.Label((string)item, 11, Ui.Secondary, bold: true);
            var cell = new NSTableCellView { TextField = label };   // TextField: Schriftfarbe passt sich der markierten Zeile an
            cell.AddSubview(label);
            Ui.Pin(label, cell, null, 6, null, 6);
            label.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor).Active = true;
            return cell;
        }

        public override void SelectionDidChange(NSNotification notification) => owner.ChatRowSelected();
    }

    private NSView BuildChatList()
    {
        _chatTable = new ChatTable { HeaderView = null, Style = NSTableViewStyle.SourceList, FloatsGroupRows = false, BackgroundColor = NSColor.Clear };
        _chatTable.AddColumn(new NSTableColumn("chat"));
        _chatSource = new ChatListSource(this);
        _chatTable.Source = _chatSource;
        _chatTable.DeletePressed = () => { if (SelectedRowChat() is { } chat) DeleteChat(chat); };
        var menu = new NSMenu();
        menu.AddItem(new NSMenuItem(L.UI("ui.ChatLoeschen"), (_, _) =>
        {
            if (_chatTable.ClickedRow >= 0 && _chatSource.Rows[(int)_chatTable.ClickedRow] is ChatInfo chat)
                DeleteChat(chat);
        }));
        _chatTable.Menu = menu;
        var scroll = new NSScrollView
        {
            DocumentView = _chatTable, HasVerticalScroller = true, AutohidesScrollers = true, DrawsBackground = false,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        return scroll;
    }

    private ChatInfo? SelectedRowChat() =>
        _chatTable.SelectedRow >= 0 ? _chatSource.Rows[(int)_chatTable.SelectedRow] as ChatInfo : null;

    private void ReloadChatList(bool fromStore = true)
    {
        if (fromStore)
        {
            _chats.Clear();
            _chats.AddRange(_store.ListChats());
        }
        var search = _chatSearch.StringValue.Trim();
        _chatSource.Rows.Clear();
        string? group = null;
        foreach (var chat in _chats.Where(c => c.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase)))
        {
            if (chat.DateGroup != group)
                _chatSource.Rows.Add(group = chat.DateGroup);
            _chatSource.Rows.Add(chat);
        }
        _suppressSelection = true;
        _chatTable.ReloadData();
        var index = _chatSource.Rows.FindIndex(r => r is ChatInfo c && c.Id == _currentChat?.Id);
        if (index >= 0) _chatTable.SelectRow(index, false);
        else _chatTable.DeselectAll(null);
        _suppressSelection = false;
        UpdateTiles();
    }

    private void ChatRowSelected()
    {
        if (_suppressSelection || SelectedRowChat() is not { } chat) return;
        _currentChat = chat;
        _messages.Clear();
        _messages.AddRange(_store.LoadMessages(chat.Id));
        RenderChat();
        UpdateEmptyState();
        UpdatePersonaChip();
        Navigate(Page.Chat);
    }

    public void NewChat()
    {
        if (IsBusy) return;
        _currentChat = null;
        _pendingPersona = null;
        _messages.Clear();
        _chatColumn.Clear();
        ReloadChatList(false);
        UpdateEmptyState();
        UpdatePersonaChip();
        Navigate(Page.Chat);
        _status.StringValue = L.T("Neuer Chat.");
    }

    private void DeleteChat(ChatInfo chat)
    {
        if (IsBusy)
        {
            _status.StringValue = L.T("Während eine Antwort läuft, kann kein Chat gelöscht werden.");
            return;
        }
        if (!Ui.Confirm(L.F("Chat „{0}“ wirklich löschen?", chat.Title), L.T("Chat löschen"))) return;
        _store.DeleteChat(chat.Id);
        if (chat.Id == _currentChat?.Id)
            NewChat();
        ReloadChatList();
        _status.StringValue = L.F("Chat „{0}“ gelöscht.", chat.Title);
    }

    private void UpdateEmptyState()
    {
        _emptyState.Hidden = !IsReady || _chatColumn.ArrangedSubviews.Length > 0;
        _chatTitle.StringValue = _currentChat?.Title ?? L.T("Neuer Chat");
        _deleteChatButton.Hidden = _currentChat == null;
        _exportChatButton.Hidden = _currentChat == null || _messages.Count == 0;
    }

    private void UpdateSetupState()
    {
        var ready = IsReady;
        _setupPanel.Hidden = ready;
        _chatScroll.Hidden = !ready;
        _inputArea.Hidden = !ready;
        UpdateEmptyState();
        UpdateSetupGuide();
    }

    // ---------- Aufbau der Seite ----------

    private NSView BuildChatPage()
    {
        var page = new DropView { Dropped = AttachFiles };

        _chatTitle = Ui.Label(L.UI("ui.NeuerChat"), 16, bold: true);
        _exportChatButton = Ui.IconButton("square.and.arrow.up", L.UI("ui.ChatExportierenAlsMarkdownOder"), ExportChat, 15);
        _deleteChatButton = Ui.IconButton("trash", L.UI("ui.DiesenChatLoeschen"), () => { if (_currentChat != null) DeleteChat(_currentChat); }, 15);
        var header = Ui.Row(12, _chatTitle, Ui.Spacer(), _exportChatButton, _deleteChatButton);
        page.AddSubview(header);
        Ui.Pin(header, page, 34, 28, null, 20);

        var (scroll, document) = Ui.Scroller();
        _chatScroll = scroll;
        _chatColumn = new Col(0);
        document.AddSubview(_chatColumn);
        Ui.Pin(_chatColumn, document, 8, null, 16, null);
        _chatColumn.CenterXAnchor.ConstraintEqualTo(document.CenterXAnchor).Active = true;
        _chatColumn.LeadingAnchor.ConstraintGreaterThanOrEqualTo(document.LeadingAnchor, 28).Active = true;
        var width = _chatColumn.WidthAnchor.ConstraintEqualTo(800);
        width.Priority = 500;
        width.Active = true;
        page.AddSubview(scroll);
        scroll.TopAnchor.ConstraintEqualTo(header.BottomAnchor, 6).Active = true;
        Ui.Pin(scroll, page, null, 0, null, 0);

        // Autoscroll nur, solange man unten ist: Wer während einer Antwort hochscrollt, wird nicht mehr nach unten gezogen
        scroll.ContentView.PostsBoundsChangedNotifications = true;
        NSNotificationCenter.DefaultCenter.AddObserver(NSView.BoundsChangedNotification, _ =>
        {
            var end = scroll.ContentView.Bounds.Bottom >= (scroll.DocumentView?.Frame.Height ?? 0) - 40;
            if (!_autoScrolling) _followStream = end;
        }, scroll.ContentView);

        _inputArea = BuildInputArea();
        page.AddSubview(_inputArea);
        _inputArea.TopAnchor.ConstraintEqualTo(scroll.BottomAnchor, 6).Active = true;
        Ui.Pin(_inputArea, page, null, null, 10, null);
        _inputArea.CenterXAnchor.ConstraintEqualTo(page.CenterXAnchor).Active = true;
        _inputArea.LeadingAnchor.ConstraintGreaterThanOrEqualTo(page.LeadingAnchor, 24).Active = true;
        var inputWidth = _inputArea.WidthAnchor.ConstraintEqualTo(830);
        inputWidth.Priority = 500;
        inputWidth.Active = true;

        // Startansicht für einen leeren Chat
        var empty = new Col(8) { Alignment = NSLayoutAttribute.CenterX };
        empty.Add(new NSImageView
        {
            Image = NSImage.FromStream(typeof(MainWindow).Assembly.GetManifestResourceStream("app-logo.png")!)
        }, stretch: false);
        ((NSImageView)empty.ArrangedSubviews[0]).WidthAnchor.ConstraintEqualTo(56).Active = true;
        ((NSImageView)empty.ArrangedSubviews[0]).HeightAnchor.ConstraintEqualTo(56).Active = true;
        empty.Add(Ui.Label(L.UI("ui.WieKannIchHelfen"), 30, bold: true), stretch: false);
        _emptyHint = new WrapLabel(L.UI("ui.StellEineFrageOderHaeng"), 14, Ui.Secondary) { Alignment = NSTextAlignment.Center };
        _emptyHint.WidthAnchor.ConstraintEqualTo(520).Active = true;
        empty.Add(_emptyHint, stretch: false);
        _emptyState = empty;
        page.AddSubview(empty);
        empty.CenterXAnchor.ConstraintEqualTo(scroll.CenterXAnchor).Active = true;
        empty.CenterYAnchor.ConstraintEqualTo(scroll.CenterYAnchor).Active = true;

        _setupPanel = BuildSetupPanel();
        page.AddSubview(_setupPanel);
        Ui.Pin(_setupPanel, page, 0, 0, 0, 0);
        return page;
    }

    // Erster Start bzw. nichts eingerichtet: an die Hand nehmen statt Chat
    private NSView BuildSetupPanel()
    {
        var column = new Col(14);
        var title = Ui.Label(L.UI("ui.WillkommenBeiFreeGuenther"), 30, bold: true);
        column.Add(title, stretch: false);
        column.Add(Ui.Body(L.UI("ui.ZumChattenBrauchstDuEin")));

        void SetupProvider(ProviderKind kind)
        {
            SelectProviderRow(kind);
            Navigate(Page.Settings);
        }
        var local = new Col(10);
        local.Add(Ui.Heading(L.UI("ui.LokalAufDeinemRechnerKostenlos")));
        local.Add(Ui.Body(L.UI("ui.DeineTexteVerlassenDenRechner")));
        local.Add(Ui.Row(8,
            Ui.Button(L.UI("ui.LMStudioEinrichten"), () => SetupProvider(ProviderKind.LmStudio), primary: true),
            Ui.Button(L.UI("ui.OllamaEinrichten"), () => SetupProvider(ProviderKind.Ollama))), stretch: false);
        column.Add(Ui.Card(local));

        var online = new Col(10);
        online.Add(Ui.Heading(L.UI("ui.OnlineMitAPISchluessel")));
        online.Add(new Rich().Text(L.UI("ui.DieGrossenSchnellenModelleLaufen") + " ").Bold(L.UI("ui.APISchluessel")).Text(" " + L.UI("ui.EineArtPasswortFuerProgramme") + " ")
            .Bold(L.UI("ui.OpenRouter")).Text(" " + L.UI("ui.IstFuerDenAnfangAm")).View());
        online.Add(Ui.Row(8,
            Ui.Button(L.UI("ui.OpenRouterEinrichtenEmpfohlen"), () => SetupProvider(ProviderKind.OpenRouter), primary: true),
            Ui.Button(L.UI("ui.OpenAI"), () => SetupProvider(ProviderKind.OpenAI)),
            Ui.Button(L.UI("ui.AnthropicClaude"), () => SetupProvider(ProviderKind.Anthropic))), stretch: false);
        online.Add(new WrapLabel(L.UI("ui.SetzeBeimAnbieterUnbedingtEin") + " " + L.UI("ui.WarumStehtInDerHilfe") + ".", 12, Ui.Warning));
        column.Add(Ui.Card(online));

        var scroll = Ui.ScrollPage(column, 760, 48);
        return scroll;
    }

    private NSView BuildInputArea()
    {
        var area = new Col(4);

        BuildTemplatePicker(area);

        var card = new Col(8);
        _personaText = Ui.Label("", 12);
        var removePersona = Ui.IconButton("xmark", L.UI("ui.PersonaEntfernen"), () => SetPersona(null), 10);
        _personaPanel = Ui.Card(Ui.Row(6, _personaText, removePersona), Ui.PersonaFill, NSColor.Clear, 5, 9);
        _personaPanel.Hidden = true;
        card.Add(_personaPanel, stretch: false);

        _documentsPanel = new Col(4);
        _documentsPanel.Hidden = true;
        card.Add(_documentsPanel);

        _attachmentImage = new NSImageView { ImageScaling = NSImageScale.ProportionallyDown };
        _attachmentImage.HeightAnchor.ConstraintEqualTo(56).Active = true;
        _attachmentImage.WidthAnchor.ConstraintEqualTo(84).Active = true;
        _attachmentText = Ui.Label("", 12, Ui.Secondary);
        var removeImage = Ui.IconButton("xmark", L.UI("ui.BildEntfernen"), ClearImage, 11);
        _attachmentPanel = Ui.Card(Ui.Row(8, _attachmentImage, _attachmentText, removeImage), NSColor.FromSrgb(0.5f, 0.5f, 0.5f, 0.12f), NSColor.Clear, 6, 9);
        _attachmentPanel.Hidden = true;
        card.Add(_attachmentPanel, stretch: false);

        _input = new InputView(L.UI("ui.NachrichtSchreiben"))
        {
            Send = Send,
            HandleKey = HandleTemplateKeys,
            TextEdited = InputChanged,
            PasteFiles = TryPaste,
            Dropped = AttachFiles
        };
        card.Add(_input);

        _imageButton = Ui.SmallButton("paperclip", L.UI("ui.Datei"), L.UI("ui.DateiAnhaengen"), PickFiles);
        var templates = Ui.SmallButton("text.badge.plus", L.UI("ui.Vorlagen"), L.UI("ui.VorlagenUndPersonasOderIns"), () =>
        {
            _input.Text = "/";
            InputChanged();
            FocusInput();
        });
        _historyCheck = Ui.Check(L.UI("ui.VerlaufMitsenden"));
        _historyCheck.State = NSCellStateValue.On;
        _historyCheck.ToolTip = L.UI("ui.MitHakenKenntDasModell");
        _historyCheck.Font = NSFont.SystemFontOfSize(12);
        _sendButton = Ui.IconButton("arrow.up.circle.fill", L.UI("ui.SendenEnter"), Send, 22);
        _sendButton.ContentTintColor = NSColor.ControlAccent;
        _stopButton = Ui.IconButton("stop.circle.fill", L.UI("ui.AntwortAbbrechen"), () => _cts?.Cancel(), 22);
        _stopButton.Hidden = true;
        card.Add(Ui.Row(10, _imageButton, templates, _historyCheck, Ui.Spacer(), _stopButton, _sendButton));

        var box = Ui.Card(card, NSColor.ControlBackground, NSColor.Separator, 12, 16);
        area.Add(box);
        _status = Ui.Label("", 11.5f, Ui.Secondary);
        _status.ToolTip = L.UI("ui.EnterSendenUmschaltEnterNeue");
        area.Add(Ui.Pad(_status, 2, 8, 0, 8));
        return area;
    }

    private void FocusInput() => _input.Focus();

    private void UpdateTiles() => _tileChats.StringValue = _chats.Count.ToString();

    // ---------- Chat senden ----------

    private async void Send()
    {
        var text = _input.Text.Trim();
        var image = _pendingImage;
        var documents = _pendingDocuments.ToList();
        if ((text.Length == 0 && image == null && documents.Count == 0) || IsBusy || _checkingBudget) return;

        var provider = ActiveProvider;
        var config = _settings.Current;
        if (config.Model.Length == 0)
        {
            _status.StringValue = L.T("Bitte zuerst unter „Einstellungen“ ein Modell auswählen und speichern.");
            return;
        }
        if (provider.NeedsKey && config.ApiKey.Length == 0)
        {
            _status.StringValue = L.F("Für {0} fehlt der API-Schlüssel – bitte unter „Einstellungen“ eintragen.", provider.Name);
            return;
        }
        if (image != null && CurrentVision == false)
        {
            _status.StringValue = L.T("Das gewählte Modell kann keine Bilder verarbeiten – Bild entfernen oder Modell wechseln.");
            return;
        }
        var imageMode = CurrentImageGen;
        if (imageMode && text.Length == 0)
        {
            _status.StringValue = L.T("Beschreibe in Worten, welches Bild erzeugt bzw. wie das Bild verändert werden soll.");
            return;
        }
        if (imageMode && documents.Count > 0)
        {
            _status.StringValue = L.T("Bildmodelle können keine Dateien lesen – Datei entfernen oder ein Text-Modell wählen.");
            return;
        }

        _checkingBudget = true;
        var (allowed, budgetWarning) = await CheckBudgetAsync([provider]);
        _checkingBudget = false;
        if (!allowed)
        {
            _status.StringValue = L.T("Nicht gesendet – die Kostenbremse ist erreicht (siehe „Verbrauch“).");
            return;
        }

        var newChat = _currentChat == null;
        if (_currentChat == null)
        {
            var title = MakeTitle(text.Length == 0 && documents.Count > 0 ? documents[0].Name : text);
            _currentChat = _store.CreateChat(title, _pendingPersona);
            ReloadChatList();
        }
        var chat = _currentChat;

        var userMsg = new ChatMessage("user", text, image) { Documents = documents.Count > 0 ? documents : null };
        userMsg = userMsg with { Id = _store.AddMessage(chat.Id, userMsg) };
        _messages.Add(userMsg);
        _input.Text = "";
        ClearImage();
        ClearDocuments();
        _followStream = true;
        AddMessageBlock(userMsg);
        var answerView = AddMessageBlock(new ChatMessage("assistant", ""), streaming: true);

        List<ChatMessage> toSend = _historyCheck.State == NSCellStateValue.On ? _messages.ToList() : [userMsg];
        // Mit angehängten Dateien: Regel gegen Anweisungen, die in den Dateien versteckt sind (siehe PromptGuard)
        var systemPrompt = SystemPromptFor(chat.Persona);
        if (toSend.Any(m => m.Documents is { Count: > 0 }))
            systemPrompt = string.Join("\n\n", new[] { systemPrompt, PromptGuard.ChatRules }.Where(s => s.Length > 0));
        var request = new ChatRequest(provider, config.Server, config.Region, config.ApiKey, config.Model,
            systemPrompt, _settings, toSend);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
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
        var phase = L.F("Anfrage an {0} wird vorbereitet", provider.Name);
        var phaseLine = new WrapLabel("", 13, Ui.Secondary);
        answerView.Body.InsertArrangedSubview(phaseLine, 0);
        phaseLine.LeadingAnchor.ConstraintEqualTo(answerView.Body.LeadingAnchor).Active = true;
        phaseLine.TrailingAnchor.ConstraintEqualTo(answerView.Body.TrailingAnchor).Active = true;
        var ticker = new Ticker();
        void ShowPhase()
        {
            var seconds = (int)clock.Elapsed.TotalSeconds;
            var hint = seconds < 30 ? ""
                : " – " + L.T(provider.IsLocal && !imageMode ? "dauert länger, evtl. wird das Modell erst geladen"
                    : "dauert länger als üblich, abbrechen geht jederzeit mit ■");
            var line = $"⏳ {phase} … {seconds} s{hint}";
            // Während der Denkphase das Ende des Gedankengangs mitlaufen lassen
            if (reasoning.Length > 0)
            {
                var tail = reasoning.ToString(Math.Max(0, reasoning.Length - 160), Math.Min(160, reasoning.Length))
                    .ReplaceLineEndings(" ").Trim();
                line += $"\n💭 … {tail}";
            }
            phaseLine.StringValue = line;
            _status.StringValue = phase + " …";
            ScrollChatToEnd();
        }
        void SetPhase(string value)
        {
            phase = value;
            // Nach Text wieder einblenden (z. B. wenn das Modell zwischendurch ein Plugin aufruft) – dann unter dem Text
            if (!ticker.IsEnabled)
            {
                phaseLine.Hidden = false;
                answerView.Body.RemoveArrangedSubview(phaseLine);
                answerView.Body.AddArrangedSubview(phaseLine);
                ticker.Start(1);
            }
            ShowPhase();
        }
        void HidePhase()
        {
            if (!ticker.IsEnabled) return;
            ticker.Stop();
            phaseLine.Hidden = true;
            _status.StringValue = L.T("Antwort wird empfangen …");
        }
        ticker.Tick += ShowPhase;
        ticker.Start(1);
        ShowPhase();

        var streamFont = NSFont.SystemFontOfSize(15);
        var streamAttributes = new NSStringAttributes { Font = streamFont, ForegroundColor = NSColor.Label };
        try
        {
            if (imageMode)
            {
                // Bildmodell: Nachricht = Bildbeschreibung. Vorlage = angehängtes Bild, sonst (mit Verlauf) das zuletzt erzeugte
                var reference = image ?? (_historyCheck.State == NSCellStateValue.On
                    ? _messages.LastOrDefault(m => m.Role == "assistant" && m.Image != null)?.Image
                    : null);
                SetPhase(reference != null
                    ? "🎨 " + L.F("{0} bearbeitet das Bild – das kann eine Weile dauern", provider.Name)
                    : "🎨 " + L.F("{0} erzeugt das Bild – das kann eine Weile dauern", provider.Name));
                var img = await LlmClient.GenerateImageAsync(request, text, reference, token);
                generated = Images.ToPng(img.Image) ?? img.Image;
                result.InputTokens = img.InputTokens;
                result.OutputTokens = img.OutputTokens;
                cost = img.Cost;
            }
            else
            {
                var toolBox = await PrepareToolsAsync(config, SetPhase, usedTools, pluginNotes, token);
                await LlmClient.StreamAsync(request, chunk => OnMain(() =>
                {
                    HidePhase();
                    firstChunkAt ??= clock.Elapsed.TotalSeconds;
                    answer.Append(chunk);
                    answerView.Stream!.Append(new NSAttributedString(chunk, streamAttributes));
                    ScrollChatToEnd();
                }), p => OnMain(() =>
                {
                    // Denk-Tokens zählen zur Ausgabe – ab hier wird für Token/s gemessen
                    if (p == LlmClient.Phase.Thinking) firstChunkAt ??= clock.Elapsed.TotalSeconds;
                    if (!ticker.IsEnabled) return;
                    SetPhase(p switch
                    {
                        LlmClient.Phase.FindingHosters => L.F("Suche Hoster mit Standort {0}", RegionOption.Name(config.Region)),
                        LlmClient.Phase.Sending => L.F("Anfrage an {0} gesendet, warte auf Antwort", provider.Name),
                        LlmClient.Phase.Connected => L.F("{0} hat die Anfrage angenommen, warte auf die ersten Wörter", provider.Name),
                        _ => "💭 " + L.T("Das Modell denkt nach")
                    });
                }), result, toolBox, token, piece => OnMain(() =>
                {
                    reasoning.Append(piece);
                    if (ticker.IsEnabled) ShowPhase();
                }));

                // Modell hat die Werkzeuge abgelehnt: merken, damit es beim nächsten Mal gar nicht erst versucht wird
                if (result.ToolsRejected)
                {
                    config.ModelTools = false;
                    _settings.Save(_store);
                    pluginNotes.Add(L.T("Hinweis: Dieses Modell kann keine Plugins nutzen – die Antwort kam ohne Plugins."));
                }
            }
            ticker.Stop();
            _status.StringValue = L.T("Fertig.");
        }
        catch (OperationCanceledException)
        {
            note = L.T("[abgebrochen]");
            _status.StringValue = L.T("Abgebrochen.");
        }
        catch (Exception ex)
        {
            note = L.F("[Fehler: {0}]", ex.Message);
            _status.StringValue = L.T("Fehler – Anbieter, Schlüssel und Modell in den Einstellungen prüfen.");
        }
        finally
        {
            ticker.Stop();
            // Manche lokalen Modelle (z. B. Gemma in LM Studio) reichen leere Denk-Markierungen durch – entfernen
            var cleaned = Regex.Replace(answer.ToString(), @"<\|channel\|?>[\s\S]*?<\|?channel\|>", "").TrimStart();
            // Manche Modelle schreiben die Denkphase als <think>…</think> in den Text – als Gedankengang abtrennen
            var think = Regex.Match(cleaned, @"^<think>([\s\S]*?)(?:</think>|$)");
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
            var body = answerView.Body;
            body.Clear();
            if (thoughts.Length > 0)
                body.Add(MakeReasoningBlock(thoughts));
            if (generated != null)
                AddImage(body, generated, 420, 560);
            if (answer.Length > 0)
                MarkdownRenderer.Render(answer.ToString(), body);
            if (note != null)
                body.Add(new WrapLabel(note, 13, Ui.Secondary));
            foreach (var pluginNote in pluginNotes)
                body.Add(new WrapLabel(pluginNote, 12.5f, NSColor.SystemOrange));
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
                AddAnswerFooter(body, reply);
            }
            else
            {
                // Ohne Antwort: Frage nicht speichern, sondern zum erneuten Senden zurück ins Eingabefeld
                _store.DeleteMessage(userMsg.Id);
                _messages.Remove(userMsg);
                _input.Text = text;
                if (image != null) SetImage(image);
                foreach (var document in documents)
                    AddDocument(document);
            }
            ScrollChatToEnd();

            // Gerade erst angelegter Chat ohne Antwort: wieder entfernen (ein bestehender Chat bleibt, z. B. nach „Neu erzeugen“)
            if (_messages.Count == 0 && newChat)
            {
                _store.DeleteChat(chat.Id);
                _currentChat = null;
                _pendingPersona = chat.Persona;
                ReloadChatList();
            }
            else
            {
                _currentChat = chat with { Updated = _store.Touch(chat.Id) };
                ReloadChatList();
            }

            _cts.Dispose();
            _cts = null;
            SetBusy(false);
            UpdateEmptyState();
            UpdatePersonaChip();
            FocusInput();
            if (success && budgetWarning != null)
                _status.StringValue = L.T("Fertig.") + " " + budgetWarning;

            // Erste Antwort in einem neuen Chat: Titel vom Modell vorschlagen lassen (läuft im Hintergrund)
            if (success && answer.Length > 0 && !imageMode && _settings.AutoTitle && _messages.Count == 2 && _currentChat is { } titled)
                _ = SuggestTitleAsync(titled.Id, request, text, answer.ToString());
        }
    }

    // Rückrufe aus dem Netzwerkteil laufen je nach Bibliothek auf einem Hintergrundthread
    private static void OnMain(Action action)
    {
        if (NSThread.IsMain) action();
        else NSApplication.SharedApplication.BeginInvokeOnMainThread(action);
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
            await LlmClient.StreamAsync(request, chunk => { lock (text) text.Append(chunk); }, _ => { }, result, null, cts.Token);
        }
        catch (Exception)
        {
            return;   // Nur ein Komfort-Extra: bei jedem Fehler (auch aus dem Anthropic-SDK) bleibt einfach der alte Titel
        }
        if (result.InputTokens != null || result.OutputTokens != null || result.Cost != null)
            _store.AddUsage(original.Provider.Kind, new AnswerStats(original.Provider.Name, result.Model ?? original.Model, result.Hoster,
                result.InputTokens, result.OutputTokens, null, 0, result.Cost));

        var raw = Regex.Replace(text.ToString(), @"<think>[\s\S]*?(</think>|$)|<\|channel\|?>[\s\S]*?<\|?channel\|>", "");
        var title = raw.Split('\n').Select(l => l.Trim().Trim('"', '„', '“', '\'', '*', '#', ' ').TrimEnd('.', ':'))
            .FirstOrDefault(l => l.Length > 0);
        if (title == null) return;
        if (title.Length > 60) title = title[..60] + "…";

        OnMain(() =>
        {
            if (_chats.All(c => c.Id != chatId)) return;   // inzwischen gelöscht
            _store.RenameChat(chatId, title);
            if (_currentChat?.Id == chatId)
                _currentChat = _currentChat with { Title = title };
            ReloadChatList();
            UpdateEmptyState();
        });
    }

    // Werkzeuge der aktiven Plugins für diese Antwort zusammenstellen (null = keine Plugins nutzen)
    private async Task<ToolBox?> PrepareToolsAsync(ProviderConfig config, Action<string> setPhase, List<string> used,
        List<string> notes, CancellationToken ct)
    {
        if (!_plugins.Enabled.Any()) return null;
        if (config.ModelTools == false)
        {
            notes.Add(L.T("Hinweis: Das gewählte Modell kann keine Plugins nutzen – sie wurden nicht angeboten."));
            return null;
        }
        var problems = new List<string>();
        var tools = await _plugins.CollectToolsAsync(s => OnMain(() => setPhase(s)), problems, ct);
        notes.AddRange(problems.Select(p => L.T("Plugin nicht verfügbar") + " – " + p));
        if (tools.Count == 0) return null;

        return new ToolBox
        {
            Specs = tools.Select(t => t.Spec).ToList(),
            Execute = async (name, arguments, token) =>
            {
                var entry = tools.FirstOrDefault(t => t.Spec.Name == name);
                if (entry.Plugin == null) return "Fehler: Dieses Werkzeug gibt es nicht.";
                var summary = ArgumentSummary(arguments);
                var ask = _plugins.AskBeforeRun(entry.Plugin);
                if (ask)
                {
                    OnMain(() => setPhase("❓ " + L.F("Warte auf deine Bestätigung für „{0}“ (Fenster „Plugin ausführen?“)", entry.Plugin.Name)));
                    var confirmed = new TaskCompletionSource<bool>();
                    OnMain(() => confirmed.SetResult(ConfirmTool(entry.Plugin, entry.Tool, arguments)));
                    if (!await confirmed.Task)
                    {
                        used.Add(L.F("{0} abgelehnt", entry.Plugin.Name));
                        return "Der Nutzer hat diese Aktion nicht erlaubt. Frag nach oder antworte ohne.";
                    }
                }
                OnMain(() => setPhase($"🔧 {entry.Plugin.Name}: {entry.Tool}{(summary.Length > 0 ? $" ({summary})" : "")}"));
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

    private static bool ConfirmTool(PluginManifest plugin, string tool, string arguments)
    {
        var args = arguments.Length > 600 ? arguments[..600] + " …" : arguments;
        return Ui.Confirm(
            L.F("Das Modell möchte das Plugin „{0}“ verwenden:", plugin.Name) + "\n\n" + L.T("Aktion:") + $" {tool}\n" + L.T("Angaben:") + $" {args}\n\n" +
            L.T("Erlauben? (Die Rückfrage lässt sich unter Plugins → Einstellungen des Plugins abschalten.)"),
            L.T("Plugin ausführen?"));
    }

    private static string MakeTitle(string text)
    {
        var firstLine = text.Split('\n')[0].Trim();
        if (firstLine.Length == 0) return L.T("Bild");
        return firstLine.Length > 40 ? firstLine[..40] + "…" : firstLine;
    }

    // ---------- Anzeige der Nachrichten ----------

    private void RenderChat()
    {
        _chatColumn.Clear();
        foreach (var m in _messages)
            AddMessageBlock(m);
        _followStream = true;
        ScrollChatToEnd();
    }

    private bool _autoScrolling;

    // Sammelt Aufrufe: beim Streamen kommen viele Stücke pro Sekunde
    private void ScrollChatToEnd()
    {
        if (!_followStream || _scrollQueued) return;
        _scrollQueued = true;
        NSTimer.CreateScheduledTimer(0.08, _ =>
        {
            _scrollQueued = false;
            if (!_followStream || _chatScroll.DocumentView is not { } document) return;
            document.LayoutSubtreeIfNeeded();
            _autoScrolling = true;
            var y = Math.Max(0, document.Frame.Height - _chatScroll.ContentView.Bounds.Height);
            _chatScroll.ContentView.ScrollToPoint(new CGPoint(0, y));
            _chatScroll.ReflectScrolledClipView(_chatScroll.ContentView);
            _autoScrolling = false;
        });
    }

    // Body = Inhalt der Nachricht; Stream = Text, in den während des Empfangs geschrieben wird
    private sealed record MessageView(Col Body, TextBlockView? Stream);

    private MessageView AddMessageBlock(ChatMessage m, bool streaming = false)
    {
        nfloat top = _chatColumn.ArrangedSubviews.Length == 0 ? 8 : 22;

        // Eigene Nachrichten: rechtsbündige, abgerundete Blase (Text markier- und kopierbar)
        if (m.Role == "user")
        {
            var content = new Col(8);
            if (m.Image != null)
                AddImage(content, m.Image, 180, 320);
            if (m.Documents is { Count: > 0 } documents)
                content.Add(DocumentList(documents));
            if (m.Content.Length > 0)
            {
                var text = new TextBlockView();
                var attributed = new NSAttributedString(m.Content, new NSStringAttributes { Font = NSFont.SystemFontOfSize(15), ForegroundColor = NSColor.Label });
                text.SetAttributed(attributed);
                var fit = attributed.BoundingRectWithSize(new CGSize(528, 100_000), NSStringDrawingOptions.UsesLineFragmentOrigin);
                var wanted = text.WidthAnchor.ConstraintEqualTo((nfloat)(Math.Ceiling(fit.Width) + 2));
                wanted.Priority = 700;
                wanted.Active = true;
                content.Add(text, stretch: false);
            }
            var bubble = Ui.Card(content, NSColor.FromSrgb(0.5f, 0.5f, 0.5f, 0.14f), NSColor.Clear, 11, 18);
            bubble.WidthAnchor.ConstraintLessThanOrEqualTo(560).Active = true;
            // Darunter klein: „Bearbeiten“ – Nachricht zurück ins Eingabefeld, Antworten ab hier werden ersetzt
            var edit = Ui.SmallButton("pencil", L.T("Bearbeiten"), L.T("Nachricht bearbeiten und neu senden"), () => EditMessage(m));

            var wrap = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
            wrap.AddSubview(bubble);
            wrap.AddSubview(edit);
            Ui.Pin(bubble, wrap, top, null, null, 0);
            bubble.LeadingAnchor.ConstraintGreaterThanOrEqualTo(wrap.LeadingAnchor, 80).Active = true;
            edit.TopAnchor.ConstraintEqualTo(bubble.BottomAnchor, 2).Active = true;
            Ui.Pin(edit, wrap, null, null, 0, 0);
            _chatColumn.Add(wrap);
            ScrollChatToEnd();
            UpdateEmptyState();
            return new MessageView(new Col(), null);
        }

        // Antworten: ohne Blase als gut lesbarer Fließtext (Markdown)
        var body = new Col(0);
        _chatColumn.Add(Ui.Pad(body, top, 0, 0, 0));
        TextBlockView? stream = null;
        if (streaming)
        {
            stream = new TextBlockView();
            body.Add(stream);
        }
        else
        {
            if (m.Reasoning is { Length: > 0 } thoughts)
                body.Add(MakeReasoningBlock(thoughts));
            if (m.Image != null)
                AddImage(body, m.Image, 420, 560);
            MarkdownRenderer.Render(m.Content, body);
            AddAnswerFooter(body, m);
        }
        ScrollChatToEnd();
        UpdateEmptyState();
        return new MessageView(body, stream);
    }

    // Gedankengang (Denkphase) als eingeklappter Bereich über der Antwort
    private static NSView MakeReasoningBlock(string reasoning)
    {
        var words = reasoning.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var text = new WrapLabel(reasoning, 13, Ui.Secondary) { Hidden = true };
        var column = new Col(4);
        var toggle = NSButton.CreateButton("", () => { });
        toggle.Title = "💭 " + L.F("Gedankengang ({0:N0} Wörter)", words);
        toggle.Bordered = false;
        toggle.Font = NSFont.SystemFontOfSize(13);
        toggle.ContentTintColor = Ui.Secondary;
        toggle.Activated += (_, _) => text.Hidden = !text.Hidden;
        column.Add(toggle, stretch: false);
        column.Add(text);
        return Ui.Pad(column, 0, 0, 8, 0);
    }

    private void AddImage(Col target, byte[] png, double maxHeight, double maxWidth)
    {
        using var data = NSData.FromArray(png);
        var image = new NSImage(data);
        var (w, h) = Images.PixelSize(png);
        var scale = Math.Min(1, Math.Min(maxWidth / Math.Max(1, w), maxHeight / Math.Max(1, h)));
        var view = new ClickImage
        {
            Image = image, ImageScaling = NSImageScale.ProportionallyUpOrDown, ToolTip = L.T("Klicken zum Vergrößern"),
            Clicked = () => OpenImage(png)
        };
        view.WidthAnchor.ConstraintEqualTo((nfloat)(w * scale)).Active = true;
        view.HeightAnchor.ConstraintEqualTo((nfloat)(h * scale)).Active = true;
        target.Add(view, stretch: false);
        target.Gap(view, 8);
    }

    // Öffnet das Bild in voller Größe mit dem Standard-Bildbetrachter
    private static void OpenImage(byte[] png)
    {
        var dir = Path.Combine(Path.GetTempPath(), "FreeGuenther");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"bild-{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(png))[..12]}.png");
        if (!File.Exists(file))
            File.WriteAllBytes(file, png);
        NSWorkspace.SharedWorkspace.OpenUrl(NSUrl.FromFilename(file));
    }

    private AnswerStats MakeStats(LlmClient.StreamResult result, string configuredModel, double seconds, double? firstChunkAt, double? cost = null)
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
        var parts = new List<string> { $"{s.Provider} · {s.Model}" + (s.Hoster != null ? " " + L.F("(über {0})", s.Hoster) : "") };
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
    private void AddAnswerFooter(Col body, ChatMessage answer)
    {
        var (content, stats, image) = (answer.Content, answer.Stats, answer.Image);
        var buttons = new List<NSView>();

        NSButton Footer(string symbol, string text, string done, string tooltip, Func<bool> action)
        {
            NSButton? button = null;
            button = Ui.SmallButton(symbol, L.T(text), L.T(tooltip), async () =>
            {
                if (!action()) return;
                button!.Title = L.T(done);
                await Task.Delay(1800);
                button.Title = L.T(text);
            });
            return button;
        }

        if (content.Length > 0)
            buttons.Add(Footer("doc.on.doc", "Kopieren", "Kopiert ✓", "Antwort in die Zwischenablage kopieren (mit Markdown-Formatierung)",
                () => Ui.CopyText(content)));
        if (image != null)
        {
            buttons.Add(Footer("photo.on.rectangle", "Bild kopieren", "Kopiert ✓", "Bild in die Zwischenablage kopieren", () => Ui.CopyImage(image)));
            buttons.Add(Footer("square.and.arrow.down", "Speichern", "Gespeichert ✓", "Bild als PNG-Datei speichern", () => SaveImage(image)));
        }
        if (answer.Id != 0)
            buttons.Add(Ui.SmallButton("arrow.clockwise", L.T("Neu erzeugen"), L.T("Dieselbe Frage nochmal stellen – mit dem aktuell eingestellten Modell"),
                () => RegenerateAnswer(answer)));
        if (_plugins.ActiveWebhook != null)
            buttons.Add(WebhookButton(answer));

        buttons.Add(Ui.Spacer());
        var row = Ui.Row(8, buttons.ToArray());
        body.Add(row);
        if (stats != null)
            body.Add(new WrapLabel(FormatStats(stats), 11.5f, Ui.Tertiary));
    }

    // Knopf „Senden“ (Plugin Webhook): schickt diese Antwort als JSON an die eingestellte Adresse
    private NSButton WebhookButton(ChatMessage answer)
    {
        NSButton? button = null;
        button = Ui.SmallButton("paperplane", L.T("Senden"), L.T("Diese Antwort als JSON an den Webhook senden (Plugin „Webhook“)"), async () =>
        {
            if (_plugins.ActiveWebhook is not { } webhook)
            {
                _status.StringValue = L.T("Das Plugin „Webhook“ ist ausgeschaltet (Plugins → Webhook).");
                return;
            }
            button!.Enabled = false;
            button.Title = L.T("Sende …");
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
            _status.StringValue = result;
            button.Title = L.T(result.StartsWith("Fehler") ? "Fehler ✗" : "Gesendet ✓");
            await Task.Delay(2500);
            button.Title = L.T("Senden");
            button.Enabled = true;
        });
        return button;
    }

    // Antwort samt zugehöriger Frage für den Webhook
    private ExportAnswer ToExport(ChatMessage answer)
    {
        var index = _messages.FindLastIndex(m => m.Role == "assistant" && m.Content == answer.Content && m.Image == answer.Image);
        var question = index > 0 && _messages[index - 1].Role == "user" ? _messages[index - 1].Content : null;
        return new ExportAnswer(_currentChat?.Title ?? "", question, answer);
    }

    private bool SaveImage(byte[] png)
    {
        if (Ui.SaveDialog(L.T("Bild speichern"), $"FreeGuenther-Bild-{DateTime.Now:yyyy-MM-dd-HHmmss}.png", "png") is not { } path) return false;
        File.WriteAllBytes(path, png);
        _status.StringValue = L.F("Bild gespeichert: {0}", path);
        return true;
    }

    private void SetBusy(bool busy)
    {
        _sendButton.Hidden = busy;
        _stopButton.Hidden = !busy;
        _newChatButton.Enabled = !busy;
        _deleteChatButton.Enabled = !busy;
        _exportChatButton.Enabled = !busy;
        _chatTable.Enabled = !busy;
        UpdateModelUi();
    }

    // ---------- Bearbeiten und neu erzeugen ----------

    // Eigene Nachricht zurück ins Eingabefeld; sie und alles danach wird aus dem Chat entfernt
    private void EditMessage(ChatMessage message)
    {
        if (IsBusy) return;
        var index = _messages.FindIndex(m => m.Id == message.Id);
        if (index < 0 || _currentChat == null) return;
        if (_messages.Count - index > 2 && !ConfirmTruncate(_messages.Count - index - 1)) return;

        TruncateFrom(index);
        _input.Text = message.Content;
        RestoreAttachments(message);
        _status.StringValue = L.T("Nachricht bearbeiten und mit Enter neu senden.");
        FocusInput();
    }

    // Die Frage zu dieser Antwort nochmal senden (mit dem aktuell eingestellten Modell); die alte Antwort wird ersetzt
    private void RegenerateAnswer(ChatMessage answer)
    {
        if (IsBusy) return;
        var index = _messages.FindIndex(m => m.Id == answer.Id);
        if (index < 1 || _messages[index - 1].Role != "user" || _currentChat == null)
        {
            _status.StringValue = L.T("Zu dieser Antwort gibt es keine Frage zum erneuten Senden.");
            return;
        }
        if (index < _messages.Count - 1 && !ConfirmTruncate(_messages.Count - index - 1)) return;

        var question = _messages[index - 1];
        TruncateFrom(index - 1);
        _input.Text = question.Content;
        RestoreAttachments(question);
        Send();
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

    private static bool ConfirmTruncate(int later) =>
        Ui.Confirm(L.F("Danach folgen noch {0} weitere Nachricht(en). Sie werden dabei aus dem Chat entfernt. Fortfahren?", later), L.T("Nachrichten ersetzen"));

    private void TruncateFrom(int index)
    {
        _store.DeleteMessagesFrom(_currentChat!.Id, _messages[index].Id);
        _messages.RemoveRange(index, _messages.Count - index);
        RenderChat();
        UpdateEmptyState();
    }

    // ---------- Export ----------

    private void ExportChat()
    {
        if (_currentChat is not { } chat || _messages.Count == 0) return;
        var safeTitle = string.Concat(chat.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('…');
        if (Ui.SaveDialog(L.T("Chat exportieren"), $"{(safeTitle.Length > 0 ? safeTitle : "Chat")}.md", "md", "html") is not { } path) return;

        var html = Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase)
                   || Path.GetExtension(path).Equals(".htm", StringComparison.OrdinalIgnoreCase);
        try
        {
            File.WriteAllText(path, html ? ChatToHtml(chat) : ChatToMarkdown(chat), new UTF8Encoding(false));
            _status.StringValue = L.F("Chat exportiert: {0}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.StringValue = L.F("Export fehlgeschlagen: {0}", ex.Message);
        }
    }

    private string ChatToMarkdown(ChatInfo chat)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {chat.Title}").AppendLine();
        sb.AppendLine("*" + L.F("Exportiert aus FreeGuenther {0} am {1:g}", AppVersion, DateTime.Now) + "*").AppendLine();
        foreach (var m in _messages)
        {
            sb.AppendLine("---").AppendLine();
            sb.AppendLine(m.Role == "user" ? "### " + L.T("Du") : $"### {m.Stats?.Model ?? L.T("Antwort")}").AppendLine();
            if (m.Image != null)
                sb.AppendLine($"![Bild](data:image/png;base64,{Convert.ToBase64String(m.Image)})").AppendLine();
            foreach (var d in m.Documents ?? [])
                sb.AppendLine("📄 *" + L.F("Angehängt: {0}", d.Name) + "*").AppendLine();
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
            <html lang="{{L.Current}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{Enc(chat.Title)}}</title>
            <style>
            body { font: 15px/1.6 -apple-system, system-ui, sans-serif; max-width: 820px; margin: 32px auto; padding: 0 16px; color: #1f1f1f; background: #fff; }
            h1 { font-size: 24px; margin-bottom: 4px; } .meta { color: #777; font-size: 12px; }
            .user { background: #f0f0f3; border-radius: 16px; padding: 10px 16px; margin: 24px 0 8px 15%; white-space: pre-wrap; }
            .answer { margin: 8px 0 24px; } .stats { color: #888; font-size: 11.5px; }
            pre { background: #f4f4f6; padding: 10px 12px; border-radius: 8px; overflow-x: auto; } code { font-family: ui-monospace, Menlo, monospace; font-size: 13px; }
            table { border-collapse: collapse; } th, td { border: 1px solid #ccc; padding: 3px 8px; } img { max-width: 100%; border-radius: 8px; }
            details { color: #666; font-size: 13px; margin-bottom: 8px; } details div { white-space: pre-wrap; }
            @media print { body { margin: 0; } .user { break-inside: avoid; } }
            </style></head><body>
            <h1>{{Enc(chat.Title)}}</h1>
            <div class="meta">{{Enc(L.F("Exportiert aus FreeGuenther {0} am {1:g}", AppVersion, DateTime.Now))}}</div>

            """);
        foreach (var m in _messages)
        {
            var img = m.Image != null ? $"<p><img src=\"data:image/png;base64,{Convert.ToBase64String(m.Image)}\" alt=\"Bild\"></p>" : "";
            var docs = string.Concat((m.Documents ?? []).Select(d => $"<div class=\"meta\">📄 {Enc(L.F("Angehängt: {0}", d.Name))}</div>"));
            if (m.Role == "user")
                sb.AppendLine($"<div class=\"user\">{img}{docs}{Enc(m.Content)}</div>");
            else
            {
                sb.AppendLine("<div class=\"answer\">");
                if (m.Reasoning is { Length: > 0 } r)
                    sb.AppendLine($"<details><summary>{Enc(L.T("Gedankengang"))}</summary><div>{Enc(r)}</div></details>");
                sb.AppendLine(img).AppendLine(MarkdownRenderer.ToHtml(m.Content));
                if (m.Stats != null)
                    sb.AppendLine($"<div class=\"stats\">{Enc(FormatStats(m.Stats))}</div>");
                sb.AppendLine("</div>");
            }
        }
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}
