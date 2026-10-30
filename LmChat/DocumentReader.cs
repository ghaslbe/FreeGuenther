using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace LmChat;

// Wandelt angehängte Dateien in Text um, den das Modell lesen kann: PDF (PdfPig), Word (.docx), Excel (.xlsx) und Textdateien
internal static class DocumentReader
{
    private static readonly string[] TextExtensions =
    [
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".html", ".htm", ".log", ".ini", ".yaml", ".yml", ".toml",
        ".cs", ".py", ".js", ".ts", ".tsx", ".jsx", ".java", ".kt", ".c", ".h", ".cpp", ".hpp", ".go", ".rs", ".php", ".rb",
        ".swift", ".sql", ".css", ".scss", ".sh", ".ps1", ".bat", ".cmd", ".xaml", ".csproj", ".config"
    ];

    public static readonly string[] Extensions = [".pdf", ".docx", ".xlsx", .. TextExtensions];

    // Sehr große Dateien kürzen – mehr passt ohnehin in kaum ein Modell
    public const int MaxChars = 400_000;

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // ≈ Tokens (Faustregel: ein Token sind etwa 4 Zeichen)
    public static int EstimateTokens(string text) => text.Length / 4;

    // Wirft InvalidDataException mit verständlicher Meldung, wenn die Datei nicht lesbar ist
    public static AttachedDocument Read(string path)
    {
        var name = Path.GetFileName(path);
        string text;
        try
        {
            text = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".pdf" => ReadPdf(path),
                ".docx" => ReadDocx(path),
                ".xlsx" => ReadXlsx(path),
                _ => ReadText(path)
            };
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException(L.F("„{0}“ konnte nicht gelesen werden: {1}", name, ex.Message), ex);
        }

        text = text.ReplaceLineEndings("\n").Trim();
        if (text.Length == 0)
            throw new InvalidDataException(L.F("„{0}“ enthält keinen lesbaren Text (z. B. eine eingescannte PDF ohne Texterkennung).", name));
        if (text.Length > MaxChars)
            text = text[..MaxChars] + "\n\n" + L.F("[… gekürzt – die Datei hat {0:N0} Zeichen, mitgeschickt wurden die ersten {1:N0}.]", text.Length, MaxChars);
        return new AttachedDocument(name, text);
    }

    private static string ReadPdf(string path)
    {
        var sb = new StringBuilder();
        using var pdf = PdfDocument.Open(path);
        foreach (var page in pdf.GetPages())
        {
            var pageText = ContentOrderTextExtractor.GetText(page).Trim();
            if (pageText.Length == 0) continue;
            sb.AppendLine(L.F("[Seite {0}]", page.Number)).AppendLine(pageText).AppendLine();
        }
        return sb.ToString();
    }

    // Word: Text aus word/document.xml – Absätze, Tabs und Zeilenumbrüche bleiben erhalten
    private static string ReadDocx(string path)
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException(L.T("Keine gültige Word-Datei (.docx)."));
        using var reader = XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var sb = new StringBuilder();
        reader.Read();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == W)
            {
                switch (reader.LocalName)
                {
                    case "t":
                        sb.Append(reader.ReadElementContentAsString());
                        continue;
                    case "tab":
                        sb.Append('\t');
                        break;
                    case "br" or "cr":
                        sb.Append('\n');
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "p" && reader.NamespaceURI == W)
                sb.Append('\n');
            reader.Read();
        }
        return sb.ToString();
    }

    // Excel: jedes Tabellenblatt als Überschrift plus Zeilen mit Tabulatoren zwischen den Zellen
    private static string ReadXlsx(string path)
    {
        const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        using var zip = ZipFile.OpenRead(path);
        XDocument Load(string name) =>
            XDocument.Load((zip.GetEntry(name) ?? throw new InvalidDataException(L.T("Keine gültige Excel-Datei (.xlsx)."))).Open());

        // Gemeinsame Texte (Zellen mit t="s" verweisen per Index darauf)
        var shared = zip.GetEntry("xl/sharedStrings.xml") is null ? [] :
            Load("xl/sharedStrings.xml").Root!.Elements(XName.Get("si", S))
                .Select(si => string.Concat(si.Descendants(XName.Get("t", S)).Select(t => t.Value))).ToList();

        // Blattnamen und ihre Dateien (über workbook.xml.rels)
        var rels = Load("xl/_rels/workbook.xml.rels").Root!.Elements()
            .ToDictionary(e => (string)e.Attribute("Id")!, e => (string)e.Attribute("Target")!);
        var sb = new StringBuilder();
        foreach (var sheet in Load("xl/workbook.xml").Descendants(XName.Get("sheet", S)))
        {
            if (!rels.TryGetValue((string?)sheet.Attribute(XName.Get("id", R)) ?? "", out var target)) continue;
            var file = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            if (zip.GetEntry(file) is null) continue;
            sb.AppendLine($"## {(string?)sheet.Attribute("name")}");
            foreach (var row in Load(file).Descendants(XName.Get("row", S)))
            {
                var cells = new SortedDictionary<int, string>();
                foreach (var c in row.Elements(XName.Get("c", S)))
                {
                    var type = (string?)c.Attribute("t");
                    var value = type switch
                    {
                        "s" when int.TryParse((string?)c.Element(XName.Get("v", S)), out var i) && i < shared.Count => shared[i],
                        "inlineStr" => string.Concat(c.Descendants(XName.Get("t", S)).Select(t => t.Value)),
                        "b" => (string?)c.Element(XName.Get("v", S)) == "1" ? "TRUE" : "FALSE",
                        _ => (string?)c.Element(XName.Get("v", S)) ?? ""
                    };
                    cells[ColumnIndex((string?)c.Attribute("r") ?? "", cells.Count)] = value.ReplaceLineEndings(" ");
                }
                if (cells.Count == 0 || cells.Values.All(v => v.Length == 0)) continue;
                var last = cells.Keys.Max();
                sb.AppendLine(string.Join("\t", Enumerable.Range(0, last + 1).Select(i => cells.GetValueOrDefault(i, ""))));
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // "C7" → 2 (Spalte C); ohne Zellbezug die laufende Nummer
    private static int ColumnIndex(string reference, int fallback)
    {
        var letters = new string(reference.TakeWhile(char.IsLetter).ToArray());
        return letters.Length == 0 ? fallback : letters.Aggregate(0, (n, ch) => n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1)) - 1;
    }

    // UTF-8 (mit oder ohne BOM) bzw. UTF-16 mit BOM; sonst als Windows-Westeuropäisch (Latin-1) lesen
    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF) ||
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            using var sr = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return sr.ReadToEnd();
        }
        if (Array.IndexOf(bytes, (byte)0) >= 0)
            throw new InvalidDataException(L.T("Das ist keine Textdatei."));
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
