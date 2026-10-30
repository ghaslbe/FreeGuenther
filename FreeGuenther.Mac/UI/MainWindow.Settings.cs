using System.Globalization;

namespace LmChat;

// Eine Einstellung mit Haken ("eigenen Wert verwenden"), Erklärung und Schieberegler.
// Der Regler lässt nur gültige Werte im erlaubten Bereich zu.
internal sealed class ParamRow
{
    private readonly NSButton _enabled;
    private readonly NSSlider _slider;
    private readonly NSTextField _valueText;
    private readonly string _format;
    private readonly double _step;
    private bool _supported = true;

    public NSView Element { get; }

    // title und description sind deutsch und werden über L.T übersetzt
    public ParamRow(string title, string description, double min, double max, double step, double typical, string format)
    {
        (_format, _step) = (format, step);
        _enabled = Ui.Check(L.T(title), Update);
        _enabled.Font = NSFont.BoldSystemFontOfSize(13);
        _slider = new NSSlider { MinValue = min, MaxValue = max, DoubleValue = typical, Continuous = true };
        _slider.TranslatesAutoresizingMaskIntoConstraints = false;
        _slider.Activated += (_, _) =>
        {
            _slider.DoubleValue = Math.Round(_slider.DoubleValue / _step) * _step;
            Update();
        };
        _valueText = Ui.Label("", 13, bold: true);
        _valueText.Alignment = NSTextAlignment.Right;
        _valueText.WidthAnchor.ConstraintEqualTo(110).Active = true;

        var column = new Col(3);
        column.Add(_enabled);
        column.Add(Ui.Pad(Ui.Hint(L.T(description)), 0, 22, 0, 0));
        column.Add(Ui.Pad(Ui.Row(8, _slider, _valueText), 0, 22, 0, 0));
        column.Add(Ui.Pad(Ui.Label(L.F("Bereich: {0} bis {1}   ·   üblich: {2}", min.ToString(format, CultureInfo.CurrentCulture),
            max.ToString(format, CultureInfo.CurrentCulture), typical.ToString(format, CultureInfo.CurrentCulture)), 11, Ui.Tertiary), 0, 22, 0, 0));
        Element = column;
        Update();
    }

    // null = kein Haken = Standardwert des Modells
    public double? Value
    {
        get => _enabled.State == NSCellStateValue.On ? Math.Round(_slider.DoubleValue, 3) : null;
        set
        {
            _enabled.State = value.HasValue ? NSCellStateValue.On : NSCellStateValue.Off;
            if (value is { } v)
                _slider.DoubleValue = Math.Clamp(v, _slider.MinValue, _slider.MaxValue);
            Update();
        }
    }

    // Manche Anbieter kennen nicht jede Einstellung – dann ausgegraut mit Hinweis statt Fehler beim Senden
    public void SetSupported(bool supported, string providerName)
    {
        _supported = supported;
        _enabled.Enabled = supported;
        Element.AlphaValue = supported ? 1 : 0.45f;
        Element.ToolTip = supported ? "" : L.F("Wird von {0} nicht unterstützt und deshalb nicht gesendet.", providerName);
        Update();
    }

    private void Update()
    {
        var on = _enabled.State == NSCellStateValue.On && _supported;
        _slider.Enabled = on;
        _valueText.StringValue = !_supported ? L.T("nicht unterstützt") : on ? _slider.DoubleValue.ToString(_format, CultureInfo.CurrentCulture) : L.T("Standard");
        _valueText.AlphaValue = on ? 1 : 0.5f;
    }
}

// Seite „Einstellungen“: Sprache, Anbieter, Modell, System-Prompt, Vorlagen, Feineinstellungen
internal sealed partial class MainWindow
{
    private ProviderKind? _shownProvider;
    private ProviderKind? _modelsLoadedFor;
    private int _modelLoadId;
    private bool _setupJustFinished, _showingProvider, _loadingLanguage;
    private string _selectedModelId = "";    // Das gewählte Modell bleibt immer in der Liste, sonst ginge die Auswahl beim Filtern verloren
    private readonly List<ModelInfo> _models = new();
    private readonly List<ModelInfo> _shownModels = new();
    private readonly List<(string Code, string Label)> _languageItems = new();
    private readonly Dictionary<ProviderKind, (NSBox Box, NSTextField Title, NSTextField Status)> _providerRows = new();

    private NSPopUpButton _languageCombo = null!, _regionCombo = null!, _modelCombo = null!, _modelFilterCombo = null!;
    private NSTextField _serverBox = null!, _tileProvider = null!, _tileModel = null!, _tileModelHint = null!, _tileChats = null!, _providerTitle = null!;
    private NSTextField _modelHeading = null!, _settingsStatus = null!, _setupGuideTitle = null!;
    private WrapLabel _providerHint = null!, _providerActiveNote = null!, _modelHint = null!, _modelFilterInfo = null!;
    private Col _providerLinksHost = null!, _setupSteps = null!, _paramsPanel = null!;
    private SecretField _apiKeyBox = null!;
    private NSView _apiKeyPanel = null!, _regionPanel = null!, _setupGuide = null!;
    private NSSearchField _modelSearch = null!;
    private NSButton _useProviderButton = null!, _setupDoneButton = null!, _autoTitleCheck = null!;
    private TextArea _systemPromptBox = null!;

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

    private NSView Tile(string title, out NSTextField value, out NSTextField hint)
    {
        value = Ui.Label("", 15, bold: true);
        hint = Ui.Label(title, 11.5f, Ui.Secondary);
        var column = new Col(2);
        column.Add(value);
        column.Add(hint);
        return Ui.Card(column, padding: 12);
    }

    private NSView BuildSettingsPage()
    {
        var page = new Col(14);
        page.Add(Ui.PageTitle(L.UI("ui.Einstellungen")));
        page.Add(Ui.Hint(L.UI("ui.WoDasSprachmodellLaeuftWelches")));

        // Sprache
        var language = new Col(6);
        language.Add(Ui.Heading(L.UI("ui.Sprache")));
        language.Add(Ui.Hint(L.UI("ui.SpracheHinweis")));
        _languageCombo = Ui.Popup(LanguageChanged);
        language.Add(_languageCombo, stretch: false);
        page.Add(Ui.Card(language));

        // Anleitung beim ersten Einrichten
        var guide = new Col(8);
        _setupGuideTitle = Ui.Heading("");
        guide.Add(_setupGuideTitle);
        _setupSteps = new Col(8);
        guide.Add(_setupSteps);
        _setupDoneButton = Ui.Button(L.UI("ui.LosGehtSZumChat"), () =>
        {
            _setupJustFinished = false;
            UpdateSetupGuide();
            Navigate(Page.Chat);
        }, primary: true);
        guide.Add(_setupDoneButton, stretch: false);
        _setupGuide = Ui.Card(guide, Ui.WarningFill, Ui.Warning.ColorWithAlphaComponent(0.4f));
        page.Add(_setupGuide);

        // Kacheln
        var tileProvider = Tile(L.UI("ui.AktiverAnbieter"), out _tileProvider, out _);
        var tileModel = Tile(L.UI("ui.AktivesModell"), out _tileModel, out _tileModelHint);
        var tileChats = Tile(L.UI("ui.GespeicherteChats"), out _tileChats, out _);
        var tiles = Ui.Row(12, tileProvider, tileModel, tileChats);
        tiles.Distribution = NSStackViewDistribution.FillEqually;
        page.Add(tiles);

        // Anbieter: links die Liste, rechts die Einstellungen des gewählten
        var providerCard = new Col(10);
        providerCard.Add(Ui.Heading(L.UI("ui.Anbieter")));
        providerCard.Add(Ui.Hint(L.UI("ui.LokalAufDeinemRechnerBzw")));
        var list = new Col(6);
        foreach (var info in ProviderInfo.All)
        {
            var title = Ui.Label(info.Name, 13, bold: true);
            var status = Ui.Label("", 11.5f, Ui.Secondary);
            var inner = new Col(1);
            inner.Add(title);
            inner.Add(status);
            var box = Ui.Card(inner, NSColor.Clear, NSColor.Separator, 8, 8);
            var kind = info.Kind;
            box.AddGestureRecognizer(new NSClickGestureRecognizer(() => SelectProviderRow(kind)));
            list.Add(box);
            _providerRows[info.Kind] = (box, title, status);
        }
        list.WidthAnchor.ConstraintEqualTo(230).Active = true;

        var detail = new Col(8);
        _providerTitle = Ui.Label("", 15, bold: true);
        _useProviderButton = Ui.Button(L.UI("ui.ImChatVerwenden"), () => SaveSettings(activate: true), primary: true);
        _useProviderButton.ToolTip = L.UI("ui.SpeichernUndDiesenAnbieterMit");
        detail.Add(Ui.Row(8, _providerTitle, Ui.Spacer(), _useProviderButton));
        _providerActiveNote = Ui.Hint();
        detail.Add(_providerActiveNote);
        _providerHint = Ui.Hint();
        detail.Add(_providerHint);
        _providerLinksHost = new Col();
        detail.Add(_providerLinksHost);

        detail.Add(Ui.Label(L.UI("ui.ServerAdresse"), bold: true));
        _serverBox = Ui.Field();
        _serverBox.Changed += (_, _) => UpdateProviderRows();
        var defaultServer = Ui.Button(L.UI("ui.Standard"), () => { _serverBox.StringValue = ShownProvider.DefaultServer; UpdateProviderRows(); });
        detail.Add(Ui.Row(8, _serverBox, defaultServer));
        detail.Add(Ui.Hint(L.UI("ui.NormalerweisePasstDerStandardFuer") + " http://192.168.1.20:1234"));

        var keyColumn = new Col(6);
        keyColumn.Add(Ui.Label(L.UI("ui.APISchluessel"), bold: true));
        _apiKeyBox = new SecretField();
        _apiKeyBox.Changed += () =>
        {
            UpdateSetupGuide();
            UpdateProviderRows();
        };
        keyColumn.Add(_apiKeyBox);
        keyColumn.Add(Ui.Hint(L.UI("ui.DenSchluesselBekommstDuIn") + L.UI("ui.ErWirdVerschluesseltGespeichertUnd")));
        keyColumn.Add(new WrapLabel(L.UI("ui.Wichtig") + " " + L.UI("ui.SetzeBeimAnbieterUnbedingtEin2") + " " + L.UI("ui.HilfeKostenAbsichern"), 12, Ui.Warning));
        _apiKeyPanel = keyColumn;
        detail.Add(keyColumn);

        var regionColumn = new Col(6);
        regionColumn.Add(Ui.Label(L.UI("ui.StandortDerRechenzentren"), bold: true));
        regionColumn.Add(Ui.Hint(L.UI("ui.LegtFestWoDeineAnfragen")));
        _regionCombo = Ui.Popup(async () =>
        {
            if (!_showingProvider && _shownProvider == ProviderKind.OpenRouter)
                await LoadModelsAsync();
        });
        foreach (var region in RegionOption.All)
            _regionCombo.AddItem(region.Label);
        regionColumn.Add(_regionCombo, stretch: false);
        regionColumn.Add(new Rich(12, Ui.Secondary).Bold(L.UI("ui.UeberHosterAuswahlJederTarif") + " ").Text(L.UI("ui.DieAppLaesstNurHoster")).View());
        regionColumn.Add(new Rich(12, Ui.Secondary).Bold(L.UI("ui.GarantiertInRegionRouting") + " ").Text(L.UI("ui.OpenRouterSelbstStelltSicherDass"))
            .Link(L.UI("ui.Details"), "https://openrouter.ai/docs/guides/features/sovereign-ai").Text(L.UI("ui.SonstKommtDerFehlerHTTP")).View());
        _regionPanel = regionColumn;
        detail.Add(regionColumn);

        _modelHeading = Ui.Label(L.UI("ui.Modell"), bold: true);
        detail.Add(_modelHeading);
        _modelSearch = new NSSearchField { PlaceholderString = L.UI("ui.ModelleSuchenZBGemini"), TranslatesAutoresizingMaskIntoConstraints = false };
        _modelSearch.Changed += (_, _) => ApplyModelFilter();
        _modelFilterCombo = Ui.Popup(ApplyModelFilter);
        foreach (var key in new[] { "ui.AlleModelle", "ui.ErzeugtBilder", "ui.VerstehtBilder", "ui.GeradeGeladen" })
            _modelFilterCombo.AddItem(L.UI(key));
        detail.Add(Ui.Row(8, _modelSearch, _modelFilterCombo));
        _modelCombo = Ui.Popup(ModelSelectionChanged);
        var reload = Ui.Button(L.UI("ui.ListeAktualisieren"), () => _ = LoadModelsAsync());
        detail.Add(Ui.Row(8, _modelCombo, reload));
        _modelFilterInfo = Ui.Hint();
        detail.Add(_modelFilterInfo);
        _modelHint = Ui.Hint();
        detail.Add(_modelHint);
        detail.Add(Ui.Hint(L.UI("ui.DieListeKommtDirektVom") + " 🖼 " + L.UI("ui.VerstehtBilder2") + " ● " + L.UI("ui.IstGeradeGeladenUndAntwortet") + " 🎨 " + L.UI("ui.ErzeugtBilderMitDemSuchfeld")));

        var both = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal, Alignment = NSLayoutAttribute.Top, Spacing = 16,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        both.AddArrangedSubview(list);
        both.AddArrangedSubview(detail);
        detail.TrailingAnchor.ConstraintEqualTo(both.TrailingAnchor).Active = true;
        providerCard.Add(both);
        page.Add(Ui.Card(providerCard));

        // System-Prompt
        var prompt = new Col(8);
        prompt.Add(Ui.Heading(L.UI("ui.SystemPrompt")));
        prompt.Add(Ui.Hint(L.UI("ui.EineFesteAnweisungDieDas")));
        _systemPromptBox = new TextArea(90);
        prompt.Add(_systemPromptBox);
        _autoTitleCheck = Ui.Check(L.UI("ui.ChatTitelNachDerErsten"));
        prompt.Add(_autoTitleCheck);
        prompt.Add(Ui.Hint(L.UI("ui.EineKurzeZusatzanfrageAnDasselbe")));
        page.Add(Ui.Card(prompt));

        page.Add(BuildTemplateCard());

        // Feineinstellungen
        var parameters = new Col(10);
        parameters.Add(Ui.Heading(L.UI("ui.Feineinstellungen")));
        parameters.Add(Ui.Hint(L.UI("ui.SetzeEinenHakenUmEinen")));
        _paramsPanel = new Col(14);
        foreach (var row in ParamRows)
            _paramsPanel.Add(row.Element);
        parameters.Add(_paramsPanel);
        page.Add(Ui.Card(parameters));

        _settingsStatus = Ui.Label("", 12, Ui.Secondary);
        var save = Ui.Button(L.UI("ui.Speichern"), () => SaveSettings(activate: false), primary: true);
        save.ToolTip = L.UI("ui.AlleEinstellungenSpeichernDerAnbieter");
        var reset = Ui.Button(L.UI("ui.AllesAufStandard"), () =>
        {
            foreach (var row in ParamRows)
                row.Value = null;
            _settingsStatus.StringValue = L.T("Alle Feineinstellungen auf Standard – zum Übernehmen „Speichern“ klicken.");
        });
        page.Add(Ui.Row(8, save, reset, _settingsStatus, Ui.Spacer()));

        return Ui.ScrollPage(page, 860);
    }

    private void ShowSettings()
    {
        _systemPromptBox.Text = _draft.SystemPrompt;
        _autoTitleCheck.State = _draft.AutoTitle ? NSCellStateValue.On : NSCellStateValue.Off;
        ShowTemplates();
        _temperatureRow.Value = _draft.Temperature;
        _topPRow.Value = _draft.TopP;
        _topKRow.Value = _draft.TopK;
        _maxTokensRow.Value = _draft.MaxTokens;
        _repeatPenaltyRow.Value = _draft.RepeatPenalty;
        _shownProvider = null;
        SelectProviderRow(_draft.Provider);
    }

    private void ShowLanguageChoice()
    {
        _loadingLanguage = true;
        var saved = _store.GetSetting("language") ?? "";
        _languageItems.Clear();
        _languageItems.Add(("", L.T("Automatisch (Sprache von Windows)")));
        _languageItems.AddRange(L.Languages.Select(l => (l.Code, l.Name)));
        _languageCombo.RemoveAllItems();
        foreach (var (_, label) in _languageItems)
            _languageCombo.AddItem(label);
        _languageCombo.SelectItem(Math.Max(0, _languageItems.FindIndex(i => i.Code == saved)));
        _loadingLanguage = false;
    }

    // Die Oberfläche wird im Code aufgebaut: Beim Sprachwechsel entsteht das Fenster neu (ohne Neustart der App)
    private void LanguageChanged()
    {
        if (_loadingLanguage) return;
        if (IsBusy)
        {
            ShowLanguageChoice();
            _settingsStatus.StringValue = L.T("Abgebrochen.");
            return;
        }
        var code = _languageItems[(int)_languageCombo.IndexOfSelectedItem].Code;
        _store.SetSetting("language", code);
        L.Load(code);
        ((AppDelegate)NSApplication.SharedApplication.Delegate).RebuildWindow();
    }

    private void SelectProviderRow(ProviderKind kind)
    {
        // Eingaben des bisher gezeigten Anbieters im Entwurf merken, bevor gewechselt wird
        if (_shownProvider is { } previous)
            StoreProviderFields(previous);
        ShowProvider(ProviderInfo.Get(kind));
    }

    // Für den gerade gezeigten Anbieter zählen die Eingabefelder, für alle anderen der Entwurf
    private void UpdateProviderRows()
    {
        foreach (var info in ProviderInfo.All)
        {
            var kind = info.Kind;
            var config = _draft.Config(kind);
            var shown = kind == _shownProvider;
            var key = shown ? _apiKeyBox.Value.Trim() : config.ApiKey;
            var server = shown ? _serverBox.StringValue.Trim() : config.Server;
            var model = shown ? SelectedModel?.Id ?? config.Model : config.Model;
            var where = info.NeedsKey ? L.T(key.Length > 0 ? "Schlüssel ✓" : "kein Schlüssel")
                : server.Replace("http://", "").Replace("https://", "");
            var (box, title, status) = _providerRows[kind];
            status.StringValue = $"{where} · {(model.Length > 0 ? model : L.T("kein Modell"))}";
            title.StringValue = info.Name + (kind == _settings.Provider ? "  ● " + L.T("im Chat") : "");
            box.BorderColor = kind == _shownProvider ? NSColor.ControlAccent : NSColor.Separator;
        }
        var shownInfo = ShownProvider;
        var active = shownInfo.Kind == _settings.Provider;
        _providerTitle.StringValue = shownInfo.Name;
        _modelHeading.StringValue = L.T("Modell") + " – " + shownInfo.Name;
        _useProviderButton.Hidden = active;
        _providerActiveNote.StringValue = active ? "● " + L.T("Wird gerade im Chat verwendet.") : L.F("Im Chat antwortet gerade {0}.", ActiveProvider.Name);
    }

    private void StoreProviderFields(ProviderKind kind)
    {
        var config = _draft.Config(kind);
        config.Server = _serverBox.StringValue.Trim().TrimEnd('/');
        config.ApiKey = _apiKeyBox.Value.Trim();
        config.Region = kind == ProviderKind.OpenRouter ? SelectedRegion : "";
        if (SelectedModel is { } model)
        {
            config.Model = model.Id;
            config.ModelVision = model.Vision;
            config.ModelImageGen = model.ImageGen;
            config.ModelTools = model.Tools;
        }
    }

    private string SelectedRegion => RegionOption.All[Math.Max(0, (int)_regionCombo.IndexOfSelectedItem)].Code;

    private ModelInfo? SelectedModel
    {
        get
        {
            var index = (int)_modelCombo.IndexOfSelectedItem;
            return index >= 0 && index < _shownModels.Count ? _shownModels[index] : null;
        }
    }

    private void ShowProvider(ProviderInfo info)
    {
        _shownProvider = info.Kind;
        var config = _draft.Config(info.Kind);
        _providerHint.StringValue = L.T(info.Description);

        var links = new Rich(12).Bold(L.T("Links:") + "  ");
        foreach (var (index, (text, url)) in info.Links.Index())
        {
            if (index > 0) links.Text("   ·   ");
            links.Link(L.T(text), url);
        }
        _providerLinksHost.Clear();
        _providerLinksHost.Add(links.View());

        _serverBox.StringValue = config.Server;
        _apiKeyPanel.Hidden = !info.NeedsKey;
        _apiKeyBox.Reveal(false);   // Schlüssel beim Wechsel des Anbieters wieder verbergen
        _apiKeyBox.Value = config.ApiKey;
        _regionPanel.Hidden = info.Kind != ProviderKind.OpenRouter;
        _showingProvider = true;
        _regionCombo.SelectItem(Math.Max(0, Array.FindIndex(RegionOption.All, r => r.Code == config.Region)));
        _showingProvider = false;

        _temperatureRow.SetSupported(info.SupportsSampling, info.Name);
        _topPRow.SetSupported(info.SupportsSampling, info.Name);
        _topKRow.SetSupported(info.SupportsTopK, info.Name);
        _repeatPenaltyRow.SetSupported(info.SupportsRepeatPenalty, info.Name);

        _selectedModelId = config.Model;
        _modelsLoadedFor = null;
        _models.Clear();
        EnsureModelItem(config.Model, config.ModelVision, config.ModelImageGen, config.ModelTools);
        ApplyModelFilter();
        UpdateSetupGuide();
        UpdateProviderRows();
        _ = LoadModelsAsync();
    }

    // Speichert alle Einstellungen. Der Chat-Anbieter wechselt nur mit activate („Im Chat verwenden“) –
    // oder beim ersten Einrichten, sobald der gezeigte Anbieter vollständig ist (Schlüssel und Modell)
    private void SaveSettings(bool activate)
    {
        var info = ShownProvider;
        var server = _serverBox.StringValue.Trim().TrimEnd('/');
        if (!server.Contains("://"))
            server = (info.IsLocal ? "http://" : "https://") + server;
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            _settingsStatus.StringValue = L.F("Die Server-Adresse ist ungültig. Standard für {0}: {1}", info.Name, info.DefaultServer);
            return;
        }
        var hasKey = _apiKeyBox.Value.Trim().Length > 0;
        var complete = (!info.NeedsKey || hasKey) && SelectedModel != null;
        activate |= info.Kind == _settings.Provider || (!IsReady && complete);
        if (activate && info.NeedsKey && !hasKey)
        {
            _settingsStatus.StringValue = L.F("Für {0} wird ein API-Schlüssel benötigt.", info.Name);
            return;
        }
        if (activate && SelectedModel == null)
        {
            _settingsStatus.StringValue = L.T("Bitte ein Modell auswählen (ggf. erst „Liste aktualisieren“).");
            return;
        }

        _serverBox.StringValue = server;
        StoreProviderFields(info.Kind);
        if (activate)
            _draft.Provider = info.Kind;
        _draft.SystemPrompt = _systemPromptBox.Text.Trim();
        _draft.AutoTitle = _autoTitleCheck.State == NSCellStateValue.On;
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
        _apiKeyBox.Reveal(false);
        UpdateModelUi();
        UpdateProviderRows();
        _settingsStatus.StringValue = activate
            ? L.F("Gespeichert – im Chat aktiv: {0}, {1} ({2:HH:mm:ss}).", info.Name, _settings.Current.Model, DateTime.Now)
            : L.F("Gespeichert – {0} ist eingerichtet, im Chat antwortet weiter {1} ({2:HH:mm:ss}).", info.Name, ActiveProvider.Name, DateTime.Now);
    }

    // Holt die Modellliste des gerade gezeigten Anbieters (mit Adresse und Schlüssel aus den Eingabefeldern)
    private async Task LoadModelsAsync()
    {
        var info = ShownProvider;
        var key = _apiKeyBox.Value.Trim();
        var region = info.Kind == ProviderKind.OpenRouter ? SelectedRegion : "";
        // Die OpenRouter-Modellliste ist öffentlich, alle anderen Online-Dienste brauchen dafür den Schlüssel
        if (info.NeedsKey && key.Length == 0 && info.Kind != ProviderKind.OpenRouter)
        {
            _settingsStatus.StringValue = L.F("Für die Modellliste von {0} bitte zuerst den API-Schlüssel eintragen.", info.Name);
            return;
        }

        var selected = SelectedModel?.Id ?? _draft.Config(info.Kind).Model;
        var loadId = ++_modelLoadId;
        _settingsStatus.StringValue = L.F("Lade Modelle von {0} …", info.Name);
        try
        {
            // Der Hoster-Filter prüft jedes Modell einzeln – das dauert beim ersten Mal etwas länger
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(RegionOption.IsHosterFilter(region) ? 180 : 20));
            var models = await LlmClient.ListModelsAsync(info, _serverBox.StringValue, region, key, cts.Token,
                status => OnMain(() => { if (loadId == _modelLoadId) _settingsStatus.StringValue = status; }));
            if (loadId != _modelLoadId) return; // inzwischen anderer Anbieter gewählt
            _selectedModelId = selected;
            _models.Clear();
            _models.AddRange(models);
            _modelsLoadedFor = models.Count > 0 ? info.Kind : null;
            _settingsStatus.StringValue = (region.Length > 0
                    ? L.F("{0} Modelle von {1} (nur {2}) gefunden.", models.Count, info.Name, RegionOption.Name(region))
                    : L.F("{0} Modelle von {1} gefunden.", models.Count, info.Name)) +
                (selected.Length > 0 && models.All(m => m.Id != selected)
                    ? " " + L.F("„{0}“ ist hier nicht verfügbar – bitte ein anderes Modell wählen.", selected)
                    : "");
        }
        catch (Exception ex)
        {
            if (loadId != _modelLoadId) return;
            _settingsStatus.StringValue = L.F("Modelle konnten nicht geladen werden: {0}", ex.Message);
            // Liste nicht ladbar: bisherige Auswahl trotzdem anzeigen
            var saved = _draft.Config(info.Kind);
            EnsureModelItem(selected, saved.Model == selected ? saved.ModelVision : null, saved.Model == selected && saved.ModelImageGen);
        }
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

    // Alle Suchwörter müssen im Namen vorkommen, dazu die gewählte Eigenschaft
    private bool MatchesModelSearch(ModelInfo m)
    {
        var words = _modelSearch.StringValue.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.All(w => m.Id.Contains(w, StringComparison.OrdinalIgnoreCase)) &&
               _modelFilterCombo.IndexOfSelectedItem switch { 1 => m.ImageGen, 2 => m.Vision == true, 3 => m.Loaded, _ => true };
    }

    private void ApplyModelFilter()
    {
        _shownModels.Clear();
        _shownModels.AddRange(_models.Where(m => m.Id == _selectedModelId || MatchesModelSearch(m)));
        _modelCombo.RemoveAllItems();
        foreach (var model in _shownModels)
        {
            // Über das Menü-Objekt, sonst fasst das Popup gleich lautende Einträge zusammen
            _modelCombo.Menu!.AddItem(new NSMenuItem(model.Label));
        }
        var index = _shownModels.FindIndex(m => m.Id == _selectedModelId);
        if (index >= 0) _modelCombo.SelectItem(index);
        var filtered = _modelSearch.StringValue.Trim().Length > 0 || _modelFilterCombo.IndexOfSelectedItem > 0;
        _modelFilterInfo.StringValue = filtered
            ? L.F("{0} von {1} Modellen passen – Liste aufklappen zum Auswählen.", _models.Count(MatchesModelSearch), _models.Count)
            : _models.Count > 0 ? L.F("{0} Modelle", _models.Count) : "";
        ModelSelectionChanged();
    }

    private void EnsureModelItem(string id, bool? vision, bool imageGen = false, bool? tools = null)
    {
        if (id.Length > 0 && _models.All(m => m.Id != id))
            _models.Add(new ModelInfo(id, vision, false, imageGen, tools));
    }

    private void ModelSelectionChanged()
    {
        var model = SelectedModel;
        if (model != null)
            _selectedModelId = model.Id;
        UpdateSetupGuide();
        UpdateProviderRows();
        var visionHint = model?.Vision switch
        {
            true => "🖼 " + L.T("Dieses Modell versteht Bilder."),
            false => L.T("Dieses Modell versteht nur Text."),
            _ => model == null ? "" : L.T("Ob das Modell Bilder versteht, meldet dieser Anbieter nicht – einfach ausprobieren.")
        };
        _modelHint.StringValue = model?.ImageGen == true
            ? "🎨 " + L.T("Dieses Modell erzeugt Bilder: Deine Nachricht ist die Bildbeschreibung. Ein angehängtes Bild (oder mit " +
                          "„Verlauf mitsenden“ das zuletzt erzeugte) dient als Vorlage zum Bearbeiten. Bilder kosten meist mehr als Text.")
            : visionHint + model?.Tools switch
            {
                true => " 🔧 " + L.T("Kann Plugins nutzen."),
                false => " " + L.T("Kann keine Plugins nutzen."),
                _ => ""
            };
    }

    private void UpdateModelUi()
    {
        var vision = CurrentVision;
        var model = _settings.Current.Model;
        var region = RegionOption.Name(_settings.Current.Region);
        var provider = ActiveProvider.Name + (region.Length > 0 ? $" ({region})" : "");
        var modelText = model.Length > 0 ? model : L.T("kein Modell gewählt");
        var visionText = L.T(CurrentImageGen ? "erzeugt Bilder" : vision switch { true => "versteht Bilder", false => "nur Text", _ => "" });

        // Karte unten in der Seitenleiste
        _modelCardProvider.StringValue = provider;
        _modelCardModel.StringValue = modelText;
        _modelCardModel.ToolTip = $"{provider} · {modelText}" + (visionText.Length > 0 ? $" · {visionText}" : "");

        // Kacheln oben in den Einstellungen
        _tileProvider.StringValue = provider;
        _tileModel.StringValue = modelText.Contains('/') ? modelText[(modelText.LastIndexOf('/') + 1)..] : modelText;
        _tileModel.ToolTip = modelText;
        _tileModelHint.StringValue = L.T("Aktives Modell") + (visionText.Length > 0 ? $" · {visionText}" : "");

        _emptyHint.StringValue = CurrentImageGen
            ? "🎨 " + L.F("{0} über {1} erzeugt Bilder: Beschreibe, was zu sehen sein soll. Mit einem angehängten Bild wird dieses bearbeitet.",
                modelText, provider)
            : L.F("Du chattest mit {0} über {1}.", modelText, provider) + " " +
              L.T(vision == false
                  ? "PDF-, Word- und Textdateien kannst du per „Datei“, Strg+V oder Hineinziehen anhängen."
                  : "Bilder sowie PDF-, Word- und Textdateien kannst du per „Datei“, Strg+V oder Hineinziehen anhängen.") + " " +
              L.T("„/“ öffnet Vorlagen und Personas.");
        UpdateSetupState();
        // Dateien gehen immer; Bilder nur, wenn das Modell sie versteht (Bildmodelle: nur Bilder als Vorlage)
        _imageButton.Enabled = !IsBusy && !(CurrentImageGen && vision == false);
        _imageButton.ToolTip = L.T(CurrentImageGen ? "Bild als Vorlage anhängen (auch per Strg+V oder Hineinziehen)"
            : vision == false ? "PDF-, Word- oder Textdatei anhängen – Bilder versteht dieses Modell nicht"
            : "Bild, PDF-, Word- oder Textdatei anhängen (auch per Strg+V oder Hineinziehen)");
    }

    // Schritt-für-Schritt-Karte oben in den Einstellungen: zeigt, was schon erledigt ist und was als Nächstes kommt
    private void UpdateSetupGuide()
    {
        var ready = IsReady;
        _setupGuide.Hidden = ready && !_setupJustFinished;
        if (_setupGuide.Hidden) return;

        var info = ShownProvider;
        var hasKey = _apiKeyBox.Value.Trim().Length > 0;
        var hasModel = SelectedModel != null;
        var saved = ready && _settings.Provider == info.Kind;
        _setupGuideTitle.StringValue = L.T(saved ? "Fertig eingerichtet – du kannst loslegen!" : "So geht's los – in 4 Schritten");
        _setupDoneButton.Hidden = !saved;

        _setupSteps.Clear();
        void Step(bool done, string title, Action<Rich> body)
        {
            var text = new Rich(13).Text(done ? "✅ " : "⚪️ ").Bold(title + "\n");
            body(text);
            var view = text.View();
            view.AlphaValue = done ? 0.75f : 1;
            _setupSteps.Add(view);
        }

        Step(true, L.T("1. Anbieter wählen"), r => r.Text(L.F("Gewählt: {0}. Ändern kannst du das unten unter „Anbieter“.", info.Name)));

        if (info.NeedsKey)
        {
            var limit = info.Links.FirstOrDefault(l => l.Item1.Contains("Limit"));
            Step(hasKey, L.T("2. API-Schlüssel eintragen"), r =>
            {
                r.Text(L.T("Beim Anbieter ein Konto anlegen und einen Schlüssel erstellen:") + " ")
                    .Link(info.KeyUrl!.Replace("https://", ""), info.KeyUrl)
                    .Text(". " + L.T("Den Schlüssel kopieren und unten bei „API-Schlüssel“ einfügen.") + " ");
                if (limit.Item2 != null)
                    r.Bold(L.T("Gleich dabei ein Ausgabenlimit setzen, damit nie mehr als gewollt abgebucht wird.") + " ")
                        .Link(L.T("⚠ Ausgabenlimit setzen"), limit.Item2);
            });
        }
        else if (info.Kind == ProviderKind.LmStudio)
            Step(_modelsLoadedFor == info.Kind, L.T("2. LM Studio starten"), r => r
                .Text(L.T("LM Studio installieren:") + " ").Link("lmstudio.ai", "https://lmstudio.ai")
                .Text(". " + L.T("Dort ein Modell herunterladen (z. B. Gemma oder Qwen) und unter „Developer“ den Server starten. " +
                                 "Läuft LM Studio auf einem anderen Rechner: dort „Serve on Local Network“ einschalten und unten dessen Adresse eintragen. " +
                                 "Danach unten bei „Modell“ auf „Liste aktualisieren“ klicken.")));
        else
            Step(_modelsLoadedFor == info.Kind, L.T("2. Ollama starten"), r => r
                .Text(L.T("Ollama installieren:") + " ").Link("ollama.com", "https://ollama.com")
                .Text(". " + L.T("Dann ein Modell laden, z. B. in der Eingabeaufforderung:") + " ").Mono("ollama pull gemma3")
                .Text(". " + L.T("Danach unten bei „Modell“ auf „Liste aktualisieren“ klicken.")));

        Step(hasModel, L.T("3. Modell auswählen"), r => r.Text(L.T("Unten unter „Modell“ eins aus der Liste wählen.") + " " + L.T(info.Kind switch
        {
            ProviderKind.OpenRouter => "Für den Anfang gut: „openrouter/auto“ – sucht automatisch ein passendes Modell aus.",
            ProviderKind.OpenAI or ProviderKind.Anthropic => "Die Liste erscheint, sobald der Schlüssel eingetragen ist (sonst „Liste aktualisieren“).",
            _ => "Ist die Liste leer, läuft der Server noch nicht oder die Adresse stimmt nicht."
        })));
        Step(saved, L.T("4. Speichern"), r => r.Text(L.T("Ganz unten auf „Speichern“ klicken – danach kannst du im Chat loslegen.")));
    }
}
