using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Anthropic.Models.Models;

namespace LmChat;

public enum ProviderKind { LmStudio, Ollama, OpenAI, Anthropic, OpenRouter }

// Vision = null heißt: unbekannt (Anbieter meldet es nicht oder Server nicht erreichbar)
public record ModelInfo(string Id, bool? Vision, bool Loaded)
{
    public string Label => Id + (Vision == true ? "    🖼 Bilder" : "") + (Loaded ? "    ● geladen" : "");
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
    public string Label => (IsLocal ? "Lokal  ·  " : "Remote  ·  ") + Name;

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
            ("Modelle", "https://platform.openai.com/docs/models"),
            ("Preise", "https://openai.com/api/pricing")
        ],
        ProviderKind.Anthropic =>
        [
            ("API-Schlüssel", "https://platform.claude.com/settings/keys"),
            ("Modelle", "https://platform.claude.com/docs/en/about-claude/models/overview"),
            ("Preise", "https://platform.claude.com/docs/en/about-claude/pricing")
        ],
        ProviderKind.OpenRouter =>
        [
            ("Website", "https://openrouter.ai"),
            ("API-Schlüssel", "https://openrouter.ai/keys"),
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

// Regionen-Bindung bei OpenRouter (In-Region-Routing)
public sealed record RegionOption(string Code, string Label)
{
    public static readonly RegionOption[] All =
    [
        new("", "Weltweit – alle Hoster"),
        new("eu", "Nur Europa (EU/EWR, Großbritannien, Schweiz)"),
        new("us", "Nur USA"),
    ];

    public static string Name(string code) => code switch { "eu" => "EU", "us" => "USA", _ => "" };
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
        if (p.Kind == ProviderKind.OpenRouter && region.Length > 0 && server == p.DefaultServer)
            return $"https://{region}.openrouter.ai/api";
        return server;
    }

    public static async Task<List<ModelInfo>> ListModelsAsync(ProviderInfo p, string server, string region, string apiKey,
        CancellationToken ct)
    {
        server = EffectiveServer(p, server, region);
        switch (p.Kind)
        {
            case ProviderKind.LmStudio:
            {
                // LM-Studio-eigene API: Typ "vlm" = versteht Bilder
                var json = await SendJsonAsync(HttpMethod.Get, server + "/api/v0/models", p, apiKey, null, ct);
                return (json?["data"] as JsonArray ?? [])
                    .Select(n => (Id: (string?)n?["id"], Type: (string?)n?["type"], State: (string?)n?["state"]))
                    .Where(m => m.Id != null && m.Type is "llm" or "vlm")
                    .Select(m => new ModelInfo(m.Id!, m.Type == "vlm", m.State == "loaded"))
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
                        return list.Contains("completion") ? new ModelInfo(name, list.Contains("vision"), loaded.Contains(name)) : null;
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
                // Bei OpenAI selbst nur Chat-Modelle zeigen (keine Audio-, Bild-, Embedding-Modelle)
                if (server.Contains("api.openai.com"))
                    ids = ids.Where(IsOpenAIChatModel);
                return ids.OrderBy(id => id).Select(id => new ModelInfo(id, null, false)).ToList();
            }

            case ProviderKind.OpenRouter:
            {
                var json = await SendJsonAsync(HttpMethod.Get, server + "/v1/models", p, apiKey, null, ct);
                var models = (json?["data"] as JsonArray ?? [])
                    .Where(n => ((n?["architecture"]?["output_modalities"] as JsonArray)?.Any(x => (string?)x == "text")) ?? true)
                    .Select(n => new ModelInfo(
                        (string?)n?["id"] ?? "",
                        (n?["architecture"]?["input_modalities"] as JsonArray)?.Any(x => (string?)x == "image"),
                        false))
                    .Where(m => m.Id.Length > 0)
                    .OrderBy(m => m.Id)
                    .ToList();
                if (region.Length == 0)
                    models.Insert(0, new ModelInfo("openrouter/auto", true, false));
                return models;
            }

            case ProviderKind.Anthropic:
            {
                var client = CreateAnthropicClient(server, apiKey);
                var page = await client.Models.List(new ModelListParams { Limit = 100 }, ct);
                return page.Items.Select(m => new ModelInfo(m.ID, true, false)).ToList();
            }

            default:
                return [];
        }
    }

    private static bool IsOpenAIChatModel(string id) =>
        (id.StartsWith("gpt-") || id.StartsWith("chatgpt-") || id.StartsWith("o1") || id.StartsWith("o3") || id.StartsWith("o4")) &&
        !new[] { "audio", "realtime", "transcribe", "tts", "image", "embedding", "search", "moderation", "instruct" }.Any(id.Contains);

    // ---------- Chat ----------

    public static Task StreamAsync(ChatRequest r, Action<string> onChunk, CancellationToken ct) =>
        r.Provider.Kind == ProviderKind.Anthropic ? StreamAnthropicAsync(r, onChunk, ct) : StreamOpenAICompatibleAsync(r, onChunk, ct);

    private static async Task StreamOpenAICompatibleAsync(ChatRequest r, Action<string> onChunk, CancellationToken ct)
    {
        var p = r.Provider;
        var s = r.Settings;
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(r.SystemPrompt))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = r.SystemPrompt });
        foreach (var m in r.Messages)
            messages.Add(ToOpenAIMessage(m));

        var body = new JsonObject { ["model"] = r.Model, ["messages"] = messages, ["stream"] = true };
        if (p.SupportsSampling && s.Temperature is { } temperature) body["temperature"] = temperature;
        if (p.SupportsSampling && s.TopP is { } topP) body["top_p"] = topP;
        if (p.SupportsTopK && s.TopK is { } topK) body["top_k"] = (int)topK;
        if (p.SupportsRepeatPenalty && s.RepeatPenalty is { } repeat)
            body[p.Kind == ProviderKind.OpenRouter ? "repetition_penalty" : "repeat_penalty"] = repeat;
        if (s.MaxTokens is { } maxTokens)
            body[p.Kind == ProviderKind.OpenAI ? "max_completion_tokens" : "max_tokens"] = (int)maxTokens;

        using var request = new HttpRequestMessage(HttpMethod.Post, EffectiveServer(p, r.Server, r.Region) + "/v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        AddHeaders(request, p, r.ApiKey);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {ErrorText(await response.Content.ReadAsStringAsync(ct))}");

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:")) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;

            var json = JsonNode.Parse(data);
            if (json?["error"] is { } error)
                throw new HttpRequestException(ErrorText(error.ToJsonString()));
            if (json?["choices"] is not JsonArray { Count: > 0 } choices) continue;
            var chunk = choices[0]?["delta"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(chunk))
                onChunk(chunk);
        }
    }

    private static JsonObject ToOpenAIMessage(ChatMessage m)
    {
        if (m.Image == null)
            return new JsonObject { ["role"] = m.Role, ["content"] = m.Content };

        var parts = new JsonArray();
        if (m.Content.Length > 0)
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = m.Content });
        parts.Add(new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(m.Image) }
        });
        return new JsonObject { ["role"] = m.Role, ["content"] = parts };
    }

    private static async Task StreamAnthropicAsync(ChatRequest r, Action<string> onChunk, CancellationToken ct)
    {
        var client = CreateAnthropicClient(r.Server, r.ApiKey);
        var useFallback = FallbackModels.Contains(r.Model);
        var parameters = new MessageCreateParams
        {
            Model = r.Model,
            MaxTokens = (long)(r.Settings.MaxTokens ?? 64000),
            Messages = r.Messages.Select(ToAnthropicMessage).ToList(),
            System = string.IsNullOrWhiteSpace(r.SystemPrompt) ? (MessageCreateParamsSystem?)null : r.SystemPrompt,
            // Lehnt das Modell eine Anfrage ab, beantwortet sie serverseitig automatisch ein passendes Ersatzmodell
            Betas = useFallback ? ["server-side-fallback-2026-07-01"] : null,
            Fallbacks = useFallback ? new Default() : (BetaFallbacksParam?)null,
        };

        await foreach (var ev in client.Beta.Messages.CreateStreaming(parameters, ct))
        {
            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                onChunk(text.Text);
            else if (ev.TryPickDelta(out var messageDelta) && messageDelta.Delta.StopReason == "refusal")
                onChunk("\n\n*[Das Modell hat diese Anfrage abgelehnt.]*");
        }
    }

    private static BetaMessageParam ToAnthropicMessage(ChatMessage m)
    {
        if (m.Role == "assistant")
            return new BetaAssistantMessageParam { Content = m.Content };
        if (m.Image == null)
            return new BetaUserMessageParam { Content = m.Content };

        var blocks = new List<BetaContentBlockParam>
        {
            new BetaImageBlockParam
            {
                Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(m.Image), MediaType = MediaType.ImagePng }
            }
        };
        if (m.Content.Length > 0)
            blocks.Add(new BetaTextBlockParam { Text = m.Content });
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
            request.Headers.Add("HTTP-Referer", "https://github.com/ghaslbe/lmstudioChatWindows");
            request.Headers.Add("X-Title", "LM Chat");
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
