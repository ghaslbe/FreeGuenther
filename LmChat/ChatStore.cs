using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LmChat;

public record ChatInfo(long Id, string Title, DateTime Updated)
{
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
}

// Angaben unter einer Antwort: wer geantwortet hat, wie schnell und mit wie vielen Tokens
public sealed record AnswerStats(
    string Provider,
    string Model,
    string? Hoster,
    long? InputTokens,
    long? OutputTokens,
    double? TokensPerSecond,
    double Seconds);

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
    }

    public List<ChatInfo> ListChats()
    {
        using var conn = Open();
        using var cmd = Command(conn, "SELECT id, title, updated FROM chats ORDER BY updated DESC");
        using var r = cmd.ExecuteReader();
        var list = new List<ChatInfo>();
        while (r.Read())
            list.Add(new ChatInfo(r.GetInt64(0), r.GetString(1), ParseDate(r.GetString(2))));
        return list;
    }

    public ChatInfo CreateChat(string title)
    {
        var now = DateTime.Now;
        var id = (long)Scalar("INSERT INTO chats (title, updated) VALUES ($t, $u) RETURNING id",
            ("$t", title), ("$u", FormatDate(now)))!;
        return new ChatInfo(id, title, now);
    }

    public DateTime Touch(long chatId)
    {
        var now = DateTime.Now;
        Execute("UPDATE chats SET updated = $u WHERE id = $id", ("$u", FormatDate(now)), ("$id", chatId));
        return now;
    }

    public void DeleteChat(long chatId) =>
        Execute("DELETE FROM messages WHERE chat_id = $id; DELETE FROM chats WHERE id = $id", ("$id", chatId));

    public List<ChatMessage> LoadMessages(long chatId)
    {
        using var conn = Open();
        using var cmd = Command(conn, "SELECT id, role, content, image, meta FROM messages WHERE chat_id = $id ORDER BY id",
            ("$id", chatId));
        using var r = cmd.ExecuteReader();
        var list = new List<ChatMessage>();
        while (r.Read())
            list.Add(new ChatMessage(r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : (byte[])r[3])
            {
                Id = r.GetInt64(0),
                Stats = r.IsDBNull(4) ? null : ParseStats(r.GetString(4))
            });
        return list;
    }

    public long AddMessage(long chatId, ChatMessage m) =>
        (long)Scalar("""
            INSERT INTO messages (chat_id, role, content, image, created, meta)
            VALUES ($c, $r, $t, $i, $d, $m) RETURNING id
            """,
            ("$c", chatId), ("$r", m.Role), ("$t", m.Content), ("$i", m.Image), ("$d", FormatDate(DateTime.Now)),
            ("$m", m.Stats == null ? null : JsonSerializer.Serialize(m.Stats)))!;

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
