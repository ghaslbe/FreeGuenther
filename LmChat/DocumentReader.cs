using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace LmChat;

// Wandelt angehängte Dateien in Text um, den das Modell lesen kann: PDF (PdfPig), Word (.docx) und Textdateien
internal static class DocumentReader
{
    private static readonly string[] TextExtensions =
    [
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".html", ".htm", ".log", ".ini", ".yaml", ".yml", ".toml",
        ".cs", ".py", ".js", ".ts", ".tsx", ".jsx", ".java", ".kt", ".c", ".h", ".cpp", ".hpp", ".go", ".rs", ".php", ".rb",
        ".swift", ".sql", ".css", ".scss", ".sh", ".ps1", ".bat", ".cmd", ".xaml", ".csproj", ".config"
    ];

    public static readonly string[] Extensions = [".pdf", ".docx", .. TextExtensions];

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
                _ => ReadText(path)
            };
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"„{name}“ konnte nicht gelesen werden: {ex.Message}", ex);
        }

        text = text.Trim();
        if (text.Length == 0)
            throw new InvalidDataException($"„{name}“ enthält keinen lesbaren Text (z. B. eine eingescannte PDF ohne Texterkennung).");
        if (text.Length > MaxChars)
            text = text[..MaxChars] + $"\n\n[… gekürzt – die Datei hat {text.Length:N0} Zeichen, mitgeschickt wurden die ersten {MaxChars:N0}.]";
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
            sb.AppendLine($"[Seite {page.Number}]").AppendLine(pageText).AppendLine();
        }
        return sb.ToString();
    }

    // Word: Text aus word/document.xml – Absätze, Tabs und Zeilenumbrüche bleiben erhalten
    private static string ReadDocx(string path)
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("Keine gültige Word-Datei (.docx).");
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
            throw new InvalidDataException("Das ist keine Textdatei.");
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
