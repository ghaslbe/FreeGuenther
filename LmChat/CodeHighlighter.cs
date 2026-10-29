using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace LmChat;

// Einfache Syntax-Hervorhebung für Code-Blöcke: Kommentare, Zeichenketten, Zahlen und Schlüsselwörter.
// Kein vollständiger Parser – reicht für gut lesbare Antworten. Die Farben sind in Hell und Dunkel lesbar.
internal static class CodeHighlighter
{
    private static readonly Brush KeywordBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4F, 0x8C, 0xF0)));
    private static readonly Brush StringBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x5E)));
    private static readonly Brush NumberBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD9, 0x7E, 0x1A)));
    private static readonly Brush CommentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C)));

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

    public static void Highlight(string code, string? language, InlineCollection target)
    {
        if (ForName(language) is not { } lang)
        {
            target.Add(new Run(code));
            return;
        }

        var pos = 0;
        foreach (Match m in lang.Pattern.Matches(code))
        {
            Brush? brush = null;
            if (m.Groups["c"].Success) brush = CommentBrush;
            else if (m.Groups["s"].Success) brush = StringBrush;
            else if (m.Groups["n"].Success) brush = NumberBrush;
            else if (m.Groups["w"].Success)
            {
                var word = m.Value;
                var isKeyword = lang == MarkupLang ||
                                lang.Keywords.Contains(lang.IgnoreCase ? word.ToLowerInvariant() : word) ||
                                (lang.IgnoreCase && lang.Keywords.Contains(word));
                if (!isKeyword) continue;
                brush = KeywordBrush;
            }
            if (brush == null) continue;

            if (m.Index > pos) target.Add(new Run(code[pos..m.Index]));
            var run = new Run(m.Value) { Foreground = brush };
            if (brush == CommentBrush) run.FontStyle = FontStyles.Italic;
            target.Add(run);
            pos = m.Index + m.Length;
        }
        if (pos < code.Length) target.Add(new Run(code[pos..]));
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
