namespace LmChat;

// Dateien aus dem Finder ins Fenster ziehen
internal sealed class DropView : NSView
{
    public Action<string[]>? Dropped;

    public DropView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        RegisterForDraggedTypes(["public.file-url"]);
    }

    public override NSDragOperation DraggingEntered(INSDraggingInfo sender) => Drop.HasFiles(sender) ? NSDragOperation.Copy : NSDragOperation.None;

    public override bool PerformDragOperation(INSDraggingInfo sender)
    {
        var files = Drop.Files(sender.DraggingPasteboard);
        if (files.Length == 0) return false;
        Dropped?.Invoke(files);
        return true;
    }
}

internal static class Drop
{
    public static bool HasFiles(INSDraggingInfo info) => Files(info.DraggingPasteboard).Length > 0;

    public static string[] Files(NSPasteboard board) =>
        (board.PasteboardItems ?? [])
        .Select(item => item.GetStringForType("public.file-url"))
        .Where(url => url != null)
        .Select(url => new NSUrl(url!).Path)
        .OfType<string>()
        .ToArray();
}

// Eingabefeld des Chats: wächst mit dem Text, Enter sendet, Umschalt+Enter macht eine neue Zeile
internal sealed class InputView : NSView
{
    private const double MinHeight = 24, MaxHeight = 180;

    private sealed class Editor : NSTextView
    {
        public required InputView Owner { get; init; }

        public Editor() : base(new CGRect(0, 0, 200, 24)) { }

        public override void KeyDown(NSEvent theEvent)
        {
            if (!HasMarkedText && Owner.HandleKey?.Invoke(theEvent.KeyCode) == true) return;
            var shift = theEvent.ModifierFlags.HasFlag(NSEventModifierMask.ShiftKeyMask);
            if (theEvent.KeyCode is 36 or 76 && !shift && !HasMarkedText)
            {
                Owner.Send?.Invoke();
                return;
            }
            base.KeyDown(theEvent);
        }

        public override void DidChangeText()
        {
            base.DidChangeText();
            Owner.Changed();
        }

        // ⌘V: Bilder und Dateien anhängen, sonst normal einfügen (nur als Text)
        [Export("paste:")]
        public void PasteContent(NSObject sender)
        {
            if (Owner.PasteFiles?.Invoke() != true) PasteAsPlainText(sender);
        }

        public override bool PerformDragOperation(INSDraggingInfo sender)
        {
            var files = Drop.Files(sender.DraggingPasteboard);
            if (files.Length == 0) return base.PerformDragOperation(sender);
            Owner.Dropped?.Invoke(files);
            return true;
        }
    }

    private readonly Editor _editor;
    private readonly NSScrollView _scroll;
    private readonly NSTextField _placeholder;
    private readonly NSLayoutConstraint _height;

    public Action? Send, TextEdited;
    public Func<ushort, bool>? HandleKey;
    public Func<bool>? PasteFiles;
    public Action<string[]>? Dropped;

    public InputView(string placeholder)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _editor = new Editor
        {
            Owner = this,
            RichText = false,
            Font = NSFont.SystemFontOfSize(15),
            TextColor = NSColor.Label,
            DrawsBackground = false,
            AllowsUndo = true,
            AutomaticQuoteSubstitutionEnabled = false,
            VerticallyResizable = true,
            HorizontallyResizable = false,
            AutoresizingMask = NSViewResizingMask.WidthSizable,
            TextContainerInset = new CGSize(0, 2),
        };
        _editor.TextContainer!.WidthTracksTextView = true;
        _editor.TextContainer.LineFragmentPadding = 0;
        _scroll = new NSScrollView
        {
            DocumentView = _editor, DrawsBackground = false, HasVerticalScroller = true, AutohidesScrollers = true,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        AddSubview(_scroll);
        Ui.Pin(_scroll, this, 0, 0, 0, 0);
        _placeholder = Ui.Label(placeholder, 15, Ui.Tertiary);
        AddSubview(_placeholder);
        Ui.Pin(_placeholder, this, 2, 0, null, 0);
        _height = HeightAnchor.ConstraintEqualTo((nfloat)MinHeight);
        _height.Active = true;
    }

    public string Text
    {
        get => _editor.Value;
        set
        {
            _editor.Value = value;
            _editor.SetSelectedRange(new NSRange(value.Length, 0));
            Changed();
        }
    }

    public void Focus() => Window?.MakeFirstResponder(_editor);

    private void Changed()
    {
        _placeholder.Hidden = _editor.Value.Length > 0;
        _editor.LayoutManager!.EnsureLayoutForTextContainer(_editor.TextContainer!);
        var used = _editor.LayoutManager.GetUsedRect(_editor.TextContainer!).Height + 6;
        _height.Constant = (nfloat)Math.Clamp(used, MinHeight, MaxHeight);
        TextEdited?.Invoke();
    }
}
