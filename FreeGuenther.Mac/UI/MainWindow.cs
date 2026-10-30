using System.Reflection;

namespace LmChat;

internal enum Page { Chat, Automation, Mail, Settings, Usage, Plugins, Compare, Help }

// Hauptfenster: Seitenleiste und Seiten. Aufgeteilt wie unter Windows (MainWindow.Chat.cs, MainWindow.Settings.cs …),
// Logik ohne Oberfläche kommt aus denselben Dateien wie dort.
internal sealed partial class MainWindow
{
    // FREEGUENTHER_DB = andere Datenbank verwenden (z. B. zum Testen mit leerem Stand); Plugins liegen daneben in "plugins"
    public static readonly string DbPath = ResolveDbPath();

    private static string ResolveDbPath()
    {
        if (Environment.GetEnvironmentVariable("FREEGUENTHER_DB") is { Length: > 0 } db)
            return db;
        var support = NSFileManager.DefaultManager
            .GetUrls(NSSearchPathDirectory.ApplicationSupportDirectory, NSSearchPathDomain.User)[0].Path!;
        return Path.Combine(support, "FreeGuenther", "chats.db");
    }

    public static string AppVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version!;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private const string ProjectUrl = "https://www.guentherhaslbeck.de/projekte/freeguenther/";

    private readonly ChatStore _store = new(DbPath);
    private readonly PluginManager _plugins;
    private readonly NSWindow _window;
    private readonly NSView _pageHost = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly Dictionary<Page, NSView> _pages = new();
    private readonly Dictionary<Page, NavItem> _nav = new();
    private Page _page = Page.Chat;

    // _settings = gespeicherter, aktiver Stand; _draft = was gerade auf der Einstellungsseite bearbeitet wird
    private LlmSettings _settings;
    private LlmSettings _draft;

    // Eintrag der Seitenleiste
    private sealed class NavItem : NSView
    {
        private readonly NSBox _background;
        private readonly Action _click;
        public NSBox Dot { get; }
        public NSTextField Title { get; }

        public NavItem(string symbol, string title, Action click, bool dot = false)
        {
            _click = click;
            TranslatesAutoresizingMaskIntoConstraints = false;
            _background = new NSBox
            {
                BoxType = NSBoxType.NSBoxCustom, TitlePosition = NSTitlePosition.NoTitle, BorderWidth = 0, CornerRadius = 6,
                FillColor = NSColor.Clear, TranslatesAutoresizingMaskIntoConstraints = false
            };
            AddSubview(_background);
            Ui.Pin(_background, this, 0, 0, 0, 0);

            var icon = new NSImageView
            {
                Image = NSImage.GetSystemSymbol(symbol, null), ContentTintColor = NSColor.SecondaryLabel,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            Title = Ui.Label(title);
            Dot = new NSBox
            {
                BoxType = NSBoxType.NSBoxCustom, TitlePosition = NSTitlePosition.NoTitle, BorderWidth = 0, CornerRadius = 4,
                FillColor = NSColor.TertiaryLabel, Hidden = !dot, TranslatesAutoresizingMaskIntoConstraints = false
            };
            Dot.WidthAnchor.ConstraintEqualTo(8).Active = true;
            Dot.HeightAnchor.ConstraintEqualTo(8).Active = true;
            icon.WidthAnchor.ConstraintEqualTo(18).Active = true;
            var row = Ui.Row(8, icon, Title, Ui.Spacer(), Dot);
            AddSubview(row);
            Ui.Pin(row, this, 0, 8, 0, 10);
            HeightAnchor.ConstraintEqualTo(28).Active = true;
        }

        public void SetSelected(bool selected) =>
            _background.FillColor = selected ? NSColor.FromSrgb(0.5f, 0.5f, 0.5f, 0.18f) : NSColor.Clear;

        public override void MouseUp(NSEvent theEvent) => _click();
    }

    // Stellt eine fertige View als View-Controller bereit (für die Seitenleiste)
    private sealed class Host(NSView view) : NSViewController
    {
        public override void LoadView() => View = view;
    }

    public MainWindow()
    {
        _settings = LlmSettings.Load(_store);
        _draft = _settings.Clone();
        _plugins = new PluginManager(_store, Path.Combine(Path.GetDirectoryName(DbPath)!, "plugins"))
        {
            // Für das Webhook-Plugin: die letzte Antwort im geöffneten Chat
            LastAnswer = () => _messages.LastOrDefault(m => m.Role == "assistant") is { } last ? ToExport(last) : null
        };

        var split = new NSSplitViewController();
        var sidebar = NSSplitViewItem.CreateSidebar(new Host(BuildSidebar()));
        sidebar.MinimumThickness = 230;
        sidebar.MaximumThickness = 340;
        split.AddSplitViewItem(sidebar);
        var content = NSSplitViewItem.FromViewController(new Host(_pageHost));
        content.MinimumThickness = 620;
        split.AddSplitViewItem(content);

        _window = new NSWindow(new CGRect(0, 0, 1180, 780),
            NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable | NSWindowStyle.Resizable | NSWindowStyle.FullSizeContentView,
            NSBackingStore.Buffered, false)
        {
            Title = $"FreeGuenther {AppVersion}",
            TitleVisibility = NSWindowTitleVisibility.Hidden,
            TitlebarAppearsTransparent = true,
            ReleasedWhenClosed = false,
            ContentViewController = split,
            MinSize = new CGSize(900, 600)
        };
        _window.FrameAutosaveName = "FreeGuentherMain";
        if (_window.Frame.Width < 900) _window.SetContentSize(new CGSize(1180, 780));

        AddPage(Page.Chat, BuildChatPage());
        AddPage(Page.Settings, BuildSettingsPage());
        AddPage(Page.Usage, BuildUsagePage());
        AddPage(Page.Plugins, BuildPluginsPage());
        AddPage(Page.Help, BuildHelpPage());
        AddPage(Page.Compare, BuildComparePage());
        AddPage(Page.Automation, BuildAutomationPage());
        AddPage(Page.Mail, BuildMailPage());

        ShowSettings();
        ShowLanguageChoice();
        ReloadChatList();
        InitAutomation();
        InitMail();
        UpdateModelUi();
        Navigate(Page.Chat);
    }

    public void Show()
    {
        _window.Center();
        _window.MakeKeyAndOrderFront(null);
        FocusInput();
    }

    private void AddPage(Page page, NSView view)
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        view.Hidden = true;
        _pageHost.AddSubview(view);
        Ui.Pin(view, _pageHost, 0, 0, 0, 0);
        _pages[page] = view;
    }

    public void Navigate(Page page)
    {
        // Eingaben der Automatiken auch ohne „Start“ behalten
        SaveAutomationDraft();
        SaveMailDraft();
        _page = page;
        foreach (var (key, view) in _pages)
            view.Hidden = key != page;
        foreach (var (key, item) in _nav)
            item.SetSelected(key == page);
        switch (page)
        {
            case Page.Chat:
                FocusInput();
                break;
            case Page.Compare:
                ShowCompare();
                break;
            case Page.Usage:
                _ = ShowUsageAsync();
                _ = ShowBudgetAsync();
                break;
            case Page.Plugins:
                ShowPlugins(loadCatalog: _catalog == null);
                break;
        }
    }

    private NSView BuildSidebar()
    {
        var column = new Col(2);

        var logo = new ClickImage
        {
            Image = NSImage.FromStream(typeof(MainWindow).Assembly.GetManifestResourceStream("app-logo.png")!),
            ImageScaling = NSImageScale.ProportionallyUpOrDown,
            Clicked = () => Ui.OpenUrl(ProjectUrl),
            ToolTip = "guentherhaslbeck.de/projekte/freeguenther"
        };
        logo.WidthAnchor.ConstraintEqualTo(32).Active = true;
        logo.HeightAnchor.ConstraintEqualTo(32).Active = true;
        var head = Ui.Row(8, logo, Ui.Label("FreeGuenther", 15, bold: true));
        column.Add(head, stretch: false);
        column.Gap(head, 12);

        var newChat = Ui.Button(L.UI("ui.NeuerChat"), NewChat);
        _newChatButton = newChat;
        column.Add(newChat);
        column.Gap(newChat, 10);

        NavItem Item(Page page, string symbol, string key, bool dot = false)
        {
            var item = new NavItem(symbol, L.UI(key), () => Navigate(page), dot);
            _nav[page] = item;
            column.Add(item);
            return item;
        }
        Item(Page.Chat, "bubble.left", "ui.Chat");
        Item(Page.Automation, "folder.badge.gearshape", "ui.DateiAutomatik", dot: true);
        Item(Page.Mail, "envelope", "ui.EMailAutomatik", dot: true);
        Item(Page.Settings, "gearshape", "ui.Einstellungen");
        Item(Page.Usage, "chart.bar", "ui.Verbrauch");
        Item(Page.Plugins, "puzzlepiece.extension", "ui.Plugins");
        Item(Page.Compare, "rectangle.split.2x1", "ui.ModelleVergleichen");
        var help = Item(Page.Help, "questionmark.circle", "ui.Hilfe");
        column.Gap(help, 10);

        _chatSearch = new NSSearchField { PlaceholderString = L.UI("ui.ChatsDurchsuchen"), TranslatesAutoresizingMaskIntoConstraints = false };
        _chatSearch.Changed += (_, _) => ReloadChatList();
        column.Add(_chatSearch);
        column.Gap(_chatSearch, 6);

        var view = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        view.AddSubview(column);
        Ui.Pin(column, view, 34, 12, null, 12);

        var list = BuildChatList();
        view.AddSubview(list);
        list.TopAnchor.ConstraintEqualTo(column.BottomAnchor, 4).Active = true;
        Ui.Pin(list, view, null, 0, null, 0);

        _modelCardProvider = Ui.Label("", 11, Ui.Secondary);
        _modelCardModel = Ui.Label("", 12);
        var cardColumn = new Col(1);
        cardColumn.Add(_modelCardProvider);
        cardColumn.Add(_modelCardModel);
        var card = Ui.Card(cardColumn, NSColor.FromSrgb(0.5f, 0.5f, 0.5f, 0.10f), NSColor.Clear, 8, 8);
        card.ToolTip = L.UI("ui.AnbieterUndModellAendern");
        card.AddGestureRecognizer(new NSClickGestureRecognizer(() => Navigate(Page.Settings)));
        var version = Ui.Label($"Version {AppVersion}", 10.5f, Ui.Tertiary);
        var foot = new Col(4);
        foot.Add(card);
        foot.Add(version);
        view.AddSubview(foot);
        Ui.Pin(foot, view, null, 12, 10, 12);
        list.BottomAnchor.ConstraintEqualTo(foot.TopAnchor, -8).Active = true;
        return view;
    }

    // Vor dem Neuaufbau nach einem Sprachwechsel: Zeitgeber anhalten, sonst liefen die Automatiken doppelt
    public void Dispose()
    {
        Closing();
        _automationTimer.Stop();
        _mailTimer.Stop();
        _window.Close();
    }

    // Beim Beenden: laufende Antwort abbrechen, Eingaben merken; Automatiken bleiben eingeschaltet und laufen beim nächsten Start weiter
    public void Closing()
    {
        _cts?.Cancel();
        SaveAutomationDraft();
        SaveMailDraft();
        _automationCts?.Cancel();
        _mailCts?.Cancel();
        _ = _plugins.DisposeAsync().AsTask();   // gestartete MCP-Server beenden
    }
}
