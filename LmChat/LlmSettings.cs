using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LmChat;

// Adresse, Schlüssel und Modell – je Anbieter getrennt gespeichert
public sealed class ProviderConfig
{
    public string Server { get; set; } = "";
    public string Model { get; set; } = "";
    public bool? ModelVision { get; set; }
    public bool ModelImageGen { get; set; }
    public bool? ModelTools { get; set; }   // kann Werkzeuge (Plugins) aufrufen; null = unbekannt

    // Nur OpenRouter: "" = weltweit, "eu" / "us" = nur Rechenzentren in dieser Region
    public string Region { get; set; } = "";

    // API-Schlüssel nur verschlüsselt (Windows DPAPI, nur für das eigene Windows-Konto lesbar)
    public string ApiKeyProtected { get; set; } = "";

    [JsonIgnore]
    public string ApiKey
    {
        get => Secret.Unprotect(ApiKeyProtected);
        set => ApiKeyProtected = Secret.Protect(value);
    }
}

// Vorlage: Persona = wirkt als zusätzlicher System-Prompt für den ganzen Chat, sonst Text fürs Eingabefeld
public sealed class PromptTemplate
{
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Persona { get; set; }

    // Nur für die Anzeige in Listen
    [JsonIgnore] public string Kind => Persona ? "🎭 " + L.T("Persona") : "📝 " + L.T("Vorlage");
    [JsonIgnore]
    public string Preview
    {
        get
        {
            var oneLine = Text.ReplaceLineEndings(" ").Trim();
            return oneLine.Length > 90 ? oneLine[..90] + " …" : oneLine;
        }
    }

    // Mitgelieferte Beispiele in der eingestellten Sprache (werden erst beim Speichern in den Einstellungen fest übernommen)
    public static List<PromptTemplate> Defaults() =>
    [
        new() { Name = L.T("Übersetzen ins Englische"), Text = L.T("Übersetze den folgenden Text ins Englische. Gib nur die Übersetzung aus:") + "\n\n" },
        new() { Name = L.T("Zusammenfassen"), Text = L.T("Fasse den folgenden Text in höchstens 5 Stichpunkten zusammen:") + "\n\n" },
        new() { Name = L.T("Korrekturlesen"), Text = L.T("Korrigiere Rechtschreibung, Grammatik und Zeichensetzung im folgenden Text. " +
                                                         "Gib den korrigierten Text aus und nenne danach kurz die wichtigsten Änderungen:") + "\n\n" },
        new() { Name = L.T("E-Mail aus Stichpunkten"), Text = L.T("Formuliere aus den folgenden Stichpunkten eine kurze, freundliche E-Mail:") + "\n\n" },
        new() { Name = L.T("Kurz und knapp"), Persona = true, Text = L.T("Antworte so kurz wie möglich – ohne Einleitung, ohne Wiederholungen, ohne Schlussformel.") },
        new() { Name = L.T("Einfach erklärt"), Persona = true, Text = L.T("Erkläre alles so einfach, dass es ein 12-jähriges Kind versteht. Verwende Beispiele aus dem Alltag und vermeide Fachwörter.") },
        new() { Name = L.T("Erfahrener Entwickler"), Persona = true, Text = L.T("Du bist ein erfahrener Softwareentwickler. Antworte präzise, mit lauffähigem Code und einer kurzen Begründung. Weise auf Fallstricke hin.") },
    ];
}

// Ein Modell im Modellvergleich
public sealed record CompareSlot(ProviderKind Provider, string Model);

// Verschlüsselt Geheimnisse (API-Schlüssel) mit Windows DPAPI – nur für das eigene Windows-Konto lesbar
public static class Secret
{
    public static string Protect(string value) => value.Length == 0
        ? ""
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string protectedValue)
    {
        if (protectedValue.Length == 0) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";
        }
    }
}

// Alle Einstellungen. Feineinstellungen: null = Standard des Modells.
public sealed class LlmSettings
{
    public ProviderKind Provider { get; set; } = ProviderKind.LmStudio;
    public Dictionary<ProviderKind, ProviderConfig> Providers { get; set; } = new();
    public string SystemPrompt { get; set; } = "";
    public bool AutoTitle { get; set; } = true;   // nach der ersten Antwort einen Chat-Titel vom Modell vorschlagen lassen

    // Kostenbremse für Online-Anbieter in US-Dollar; null = kein Limit. Hart = ohne Nachfrage sperren.
    public double? DailyBudget { get; set; }
    public double? MonthlyBudget { get; set; }
    public bool BudgetHardStop { get; set; }

    // Vorlagen (Text fürs Eingabefeld) und Personas (System-Prompt je Chat); null = mitgelieferte Beispiele
    public List<PromptTemplate>? Templates { get; set; }

    // Modellvergleich: zuletzt gewählte Modelle links und rechts
    public CompareSlot? CompareLeft { get; set; }
    public CompareSlot? CompareRight { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public double? TopK { get; set; }
    public double? MaxTokens { get; set; }
    public double? RepeatPenalty { get; set; }

    // Nur zum Einlesen von Einstellungen aus Version 1.3 und älter (damals gab es nur LM Studio)
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Server { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    [JsonIgnore]
    public ProviderConfig Current => Config(Provider);

    public ProviderConfig Config(ProviderKind kind)
    {
        if (!Providers.TryGetValue(kind, out var config))
        {
            var info = ProviderInfo.Get(kind);
            config = new ProviderConfig { Server = info.DefaultServer, Model = info.DefaultModel };
            Providers[kind] = config;
        }
        return config;
    }

    public LlmSettings Clone() => JsonSerializer.Deserialize<LlmSettings>(JsonSerializer.Serialize(this))!;

    public static LlmSettings Load(ChatStore store)
    {
        var json = store.GetSetting("llm");
        LlmSettings settings;
        try
        {
            settings = json is null ? new LlmSettings() : JsonSerializer.Deserialize<LlmSettings>(json) ?? new LlmSettings();
        }
        catch (JsonException)
        {
            settings = new LlmSettings();
        }

        if (settings.Server != null || settings.Model != null)
        {
            var lmStudio = settings.Config(ProviderKind.LmStudio);
            lmStudio.Server = settings.Server ?? lmStudio.Server;
            lmStudio.Model = settings.Model ?? lmStudio.Model;
            settings.Server = settings.Model = null;
        }
        return settings;
    }

    public void Save(ChatStore store) => store.SetSetting("llm", JsonSerializer.Serialize(this));
}
