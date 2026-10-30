using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace LmChat;

// Wandelt Markdown (wie es die Modelle liefern) in Views um: zusammenhängender Text landet in einer gemeinsamen
// TextBlockView (Markieren über mehrere Absätze), Code-Blöcke bekommen eine eigene Karte mit Knopf „Kopieren“.
internal sealed class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        .Build();

    // Für den Export als HTML: rohes HTML aus Modellantworten nicht übernehmen
    private static readonly MarkdownPipeline HtmlPipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        .DisableHtml()
        .Build();

    private const float BodySize = 15;
    private static readonly NSColor CodeFill = NSColor.FromSrgb(0.5f, 0.5f, 0.5f, 0.15f);
    private static readonly NSColor Rule = NSColor.FromSrgb(0.5f, 0.5f, 0.5f, 0.45f);

    private readonly Col _target;
    private readonly NSMutableAttributedString _text = new();

    private MarkdownRenderer(Col target) => _target = target;

    public static string ToHtml(string markdown) => Markdown.ToHtml(markdown, HtmlPipeline);

    public static void Render(string markdown, Col target)
    {
        var renderer = new MarkdownRenderer(target);
        foreach (var block in Markdown.Parse(markdown, Pipeline))
            renderer.AddBlock(block, new Context());
        renderer.Flush();
    }

    // Was für die aktuelle Stelle im Dokument gilt
    private sealed record Context(double Indent = 0, double HangIndent = 0, NSColor? Color = null, NSTextBlock[]? Blocks = null);

    private void Flush()
    {
        if (_text.Length == 0) return;
        // Letzten Zeilenumbruch nicht als leere Zeile stehen lassen
        if (_text.Value.EndsWith('\n'))
            _text.DeleteRange(new NSRange(_text.Length - 1, 1));
        var view = new TextBlockView();
        view.SetAttributed((NSAttributedString)_text.Copy());
        _target.Add(view);
        _target.Gap(view, 8);
        _text.SetString(new NSAttributedString(""));
    }

    private static NSMutableParagraphStyle Paragraph(Context c, double after, double before = 0)
    {
        var style = new NSMutableParagraphStyle
        {
            LineSpacing = 4,
            ParagraphSpacing = (nfloat)after,
            ParagraphSpacingBefore = (nfloat)before,
            FirstLineHeadIndent = (nfloat)c.Indent,
            HeadIndent = (nfloat)(c.Indent + c.HangIndent),
        };
        if (c.HangIndent > 0)
            style.TabStops = [new NSTextTab(NSTextAlignment.Left, (nfloat)(c.Indent + c.HangIndent), new NSDictionary())];
        if (c.Blocks != null) style.TextBlocks = c.Blocks;
        return style;
    }

    private void AddBlock(MdBlock block, Context c)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                var size = heading.Level switch { 1 => 22, 2 => 19, 3 => 17, _ => 15 };
                AddParagraph(heading.Inline, c, Paragraph(c, 4, 10), NSFont.BoldSystemFontOfSize(size));
                break;
            }

            case ParagraphBlock paragraph:
                AddParagraph(paragraph.Inline, c, Paragraph(c, 8), NSFont.SystemFontOfSize(BodySize));
                break;

            case ListBlock list:
            {
                var number = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var marker = list.IsOrdered ? $"{number++}." : "•";
                    var inner = c with { Indent = c.Indent, HangIndent = 22 };
                    var first = true;
                    foreach (var child in item)
                    {
                        if (first && child is ParagraphBlock p)
                        {
                            AddText(marker + "\t", Attributes(NSFont.SystemFontOfSize(BodySize), c, Paragraph(inner, 3)));
                            AddInlines(p.Inline, NSFont.SystemFontOfSize(BodySize), c, Paragraph(inner, 3));
                            AddText("\n", Attributes(NSFont.SystemFontOfSize(BodySize), c, Paragraph(inner, 3)));
                        }
                        else
                            AddBlock(child, c with { Indent = c.Indent + 22, HangIndent = 0 });
                        first = false;
                    }
                }
                break;
            }

            case CodeBlock code:
                Flush();
                _target.Add(CodeCard(code.Lines.ToString().TrimEnd('\r', '\n'), (code as FencedCodeBlock)?.Info));
                _target.Gap(_target.ArrangedSubviews[^1], 10);
                break;

            case QuoteBlock quote:
                foreach (var child in quote)
                    AddBlock(child, c with { Indent = c.Indent + 14, Color = NSColor.SecondaryLabel });
                break;

            case ThematicBreakBlock:
            {
                Flush();
                var line = new NSBox { BoxType = NSBoxType.NSBoxSeparator, TranslatesAutoresizingMaskIntoConstraints = false };
                _target.Add(line);
                _target.Gap(line, 12);
                break;
            }

            case Table table:
                AddTable(table, c);
                break;

            case ContainerBlock container:
                foreach (var child in container)
                    AddBlock(child, c);
                break;

            case LeafBlock leaf when leaf.Lines.Count > 0:
                // z. B. HTML-Blöcke: als Text zeigen, nicht ausführen
                AddText(leaf.Lines.ToString() + "\n", Attributes(NSFont.SystemFontOfSize(BodySize), c, Paragraph(c, 8)));
                break;
        }
    }

    private void AddTable(Table source, Context c)
    {
        var rows = source.OfType<TableRow>().ToList();
        var columns = rows.Select(r => r.Count).DefaultIfEmpty(0).Max();
        if (columns == 0) return;
        var table = new NSTextTable { Columns = columns, LayoutAlgorithm = NSTextTableLayoutAlgorithm.Automatic, CollapsesBorders = true };
        table.SetContentWidth(100, NSTextBlockValueType.Percentage);
        for (var r = 0; r < rows.Count; r++)
        {
            var cells = rows[r].OfType<TableCell>().ToList();
            for (var i = 0; i < columns; i++)
            {
                var block = new NSTextTableBlock(table, r, 1, i, 1);
                block.SetBorderColor(Rule);
                block.SetWidth(1, NSTextBlockValueType.Absolute, NSTextBlockLayer.Border);
                block.SetWidth(6, NSTextBlockValueType.Absolute, NSTextBlockLayer.Padding);
                if (rows[r].IsHeader) block.BackgroundColor = CodeFill;
                var font = rows[r].IsHeader ? NSFont.BoldSystemFontOfSize(13.5f) : NSFont.SystemFontOfSize(13.5f);
                var style = new NSMutableParagraphStyle { TextBlocks = [block] };
                if (i < cells.Count)
                    foreach (var inner in cells[i].OfType<ParagraphBlock>())
                        AddInlines(inner.Inline, font, c, style);
                AddText("\n", Attributes(font, c, style));
            }
        }
        AddText("\n", Attributes(NSFont.SystemFontOfSize(6), c, new NSMutableParagraphStyle()));
    }

    private void AddParagraph(ContainerInline? inline, Context c, NSParagraphStyle style, NSFont font)
    {
        AddInlines(inline, font, c, style);
        AddText("\n", Attributes(font, c, style));
    }

    private void AddText(string text, NSStringAttributes attributes) =>
        _text.Append(new NSAttributedString(text, attributes));

    private static NSStringAttributes Attributes(NSFont font, Context c, NSParagraphStyle style) => new()
    {
        Font = font,
        ForegroundColor = c.Color ?? NSColor.Label,
        ParagraphStyle = style
    };

    private void AddInlines(ContainerInline? container, NSFont font, Context c, NSParagraphStyle style)
    {
        if (container == null) return;
        foreach (var inline in container)
            AddInline(inline, font, c, style, null, false);
    }

    private void AddInline(MdInline inline, NSFont font, Context c, NSParagraphStyle style, NSUrl? link, bool strike)
    {
        NSStringAttributes Attr()
        {
            var a = Attributes(font, c, style);
            if (link != null) a.LinkUrl = link;
            if (strike) a.StrikethroughStyle = (int)NSUnderlineStyle.Single;
            return a;
        }

        switch (inline)
        {
            case LiteralInline literal:
                AddText(literal.Content.ToString(), Attr());
                break;

            case CodeInline code:
            {
                var a = Attr();
                a.Font = NSFont.MonospacedSystemFont(font.PointSize - 1.5f, NSFontWeight.Regular);
                a.BackgroundColor = CodeFill;
                AddText(code.Content, a);
                break;
            }

            case LineBreakInline lineBreak:
                AddText(lineBreak.IsHard ? "\u2028" : " ", Attr());
                break;

            case EmphasisInline emphasis:
            {
                var isStrike = emphasis.DelimiterChar == '~';
                var inner = isStrike ? font
                    : emphasis.DelimiterCount >= 2 ? NSFontManager.SharedFontManager.ConvertFont(font, NSFontTraitMask.Bold)!
                    : NSFontManager.SharedFontManager.ConvertFont(font, NSFontTraitMask.Italic)!;
                foreach (var child in emphasis)
                    AddInline(child, inner, c, style, link, strike || isStrike);
                break;
            }

            case LinkInline l when !l.IsImage && SafeUri(l.Url) is { } uri:
            {
                var before = _text.Length;
                foreach (var child in l)
                    AddInline(child, font, c, style, uri, strike);
                if (_text.Length == before)
                    AddText(l.Url ?? "", Attr());
                break;
            }

            case AutolinkInline auto when SafeUri(auto.IsEmail ? "mailto:" + auto.Url : auto.Url) is { } uri:
            {
                var a = Attr();
                a.LinkUrl = uri;
                AddText(auto.Url, a);
                break;
            }

            case TaskList task:
                AddText(task.Checked ? "☑ " : "☐ ", Attr());
                break;

            case ContainerInline container:
                foreach (var child in container)
                    AddInline(child, font, c, style, link, strike);
                break;

            case HtmlInline html:
                AddText(html.Tag, Attr());
                break;

            case HtmlEntityInline entity:
                AddText(entity.Transcoded.ToString(), Attr());
                break;
        }
    }

    // Nur Web- und Mail-Links öffnen – keine Dateipfade o. Ä. aus Modellantworten
    private static NSUrl? SafeUri(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto" ? new NSUrl(uri.AbsoluteUri) : null;

    // Kopfzeile mit Sprache und Knopf „Kopieren“, darunter der Code mit Hervorhebung
    private static NSView CodeCard(string code, string? language)
    {
        var name = Ui.Label(string.IsNullOrWhiteSpace(language) ? "Code" : language.Trim(), 11.5f, NSColor.SecondaryLabel);
        NSButton? copy = null;
        copy = Ui.SmallButton("doc.on.doc", L.T("Kopieren"), L.T("Code in die Zwischenablage kopieren"), async () =>
        {
            if (!Ui.CopyText(code)) return;
            copy!.Title = L.T("Kopiert ✓");
            await Task.Delay(1800);
            copy.Title = L.T("Kopieren");
        });
        var head = Ui.Row(6, name, Ui.Spacer(), copy);

        var body = new TextBlockView();
        body.SetAttributed(CodeHighlighter.Highlight(code, language));

        var column = new Col(6);
        column.Add(head);
        column.Add(body);
        return Ui.Card(column, CodeFill, NSColor.Clear, 10, 8);
    }
}

// Einfache Syntax-Hervorhebung für Code-Blöcke: Kommentare, Zeichenketten, Zahlen und Schlüsselwörter.
// Kein vollständiger Parser – reicht für gut lesbare Antworten. Die Farben sind in Hell und Dunkel lesbar.
internal static class CodeHighlighter
{
    private static readonly NSColor KeywordColor = NSColor.FromSrgb(0x4F / 255f, 0x8C / 255f, 0xF0 / 255f, 1);
    private static readonly NSColor StringColor = NSColor.FromSrgb(0x2E / 255f, 0xA0 / 255f, 0x5E / 255f, 1);
    private static readonly NSColor NumberColor = NSColor.FromSrgb(0xD9 / 255f, 0x7E / 255f, 0x1A / 255f, 1);
    private static readonly NSColor CommentColor = NSColor.FromSrgb(0x8C / 255f, 0x8C / 255f, 0x8C / 255f, 1);

    private sealed record Language(Regex Pattern, HashSet<string> Keywords, bool IgnoreCase = false);

    private const string CLike = "abstract as async await base bool break byte case catch char class const continue default delegate do double " +
        "else enum export extends false final finally float fn for foreach from func function go if impl implements import in int " +
        "interface internal is let long match mod mut namespace new nil null object operator out override package private protected " +
        "pub public readonly record ref return sealed self short static string struct super switch this throw true try type typeof " +
        "uint ulong use using val var virtual void volatile where while yield";
    private const string Python = "and as assert async await break class continue def del elif else except False finally for from global " +
        "if import in is lambda None nonlocal not or pass raise return self True try while with yield print";
    private const string Shell = "if then else elif fi for while do done case esac function in return echo export local set " +
        "param foreach switch try catch finally throw begin process end true false null";
    private const string Sql = "select from where and or not insert into values update set delete create table index drop alter add " +
        "primary key foreign references join left right inner outer on group by order having limit offset as distinct union all " +
        "null is in like between case when then else end count sum avg min max exists";

    // Reihenfolge in der Alternation: Kommentar vor Zeichenkette vor Zahl vor Wort
    private static Regex Build(string comments, string strings) => new(
        $@"(?<c>{comments})|(?<s>{strings})|(?<n>\b\d[\d_]*(\.\d+)?([eE][+-]?\d+)?[fFdDmMuUlL]?\b|\b0x[0-9a-fA-F]+\b)|(?<w>[A-Za-z_$@][\w$]*)",
        RegexOptions.Compiled);

    private const string DoubleQuoted = "\"(?:\\\\.|[^\"\\\\\\n])*\"";
    private const string SingleQuoted = "'(?:\\\\.|[^'\\\\\\n])*'";

    private static readonly Language CLikeLang = new(
        Build(@"//[^\n]*|/\*[\s\S]*?\*/", $"{DoubleQuoted}|{SingleQuoted}|`[^`]*`"), Words(CLike));
    private static readonly Language PythonLang = new(
        Build("#[^\\n]*", "\"\"\"[\\s\\S]*?\"\"\"|'''[\\s\\S]*?'''|" + $"{DoubleQuoted}|{SingleQuoted}"), Words(Python));
    private static readonly Language ShellLang = new(
        Build("#[^\\n]*", $"{DoubleQuoted}|{SingleQuoted}"), Words(Shell), IgnoreCase: true);
    private static readonly Language SqlLang = new(
        Build("--[^\\n]*|/\\*[\\s\\S]*?\\*/", SingleQuoted), Words(Sql), IgnoreCase: true);
    private static readonly Language JsonLang = new(
        Build("(?!)", DoubleQuoted), Words("true false null"));
    private static readonly Language MarkupLang = new(
        new Regex(@"(?<c><!--[\s\S]*?-->)|(?<s>""[^""]*""|'[^']*')|(?<w></?[A-Za-z][\w:.-]*|/?>)", RegexOptions.Compiled), []);

    private static HashSet<string> Words(string list) => list.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    private static Language? ForName(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "text" or "txt" or "plaintext" or "markdown" or "md" => null,
        "py" or "python" or "python3" => PythonLang,
        "sh" or "bash" or "shell" or "zsh" or "ps" or "ps1" or "powershell" or "pwsh" or "bat" or "cmd" or "yaml" or "yml"
            or "toml" or "ini" or "dockerfile" or "makefile" => ShellLang,
        "sql" or "sqlite" or "postgres" or "mysql" => SqlLang,
        "json" or "jsonc" => JsonLang,
        "html" or "xml" or "xaml" or "svg" or "csproj" => MarkupLang,
        _ => CLikeLang
    };

    public static NSAttributedString Highlight(string code, string? language)
    {
        var font = NSFont.MonospacedSystemFont(13, NSFontWeight.Regular);
        var result = new NSMutableAttributedString();
        void Add(string text, NSColor color) =>
            result.Append(new NSAttributedString(text, new NSStringAttributes { Font = font, ForegroundColor = color }));

        if (ForName(language) is not { } lang)
        {
            Add(code, NSColor.Label);
            return result;
        }

        var pos = 0;
        foreach (Match m in lang.Pattern.Matches(code))
        {
            NSColor? color = null;
            if (m.Groups["c"].Success) color = CommentColor;
            else if (m.Groups["s"].Success) color = StringColor;
            else if (m.Groups["n"].Success) color = NumberColor;
            else if (m.Groups["w"].Success)
            {
                var word = m.Value;
                var isKeyword = lang == MarkupLang ||
                                lang.Keywords.Contains(lang.IgnoreCase ? word.ToLowerInvariant() : word) ||
                                (lang.IgnoreCase && lang.Keywords.Contains(word));
                if (!isKeyword) continue;
                color = KeywordColor;
            }
            if (color == null) continue;

            if (m.Index > pos) Add(code[pos..m.Index], NSColor.Label);
            Add(m.Value, color);
            pos = m.Index + m.Length;
        }
        if (pos < code.Length) Add(code[pos..], NSColor.Label);
        return result;
    }
}
