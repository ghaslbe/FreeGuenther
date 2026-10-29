using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Anthropic.Models.Models;

namespace LmChat;

public enum ProviderKind { LmStudio, Ollama, OpenAI, Anthropic, OpenRouter }

// Vision = null heißt: unbekannt (Anbieter meldet es nicht oder Server nicht erreichbar)
// ImageGen = Modell erzeugt Bilder (wird über die Bild-Schnittstelle des Anbieters angesprochen)
// Tools = Modell kann Werkzeuge aufrufen, also Plugins nutzen (null = unbekannt)
public record ModelInfo(string Id, bool? Vision, bool Loaded, bool ImageGen = false, bool? Tools = null)
{
    public string Label => Id + (ImageGen ? "    🎨 " + L.T("erzeugt Bilder") : "") + (Vision == true ? "    🖼 " + L.T("versteht Bilder") : "") +
                           (Tools == true ? "    🔧 Plugins" : "") + (Loaded ? "    ● " + L.T("geladen") : "");
}

// Feste Eigenschaften eines Anbieters: Standard-Adresse, Schlüssel nötig?, welche Feineinstellungen er versteht
public sealed record ProviderInfo(
    ProviderKind Kind,
    string Name,
    bool IsLocal,
    string DefaultServer,
    string DefaultModel,
    string? KeyUrl,
    string Description,
    bool SupportsSampling,
    bool SupportsTopK,
    bool SupportsRepeatPenalty)
{
    public bool NeedsKey => KeyUrl != null;
    public string Label => L.T(IsLocal ? "Lokal" : "Remote") + "  ·  " + Name;

    // Beschreibung und Link-Beschriftungen sind deutsch hinterlegt und werden beim Anzeigen mit L.T übersetzt

    // Nützliche Seiten des Anbieters (Website, Schlüssel, Modelle, Preise) – werden in den Einstellungen verlinkt
    public (string Text, string Url)[] Links => Kind switch
    {
        ProviderKind.LmStudio =>
        [
            ("Website", "https://lmstudio.ai"),
            ("Modelle finden", "https://lmstudio.ai/models"),
            ("Server/API-Doku", "https://lmstudio.ai/docs/developer")
        ],
        ProviderKind.Ollama =>
        [
            ("Website", "https://ollama.com"),
            ("Modelle finden", "https://ollama.com/search"),
            ("Dokumentation", "https://docs.ollama.com")
        ],
        ProviderKind.OpenAI =>
        [
            ("API-Schlüssel", "https://platform.openai.com/api-keys"),
            ("⚠ Ausgabenlimit setzen", "https://platform.openai.com/settings/organization/limits"),
            ("Modelle", "https://platform.openai.com/docs/models"),
            ("Preise", "https://openai.com/api/pricing")
        ],
        ProviderKind.Anthropic =>
        [
            ("API-Schlüssel", "https://platform.claude.com/settings/keys"),
            ("⚠ Ausgabenlimit setzen", "https://platform.claude.com/settings/limits"),
            ("Modelle", "https://platform.claude.com/docs/en/about-claude/models/overview"),
            ("Preise", "https://platform.claude.com/docs/en/about-claude/pricing")
        ],
        ProviderKind.OpenRouter =>
        [
            ("Website", "https://openrouter.ai"),
            ("API-Schlüssel", "https://openrouter.ai/keys"),
            ("⚠ Limit je Schlüssel setzen", "https://openrouter.ai/settings/keys"),
            ("Modelle und Preise", "https://openrouter.ai/models"),
            ("Guthaben", "https://openrouter.ai/settings/credits")
        ],
        _ => []
    };

    public static readonly ProviderInfo[] All =
    [
        new(ProviderKind.LmStudio, "LM Studio", true, "http://localhost:1234", "", null,
            "LM Studio auf diesem oder einem anderen Rechner im Netzwerk. In LM Studio unter „Developer“ den Server " +
            "starten; für Zugriff von anderen Rechnern „Serve on Local Network“ einschalten. Läuft komplett lokal, kostenlos.",
            true, true, true),
        new(ProviderKind.Ollama, "Ollama", true, "http://localhost:11434", "", null,
            "Ollama auf diesem oder einem anderen Rechner. Modelle lädst du mit „ollama pull <name>“. Für Zugriff von " +
            "anderen Rechnern Ollama mit OLLAMA_HOST=0.0.0.0 starten. Läuft komplett lokal, kostenlos.",
            true, false, false),
        new(ProviderKind.OpenAI, "OpenAI", false, "https://api.openai.com", "", "https://platform.openai.com/api-keys",
            "Die Modelle von OpenAI (ChatGPT). Braucht einen API-Schlüssel; die Nutzung kostet pro Anfrage. " +
            "Hinweis: Reasoning-Modelle (o-Serie, GPT-5) lehnen Temperatur/Top-P ab – dann den Haken dort weglassen. " +
            "Die Adresse kann auch auf einen anderen OpenAI-kompatiblen Dienst zeigen.",
            true, false, false),
        new(ProviderKind.Anthropic, "Anthropic (Claude)", false, "https://api.anthropic.com", "claude-opus-5-5",
            "https://platform.claude.com/settings/keys",
            "Die Claude-Modelle von Anthropic. Braucht einen API-Schlüssel; die Nutzung kostet pro Anfrage. " +
            "Alle aktuellen Claude-Modelle verstehen Bilder. Temperatur, Top-P und Top-K werden von der API nicht mehr angenommen.",
            false, false, false),
        new(ProviderKind.OpenRouter, "OpenRouter", false, "https://openrouter.ai/api", "openrouter/auto",
            "https://openrouter.ai/keys",
            "Ein Dienst mit Zugriff auf Hunderte Modelle verschiedener Hersteller über einen Schlüssel. " +
            "Braucht einen API-Schlüssel; die Kosten hängen vom Modell ab (manche sind kostenlos). " +
            "„openrouter/auto“ wählt automatisch ein passendes Modell.",
            true, true, true),
    ];

    public static ProviderInfo Get(ProviderKind kind) => All.First(p => p.Kind == kind);
}

// Standortwahl bei OpenRouter:
//  "hoster-…" = nur Hoster-Endpunkte mit bekanntem Standort in der Region (geht mit jedem Tarif, Annäherung)
//  "eu" / "us" = In-Region-Routing von OpenRouter über eu./us.openrouter.ai (garantiert, nur Business-Tarif)
public sealed record RegionOption(string Code, string GermanLabel)
{
    public string Label => L.T(GermanLabel);

    public static readonly RegionOption[] All =
    [
        new("", "Weltweit – alle Hoster (Standard)"),
        new("hoster-eu", "Europa – über Hoster-Auswahl (jeder Tarif)"),
        new("hoster-de", "Deutschland – über Hoster-Auswahl (jeder Tarif)"),
        new("hoster-us", "USA – über Hoster-Auswahl (jeder Tarif)"),
        new("eu", "Europa – garantiert (In-Region-Routing, nur Business-Tarif)"),
        new("us", "USA – garantiert (In-Region-Routing, nur Business-Tarif)"),
    ];

    public static string Name(string code) => code switch
    {
        "eu" => L.T("EU, garantiert"),
        "us" => L.T("USA, garantiert"),
        "hoster-eu" => "EU-Hoster",
        "hoster-de" => "DE-Hoster",
        "hoster-us" => "US-Hoster",
        _ => ""
    };

    public static bool IsHosterFilter(string code) => code.StartsWith("hoster-");
}

// Ermittelt bei OpenRouter, wo die einzelnen Hoster-Endpunkte eines Modells stehen.
// Quellen: Regionsangabe in der Endpunkt-Kennung (z. B. "mistral/eu", "amazon-bedrock/eu-central-1",
// "google-vertex/us") und – falls dort nichts steht – die Rechenzentrums-Länder aus /api/v1/providers.
// Endpunkte ohne erkennbaren Standort (z. B. "…/global") gelten als unbekannt und werden ausgeschlossen.
public static class OpenRouterHosting
{
    private static readonly HashSet<string> EuropeCountries =
    [
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU",
        "MT", "NL", "PL", "PT", "RO", "SK", "SI", "ES", "SE", "IS", "LI", "NO", "GB", "UK", "CH"
    ];

    // Rechenzentren in Deutschland (Frankfurt/Berlin) bei den großen Clouds
    private static readonly string[] GermanRegionNames =
        ["eu-central-1", "europe-west3", "europe-west10", "germanywestcentral", "germanynorth", "germany", "frankfurt", "de"];

    private static Dictionary<string, string[]>? _providerCountries;
    private static readonly Dictionary<string, List<string>> EndpointCache = new();
    private static readonly SemaphoreSlim Throttle = new(12);

    // Welche Standort-Kürzel ein Endpunkt erfüllt: "EU", "DE" (zählt auch als EU), "US"
    public static HashSet<string> Locations(string tag)
    {
        var result = new HashSet<string>();
        var parts = tag.ToLowerInvariant().Split('/');
        foreach (var part in parts.Skip(1))
        {
            if (GermanRegionNames.Contains(part)) { result.Add("DE"); result.Add("EU"); }
            else if (part == "eu" || part.StartsWith("eu-") || part.Contains("europe")) result.Add("EU");
            else if (part == "us" || part.StartsWith("us-") || System.Text.RegularExpressions.Regex.IsMatch(part, @"^(east|west|central|north|south)us\d*$"))
                result.Add("US");
        }
        if (result.Count > 0 || parts.Skip(1).Contains("global")) return result;

        // Keine Region in der Kennung: Rechenzentrums-Länder des Hosters (nur wenn alle in derselben Region liegen)
        if (_providerCountries != null && _providerCountries.TryGetValue(parts[0], out var countries) && countries.Length > 0)
        {
            if (countries.All(EuropeCountries.Contains)) result.Add("EU");
            if (countries.All(c => c == "DE")) result.Add("DE");
            if (countries.All(c => c == "US")) result.Add("US");
        }
        return result;
    }

    public static string LocationCode(string region) => region switch { "hoster-de" => "DE", "hoster-us" => "US", _ => "EU" };

    public static async Task EnsureProvidersAsync(HttpClient http, string server, CancellationToken ct)
    {
        if (_providerCountries != null) return;
        var json = JsonNode.Parse(await http.GetStringAsync(server + "/v1/providers", ct));
        _providerCountries = (json?["data"] as JsonArray ?? [])
            .Where(n => (string?)n?["slug"] != null)
            .ToDictionary(
                n => ((string?)n!["slug"])!,
                n => (n!["datacenters"] as JsonArray ?? []).Select(c => (string?)c).OfType<string>().ToArray());
    }

    public static async Task<List<string>> EndpointTagsAsync(HttpClient http, string server, string modelId, CancellationToken ct)
    {
        lock (EndpointCache)
            if (EndpointCache.TryGetValue(modelId, out var cached)) return cached;

        await Throttle.WaitAsync(ct);
        try
        {
            var json = JsonNode.Parse(await http.GetStringAsync($"{server}/v1/models/{modelId}/endpoints", ct));
            var tags = (json?["data"]?["endpoints"] as JsonArray ?? [])
                .Select(e => (string?)e?["tag"]).OfType<string>().ToList();
            lock (EndpointCache)
                EndpointCache[modelId] = tags;
            return tags;
        }
        finally
        {
            Throttle.Release();
        }
    }

    // Die Endpunkte des Modells, die in der gewünschten Region stehen
    public static async Task<List<string>> TagsInRegionAsync(HttpClient http, string server, string modelId, string region,
        CancellationToken ct)
    {
        await EnsureProvidersAsync(http, server, ct);
        var code = LocationCode(region);
        return (await EndpointTagsAsync(http, server, modelId, ct)).Where(t => Locations(t).Contains(code)).ToList();
    }
}

// Schätzt Kosten, wenn der Anbieter keine meldet (OpenAI, Anthropic, ältere OpenRouter-Antworten).
// Grundlage ist die öffentliche Preisliste von OpenRouter (US-Dollar pro Token), die auch die Modelle von
// OpenAI und Anthropic unter "openai/…" bzw. "anthropic/…" führt. Nur eine Annäherung: Rabatte, Caching, Batch usw. fehlen.
public static class PriceEstimate
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static Dictionary<string, (double Input, double Output)>? _prices;

    public static async Task LoadAsync(CancellationToken ct)
    {
        if (_prices != null) return;
        var json = JsonNode.Parse(await Http.GetStringAsync("https://openrouter.ai/api/v1/models", ct));
        var prices = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in json?["data"] as JsonArray ?? [])
        {
            if ((string?)n?["id"] is not { } id) continue;
            if (double.TryParse((string?)n?["pricing"]?["prompt"], NumberStyles.Float, CultureInfo.InvariantCulture, out var input)
                && double.TryParse((string?)n?["pricing"]?["completion"], NumberStyles.Float, CultureInfo.InvariantCulture, out var output))
                prices[id] = (input, output);
        }
        _prices = prices;
    }

    // null = kein Preis bekannt (Preisliste nicht geladen oder Modell nicht gefunden)
    public static double? Estimate(ProviderKind provider, string model, long inputTokens, long outputTokens)
    {
        if (_prices == null) return null;
        if (provider == ProviderKind.OpenRouter)
            return _prices.TryGetValue(model, out var listed) ? inputTokens * listed.Input + outputTokens * listed.Output : null;
        var vendor = provider switch { ProviderKind.OpenAI => "openai", ProviderKind.Anthropic => "anthropic", _ => null };
        if (vendor == null) return null;

        // Schreibweisen angleichen: Datum am Ende weg ("…-20250929", "…-2025-04-14"),
        // Claude-Versionen mit Punkt ("claude-opus-5-5" → "claude-opus-5.5")
        var name = Regex.Replace(model, @"-(\d{8}|\d{4}-\d{2}-\d{2})$", "");
        string[] candidates = [model, name, Regex.Replace(name, @"(\d+)-(\d+)$", "$1.$2")];
        foreach (var candidate in candidates)
            if (_prices.TryGetValue($"{vendor}/{candidate}", out var price))
                return inputTokens * price.Input + outputTokens * price.Output;
        return null;
    }
}

public sealed record ChatRequest(
    ProviderInfo Provider,
    string Server,
    string Region,
    string ApiKey,
    string Model,
    string SystemPrompt,
    LlmSettings Settings,
    IReadOnlyList<ChatMessage> Messages);

// Spricht mit allen Anbietern: OpenAI-kompatibles Protokoll (LM Studio, Ollama, OpenAI, OpenRouter)
// bzw. das offizielle Anthropic-SDK für Claude.
public static class LlmClient
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    // Diese Claude-Modelle bekommen bei einer Ablehnung automatisch ein Ersatzmodell (serverseitiger Fallback)
    private static readonly string[] FallbackModels = ["claude-opus-5-5", "claude-opus-5", "claude-fable-5-1", "claude-sonnet-5-5"];

    // ---------- Modelllisten ----------

    // OpenRouter mit Region: statt openrouter.ai die regionale Domain (eu./us.openrouter.ai) verwenden.
    // Dort wird nur an Rechenzentren in dieser Region weitergeleitet – sonst gibt es einen Fehler.
    public static string EffectiveServer(ProviderInfo p, string server, string region)
    {
        server = server.Trim().TrimEnd('/');
        if (p.Kind == ProviderKind.OpenRouter && region is "eu" or "us" && server == p.DefaultServer)
            return $"https://{region}.openrouter.ai/api";
        return server;
    }

    public static async Task<List<ModelInfo>> ListModelsAsync(ProviderInfo p, string server, string region, string apiKey,
        CancellationToken ct, Action<string>? progress = null)
    {
        server = EffectiveServer(p, server, region);
        switch (p.Kind)
        {
            case ProviderKind.LmStudio:
            {
                // LM-Studio-eigene API: Typ "vlm" = versteht Bilder, Fähigkeit "tool_use" = kann Plugins nutzen
                var json = await SendJsonAsync(HttpMethod.Get, server + "/api/v0/models", p, apiKey, null, ct);
                return (json?["data"] as JsonArray ?? [])
                    .Select(n => (Id: (string?)n?["id"], Type: (string?)n?["type"], State: (string?)n?["state"],
                        Caps: n?["capabilities"] as JsonArray))
                    .Where(m => m.Id != null && m.Type is "llm" or "vlm")
                    .Select(m => new ModelInfo(m.Id!, m.Type == "vlm", m.State == "loaded",
                        Tools: m.Caps?.Any(c => (string?)c == "tool_use") ?? false))
                    .OrderByDescending(m => m.Loaded).ThenBy(m => m.Id)
                    .ToList();
            }

            case ProviderKind.Ollama:
            {
                var tags = await SendJsonAsync(HttpMethod.Get, server + "/api/tags", p, apiKey, null, ct);
                var names = (tags?["models"] as JsonArray ?? []).Select(n => (string?)n?["name"]).OfType<string>().ToList();
                var loaded = new HashSet<string>();
                try
                {
                    var ps = await SendJsonAsync(HttpMethod.Get, server + "/api/ps", p, apiKey, null, ct);
                    loaded.UnionWith((ps?["models"] as JsonArray ?? []).Select(n => (string?)n?["name"]).OfType<string>());
                }
                catch (HttpRequestException) { }

                // Fähigkeiten je Modell: "vision" = versteht Bilder, ohne "completion" = reines Embedding-Modell
                var infos = await Task.WhenAll(names.Select(async name =>
                {
                    try
                    {
                        var show = await SendJsonAsync(HttpMethod.Post, server + "/api/show", p, apiKey,
                            new JsonObject { ["model"] = name }, ct);
                        if (show?["capabilities"] is not JsonArray caps)
                            return new ModelInfo(name, null, loaded.Contains(name));
                        var list = caps.Select(c => (string?)c).ToList();
                        return list.Contains("completion")
                            ? new ModelInfo(name, list.Contains("vision"), loaded.Contains(name), Tools: list.Contains("tools"))
                            : null;
                    }
                    catch (HttpRequestException)
                    {
                        return new ModelInfo(name, null, loaded.Contains(name));
                    }
                }));
                return infos.OfType<ModelInfo>().OrderByDescending(m => m.Loaded).ThenBy(m => m.Id).ToList();
            }

            case ProviderKind.OpenAI:
            {
                var json = await SendJsonAsync(HttpMethod.Get, server + "/v1/models", p, apiKey, null, ct);
                var ids = (json?["data"] as JsonArray ?? []).Select(n => (string?)n?["id"]).OfType<string>();
                // Bei OpenAI selbst nur Chat- und Bildmodelle zeigen (keine Audio-, Embedding-Modelle usw.)
                if (server.Contains("api.openai.com"))
                    ids = ids.Where(id => IsOpenAIChatModel(id) || IsOpenAIImageModel(id));
                return ids.OrderBy(id => id)
                    .Select(id => new ModelInfo(id, IsOpenAIImageModel(id) ? true : null, false, IsOpenAIImageModel(id),
                        IsOpenAIImageModel(id) ? false : null))
                    .ToList();
            }

            case ProviderKind.OpenRouter:
            {
                var json = await SendJsonAsync(HttpMethod.Get, server + "/v1/models", p, apiKey, null, ct);
                bool Has(JsonNode? n, string list, string value) =>
                    (n?["architecture"]?[list] as JsonArray)?.Any(x => (string?)x == value) ?? false;
                var models = (json?["data"] as JsonArray ?? [])
                    .Where(n => Has(n, "output_modalities", "text") || Has(n, "output_modalities", "image"))
                    .Select(n => new ModelInfo(
                        (string?)n?["id"] ?? "",
                        (n?["architecture"]?["input_modalities"] as JsonArray)?.Any(x => (string?)x == "image"),
                        false,
                        Has(n, "output_modalities", "image") && !((string?)n?["id"] ?? "").StartsWith("openrouter/"),
                        (n?["supported_parameters"] as JsonArray)?.Any(x => (string?)x == "tools")))
                    .Where(m => m.Id.Length > 0)
                    .ToList();

                // Reine Bildmodelle (z. B. Seedream, FLUX) stehen nur in der Liste der Bild-Schnittstelle
                if (region.Length == 0 && apiKey.Length > 0)
                {
                    try
                    {
                        var images = await SendJsonAsync(HttpMethod.Get, server + "/v1/images/models", p, apiKey, null, ct);
                        foreach (var n in images?["data"] as JsonArray ?? [])
                        {
                            var id = (string?)n?["id"];
                            if (id == null) continue;
                            var vision = Has(n, "input_modalities", "image");
                            var index = models.FindIndex(m => m.Id == id);
                            if (index >= 0) models[index] = models[index] with { ImageGen = true };
                            else models.Add(new ModelInfo(id, vision, false, true));
                        }
                    }
                    catch (HttpRequestException) { }
                }
                models = models.OrderBy(m => m.Id).ToList();
                if (RegionOption.IsHosterFilter(region))
                {
                    // Für jedes Modell die Endpunkte prüfen: bleibt nur, wer mindestens einen Hoster in der Region hat
                    await OpenRouterHosting.EnsureProvidersAsync(Http, server, ct);
                    var done = 0;
                    var checks = await Task.WhenAll(models.Select(async m =>
                    {
                        var ok = false;
                        try
                        {
                            ok = !m.Id.Contains(':') &&
                                 (await OpenRouterHosting.TagsInRegionAsync(Http, server, m.Id, region, ct)).Count > 0;
                        }
                        catch (HttpRequestException) { }
                        if (++done % 25 == 0)
                            progress?.Invoke(L.F("Prüfe Hoster-Standorte … {0} von {1} Modellen", done, models.Count));
                        return ok ? m : null;
                    }));
                    return checks.OfType<ModelInfo>().ToList();
                }
                if (region.Length == 0)
                    models.Insert(0, new ModelInfo("openrouter/auto", true, false, Tools: true));
                return models;
            }

            case ProviderKind.Anthropic:
            {
                var client = CreateAnthropicClient(server, apiKey);
                var page = await client.Models.List(new ModelListParams { Limit = 100 }, ct);
                return page.Items.Select(m => new ModelInfo(m.ID, true, false, Tools: true)).ToList();
            }

            default:
                return [];
        }
    }

    public static bool IsOpenAIImageModel(string id) => id.StartsWith("gpt-image") || id.StartsWith("dall-e");

    private static bool IsOpenAIChatModel(string id) =>
        (id.StartsWith("gpt-") || id.StartsWith("chatgpt-") || id.StartsWith("o1") || id.StartsWith("o3") || id.StartsWith("o4")) &&
        !new[] { "audio", "realtime", "transcribe", "tts", "image", "embedding", "search", "moderation", "instruct" }.Any(id.Contains);

    // ---------- Chat ----------

    // Was der Anbieter über die Antwort meldet (wird während des Streamings befüllt, Werte sind optional)
    public sealed class StreamResult
    {
        public string? Model { get; set; }      // tatsächlich antwortendes Modell (z. B. bei openrouter/auto)
        public string? Hoster { get; set; }     // OpenRouter: Hoster, der die Anfrage bedient hat
        public long? InputTokens { get; set; }
        public long? OutputTokens { get; set; }
        public double? Cost { get; set; }       // OpenRouter: Kosten in US-Dollar laut Anbieter
        public bool ToolsRejected { get; set; } // Modell hat Werkzeuge abgelehnt – Antwort kam ohne Plugins
    }

    // Was gerade passiert, solange noch kein Antworttext da ist (für die Statusanzeige im Chat)
    public enum Phase { FindingHosters, Sending, Connected, Thinking }

    // ---------- Bilder erzeugen ----------

    public sealed record ImageResult(byte[] Image, long? InputTokens, long? OutputTokens, double? Cost);

    // OpenRouter: POST /api/v1/images (Vorlagebild über input_references)
    // OpenAI:     POST /v1/images/generations bzw. /v1/images/edits (mit Vorlagebild, multipart)
    public static async Task<ImageResult> GenerateImageAsync(ChatRequest r, string prompt, byte[]? reference, CancellationToken ct)
    {
        var p = r.Provider;
        var server = EffectiveServer(p, r.Server, r.Region);
        HttpRequestMessage request;

        if (p.Kind == ProviderKind.OpenAI && reference != null)
        {
            var form = new MultipartFormDataContent
            {
                { new StringContent(r.Model), "model" },
                { new StringContent(prompt), "prompt" }
            };
            var file = new ByteArrayContent(reference);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "image", "vorlage.png");
            request = new HttpRequestMessage(HttpMethod.Post, server + "/v1/images/edits") { Content = form };
        }
        else
        {
            var body = new JsonObject { ["model"] = r.Model, ["prompt"] = prompt, ["n"] = 1 };
            if (p.Kind == ProviderKind.OpenAI && r.Model.StartsWith("dall-e"))
                body["response_format"] = "b64_json"; // DALL·E liefert sonst nur eine URL
            if (p.Kind == ProviderKind.OpenRouter && reference != null)
                body["input_references"] = new JsonArray(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(reference) }
                });
            var path = p.Kind == ProviderKind.OpenAI ? "/v1/images/generations" : "/v1/images";
            request = new HttpRequestMessage(HttpMethod.Post, server + path)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
        }

        using (request)
        {
            AddHeaders(request, p, r.ApiKey);
            using var response = await Http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {ErrorText(text)}");

            var json = JsonNode.Parse(text);
            var b64 = (string?)json?["data"]?[0]?["b64_json"];
            if (string.IsNullOrEmpty(b64))
                throw new HttpRequestException(L.T("Der Anbieter hat kein Bild geliefert."));
            var usage = json?["usage"];
            return new ImageResult(
                Convert.FromBase64String(b64),
                (long?)usage?["prompt_tokens"] ?? (long?)usage?["input_tokens"],
                (long?)usage?["completion_tokens"] ?? (long?)usage?["output_tokens"],
                usage?["cost"] is JsonValue cost ? (double?)cost : null);
        }
    }

    // ---------- Text-Chat ----------

    // So oft darf das Modell pro Antwort Werkzeuge aufrufen; danach muss es ohne Werkzeuge antworten
    public const int MaxToolRounds = 8;

    // tools = Werkzeuge der aktiven Plugins (null = keine). Ruft das Modell eins auf, wird es ausgeführt, das Ergebnis
    // zurückgeschickt und weiter gestreamt – bis das Modell eine normale Antwort gibt.
    // onReasoning bekommt den Gedankengang (Denkphase) stückweise, sofern der Anbieter ihn mitschickt
    public static async Task StreamAsync(ChatRequest r, Action<string> onChunk, Action<Phase> onPhase, StreamResult result,
        ToolBox? tools, CancellationToken ct, Action<string>? onReasoning = null)
    {
        onReasoning ??= _ => { };
        if (r.Provider.Kind == ProviderKind.Anthropic)
            await StreamAnthropicAsync(r, onChunk, onPhase, onReasoning, result, tools, ct);
        else
            await StreamOpenAICompatibleAsync(r, onChunk, onPhase, onReasoning, result, tools, ct);
    }

    private static void AddUsage(StreamResult result, long? input, long? output, double? cost)
    {
        if (input != null) result.InputTokens = (result.InputTokens ?? 0) + input;
        if (output != null) result.OutputTokens = (result.OutputTokens ?? 0) + output;
        if (cost != null) result.Cost = (result.Cost ?? 0) + cost;
    }

    private static async Task StreamOpenAICompatibleAsync(ChatRequest r, Action<string> onChunk, Action<Phase> onPhase,
        Action<string> onReasoning, StreamResult result, ToolBox? tools, CancellationToken ct)
    {
        var p = r.Provider;
        var s = r.Settings;
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(r.SystemPrompt))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = r.SystemPrompt });
        foreach (var m in r.Messages)
            messages.Add(ToOpenAIMessage(m));

        var body = new JsonObject
        {
            ["model"] = r.Model,
            ["messages"] = messages,
            ["stream"] = true,
            // Token-Verbrauch im letzten Datenpaket mitschicken lassen
            ["stream_options"] = new JsonObject { ["include_usage"] = true }
        };
        if (p.SupportsSampling && s.Temperature is { } temperature) body["temperature"] = temperature;
        if (p.SupportsSampling && s.TopP is { } topP) body["top_p"] = topP;
        if (p.SupportsTopK && s.TopK is { } topK) body["top_k"] = (int)topK;
        if (p.SupportsRepeatPenalty && s.RepeatPenalty is { } repeat)
            body[p.Kind == ProviderKind.OpenRouter ? "repetition_penalty" : "repeat_penalty"] = repeat;
        if (s.MaxTokens is { } maxTokens)
            body[p.Kind == ProviderKind.OpenAI ? "max_completion_tokens" : "max_tokens"] = (int)maxTokens;

        // OpenRouter mit Hoster-Filter: nur Endpunkte mit Standort in der Region zulassen, keine Ausweichhoster
        if (p.Kind == ProviderKind.OpenRouter && RegionOption.IsHosterFilter(r.Region))
        {
            onPhase(Phase.FindingHosters);
            var tags = await OpenRouterHosting.TagsInRegionAsync(Http, EffectiveServer(p, r.Server, r.Region), r.Model, r.Region, ct);
            if (tags.Count == 0)
                throw new HttpRequestException(
                    L.F("Für {0} ist kein Hoster mit Standort {1} bekannt – bitte ein anderes Modell wählen.", r.Model, RegionOption.Name(r.Region)));
            body["provider"] = new JsonObject
            {
                ["only"] = new JsonArray(tags.Select(t => (JsonNode)JsonValue.Create(t)).ToArray()),
                ["allow_fallbacks"] = false
            };
        }

        var toolsJson = tools is { Specs.Count: > 0 }
            ? new JsonArray(tools.Specs.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone()
                }
            }).ToArray())
            : null;

        for (var round = 0; ; round++)
        {
            var offerTools = toolsJson != null && round < MaxToolRounds;
            if (offerTools) body["tools"] = toolsJson!.DeepClone();
            else body.Remove("tools");

            using var request = new HttpRequestMessage(HttpMethod.Post, EffectiveServer(p, r.Server, r.Region) + "/v1/chat/completions")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            AddHeaders(request, p, r.ApiKey);
            onPhase(Phase.Sending);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = ErrorText(await response.Content.ReadAsStringAsync(ct));
                // Modell kann keine Werkzeuge: ohne Plugins nochmal versuchen statt mit Fehler abzubrechen
                if (offerTools && round == 0 && error.Contains("tool", StringComparison.OrdinalIgnoreCase))
                {
                    toolsJson = null;
                    result.ToolsRejected = true;
                    round = -1;
                    continue;
                }
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {error}");
            }
            onPhase(Phase.Connected);

            var thinking = false;
            var text = new StringBuilder();
            var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
            long? roundInput = null, roundOutput = null;
            double? roundCost = null;
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:")) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;

                var json = JsonNode.Parse(data);
                if (json?["error"] is { } streamError)
                    throw new HttpRequestException(ErrorText(streamError.ToJsonString()));
                if (json?["model"] is JsonValue model && (string?)model is { Length: > 0 } modelId)
                    result.Model = modelId;
                if (json?["provider"] is JsonValue hoster && (string?)hoster is { Length: > 0 } hosterName)
                    result.Hoster = hosterName;
                if (json?["usage"] is JsonObject usage)
                {
                    roundInput = (long?)usage["prompt_tokens"] ?? roundInput;
                    roundOutput = (long?)usage["completion_tokens"] ?? roundOutput;
                    if (usage["cost"] is JsonValue cost && cost.TryGetValue<double>(out var dollars))
                        roundCost = dollars;
                }
                if (json?["choices"] is not JsonArray { Count: > 0 } choices) continue;
                var delta = choices[0]?["delta"];
                // Denkphase: OpenRouter/Ollama melden sie als "reasoning", LM Studio als "reasoning_content"
                if ((delta?["reasoning"] ?? delta?["reasoning_content"]) is JsonValue reasoning
                    && reasoning.TryGetValue<string>(out var reasoningText) && reasoningText.Length > 0)
                {
                    if (!thinking) onPhase(Phase.Thinking);
                    thinking = true;
                    onReasoning(reasoningText);
                }
                // Werkzeugaufrufe kommen stückweise: je Index Kennung, Name und Argumente (JSON) zusammensetzen
                foreach (var call in delta?["tool_calls"] as JsonArray ?? [])
                {
                    var index = (int?)call?["index"] ?? 0;
                    if (!calls.TryGetValue(index, out var entry))
                        entry = ("", "", new StringBuilder());
                    var id = (string?)call?["id"];
                    var name = (string?)call?["function"]?["name"];
                    entry = (string.IsNullOrEmpty(id) ? entry.Id : id, string.IsNullOrEmpty(name) ? entry.Name : entry.Name + name, entry.Args);
                    entry.Args.Append((string?)call?["function"]?["arguments"]);
                    calls[index] = entry;
                }
                var chunk = delta?["content"] is JsonValue c && c.TryGetValue<string>(out var piece) ? piece : null;
                if (!string.IsNullOrEmpty(chunk))
                {
                    text.Append(chunk);
                    onChunk(chunk);
                }
            }
            AddUsage(result, roundInput, roundOutput, roundCost);

            if (calls.Count == 0 || tools == null) break;

            // Aufrufe ausführen und Ergebnisse fürs Modell anhängen
            var assistant = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = text.Length > 0 ? text.ToString() : null,
                ["tool_calls"] = new JsonArray(calls.Values.Select((call, i) => (JsonNode)new JsonObject
                {
                    ["id"] = call.Id.Length > 0 ? call.Id : $"call_{round}_{i}",
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Args.Length > 0 ? call.Args.ToString() : "{}" }
                }).ToArray())
            };
            messages.Add(assistant);
            if (text.Length > 0) onChunk("\n\n");
            foreach (var call in (JsonArray)assistant["tool_calls"]!)
            {
                var output = await tools.Execute((string)call!["function"]!["name"]!, (string)call["function"]!["arguments"]!, ct);
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = (string)call["id"]!, ["content"] = output });
            }
        }
    }

    // Antworten dürfen bei den APIs keine Bilder enthalten – ein erzeugtes Bild wird im Verlauf als Hinweis mitgeschickt
    private static string AssistantText(ChatMessage m) =>
        m.Image == null ? m.Content : (m.Content.Length > 0 ? m.Content + "\n\n" : "") + "[Hier wurde ein Bild erzeugt.]";

    private static JsonObject ToOpenAIMessage(ChatMessage m)
    {
        if (m.Role == "assistant")
            return new JsonObject { ["role"] = "assistant", ["content"] = AssistantText(m) };
        var text = m.TextForModel;
        if (m.Image == null)
            return new JsonObject { ["role"] = m.Role, ["content"] = text };

        var parts = new JsonArray();
        if (text.Length > 0)
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        parts.Add(new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(m.Image) }
        });
        return new JsonObject { ["role"] = m.Role, ["content"] = parts };
    }

    private static async Task StreamAnthropicAsync(ChatRequest r, Action<string> onChunk, Action<Phase> onPhase,
        Action<string> onReasoning, StreamResult result, ToolBox? tools, CancellationToken ct)
    {
        var client = CreateAnthropicClient(r.Server, r.ApiKey);
        var useFallback = FallbackModels.Contains(r.Model);
        var messages = r.Messages.Select(ToAnthropicMessage).ToList();
        var toolDefs = tools is { Specs.Count: > 0 }
            ? tools.Specs.Select(t => (BetaToolUnion)new BetaTool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = new InputSchema
                {
                    Properties = (t.Parameters["properties"] as JsonObject ?? new JsonObject())
                        .ToDictionary(p => p.Key, p => JsonSerializer.Deserialize<JsonElement>(p.Value?.ToJsonString() ?? "{}")),
                    Required = (t.Parameters["required"] as JsonArray)?.Select(x => (string?)x).OfType<string>().ToList(),
                },
            }).ToList()
            : null;

        for (var round = 0; ; round++)
        {
            var parameters = new MessageCreateParams
            {
                Model = r.Model,
                MaxTokens = (long)(r.Settings.MaxTokens ?? 64000),
                Messages = messages,
                System = string.IsNullOrWhiteSpace(r.SystemPrompt) ? (MessageCreateParamsSystem?)null : r.SystemPrompt,
                Tools = toolDefs != null && round < MaxToolRounds ? toolDefs : null,
                // Lehnt das Modell eine Anfrage ab, beantwortet sie serverseitig automatisch ein passendes Ersatzmodell
                Betas = useFallback ? ["server-side-fallback-2026-07-01"] : null,
                Fallbacks = useFallback ? new Default() : (BetaFallbacksParam?)null,
            };

            var text = new StringBuilder();
            var uses = new List<(string Id, string Name, StringBuilder Json)>();
            onPhase(Phase.Sending);
            await foreach (var ev in client.Beta.Messages.CreateStreaming(parameters, ct))
            {
                if (ev.TryPickContentBlockDelta(out var delta))
                {
                    if (delta.Delta.TryPickText(out var t))
                    {
                        text.Append(t.Text);
                        onChunk(t.Text);
                    }
                    else if (delta.Delta.TryPickInputJson(out var json) && uses.Count > 0)
                        uses[^1].Json.Append(json.PartialJson);
                    else if (delta.Delta.TryPickThinking(out var thought))
                    {
                        onPhase(Phase.Thinking);
                        onReasoning(thought.Thinking);
                    }
                }
                else if (ev.TryPickContentBlockStart(out var blockStart))
                {
                    if (blockStart.ContentBlock.TryPickBetaToolUse(out var use))
                        uses.Add((use.ID, use.Name, new StringBuilder()));
                }
                else if (ev.TryPickStart(out var start))
                {
                    onPhase(Phase.Connected);
                    result.Model = start.Message.Model.ToString();
                    AddUsage(result, start.Message.Usage.InputTokens, null, null);
                }
                else if (ev.TryPickDelta(out var messageDelta))
                {
                    AddUsage(result, null, messageDelta.Usage.OutputTokens, null);
                    if (messageDelta.Delta.StopReason == "refusal")
                        onChunk("\n\n*" + L.T("[Das Modell hat diese Anfrage abgelehnt.]") + "*");
                }
            }

            if (uses.Count == 0 || tools == null) break;

            // Antwort mit Werkzeugaufrufen zurückspielen, Aufrufe ausführen, Ergebnisse als Nutzer-Nachricht anhängen
            var assistantBlocks = new List<BetaContentBlockParam>();
            if (text.Length > 0)
            {
                assistantBlocks.Add(new BetaTextBlockParam { Text = text.ToString() });
                onChunk("\n\n");
            }
            var resultBlocks = new List<BetaContentBlockParam>();
            foreach (var (id, name, json) in uses)
            {
                var input = json.Length > 0 ? json.ToString() : "{}";
                assistantBlocks.Add(new BetaToolUseBlockParam
                {
                    ID = id,
                    Name = name,
                    Input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(input) ?? new(),
                });
                resultBlocks.Add(new BetaToolResultBlockParam { ToolUseID = id, Content = await tools.Execute(name, input, ct) });
            }
            messages.Add(new BetaAssistantMessageParam { Content = assistantBlocks });
            messages.Add(new BetaUserMessageParam { Content = resultBlocks });
        }
    }

    private static BetaMessageParam ToAnthropicMessage(ChatMessage m)
    {
        if (m.Role == "assistant")
            return new BetaAssistantMessageParam { Content = AssistantText(m) };
        var text = m.TextForModel;
        if (m.Image == null)
            return new BetaUserMessageParam { Content = text };

        var blocks = new List<BetaContentBlockParam>
        {
            new BetaImageBlockParam
            {
                Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(m.Image), MediaType = MediaType.ImagePng }
            }
        };
        if (text.Length > 0)
            blocks.Add(new BetaTextBlockParam { Text = text });
        return new BetaUserMessageParam { Content = blocks };
    }

    private static AnthropicClient CreateAnthropicClient(string server, string apiKey)
    {
        var client = new AnthropicClient { ApiKey = apiKey };
        server = server.Trim().TrimEnd('/');
        if (server.Length > 0 && server != ProviderInfo.Get(ProviderKind.Anthropic).DefaultServer)
            client = new AnthropicClient { ApiKey = apiKey, BaseUrl = server };
        return client;
    }

    // ---------- HTTP-Hilfen ----------

    private static void AddHeaders(HttpRequestMessage request, ProviderInfo p, string apiKey)
    {
        if (apiKey.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (p.Kind == ProviderKind.OpenRouter)
        {
            request.Headers.Add("HTTP-Referer", "https://github.com/ghaslbe/FreeGuenther");
            request.Headers.Add("X-Title", "FreeGuenther");
        }
    }

    private static async Task<JsonNode?> SendJsonAsync(HttpMethod method, string url, ProviderInfo p, string apiKey,
        JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        AddHeaders(request, p, apiKey);
        if (body != null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {ErrorText(text)}");
        return JsonNode.Parse(text);
    }

    // Holt die eigentliche Fehlermeldung aus einer JSON-Fehlerantwort
    private static string ErrorText(string raw)
    {
        try
        {
            var json = JsonNode.Parse(raw);
            var message = (string?)json?["error"]?["message"] ?? (string?)json?["message"] ??
                          (json?["error"] is JsonValue v ? (string?)v : null);
            if (!string.IsNullOrWhiteSpace(message)) return message;
        }
        catch (System.Text.Json.JsonException) { }
        return raw.Length > 300 ? raw[..300] + "…" : raw;
    }
}
