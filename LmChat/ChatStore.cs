using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace LmChat;

public record ChatInfo(long Id, string Title, DateTime Updated);

public record ChatMessage(string Role, string Content, byte[]? Image = null)
{
    public long Id { get; init; }
}

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
        using var cmd = Command(conn, "SELECT id, role, content, image FROM messages WHERE chat_id = $id ORDER BY id",
            ("$id", chatId));
        using var r = cmd.ExecuteReader();
        var list = new List<ChatMessage>();
        while (r.Read())
            list.Add(new ChatMessage(r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : (byte[])r[3]) { Id = r.GetInt64(0) });
        return list;
    }

    public long AddMessage(long chatId, ChatMessage m) =>
        (long)Scalar("""
            INSERT INTO messages (chat_id, role, content, image, created)
            VALUES ($c, $r, $t, $i, $d) RETURNING id
            """,
            ("$c", chatId), ("$r", m.Role), ("$t", m.Content), ("$i", m.Image), ("$d", FormatDate(DateTime.Now)))!;

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
