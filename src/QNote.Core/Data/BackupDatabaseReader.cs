using System.Globalization;
using Microsoft.Data.Sqlite;
using QNote.Models;

namespace QNote.Data;

/// <summary>
/// Read-only projection of a STAGED backup database (a <c>qnote.db</c> extracted from
/// a <c>.qns</c> archive into a temp dir). Used by the restore pipeline for conflict
/// analysis and per-note import. This is not the live store, so it deliberately does
/// not go through <see cref="DbConnectionFactory"/> (which is bound to the live path)
/// and never writes — the merge/overwrite logic lives in <c>BackupService</c>.
/// </summary>
internal static class BackupDatabaseReader
{
    private const DateTimeStyles UtcStyles =
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    /// <summary>The staged DB's <c>PRAGMA user_version</c> (schema version).</summary>
    public static long GetUserVersion(string dbPath)
    {
        using var conn = OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>Uuid → UpdatedAt for every note in the staged DB (conflict analysis).</summary>
    public static IReadOnlyDictionary<string, DateTimeOffset> ReadUuidMap(string dbPath)
    {
        using var conn = OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Uuid, UpdatedAt FROM notes;";

        var map = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            map[reader.GetString(0)] = ParseUtc(reader.GetString(1));
        return map;
    }

    /// <summary>All notes as full records (incl. Content). Requires schema ≥ v2 (PlainText).</summary>
    public static List<Note> ReadNotes(string dbPath)
    {
        using var conn = OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT Id, Uuid, Title, Category, CreatedAt, UpdatedAt, PlainText, Content FROM notes ORDER BY Id;";

        var list = new List<Note>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Note
            {
                Id = reader.GetInt64(0),
                Uuid = reader.GetString(1),
                Title = reader.GetString(2),
                Category = reader.GetString(3),
                CreatedAt = ParseUtc(reader.GetString(4)),
                UpdatedAt = ParseUtc(reader.GetString(5)),
                PlainText = reader.GetString(6),
                Content = reader.GetString(7),
            });
        }
        return list;
    }

    /// <summary>All categories (v4+ shape; IconKey/Color tolerated as missing on older schemas).</summary>
    public static List<Category> ReadCategories(string dbPath)
    {
        using var conn = OpenReadOnly(dbPath);
        if (!TableExists(conn, "categories"))
            return [];

        var hasColor = ColumnExists(conn, "categories", "Color");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = hasColor
            ? "SELECT Id, Name, IconKey, Color, SortOrder FROM categories ORDER BY SortOrder;"
            : "SELECT Id, Name, IconKey, '' AS Color, SortOrder FROM categories ORDER BY SortOrder;";

        var list = new List<Category>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Category
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                IconKey = reader.GetString(2),
                Color = reader.GetString(3),
                SortOrder = reader.GetInt32(4),
            });
        }
        return list;
    }

    /// <summary><c>note_images</c> rows grouped by note id (empty when the staged DB predates schema v5).</summary>
    public static Dictionary<long, List<NoteImage>> ReadNoteImagesByNote(string dbPath)
    {
        var map = new Dictionary<long, List<NoteImage>>();
        using var conn = OpenReadOnly(dbPath);
        if (!TableExists(conn, "note_images"))
            return map;

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT note_id, sha256, ext, byte_size, width, height, created_at FROM note_images ORDER BY note_id;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var noteId = reader.GetInt64(0);
            if (!map.TryGetValue(noteId, out var rows))
                map[noteId] = rows = [];
            rows.Add(new NoteImage
            {
                NoteId = noteId,
                Sha256 = reader.GetString(1),
                Ext = reader.GetString(2),
                ByteSize = reader.GetInt64(3),
                Width = reader.GetInt32(4),
                Height = reader.GetInt32(5),
                CreatedAt = ParseUtc(reader.GetString(6)),
            });
        }
        return map;
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false, // staged temp file — never let a pool pin its handle
        }.ToString());
        conn.Open();
        return conn;
    }

    private static bool TableExists(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        cmd.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        // PRAGMA table_info cannot be parameterized; names here are internal constants.
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, UtcStyles);
}
