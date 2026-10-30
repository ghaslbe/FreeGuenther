#if DEBUG
namespace LmChat;

// Nur im Debug-Build: FG_TEST_DIR gesetzt = die App fährt alle Seiten ab, sendet eine Nachricht und speichert Bilder
// ihrer Fenster in diesen Ordner. So lässt sie sich ohne Bildschirmfreigabe prüfen.
internal sealed partial class MainWindow
{
    // FG_SHOT_DIR gesetzt = Bausteine für die README-Screenshots speichern (Seitenleiste und Seite getrennt, siehe tools/mac-screenshots.py)
    public async void RunScreenshots(string dir)
    {
        async Task Save(string name, NSView view)
        {
            await Task.Delay(300);
            view.LayoutSubtreeIfNeeded();
            var rep = view.BitmapImageRepForCachingDisplayInRect(view.Bounds)!;
            view.CacheDisplay(view.Bounds, rep);
            var png = rep.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png, new NSDictionary());
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), png.ToArray());
        }
        async Task Parts(string name, Page page)
        {
            Navigate(page);
            await Task.Delay(1800);
            await Save(name + "-sidebar", ((NSSplitViewController)_window.ContentViewController!).SplitViewItems[0].ViewController.View);
            await Save(name + "-page", _pages[page] is NSScrollView { DocumentView: { } content } ? content : _pages[page]);
        }
        if (Environment.GetEnvironmentVariable("FG_TEST_DARK") == "1")
            NSApplication.SharedApplication.Appearance = NSAppearance.GetAppearance(NSAppearance.NameDarkAqua);
        _chatTable.SelectionHighlightStyle = NSTableViewSelectionHighlightStyle.None;
        _window.SetContentSize(new CGSize(1180, 780));
        await Task.Delay(1500);
        _chatTable.SelectRow(1, false);
        await Parts("chat", Page.Chat);
        if (Environment.GetEnvironmentVariable("FG_TEST_DARK") != "1")
        {
            await Parts("settings", Page.Settings);
            await Parts("plugins", Page.Plugins);
            await Parts("automation", Page.Automation);
            Navigate(Page.Compare);
            await Task.Delay(1500);
            _compareInput.StringValue = "Was ist ein Stack?";
            CompareSend();
            await Task.Delay(5000);
            await Parts("compare", Page.Compare);
        }
        NSApplication.SharedApplication.Terminate(null);
    }

    public async void RunSelfTest(string dir)
    {
        void Log(string text) => File.AppendAllText(Path.Combine(dir, "test.log"), text + "\n");
        async Task Shot(string name, int waitMs = 700, NSView? target = null)
        {
            await Task.Delay(waitMs);
            var view = target ?? _window.ContentView!;
            view.LayoutSubtreeIfNeeded();
            var rep = view.BitmapImageRepForCachingDisplayInRect(view.Bounds)!;
            view.CacheDisplay(view.Bounds, rep);
            var png = rep.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png, new NSDictionary());
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), png.ToArray());
            Log("shot " + name);
        }
        try
        {
            if (Environment.GetEnvironmentVariable("FG_TEST_DARK") == "1")
                NSApplication.SharedApplication.Appearance = NSAppearance.GetAppearance(NSAppearance.NameDarkAqua);
            _window.SetContentSize(new CGSize(1180, 820));
            await Shot("01-start", 1500);
            _chatTable.SelectRow(1, false);
            await Shot("02-chat-markdown");
            await Shot("02b-sidebar", 200, ((NSSplitViewController)_window.ContentViewController!).SplitViewItems[0].ViewController.View);
            void Tree(NSView v, int d) { if (d > 5) return; Log(new string(' ', d) + v.GetType().Name + " " + v.Frame + (v.Hidden ? " H" : "")); foreach (var c in v.Subviews.Take(6)) Tree(c, d + 1); }
            foreach (var (page, name) in new[] { (Page.Settings, "03-settings"), (Page.Usage, "04-usage"), (Page.Plugins, "05-plugins"),
                         (Page.Help, "06-help"), (Page.Compare, "07-compare"), (Page.Automation, "08-automation"), (Page.Mail, "09-mail") })
            {
                Navigate(page);
                await Shot(name, 1500);
                if (_pages[page] is NSScrollView { DocumentView: { } content } page2)
                {
                    await Shot(name + "-content", 200, content);
                    var centre = new CGPoint(_window.ContentView!.Bounds.Width * 0.6, _window.ContentView.Bounds.Height * 0.55);
                    var hit = _window.ContentView.HitTest(centre);
                    Log($"{page}: visible={content.VisibleRect()} hit={hit?.GetType().Name} inPage={hit?.IsDescendantOf(page2)} scrollAlpha={page2.AlphaValue} hidden={page2.Hidden}");
                }
                if (_pages[page] is NSScrollView sc)
                    Log($"{page}: scroll={sc.Frame} clip={sc.ContentView.Bounds} doc={sc.DocumentView?.Frame} kids={sc.DocumentView?.Subviews.Length} first={sc.DocumentView?.Subviews.FirstOrDefault()?.Frame} ambiguous={sc.DocumentView?.Subviews.FirstOrDefault()?.HasAmbiguousLayout}");
            }
            NewChat();
            await Shot("10-new-chat");
            _input.Text = "/";
            await Shot("11-template-picker");
            _input.Text = "Hallo Streaming-Test";
            Send();
            await Shot("12-streaming", 900);
            await Shot("13-answer", int.Parse(Environment.GetEnvironmentVariable("FG_TEST_WAIT") ?? "4000"));
            Log("chats=" + _chats.Count + " current=" + _currentChat?.Title + " messages=" + _messages.Count);
            Log("status=" + _status.StringValue);
            Log("usage=" + string.Join(";", _store.UsageByModel(null).Select(u => $"{u.Provider}/{u.Model}:{u.Requests}/{u.InputTokens}/{u.OutputTokens}")));
            Log("DONE");
        }
        catch (Exception ex)
        {
            Log("EXCEPTION " + ex);
        }
        NSApplication.SharedApplication.Terminate(null);
    }
}
#endif
