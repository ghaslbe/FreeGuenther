using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;

namespace LmChat;

// Wandelt Markdown (wie es die Modelle liefern) in WPF-FlowDocument-Blöcke um:
// Überschriften, Absätze, fett/kursiv, Listen, Code, Zitate, Tabellen, Links.
internal static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        .Build();

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private static readonly Brush CodeBackground = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0x80, 0x80, 0x80)));
    private static readonly Brush RuleBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x70, 0x80, 0x80, 0x80)));

    public static void Render(string markdown, BlockCollection target)
    {
        foreach (var block in Markdown.Parse(markdown, Pipeline))
            AddBlock(block, target);
    }

    private static void AddBlock(MdBlock block, BlockCollection target)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                var p = new Paragraph
                {
                    FontWeight = FontWeights.SemiBold,
                    FontSize = heading.Level switch { 1 => 22, 2 => 19, 3 => 17, _ => 15 },
                    Margin = new Thickness(0, 10, 0, 4)
                };
                AddInlines(heading.Inline, p.Inlines);
                target.Add(p);
                break;
            }

            case ParagraphBlock paragraph:
            {
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                AddInlines(paragraph.Inline, p.Inlines);
                target.Add(p);
                break;
            }

            case ListBlock list:
            {
                var l = new List
                {
                    MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(22, 0, 0, 0)
                };
                if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start))
                    l.StartIndex = start;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var li = new ListItem();
                    foreach (var child in item)
                        AddBlock(child, li.Blocks);
                    foreach (var p in li.Blocks.OfType<Paragraph>())
                        p.Margin = new Thickness(0, 0, 0, 3);
                    l.ListItems.Add(li);
                }
                target.Add(l);
                break;
            }

            case CodeBlock code:
                target.Add(new Paragraph(new Run(code.Lines.ToString().TrimEnd('\r', '\n')))
                {
                    FontFamily = Mono,
                    FontSize = 13,
                    Background = CodeBackground,
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 2, 0, 10)
                });
                break;

            case QuoteBlock quote:
            {
                var section = new Section
                {
                    BorderBrush = RuleBrush,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 0, 0, 0),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                foreach (var child in quote)
                    AddBlock(child, section.Blocks);
                target.Add(section);
                break;
            }

            case ThematicBreakBlock:
                target.Add(new Paragraph
                {
                    BorderBrush = RuleBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    FontSize = 2,
                    Margin = new Thickness(0, 4, 0, 12)
                });
                break;

            case MdTable table:
                target.Add(BuildTable(table));
                break;

            case ContainerBlock container:
                foreach (var child in container)
                    AddBlock(child, target);
                break;

            case LeafBlock leaf when leaf.Lines.Count > 0:
                // z. B. HTML-Blöcke: als Text zeigen, nicht ausführen
                target.Add(new Paragraph(new Run(leaf.Lines.ToString())) { Margin = new Thickness(0, 0, 0, 8) });
                break;
        }
    }

    private static System.Windows.Documents.Table BuildTable(MdTable source)
    {
        var table = new System.Windows.Documents.Table
        {
            CellSpacing = 0,
            BorderBrush = RuleBrush,
            BorderThickness = new Thickness(1, 1, 0, 0),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var rows = source.OfType<MdTableRow>().ToList();
        var columns = rows.Select(r => r.Count).DefaultIfEmpty(0).Max();
        for (var i = 0; i < columns; i++)
            table.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        foreach (var row in rows)
        {
            var tr = new System.Windows.Documents.TableRow();
            if (row.IsHeader)
            {
                tr.FontWeight = FontWeights.SemiBold;
                tr.Background = CodeBackground;
            }
            foreach (var cell in row.OfType<MdTableCell>())
            {
                var tc = new System.Windows.Documents.TableCell
                {
                    BorderBrush = RuleBrush,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(6, 3, 6, 3)
                };
                foreach (var child in cell)
                    AddBlock(child, tc.Blocks);
                foreach (var p in tc.Blocks.OfType<Paragraph>())
                    p.Margin = new Thickness(0);
                tr.Cells.Add(tc);
            }
            group.Rows.Add(tr);
        }
        table.RowGroups.Add(group);
        return table;
    }

    private static void AddInlines(ContainerInline? container, InlineCollection target)
    {
        if (container == null) return;
        foreach (var inline in container)
            AddInline(inline, target);
    }

    private static void AddInline(MdInline inline, InlineCollection target)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;

            case CodeInline code:
                target.Add(new Run(code.Content) { FontFamily = Mono, Background = CodeBackground });
                break;

            case LineBreakInline lineBreak:
                target.Add(lineBreak.IsHard ? new LineBreak() : new Run(" "));
                break;

            case EmphasisInline emphasis:
            {
                Span span = emphasis.DelimiterChar == '~'
                    ? new Span { TextDecorations = TextDecorations.Strikethrough }
                    : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                foreach (var child in emphasis)
                    AddInline(child, span.Inlines);
                target.Add(span);
                break;
            }

            case LinkInline link when !link.IsImage && SafeUri(link.Url) is { } uri:
            {
                var hyperlink = MakeHyperlink(uri);
                foreach (var child in link)
                    AddInline(child, hyperlink.Inlines);
                if (hyperlink.Inlines.Count == 0)
                    hyperlink.Inlines.Add(new Run(link.Url));
                target.Add(hyperlink);
                break;
            }

            case AutolinkInline auto when SafeUri(auto.IsEmail ? "mailto:" + auto.Url : auto.Url) is { } uri:
            {
                var hyperlink = MakeHyperlink(uri);
                hyperlink.Inlines.Add(new Run(auto.Url));
                target.Add(hyperlink);
                break;
            }

            case TaskList task:
                target.Add(new Run(task.Checked ? "☑ " : "☐ "));
                break;

            case ContainerInline container:
                foreach (var child in container)
                    AddInline(child, target);
                break;

            case HtmlInline html:
                target.Add(new Run(html.Tag));
                break;

            case HtmlEntityInline entity:
                target.Add(new Run(entity.Transcoded.ToString()));
                break;
        }
    }

    // Nur Web- und Mail-Links öffnen – keine Dateipfade o. Ä. aus Modellantworten
    private static Uri? SafeUri(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto" ? uri : null;

    private static Hyperlink MakeHyperlink(Uri uri)
    {
        var hyperlink = new Hyperlink { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
        hyperlink.Click += (_, _) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return hyperlink;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
