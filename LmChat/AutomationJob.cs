using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LmChat;

// Auftrag der Datei-Automatik: Neue Dateien eines Typs im Ordner werden mit dem Prompt an ein Modell geschickt,
// das Ergebnis landet daneben (gleicher Name, Endung der Ausgabe) – sofern es diese Datei noch nicht gibt.
public sealed class AutomationJob
{
    public bool Enabled { get; set; }
    public string Folder { get; set; } = "";
    public string Kind { get; set; } = "pdf";        // images | pdf | excel | word
    public string Prompt { get; set; } = "";
    public string Output { get; set; } = "txt";      // txt | json | md
    public int IntervalSeconds { get; set; } = 30;
    public ProviderKind Provider { get; set; } = ProviderKind.LmStudio;
    public string Model { get; set; } = "";

    // Dateinamen, die beim Starten schon im Ordner lagen – die werden nicht bearbeitet
    public List<string> Baseline { get; set; } = new();

    public const int MinInterval = 5;

    public static readonly string[] Kinds = ["images", "pdf", "excel", "word"];
    public static readonly string[] Outputs = ["txt", "json", "md"];

    public static string[] ExtensionsOf(string kind) => kind switch
    {
        "images" => [".jpg", ".jpeg", ".png", ".bmp"],
        "excel" => [".xlsx"],
        "word" => [".docx"],
        _ => [".pdf"]
    };

    public bool Matches(string path) => ExtensionsOf(Kind).Contains(Path.GetExtension(path).ToLowerInvariant());

    public string OutputPath(string path) => Path.ChangeExtension(path, "." + Output);

    public IEnumerable<string> MatchingFiles() =>
        Directory.Exists(Folder) ? Directory.EnumerateFiles(Folder).Where(Matches) : [];

    // Anweisung zum Ausgabeformat (geht ans Modell, deshalb nicht übersetzt)
    public string FormatInstruction => Output switch
    {
        "json" => "Antworte ausschließlich mit gültigem JSON – ohne Erklärung, ohne Codeblock, ohne Text davor oder danach.",
        "md" => "Antworte in Markdown.",
        _ => "Antworte als reiner Text ohne Markdown-Formatierung."
    };

    // Antwort des Modells in den Dateiinhalt umwandeln. Bei JSON wird geprüft; ungültige Antworten werden verpackt,
    // damit die Datei trotzdem gültiges JSON ist (validJson = false)
    public string FileContent(string answer, string sourceName, out bool validJson)
    {
        validJson = true;
        if (Output != "json") return answer.Trim() + Environment.NewLine;
        var json = Regex.Replace(answer.Trim(), @"^```(?:json)?\s*|\s*```$", "").Trim();
        try
        {
            var node = JsonNode.Parse(json);
            return node!.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
                   + Environment.NewLine;
        }
        catch (JsonException)
        {
            validJson = false;
            return new JsonObject { ["datei"] = sourceName, ["antwort"] = answer.Trim() }
                .ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
                + Environment.NewLine;
        }
    }

    public static AutomationJob Load(ChatStore store)
    {
        try
        {
            return store.GetSetting("automation") is { } json ? JsonSerializer.Deserialize<AutomationJob>(json) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save(ChatStore store) => store.SetSetting("automation", JsonSerializer.Serialize(this));
}
