using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LmChat;

public record ChatInfo(long Id, string Title, DateTime Updated)
{
    // Persona dieses Chats (wirkt zusätzlich zum System-Prompt); null = keine
    public PromptTemplate? Persona { get; init; }

    // Überschrift der Gruppe in der Chatliste
    public string DateGroup
    {
        get
        {
            var days = (DateTime.Today - Updated.Date).Days;
            return days <= 0 ? "Heute" : days == 1 ? "Gestern" : days < 7 ? "Letzte 7 Tage" : days < 30 ? "Letzte 30 Tage" : "Älter";
        }
    }
}

public record ChatMessage(string Role, string Content, byte[]? Image = null)
{
    public long Id { get; init; }
    public AnswerStats? Stats { get; init; }
    public string? Reasoning { get; init; }   // Gedankengang des Modells (Denkphase), nur zum Anzeigen
    public IReadOnlyList<AttachedDocument>? Documents { get; init; }   // angehängte Dateien (als Text)

    // Was das Modell von einer Nutzer-Nachricht bekommt: angehängte Dateien vor dem eigentlichen Text
    public string TextForModel => Documents is not { Count: > 0 } docs
        ? Content
        : string.Concat(docs.Select(d => $"<datei name=\"{d.Name}\">\n{d.Text}\n</datei>\n\n")) + Content;
}

// Angehängte Datei, bereits in Text umgewandelt (PDF, Word, Textdateien)
public sealed record AttachedDocument(string Name, string Text);

// Angaben unter einer Antwort: wer geantwortet hat, wie schnell und mit wie vielen Tokens
public sealed record AnswerStats(
    string Provider,
    string Model,
    string? Hoster,
    long? InputTokens,
    long? OutputTokens,
    double? TokensPerSecond,
    double Seconds,
    double? Cost = null,
    string? Tools = null);   // genutzte Plugins, z. B. "Wetter (Rügen)"

// Verbrauch eines Modells im gewählten Zeitraum. CostCount = Antworten mit vom Anbieter gemeldeten Kosten.
public sealed record UsageRow(ProviderKind Provider, string Model, long Requests, long InputTokens, long OutputTokens,
    double Cost, long CostCount);

// Speichert Chats, Nachrichten (inkl. Bilder) und Einstellungen in einer SQLite-Datei
public sealed class ChatStore
{
    private readonly string _connectionString;

    public ChatStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        Execute("""
            CREATE TABLE IF NOT EXISTS chats (
                id      INTEGER PRIMARY KEY,
                title   TEXT NOT NULL,
                updated TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS messages (
                id      INTEGER PRIMARY KEY,
                chat_id INTEGER NOT NULL,
                role    TEXT NOT NULL,
                content TEXT NOT NULL,
                image   BLOB,
                created TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_messages_chat ON messages(chat_id);
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL);
            """);

        // Ab Version 1.8: Statistik je Antwort (JSON); ältere Datenbanken bekommen die Spalte nachträglich
        if (Scalar("SELECT COUNT(*) FROM pragma_table_info('messages') WHERE name = 'meta'") is 0L)
            Execute("ALTER TABLE messages ADD COLUMN meta TEXT");

        // Ab Version 1.15: Gedankengang (Denkphase) je Antwort
        if (Scalar("SELECT COUNT(*) FROM pragma_table_info('messages') WHERE name = 'reasoning'") is 0L)
            Execute("ALTER TABLE messages ADD COLUMN reasoning TEXT");

        // Ab Version 1.16: angehängte Dateien je Nachricht (JSON) und Persona je Chat (JSON)
        if (Scalar("SELECT COUNT(*) FROM pragma_table_info('messages') WHERE name = 'documents'") is 0L)
            Execute("ALTER TABLE messages ADD COLUMN documents TEXT");
        if (Scalar("SELECT COUNT(*) FROM pragma_table_info('chats') WHERE name = 'persona'") is 0L)
            Execute("ALTER TABLE chats ADD COLUMN persona TEXT");

        // Ab Version 1.11: Verbrauch je Antwort, unabhängig von den Chats (bleibt beim Löschen eines Chats erhalten).
        // Beim ersten Start wird er aus den Statistiken der vorhandenen Antworten übernommen.
        if (Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'usage'") is 0L)
        {
            Execute("""
                CREATE TABLE usage (
                    id            INTEGER PRIMARY KEY,
                    created       TEXT NOT NULL,
                    provider      TEXT NOT NULL,
                    model         TEXT NOT NULL,
                    input_tokens  INTEGER,
                    output_tokens INTEGER,
                    cost          REAL);
                """);
            ImportUsageFromMessages();
        }
    }

    // ---------- Verbrauch ----------

    public void AddUsage(ProviderKind provider, AnswerStats s, DateTime? created = null) =>
        Execute("""
            INSERT INTO usage (created, provider, model, input_tokens, output_tokens, cost)
            VALUES ($d, $p, $m, $i, $o, $c)
            """,
            ("$d", FormatDate(created ?? DateTime.Now)), ("$p", provider.ToString()), ("$m", s.Model),
            ("$i", s.InputTokens), ("$o", s.OutputTokens), ("$c", s.Cost));

    // Summen je Anbieter und Modell ab einem Zeitpunkt (null = alles)
    public List<UsageRow> UsageByModel(DateTime? since)
    {
        using var conn = Open();
        using var cmd = Command(conn, """
            SELECT provider, model, COUNT(*), SUM(input_tokens), SUM(output_tokens), SUM(cost), COUNT(cost)
            FROM usage WHERE created >= $since
            GROUP BY provider, model ORDER BY COUNT(*) DESC
            """, ("$since", since == null ? "" : FormatDate(since.Value)));
        using var r = cmd.ExecuteReader();
        var list = new List<UsageRow>();
        while (r.Read())
        {
            if (!Enum.TryParse<ProviderKind>(r.GetString(0), out var kind)) continue;
            list.Add(new UsageRow(kind, r.GetString(1), r.GetInt64(2),
                r.IsDBNull(3) ? 0 : r.GetInt64(3), r.IsDBNull(4) ? 0 : r.GetInt64(4),
                r.IsDBNull(5) ? 0 : r.GetDouble(5), r.GetInt64(6)));
        }
        return list;
    }

    public void ClearUsage() => Execute("DELETE FROM usage");

    private void ImportUsageFromMessages()
    {
        var rows = new List<(string Created, string Meta)>();
        using (var conn = Open())
        using (var cmd = Command(conn, "SELECT created, meta FROM messages WHERE role = 'assistant' AND meta IS NOT NULL"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                rows.Add((r.GetString(0), r.GetString(1)));

        foreach (var (created, meta) in rows)
        {
            // In der Statistik steht der Anzeigename, ggf. mit Region: "OpenRouter (EU-Hoster)"
            if (ParseStats(meta) is not { } s) continue;
            var info = ProviderInfo.All.FirstOrDefault(p => s.Provider == p.Name || s.Provider.StartsWith(p.Name + " ("));
            if (info != null)
                AddUsage(info.Kind, s, ParseDate(created));
        }
    }

    public List<ChatInfo> ListChats()
    {
        using var conn = Open();
        using var cmd = Command(conn, "SELECT id, title, updated, persona FROM chats ORDER BY updated DESC");
        using var r = cmd.ExecuteReader();
        var list = new List<ChatInfo>();
        while (r.Read())
            list.Add(new ChatInfo(r.GetInt64(0), r.GetString(1), ParseDate(r.GetString(2)))
            {
                Persona = r.IsDBNull(3) ? null : ParseJson<PromptTemplate>(r.GetString(3))
            });
        return list;
    }

    public ChatInfo CreateChat(string title, PromptTemplate? persona = null)
    {
        var now = DateTime.Now;
        var id = (long)Scalar("INSERT INTO chats (title, updated, persona) VALUES ($t, $u, $p) RETURNING id",
            ("$t", title), ("$u", FormatDate(now)), ("$p", persona == null ? null : JsonSerializer.Serialize(persona)))!;
        return new ChatInfo(id, title, now) { Persona = persona };
    }

    public void SetPersona(long chatId, PromptTemplate? persona) =>
        Execute("UPDATE chats SET persona = $p WHERE id = $id",
            ("$p", persona == null ? null : JsonSerializer.Serialize(persona)), ("$id", chatId));

    public DateTime Touch(long chatId)
    {
        var now = DateTime.Now;
        Execute("UPDATE chats SET updated = $u WHERE id = $id", ("$u", FormatDate(now)), ("$id", chatId));
        return now;
    }

    public void DeleteChat(long chatId) =>
        Execute("DELETE FROM messages WHERE chat_id = $id; DELETE FROM chats WHERE id = $id", ("$id", chatId));

    public void RenameChat(long chatId, string title) =>
        Execute("UPDATE chats SET title = $t WHERE id = $id", ("$t", title), ("$id", chatId));

    public List<ChatMessage> LoadMessages(long chatId)
    {
        using var conn = Open();
        using var cmd = Command(conn,
            "SELECT id, role, content, image, meta, reasoning, documents FROM messages WHERE chat_id = $id ORDER BY id",
            ("$id", chatId));
        using var r = cmd.ExecuteReader();
        var list = new List<ChatMessage>();
        while (r.Read())
            list.Add(new ChatMessage(r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : (byte[])r[3])
            {
                Id = r.GetInt64(0),
                Stats = r.IsDBNull(4) ? null : ParseStats(r.GetString(4)),
                Reasoning = r.IsDBNull(5) ? null : r.GetString(5),
                Documents = r.IsDBNull(6) ? null : ParseJson<List<AttachedDocument>>(r.GetString(6))
            });
        return list;
    }

    public long AddMessage(long chatId, ChatMessage m) =>
        (long)Scalar("""
            INSERT INTO messages (chat_id, role, content, image, created, meta, reasoning, documents)
            VALUES ($c, $r, $t, $i, $d, $m, $g, $f) RETURNING id
            """,
            ("$c", chatId), ("$r", m.Role), ("$t", m.Content), ("$i", m.Image), ("$d", FormatDate(DateTime.Now)),
            ("$m", m.Stats == null ? null : JsonSerializer.Serialize(m.Stats)), ("$g", m.Reasoning),
            ("$f", m.Documents is { Count: > 0 } docs ? JsonSerializer.Serialize(docs) : null))!;

    private static T? ParseJson<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Nachricht und alles danach im selben Chat löschen (Bearbeiten bzw. neu erzeugen)
    public void DeleteMessagesFrom(long chatId, long messageId) =>
        Execute("DELETE FROM messages WHERE chat_id = $c AND id >= $id", ("$c", chatId), ("$id", messageId));

    private static AnswerStats? ParseStats(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AnswerStats>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void DeleteMessage(long id) => Execute("DELETE FROM messages WHERE id = $id", ("$id", id));

    public string? GetSetting(string key) => Scalar("SELECT value FROM settings WHERE key = $k", ("$k", key)) as string;

    public void SetSetting(string key, string value) =>
        Execute("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v",
            ("$k", key), ("$v", value));

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static SqliteCommand Command(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private void Execute(string sql, params (string, object?)[] args)
    {
        using var conn = Open();
        using var cmd = Command(conn, sql, args);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string, object?)[] args)
    {
        using var conn = Open();
        using var cmd = Command(conn, sql, args);
        return cmd.ExecuteScalar();
    }

    private static string FormatDate(DateTime d) => d.ToString("o", CultureInfo.InvariantCulture);
    private static DateTime ParseDate(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
