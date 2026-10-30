using ObjCRuntime;

namespace LmChat;

// Bausteine für die Oberfläche: alles wird im Code aufgebaut, System-Farben folgen automatisch dem Hell-/Dunkelmodus
internal sealed class FlippedView : NSView
{
    public override bool IsFlipped => true;
}

// Nicht editierbarer Text, der in der verfügbaren Breite umbricht
internal sealed class WrapLabel : NSTextField
{
    public WrapLabel(string text = "", nfloat? size = null, NSColor? color = null, bool bold = false)
    {
        Editable = false;
        Selectable = true;
        Bordered = false;
        DrawsBackground = false;
        LineBreakMode = NSLineBreakMode.ByWordWrapping;
        Cell.Wraps = true;
        MaximumNumberOfLines = 0;
        TranslatesAutoresizingMaskIntoConstraints = false;
        Font = bold ? NSFont.BoldSystemFontOfSize(size ?? 13) : NSFont.SystemFontOfSize(size ?? 13);
        TextColor = color ?? NSColor.Label;
        StringValue = text;
        SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
    }

    public override void Layout()
    {
        base.Layout();
        if (PreferredMaxLayoutWidth != Bounds.Width)
        {
            PreferredMaxLayoutWidth = Bounds.Width;
            InvalidateIntrinsicContentSize();
        }
    }
}

// Markierbarer Text mit Formatierung und Links, Höhe richtet sich nach dem Inhalt
internal class TextBlockView : NSTextView
{
    public TextBlockView() : base(new CGRect(0, 0, 200, 16))
    {
        Editable = false;
        Selectable = true;
        DrawsBackground = false;
        TextContainerInset = CGSize.Empty;
        VerticallyResizable = false;
        HorizontallyResizable = false;
        AutoresizingMask = NSViewResizingMask.NotSizable;
        TextContainer!.LineFragmentPadding = 0;
        TextContainer.WidthTracksTextView = true;
        TextContainer.Size = new CGSize(200, 10_000_000);
        TranslatesAutoresizingMaskIntoConstraints = false;
        SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Vertical);
    }

    public override CGSize IntrinsicContentSize
    {
        get
        {
            LayoutManager!.EnsureLayoutForTextContainer(TextContainer!);
            return new CGSize(NoIntrinsicMetric, Math.Ceiling(LayoutManager.GetUsedRect(TextContainer!).Height));
        }
    }

    public override void SetFrameSize(CGSize newSize)
    {
        var changed = newSize.Width != Frame.Width;
        base.SetFrameSize(newSize);
        if (changed) InvalidateIntrinsicContentSize();
    }

    public void SetAttributed(NSAttributedString text)
    {
        TextStorage!.SetString(text);
        InvalidateIntrinsicContentSize();
    }

    public void Append(NSAttributedString text)
    {
        TextStorage!.Append(text);
        InvalidateIntrinsicContentSize();
    }
}

// Mehrzeilige Eingabe mit Rahmen
internal sealed class TextArea : NSView
{
    private sealed class Editor : NSTextView
    {
        public Action? Changed;

        public Editor() : base(new CGRect(0, 0, 200, 80)) { }

        public override void DidChangeText()
        {
            base.DidChangeText();
            Changed?.Invoke();
        }
    }

    private readonly Editor _text = new();

    public event Action? Changed;

    public TextArea(nfloat height, bool editable = true, bool mono = false)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        var scroll = new NSScrollView { HasVerticalScroller = true, BorderType = NSBorderType.BezelBorder, TranslatesAutoresizingMaskIntoConstraints = false };
        _text.RichText = false;
        _text.Editable = editable;
        _text.AutomaticQuoteSubstitutionEnabled = false;
        _text.AutomaticDashSubstitutionEnabled = false;
        _text.AllowsUndo = true;
        _text.Font = mono ? NSFont.MonospacedSystemFont(12, NSFontWeight.Regular) : NSFont.SystemFontOfSize(13);
        _text.TextContainerInset = new CGSize(4, 6);
        _text.VerticallyResizable = true;
        _text.HorizontallyResizable = false;
        _text.AutoresizingMask = NSViewResizingMask.WidthSizable;
        _text.TextContainer!.WidthTracksTextView = true;
        _text.Changed = () => Changed?.Invoke();
        scroll.DocumentView = _text;
        AddSubview(scroll);
        Ui.Pin(scroll, this, 0, 0, 0, 0);
        HeightAnchor.ConstraintEqualTo(height).Active = true;
    }

    public string Text
    {
        get => _text.Value;
        set => _text.Value = value;
    }

    public bool Enabled
    {
        get => _text.Editable;
        set => _text.Editable = value;
    }

    public void Focus() => Window?.MakeFirstResponder(_text);

    public void CaretToEnd() => _text.SetSelectedRange(new NSRange(_text.Value.Length, 0));
}

// Schlüsselfeld mit Augen-Knopf: zeigt den Inhalt auf Wunsch im Klartext
internal sealed class SecretField : NSView
{
    private readonly NSSecureTextField _hidden = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly NSTextField _plain = new() { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
    private readonly NSButton _eye;

    public event Action? Changed;

    public SecretField()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _eye = Ui.IconButton("eye", L.UI("ui.SchluesselAnzeigenBzwWiederVerbergen"), () => Reveal(_plain.Hidden));
        AddSubview(_hidden);
        AddSubview(_plain);
        AddSubview(_eye);
        foreach (var field in new NSTextField[] { _hidden, _plain })
        {
            Ui.Pin(field, this, 0, 0, 0, null);
            field.TrailingAnchor.ConstraintEqualTo(_eye.LeadingAnchor, -6).Active = true;
            field.Changed += (_, _) =>
            {
                (field == _hidden ? _plain : _hidden).StringValue = field.StringValue;
                Changed?.Invoke();
            };
        }
        _eye.TrailingAnchor.ConstraintEqualTo(TrailingAnchor).Active = true;
        _eye.CenterYAnchor.ConstraintEqualTo(CenterYAnchor).Active = true;
    }

    public string Value
    {
        get => _hidden.StringValue;
        set => _hidden.StringValue = _plain.StringValue = value;
    }

    public void Reveal(bool show)
    {
        _plain.Hidden = !show;
        _hidden.Hidden = show;
        _eye.Image = NSImage.GetSystemSymbol(show ? "eye.slash" : "eye", null);
    }

    public void Focus() => Window?.MakeFirstResponder(_plain.Hidden ? _hidden : _plain);
}

// Senkrechte Liste; Kinder füllen die Breite, sofern nicht anders gewünscht
internal class Col : NSStackView
{
    public Col(nfloat? spacing = null)
    {
        Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        Alignment = NSLayoutAttribute.Leading;
        Spacing = spacing ?? 0;
        TranslatesAutoresizingMaskIntoConstraints = false;
        // Nicht höher werden als der Inhalt; in der Breite darf eine Spalte den freien Platz nehmen
        SetHuggingPriority(750, NSLayoutConstraintOrientation.Vertical);
        SetHuggingPriority(200, NSLayoutConstraintOrientation.Horizontal);
    }

    public T Add<T>(T view, bool stretch = true) where T : NSView
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        AddArrangedSubview(view);
        if (stretch)
        {
            view.LeadingAnchor.ConstraintEqualTo(LeadingAnchor).Active = true;
            view.TrailingAnchor.ConstraintEqualTo(TrailingAnchor).Active = true;
        }
        return view;
    }

    public void Gap(NSView after, nfloat space) => SetCustomSpacing(space, after);

    public void Clear()
    {
        foreach (var view in ArrangedSubviews.ToArray())
        {
            RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
        }
    }
}

// Führt regelmäßig etwas auf dem Hauptthread aus
internal sealed class Ticker
{
    private NSTimer? _timer;
    public event Action? Tick;

    public bool IsEnabled => _timer != null;

    public void Start(double seconds)
    {
        Stop();
        _timer = NSTimer.CreateRepeatingScheduledTimer(seconds, _ => Tick?.Invoke());
    }

    public void Stop()
    {
        _timer?.Invalidate();
        _timer = null;
    }
}

internal static class Ui
{
    public static NSColor Secondary => NSColor.SecondaryLabel;
    public static NSColor Tertiary => NSColor.TertiaryLabel;
    public static readonly NSColor Warning = NSColor.FromSrgb(0.96f, 0.62f, 0.04f, 1);
    public static readonly NSColor WarningFill = NSColor.FromSrgb(0.96f, 0.62f, 0.04f, 0.12f);
    public static readonly NSColor PersonaFill = NSColor.FromSrgb(0.55f, 0.36f, 0.96f, 0.12f);
    public static readonly NSColor Running = NSColor.FromSrgb(0.90f, 0.28f, 0.30f, 1);
    public static readonly NSColor Done = NSColor.FromSrgb(0.09f, 0.64f, 0.29f, 1);

    public static NSTextField Label(string text = "", nfloat? size = null, NSColor? color = null, bool bold = false)
    {
        var label = NSTextField.CreateLabel(text);
        label.Font = bold ? NSFont.BoldSystemFontOfSize(size ?? 13) : NSFont.SystemFontOfSize(size ?? 13);
        label.TextColor = color ?? NSColor.Label;
        label.LineBreakMode = NSLineBreakMode.TruncatingTail;
        label.TranslatesAutoresizingMaskIntoConstraints = false;
        label.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        return label;
    }

    public static WrapLabel Hint(string text = "") => new(text, 12, Secondary);

    public static WrapLabel Body(string text = "") => new(text, 13);

    public static NSTextField Heading(string text) => Label(text, 15, bold: true);

    public static NSTextField PageTitle(string text) => Label(text, 24, bold: true);

    public static NSButton Button(string title, Action click, bool primary = false)
    {
        var button = new NSButton { Title = title, BezelStyle = NSBezelStyle.Rounded, TranslatesAutoresizingMaskIntoConstraints = false };
        if (primary) button.BezelColor = NSColor.ControlAccent;
        button.Activated += (_, _) => click();
        return button;
    }

    public static NSButton IconButton(string symbol, string tooltip, Action click, nfloat? size = null)
    {
        var button = new NSButton
        {
            Title = "",
            Image = NSImage.GetSystemSymbol(symbol, null),
            ImagePosition = NSCellImagePosition.ImageOnly,
            Bordered = false,
            ToolTip = tooltip,
            ContentTintColor = Secondary,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        if (size is { } s)
            button.SymbolConfiguration = NSImageSymbolConfiguration.Create(s, NSFontWeight.Regular);
        button.Activated += (_, _) => click();
        return button;
    }

    // Kleiner Knopf mit Symbol und Text (unter Nachrichten)
    public static NSButton SmallButton(string symbol, string title, string tooltip, Action click)
    {
        var button = new NSButton
        {
            Title = title,
            Image = NSImage.GetSystemSymbol(symbol, null),
            ImagePosition = NSCellImagePosition.ImageLeading,
            Bordered = false,
            ToolTip = tooltip,
            ContentTintColor = Secondary,
            Font = NSFont.SystemFontOfSize(11.5f),
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        button.SymbolConfiguration = NSImageSymbolConfiguration.Create(11, NSFontWeight.Regular);
        button.Activated += (_, _) => click();
        return button;
    }

    public static NSButton Check(string title, Action? changed = null)
    {
        var box = NSButton.CreateCheckbox(title, () => { });
        box.TranslatesAutoresizingMaskIntoConstraints = false;
        box.LineBreakMode = NSLineBreakMode.ByWordWrapping;
        if (changed != null) box.Activated += (_, _) => changed();
        return box;
    }

    public static NSPopUpButton Popup(Action? changed = null)
    {
        var popup = new NSPopUpButton { TranslatesAutoresizingMaskIntoConstraints = false };
        popup.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        if (changed != null) popup.Activated += (_, _) => changed();
        return popup;
    }

    public static NSTextField Field(string placeholder = "")
    {
        var field = new NSTextField { PlaceholderString = placeholder, TranslatesAutoresizingMaskIntoConstraints = false };
        field.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        return field;
    }

    public static NSStackView Row(nfloat spacing, params NSView[] views)
    {
        var row = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = spacing,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        foreach (var view in views)
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            row.AddArrangedSubview(view);
        }
        return row;
    }

    public static NSView Spacer()
    {
        var view = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        view.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        view.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);
        return view;
    }

    public static NSBox Card(NSView content, NSColor? fill = null, NSColor? border = null, nfloat? padding = null, nfloat? radius = null)
    {
        var box = new NSBox
        {
            BoxType = NSBoxType.NSBoxCustom,
            TitlePosition = NSTitlePosition.NoTitle,
            FillColor = fill ?? NSColor.ControlBackground,
            BorderColor = border ?? NSColor.Separator,
            BorderWidth = 1,
            CornerRadius = radius ?? 12,
            ContentViewMargins = CGSize.Empty,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        var pad = padding ?? 16;
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        var inner = (NSView)box.ContentView!;
        inner.AddSubview(content);
        Pin(content, inner, pad, pad, pad, pad);
        return box;
    }

    public static NSView Pad(NSView content, nfloat top, nfloat left, nfloat bottom, nfloat right)
    {
        var view = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        view.AddSubview(content);
        Pin(content, view, top, left, bottom, right);
        return view;
    }

    // Abstände zu den Rändern; null = diese Seite nicht festlegen
    public static void Pin(NSView view, NSView parent, nfloat? top, nfloat? left, nfloat? bottom, nfloat? right)
    {
        if (top is { } t) view.TopAnchor.ConstraintEqualTo(parent.TopAnchor, t).Active = true;
        if (left is { } l) view.LeadingAnchor.ConstraintEqualTo(parent.LeadingAnchor, l).Active = true;
        if (bottom is { } b) view.BottomAnchor.ConstraintEqualTo(parent.BottomAnchor, -b).Active = true;
        if (right is { } r) view.TrailingAnchor.ConstraintEqualTo(parent.TrailingAnchor, -r).Active = true;
    }

    // Scrollbare Seite: Inhalt in einer Spalte mit Höchstbreite
    public static NSScrollView ScrollPage(NSView content, nfloat maxWidth, double top = 40)
    {
        var (scroll, document) = Scroller();
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        document.AddSubview(content);
        Pin(content, document, (nfloat)top, 32, 24, null);
        content.TrailingAnchor.ConstraintLessThanOrEqualTo(document.TrailingAnchor, -32).Active = true;
        var width = content.WidthAnchor.ConstraintEqualTo(maxWidth);
        width.Priority = 500;
        width.Active = true;
        return scroll;
    }

    public static (NSScrollView Scroll, FlippedView Document) Scroller()
    {
        var document = new FlippedView { TranslatesAutoresizingMaskIntoConstraints = false };
        var scroll = new NSScrollView
        {
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            DrawsBackground = false,
            DocumentView = document,
            AutomaticallyAdjustsContentInsets = false,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        scroll.ContentView.DrawsBackground = false;
        document.LeadingAnchor.ConstraintEqualTo(scroll.ContentView.LeadingAnchor).Active = true;
        document.TrailingAnchor.ConstraintEqualTo(scroll.ContentView.TrailingAnchor).Active = true;
        document.TopAnchor.ConstraintEqualTo(scroll.ContentView.TopAnchor).Active = true;
        return (scroll, document);
    }

    public static bool Confirm(string message, string title, bool warning = false)
    {
        var alert = new NSAlert
        {
            MessageText = title,
            InformativeText = message,
            AlertStyle = warning ? NSAlertStyle.Warning : NSAlertStyle.Informational
        };
        alert.AddButton(L.T("Ja"));
        alert.AddButton(L.T("Nein"));
        return alert.RunModal() == (nint)(long)NSAlertButtonReturn.First;
    }

    public static void Info(string message, string title, bool warning = false)
    {
        var alert = new NSAlert
        {
            MessageText = title,
            InformativeText = message,
            AlertStyle = warning ? NSAlertStyle.Warning : NSAlertStyle.Informational
        };
        alert.AddButton("OK");
        alert.RunModal();
    }

    public static void OpenUrl(string url) => NSWorkspace.SharedWorkspace.OpenUrl(new NSUrl(url));

    public static bool CopyText(string text)
    {
        var board = NSPasteboard.GeneralPasteboard;
        board.ClearContents();
        return board.SetStringForType(text, NSPasteboard.NSPasteboardTypeString);
    }

    public static bool CopyImage(byte[] png)
    {
        var board = NSPasteboard.GeneralPasteboard;
        board.ClearContents();
        return board.SetDataForType(NSData.FromArray(png), "public.png");
    }

    public static string? SaveDialog(string title, string fileName, params string[] extensions)
    {
        var panel = new NSSavePanel { Title = title, NameFieldStringValue = fileName, CanCreateDirectories = true };
        panel.AllowedFileTypes = extensions;
        panel.AllowsOtherFileTypes = true;
        return panel.RunModal() == 1 ? panel.Url?.Path : null;
    }

    public static string[] OpenDialog(string title, string[]? extensions, bool multiple)
    {
        var panel = new NSOpenPanel { Title = title, CanChooseFiles = true, CanChooseDirectories = false, AllowsMultipleSelection = multiple };
        if (extensions != null) panel.AllowedFileTypes = extensions;
        return panel.RunModal() == 1 ? panel.Urls.Select(u => u.Path).OfType<string>().ToArray() : [];
    }

    public static string? FolderDialog(string title, string? start = null)
    {
        var panel = new NSOpenPanel { Title = title, CanChooseFiles = false, CanChooseDirectories = true, AllowsMultipleSelection = false, CanCreateDirectories = true };
        if (Directory.Exists(start)) panel.DirectoryUrl = NSUrl.FromFilename(start!);
        return panel.RunModal() == 1 ? panel.Url?.Path : null;
    }
}

// Formatierten Text zusammensetzen (für Hinweise mit fett gesetzten Wörtern und Links)
internal sealed class Rich
{
    private readonly nfloat _size;
    private readonly NSColor _color;

    public NSMutableAttributedString Value { get; } = new();

    public Rich(nfloat? size = null, NSColor? color = null) => (_size, _color) = (size ?? 13, color ?? NSColor.Label);

    private static NSMutableParagraphStyle Lines()
    {
        var style = new NSMutableParagraphStyle { LineSpacing = 3 };
        return style;
    }

    public Rich Text(string text)
    {
        Value.Append(new NSAttributedString(text, new NSStringAttributes
        {
            Font = NSFont.SystemFontOfSize(_size), ForegroundColor = _color, ParagraphStyle = Lines()
        }));
        return this;
    }

    public Rich Bold(string text)
    {
        Value.Append(new NSAttributedString(text, new NSStringAttributes
        {
            Font = NSFont.BoldSystemFontOfSize(_size), ForegroundColor = _color, ParagraphStyle = Lines()
        }));
        return this;
    }

    public Rich Mono(string text)
    {
        Value.Append(new NSAttributedString(text, new NSStringAttributes
        {
            Font = NSFont.MonospacedSystemFont(_size - 1, NSFontWeight.Regular), ForegroundColor = _color, ParagraphStyle = Lines()
        }));
        return this;
    }

    public Rich Link(string text, string url)
    {
        Value.Append(new NSAttributedString(text, new NSStringAttributes
        {
            Font = NSFont.SystemFontOfSize(_size), LinkUrl = new NSUrl(url), ParagraphStyle = Lines()
        }));
        return this;
    }

    public TextBlockView View()
    {
        var view = new TextBlockView();
        view.SetAttributed(Value);
        return view;
    }
}
