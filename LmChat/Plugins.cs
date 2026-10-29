using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace LmChat;

// ---------- Plugin-Manifest (eine JSON-Datei je Plugin) ----------
//
// Drei Arten:
//  "http"    – Werkzeuge, die eine oder mehrere Web-Adressen abfragen (z. B. Wetter). Kein Programmcode,
//              die App ruft nur die im Manifest stehenden Adressen auf.
//  "mcp"     – startet einen MCP-Server (Befehl) bzw. verbindet sich mit einer MCP-Adresse; dessen Werkzeuge
//              werden übernommen. Kann je nach Server Dateien lesen/ändern → standardmäßig mit Rückfrage.
//  "builtin" – in FreeGuenther eingebaute Werkzeuge (z. B. Datum und Uhrzeit).
//
// Platzhalter in Adressen, Kopfzeilen, Befehlen und im Ergebnistext:
//  {param}                 Wert, den das Modell übergibt
//  {settings.key}          Wert aus den Plugin-Einstellungen
//  {alias.pfad.0.feld}     Feld aus einer vorherigen Antwort (Anfrage mit "as": "alias")
//  {a|b|'text'}            erster nicht leerer Wert, 'text' = fester Text

public sealed class PluginManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Homepage { get; set; }
    public string Type { get; set; } = "http";
    public bool? AskBeforeRun { get; set; }           // Vorgabe für die Rückfrage vor jeder Ausführung
    public List<PluginSetting> Settings { get; set; } = [];
    public List<PluginTool> Tools { get; set; } = [];
    public McpLaunch? Mcp { get; set; }

    [JsonIgnore] public bool IsBuiltIn { get; set; }   // mit der App ausgeliefert, kann nicht entfernt werden
    [JsonIgnore] public string? FilePath { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static PluginManifest Parse(string json)
    {
        var m = JsonSerializer.Deserialize<PluginManifest>(json, Json)
                ?? throw new InvalidDataException("Die Datei enthält kein Plugin.");
        m.Validate();
        return m;
    }

    private void Validate()
    {
        if (!Regex.IsMatch(Id, "^[a-z0-9][a-z0-9-]{1,39}$"))
            throw new InvalidDataException("Ungültige Plugin-Kennung (id): nur Kleinbuchstaben, Ziffern und Bindestriche.");
        if (Name.Trim().Length == 0)
            throw new InvalidDataException("Dem Plugin fehlt ein Name.");
        switch (Type)
        {
            case "http":
                if (Tools.Count == 0) throw new InvalidDataException("Das Plugin enthält keine Werkzeuge.");
                foreach (var t in Tools)
                {
                    if (!Regex.IsMatch(t.Name, "^[a-zA-Z0-9_-]{1,48}$"))
                        throw new InvalidDataException($"Ungültiger Werkzeugname „{t.Name}“.");
                    if (t.Requests.Count == 0)
                        throw new InvalidDataException($"Werkzeug „{t.Name}“ hat keine Anfrage.");
                    foreach (var r in t.Requests)
                        if (!r.Url.StartsWith("https://") && !r.Url.StartsWith("http://"))
                            throw new InvalidDataException($"Werkzeug „{t.Name}“: nur http- und https-Adressen sind erlaubt.");
                }
                break;
            case "mcp":
                if (Mcp == null || (string.IsNullOrWhiteSpace(Mcp.Command) && string.IsNullOrWhiteSpace(Mcp.Url)))
                    throw new InvalidDataException("Dem MCP-Plugin fehlt der Befehl bzw. die Adresse.");
                break;
            case "builtin":
                break;
            default:
                throw new InvalidDataException($"Unbekannte Plugin-Art „{Type}“.");
        }
        foreach (var s in Settings)
            if (!Regex.IsMatch(s.Key, "^[a-zA-Z0-9_]{1,40}$"))
                throw new InvalidDataException($"Ungültiger Einstellungsname „{s.Key}“.");
    }

    // Was das Plugin nach außen tut – wird vor der Installation und in der Plugin-Liste angezeigt
    public string AccessSummary()
    {
        switch (Type)
        {
            case "http":
                var hosts = Tools.SelectMany(t => t.Requests)
                    .Select(r => Uri.TryCreate(Regex.Replace(r.Url, @"\{[^}]*\}", "x"), UriKind.Absolute, out var u) ? u.Host : "?")
                    .Distinct();
                return "Fragt ab: " + string.Join(", ", hosts);
            case "mcp" when Mcp!.Url is { Length: > 0 } url:
                return "Verbindet sich mit dem MCP-Server " + url;
            case "mcp":
                return "Startet auf diesem Rechner: " + Mcp!.Command + " " + string.Join(" ", Mcp.Args);
            default:
                return "Eingebaut, keine Verbindung nach außen";
        }
    }

    public string TypeLabel => Type switch { "mcp" => "MCP", "builtin" => "eingebaut", _ => "Web" };
}

public sealed class PluginSetting
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "text";        // text | secret | number | bool | choice
    public string Default { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Options { get; set; } = [];   // bei "choice": erlaubte Werte
    public List<string> OptionLabels { get; set; } = [];
}

public sealed class PluginTool
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public JsonObject Parameters { get; set; } = new() { ["type"] = "object", ["properties"] = new JsonObject() };
    public List<PluginRequest> Requests { get; set; } = [];
    public string? Result { get; set; }               // Text vor der Rohantwort, mit Platzhaltern
    public string? Builtin { get; set; }              // bei Art "builtin": welche eingebaute Funktion
}

public sealed class PluginRequest
{
    public string Url { get; set; } = "";
    public string? As { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public string? Require { get; set; }              // Pfad, der in der Antwort vorhanden sein muss
    public string? Error { get; set; }                // Meldung, wenn er fehlt
}

public sealed class McpLaunch
{
    public string? Command { get; set; }
    public List<string> Args { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = new();
    public string? Url { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
}

// Was der Nutzer je Plugin eingestellt hat (in der Datenbank unter "plugins")
public sealed class PluginState
{
    public bool Enabled { get; set; }
    public bool? AskBeforeRun { get; set; }           // null = Standard (MCP: ja, sonst nein)
    public Dictionary<string, string> Values { get; set; } = new();
    public Dictionary<string, string> Secrets { get; set; } = new();   // DPAPI-verschlüsselt
}

// Eintrag im Online-Katalog (plugins/index.json im GitHub-Repository)
public sealed record CatalogEntry(string Id, string Name, string Version, string Description, string Type, string Url);

// Eine Antwort samt Frage, wie sie das Webhook-Plugin verschickt
public sealed record ExportAnswer(string ChatTitle, string? Question, ChatMessage Answer);

// Ein Werkzeug, wie es dem Modell angeboten wird
public sealed record ToolSpec(string Name, string Description, JsonObject Parameters);

// Was während einer Antwort an Werkzeugen genutzt werden kann
public sealed class ToolBox
{
    public required List<ToolSpec> Specs { get; init; }
    public required Func<string, string, CancellationToken, Task<string>> Execute { get; init; }
}

public sealed class PluginManager : IAsyncDisposable
{
    public const string CatalogUrl = "https://raw.githubusercontent.com/ghaslbe/FreeGuenther/main/plugins/index.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly ChatStore _store;
    private readonly string _folder;
    private Dictionary<string, PluginState> _states = new();
    private readonly Dictionary<string, McpClient> _mcpClients = new();
    private readonly Dictionary<string, List<McpClientTool>> _mcpTools = new();

    public List<PluginManifest> Plugins { get; } = [];

    static PluginManager()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("FreeGuenther/" + Assembly.GetExecutingAssembly().GetName().Version?.ToString(3));
    }

    public PluginManager(ChatStore store, string folder)
    {
        _store = store;
        _folder = folder;
        Reload();
    }

    public void Reload()
    {
        try
        {
            _states = JsonSerializer.Deserialize<Dictionary<string, PluginState>>(_store.GetSetting("plugins") ?? "{}") ?? new();
        }
        catch (JsonException)
        {
            _states = new();
        }

        Plugins.Clear();
        // Mitgelieferte Plugins (eingebettet) – eine installierte Datei mit gleicher id und neuerer Version hat Vorrang
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("plugin.")))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var m = PluginManifest.Parse(new StreamReader(stream).ReadToEnd());
            m.IsBuiltIn = true;
            Plugins.Add(m);
        }
        if (Directory.Exists(_folder))
        {
            foreach (var file in Directory.GetFiles(_folder, "*.json"))
            {
                try
                {
                    var m = PluginManifest.Parse(File.ReadAllText(file));
                    m.FilePath = file;
                    var existing = Plugins.FindIndex(p => p.Id == m.Id);
                    if (existing < 0)
                        Plugins.Add(m);
                    else if (CompareVersions(m.Version, Plugins[existing].Version) > 0)
                    {
                        m.IsBuiltIn = Plugins[existing].IsBuiltIn;
                        Plugins[existing] = m;
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
                {
                    // Defekte Datei überspringen
                }
            }
        }
        Plugins.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
    }

    public static int CompareVersions(string a, string b) =>
        Version.TryParse(a, out var va) && Version.TryParse(b, out var vb) ? va.CompareTo(vb) : string.CompareOrdinal(a, b);

    // ---------- Zustand und Einstellungen ----------

    public PluginState State(string id)
    {
        if (!_states.TryGetValue(id, out var state))
            _states[id] = state = new PluginState();
        return state;
    }

    public bool AskBeforeRun(PluginManifest m) => State(m.Id).AskBeforeRun ?? m.AskBeforeRun ?? m.Type == "mcp";

    public void SaveStates() => _store.SetSetting("plugins", JsonSerializer.Serialize(_states));

    public string GetValue(PluginManifest m, PluginSetting s)
    {
        var state = State(m.Id);
        if (s.Type == "secret")
            return state.Secrets.TryGetValue(s.Key, out var p) ? Secret.Unprotect(p) : s.Default;
        return state.Values.TryGetValue(s.Key, out var v) ? v : s.Default;
    }

    public void SetValue(PluginManifest m, PluginSetting s, string value)
    {
        var state = State(m.Id);
        if (s.Type == "secret")
            state.Secrets[s.Key] = Secret.Protect(value);
        else
            state.Values[s.Key] = value;
        SaveStates();
    }

    // Einstellungen geändert oder Plugin abgeschaltet: laufenden MCP-Server beenden, beim nächsten Mal neu starten
    public async Task ResetAsync(string id)
    {
        if (_mcpClients.Remove(id, out var client))
            await client.DisposeAsync();
        _mcpTools.Remove(id);
    }

    public IEnumerable<PluginManifest> Enabled => Plugins.Where(p => State(p.Id).Enabled);

    private JsonObject SettingsObject(PluginManifest m)
    {
        var o = new JsonObject();
        foreach (var s in m.Settings)
            o[s.Key] = GetValue(m, s);
        return o;
    }

    // ---------- Installieren, Importieren, Entfernen ----------

    public async Task<List<CatalogEntry>> LoadCatalogAsync(CancellationToken ct)
    {
        var json = JsonNode.Parse(await Http.GetStringAsync(CatalogUrl + "?t=" + DateTime.UtcNow.Ticks, ct));
        return (json?["plugins"] as JsonArray ?? [])
            .Select(n => new CatalogEntry((string?)n?["id"] ?? "", (string?)n?["name"] ?? "", (string?)n?["version"] ?? "",
                (string?)n?["description"] ?? "", (string?)n?["type"] ?? "http", (string?)n?["url"] ?? ""))
            .Where(e => e.Id.Length > 0 && e.Url.StartsWith("https://"))
            .ToList();
    }

    public static async Task<PluginManifest> DownloadAsync(string url, CancellationToken ct) =>
        PluginManifest.Parse(await Http.GetStringAsync(url, ct));

    public static PluginManifest ReadFile(string path) => PluginManifest.Parse(File.ReadAllText(path));

    public async Task InstallAsync(PluginManifest m)
    {
        await ResetAsync(m.Id);
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(Path.Combine(_folder, m.Id + ".json"), JsonSerializer.Serialize(m, PluginManifest.Json));
        Reload();
    }

    public async Task RemoveAsync(PluginManifest m)
    {
        await ResetAsync(m.Id);
        if (m.FilePath != null && File.Exists(m.FilePath))
            File.Delete(m.FilePath);
        _states.Remove(m.Id);
        SaveStates();
        Reload();
    }

    // ---------- Werkzeuge für eine Antwort ----------

    // Name, wie ihn das Modell sieht: "<plugin>__<werkzeug>" (eindeutig, max. 64 Zeichen, nur erlaubte Zeichen)
    private static string ToolName(PluginManifest m, string tool)
    {
        var name = Regex.Replace($"{m.Id}__{tool}", "[^a-zA-Z0-9_-]", "_");
        return name.Length > 64 ? name[..64] : name;
    }

    // Werkzeuge aller aktiven Plugins; MCP-Server werden dafür bei Bedarf gestartet
    public async Task<List<(ToolSpec Spec, PluginManifest Plugin, string Tool)>> CollectToolsAsync(Action<string> status,
        List<string> problems, CancellationToken ct)
    {
        var list = new List<(ToolSpec, PluginManifest, string)>();
        foreach (var m in Enabled.ToList())
        {
            if (m.Type == "mcp")
            {
                try
                {
                    foreach (var t in await McpToolsAsync(m, status, ct))
                    {
                        var schema = JsonNode.Parse(t.JsonSchema.GetRawText()) as JsonObject ?? new JsonObject();
                        schema["type"] ??= "object";
                        schema["properties"] ??= new JsonObject();
                        list.Add((new ToolSpec(ToolName(m, t.Name), $"[{m.Name}] {t.Description}", schema), m, t.Name));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    problems.Add($"{m.Name}: {ex.Message}");
                }
            }
            else
            {
                foreach (var t in m.Tools)
                    list.Add((new ToolSpec(ToolName(m, t.Name), t.Description, (JsonObject)t.Parameters.DeepClone()), m, t.Name));
            }
        }
        return list;
    }

    // Für den Verbindungstest auf der Plugin-Seite
    public async Task<List<string>> ToolNamesAsync(PluginManifest m, CancellationToken ct) =>
        m.Type == "mcp"
            ? (await McpToolsAsync(m, _ => { }, ct)).Select(t => t.Name).ToList()
            : m.Tools.Select(t => t.Name).ToList();

    private async Task<List<McpClientTool>> McpToolsAsync(PluginManifest m, Action<string> status, CancellationToken ct)
    {
        if (_mcpTools.TryGetValue(m.Id, out var cached)) return cached;
        var client = await McpClientAsync(m, status, ct);
        var tools = (await client.ListToolsAsync(cancellationToken: ct)).ToList();
        _mcpTools[m.Id] = tools;
        return tools;
    }

    private async Task<McpClient> McpClientAsync(PluginManifest m, Action<string> status, CancellationToken ct)
    {
        if (_mcpClients.TryGetValue(m.Id, out var existing)) return existing;
        status($"Starte Plugin „{m.Name}“");
        var context = new Dictionary<string, JsonNode?> { ["settings"] = SettingsObject(m) };
        var launch = m.Mcp!;
        IClientTransport transport;
        if (launch.Url is { Length: > 0 })
        {
            transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(Template.Fill(launch.Url, context, Uri.EscapeDataString)),
                Name = m.Name,
                AdditionalHeaders = launch.Headers.ToDictionary(h => h.Key, h => Template.Fill(h.Value, context, null)),
            });
        }
        else
        {
            transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = launch.Command!,
                Arguments = launch.Args.Select(a => Template.Fill(a, context, null)).Where(a => a.Length > 0).ToList(),
                EnvironmentVariables = launch.Env.ToDictionary(e => e.Key, e => (string?)Template.Fill(e.Value, context, null)),
                Name = m.Name,
            });
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));   // npx lädt beim ersten Start ggf. erst Pakete herunter
        var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        _mcpClients[m.Id] = client;
        return client;
    }

    // Führt ein Werkzeug aus und liefert den Text, der ans Modell zurückgeht
    public async Task<string> ExecuteAsync(PluginManifest m, string tool, string argumentsJson, CancellationToken ct)
    {
        var args = string.IsNullOrWhiteSpace(argumentsJson) ? new JsonObject() : JsonNode.Parse(argumentsJson) as JsonObject ?? new JsonObject();
        switch (m.Type)
        {
            case "mcp":
            {
                var client = await McpClientAsync(m, _ => { }, ct);
                var dict = args.ToDictionary(p => p.Key, p => (object?)(p.Value == null ? null : JsonSerializer.Deserialize<JsonElement>(p.Value.ToJsonString())));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(120));
                var result = await client.CallToolAsync(tool, dict, cancellationToken: timeout.Token);
                var text = string.Join("\n", result.Content.Select(c => c is TextContentBlock t ? t.Text : $"[{c.Type}]"));
                return Limit((result.IsError == true ? "Fehler: " : "") + text);
            }
            case "builtin" when m.Tools.First(t => t.Name == tool).Builtin == "webhook_post":
                return LastAnswer?.Invoke() is { } answer
                    ? await PostAnswerAsync(m, answer, (string?)args["notiz"], ct)
                    : "Fehler: In diesem Chat gibt es noch keine Antwort, die gesendet werden könnte.";
            case "builtin" when m.Tools.First(t => t.Name == tool).Builtin == "http_get":
                return await WebPage.FetchAsync(Http, (string?)args["url"] ?? "", SettingsObject(m), ct);
            case "builtin":
                return Builtin(m, m.Tools.First(t => t.Name == tool).Builtin, args);
            default:
                return await RunHttpToolAsync(m, m.Tools.First(t => t.Name == tool), args, ct);
        }
    }

    private async Task<string> RunHttpToolAsync(PluginManifest m, PluginTool tool, JsonObject args, CancellationToken ct)
    {
        var context = new Dictionary<string, JsonNode?> { ["settings"] = SettingsObject(m) };
        foreach (var (key, value) in args)
            context[key] = value?.DeepClone();

        string last = "";
        foreach (var r in tool.Requests)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Template.Fill(r.Url, context, Uri.EscapeDataString));
            foreach (var (name, value) in r.Headers)
                request.Headers.TryAddWithoutValidation(name, Template.Fill(value, context, null));
            using var response = await Http.SendAsync(request, ct);
            last = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return $"Fehler: Der Dienst antwortete mit HTTP {(int)response.StatusCode}. {Limit(last, 500)}";

            JsonNode? parsed;
            try { parsed = JsonNode.Parse(last); }
            catch (JsonException) { parsed = JsonValue.Create(last); }
            context["last"] = parsed;
            if (r.As != null) context[r.As] = parsed;
            if (r.Require != null && Template.Resolve(r.Require, context) == null)
                return "Fehler: " + Template.Fill(r.Error ?? "Keine passenden Daten gefunden.", context, null);
        }
        var prefix = tool.Result == null ? "" : Template.Fill(tool.Result, context, null) + "\n\n";
        return Limit(prefix + last);
    }

    private string Builtin(PluginManifest m, string? name, JsonObject args)
    {
        var settings = SettingsObject(m);
        return name switch
        {
            "datetime" => JsonSerializer.Serialize(new
            {
                jetzt = DateTime.Now.ToString("dddd, d. MMMM yyyy, HH:mm:ss"),
                iso = DateTimeOffset.Now.ToString("o"),
                zeitzone = TimeZoneInfo.Local.DisplayName,
                kalenderwoche = System.Globalization.ISOWeek.GetWeekOfYear(DateTime.Now),
            }),
            "files_list" or "files_read" or "files_search" => LocalFiles.Run(name, (string?)settings["folder"] ?? "", args),
            _ => "Fehler: unbekannte eingebaute Funktion."
        };
    }

    // ---------- Webhook: Antwort als JSON an eine Adresse senden ----------

    // Liefert die letzte Antwort im aktuellen Chat (setzt das Hauptfenster)
    public Func<ExportAnswer?>? LastAnswer { get; set; }

    // Das aktive Webhook-Plugin (null = keins eingeschaltet)
    public PluginManifest? ActiveWebhook => Enabled.FirstOrDefault(p => p.Tools.Any(t => t.Builtin == "webhook_post"));

    public async Task<string> PostAnswerAsync(PluginManifest m, ExportAnswer a, string? note, CancellationToken ct)
    {
        var settings = SettingsObject(m);
        var url = ((string?)settings["url"] ?? "").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            return "Fehler: Im Plugin „Webhook“ ist keine gültige Ziel-Adresse eingestellt (Plugins → Webhook → Einstellungen).";
        var withImage = (string?)settings["image"] != "false";

        var image = withImage && a.Answer.Image != null
            ? new { mimeType = "image/png", base64 = Convert.ToBase64String(a.Answer.Image) }
            : null;
        var payload = new
        {
            app = "FreeGuenther",
            appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3),
            sentAt = DateTimeOffset.Now.ToString("o"),
            chat = a.ChatTitle,
            type = (a.Answer.Content.Length > 0, a.Answer.Image != null) switch
            {
                (true, true) => "text+image", (false, true) => "image", _ => "text"
            },
            question = a.Question,
            text = a.Answer.Content.Length > 0 ? a.Answer.Content : null,
            image,
            provider = a.Answer.Stats?.Provider,
            model = a.Answer.Stats?.Model,
            note = string.IsNullOrWhiteSpace(note) ? null : note,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                // Umlaute, Anführungszeichen usw. lesbar lassen statt \u-Schreibweise (wird als UTF-8 gesendet)
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }), Encoding.UTF8, "application/json")
        };
        if ((string?)settings["token"] is { Length: > 0 } token)
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        try
        {
            using var response = await Http.SendAsync(request, ct);
            return response.IsSuccessStatusCode
                ? $"Gesendet an {uri.Host} (HTTP {(int)response.StatusCode})."
                : $"Fehler: {uri.Host} antwortete mit HTTP {(int)response.StatusCode}. {Limit(await response.Content.ReadAsStringAsync(ct), 300)}";
        }
        catch (HttpRequestException ex)
        {
            return $"Fehler: {uri.Host} nicht erreichbar – {ex.Message}";
        }
    }

    private static string Limit(string text, int max = 12000) => text.Length <= max ? text : text[..max] + " … [gekürzt]";

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _mcpClients.Keys.ToList())
            await ResetAsync(id);
    }
}

// Eingebautes Plugin „Dateien“: nur lesen, nur innerhalb des eingestellten Ordners
public static class LocalFiles
{
    private const int MaxReadBytes = 200_000;

    public static string Run(string name, string folder, JsonObject args)
    {
        if (folder.Trim().Length == 0 || !Directory.Exists(folder))
            return "Fehler: Im Plugin „Dateien“ ist kein (gültiger) Ordner eingestellt. Bitte den Nutzer bitten, ihn unter Plugins → Dateien → Einstellungen festzulegen.";
        var root = Path.GetFullPath(folder.Trim()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        try
        {
            return name switch
            {
                "files_list" => List(root, (string?)args["pfad"] ?? ""),
                "files_read" => Read(root, (string?)args["pfad"] ?? ""),
                _ => Search(root, (string?)args["suchtext"] ?? ""),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "Fehler: " + ex.Message;
        }
    }

    // Pfad relativ zum Ordner auflösen – alles außerhalb wird abgelehnt (auch über ..\ oder absolute Pfade)
    private static string Resolve(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', '\\').TrimStart('\\')));
        if (!(full + "\\").StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Zugriff nur innerhalb des freigegebenen Ordners erlaubt.");
        return full;
    }

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path);

    private static string List(string root, string relative)
    {
        var dir = Resolve(root, relative);
        if (!Directory.Exists(dir)) return "Fehler: Ordner nicht gefunden.";
        var sb = new StringBuilder();
        foreach (var d in Directory.EnumerateDirectories(dir).Take(200))
            sb.AppendLine($"[Ordner] {Rel(root, d)}");
        foreach (var f in Directory.EnumerateFiles(dir).Take(500))
        {
            var info = new FileInfo(f);
            sb.AppendLine($"{Rel(root, f)}  ({info.Length:N0} Bytes, geändert {info.LastWriteTime:dd.MM.yyyy HH:mm})");
        }
        return sb.Length == 0 ? "Der Ordner ist leer." : sb.ToString();
    }

    private static string Read(string root, string relative)
    {
        var file = Resolve(root, relative);
        if (!File.Exists(file)) return "Fehler: Datei nicht gefunden.";
        var bytes = File.ReadAllBytes(file);
        var part = bytes.AsSpan(0, Math.Min(bytes.Length, MaxReadBytes));
        if (part.IndexOf((byte)0) >= 0)
            return "Fehler: Das ist keine Textdatei (z. B. Bild, PDF, Word). Nur Textdateien können gelesen werden.";
        var text = Encoding.UTF8.GetString(part);
        return bytes.Length > MaxReadBytes ? text + "\n… [Datei gekürzt]" : text;
    }

    private static string Search(string root, string query)
    {
        if (query.Trim().Length == 0) return "Fehler: Kein Suchtext angegeben.";
        var hits = new List<string>();
        foreach (var f in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            if (hits.Count >= 50) break;
            var rel = Rel(root, f);
            if (rel.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            {
                hits.Add($"{rel}  (Dateiname)");
                continue;
            }
            var info = new FileInfo(f);
            if (info.Length > 1_000_000) continue;
            var bytes = File.ReadAllBytes(f);
            if (Array.IndexOf(bytes, (byte)0) >= 0) continue;
            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            for (var i = 0; i < lines.Length && hits.Count < 50; i++)
                if (lines[i].Contains(query, StringComparison.CurrentCultureIgnoreCase))
                    hits.Add($"{rel}, Zeile {i + 1}: {lines[i].Trim()[..Math.Min(lines[i].Trim().Length, 160)]}");
        }
        return hits.Count == 0 ? "Nichts gefunden." : string.Join("\n", hits);
    }
}

// Eingebautes Plugin „Webseite abrufen“: lädt eine Adresse per GET und macht aus HTML lesbaren Text
public static class WebPage
{
    public static async Task<string> FetchAsync(HttpClient http, string url, JsonObject settings, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            return "Fehler: Bitte eine vollständige Adresse mit http:// oder https:// angeben.";

        // Nur erlaubte Domains (falls eingestellt): "example.com" erlaubt auch "www.example.com"
        var allowed = ((string?)settings["domains"] ?? "")
            .Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.ToLowerInvariant().TrimStart('*', '.')).ToList();
        var host = uri.Host.ToLowerInvariant();
        if (allowed.Count > 0 && !allowed.Any(d => host == d || host.EndsWith("." + d)))
            return $"Fehler: {host} ist nicht freigegeben. Erlaubt sind: {string.Join(", ", allowed)} (Plugins → Webseite abrufen → Einstellungen).";

        // Geräte im eigenen Netz (Router, NAS, …) nur, wenn ausdrücklich erlaubt
        if ((string?)settings["local"] != "true")
        {
            try
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(uri.Host, ct);
                if (addresses.Length == 0 || addresses.Any(IsPrivate))
                    return $"Fehler: {host} ist eine Adresse im lokalen Netz. Das ist in den Plugin-Einstellungen gesperrt.";
            }
            catch (System.Net.Sockets.SocketException)
            {
                return $"Fehler: {host} wurde nicht gefunden.";
            }
        }

        var max = int.TryParse((string?)settings["max"], out var m) ? m : 12000;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("text/html,application/json,text/plain;q=0.9,*/*;q=0.5");
        request.Headers.AcceptLanguage.ParseAdd("de,en;q=0.8");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            var head = $"Adresse: {response.RequestMessage?.RequestUri ?? uri}\nStatus: HTTP {(int)response.StatusCode}\nInhaltstyp: {type}\n\n";
            if (!(type.StartsWith("text/") || type.Contains("json") || type.Contains("xml") || type.Length == 0))
                return head + $"Der Inhalt ist keine Text- oder Webseite ({type}, {response.Content.Headers.ContentLength?.ToString("N0") ?? "?"} Bytes) und kann nicht gelesen werden.";

            // Höchstens 2 MB lesen
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new MemoryStream();
            await CopyLimitedAsync(stream, buffer, 2_000_000, ct);
            var charset = response.Content.Headers.ContentType?.CharSet;
            var encoding = charset != null ? TryEncoding(charset) : Encoding.UTF8;
            var body = encoding.GetString(buffer.ToArray());
            var text = type.Contains("html") || (type.Length == 0 && body.Contains("<html", StringComparison.OrdinalIgnoreCase))
                ? HtmlToText(body)
                : body;
            return head + (text.Length > max ? text[..max] + "\n… [gekürzt]" : text);
        }
        catch (HttpRequestException ex)
        {
            return $"Fehler: {host} nicht erreichbar – {ex.Message}";
        }
    }

    private static async Task CopyLimitedAsync(Stream from, Stream to, int limit, CancellationToken ct)
    {
        var buffer = new byte[81920];
        int read, total = 0;
        while (total < limit && (read = await from.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit - total)), ct)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, read), ct);
            total += read;
        }
    }

    private static Encoding TryEncoding(string name)
    {
        try { return Encoding.GetEncoding(name.Trim('"')); }
        catch (ArgumentException) { return Encoding.UTF8; }
    }

    private static bool IsPrivate(System.Net.IPAddress ip)
    {
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 169 && b[1] == 254) ||
               (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    // Grobe, aber robuste Umwandlung: Titel, Text mit Absätzen, ohne Skripte, Stile und Menüs
    public static string HtmlToText(string html)
    {
        var title = Regex.Match(html, @"<title[^>]*>([\s\S]*?)</title>", RegexOptions.IgnoreCase).Groups[1].Value;
        var s = Regex.Replace(html, @"<(script|style|noscript|svg|template|iframe|nav|footer|head)\b[\s\S]*?</\1\s*>", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<!--[\s\S]*?-->", " ");
        s = Regex.Replace(s, @"<(br|/p|/div|/li|/h[1-6]|/tr|/section|/article|/blockquote|/pre)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<li\b[^>]*>", "\n• ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<h[1-6]\b[^>]*>", "\n## ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", " ");
        s = System.Net.WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"[ \t\r\f\v]+", " ");
        s = Regex.Replace(s, @" *\n[ \n]*", "\n\n").Trim();
        title = System.Net.WebUtility.HtmlDecode(Regex.Replace(title, @"\s+", " ")).Trim();
        return (title.Length > 0 ? $"Titel: {title}\n\n" : "") + s;
    }
}

// Ersetzt Platzhalter wie {ort}, {settings.unit}, {geo.results.0.latitude} oder {tage|'3'}
public static class Template
{
    public static string Fill(string template, IReadOnlyDictionary<string, JsonNode?> context, Func<string, string>? encode) =>
        Regex.Replace(template, @"\{([^{}]+)\}", match =>
        {
            foreach (var alternative in match.Groups[1].Value.Split('|'))
            {
                var a = alternative.Trim();
                string? value = a.Length >= 2 && a[0] == '\'' && a[^1] == '\''
                    ? a[1..^1]
                    : Resolve(a, context) switch
                    {
                        null => null,
                        JsonValue v when v.TryGetValue<string>(out var s) => s,
                        JsonValue v => v.ToJsonString(),
                        var node => node.ToJsonString(),
                    };
                if (!string.IsNullOrEmpty(value))
                    return encode == null ? value : encode(value);
            }
            return "";
        });

    public static JsonNode? Resolve(string path, IReadOnlyDictionary<string, JsonNode?> context)
    {
        var parts = path.Split('.');
        if (!context.TryGetValue(parts[0], out var node)) return null;
        foreach (var part in parts.Skip(1))
        {
            node = node switch
            {
                JsonObject o => o[part],
                JsonArray a when int.TryParse(part, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null
            };
            if (node == null) return null;
        }
        return node;
    }
}
