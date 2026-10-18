using System.Data.Common;
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
/// Since schema v8 this reader accepts the v8 (or newer) shape ONLY, per the
/// forward-only compatibility decision: notes/categories timestamps are INTEGER
/// epoch-ms here, and a pre-v8 archive (TEXT ISO) is rejected loudly rather than
/// type-probed. Overwrite restore still works for old archives (the DB file is
/// swapped, then migrated forward by <c>SchemaInitializer</c>) — only the
/// row-by-row merge path needs this reader.
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

    /// <summary>Uuid → UpdatedAt for every LIVE note in the staged DB (conflict analysis).</summary>
    public static IReadOnlyDictionary<string, DateTimeOffset> ReadUuidMap(string dbPath)
    {
        using var conn = OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Uuid, UpdatedAt FROM notes WHERE DeletedAt IS NULL;";

        var map = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            map[reader.GetString(0)] = ReadMs(reader, 1, "notes.UpdatedAt");
        return map;
    }

    /// <summary>All LIVE notes as full records (incl. Content). Requires schema v8.</summary>
    public static List<Note> ReadNotes(string dbPath)
    {
        using var conn = OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT Id, Uuid, Title, Category, CreatedAt, UpdatedAt, DeletedAt, PlainText, Content " +
            "FROM notes WHERE DeletedAt IS NULL ORDER BY Id;";

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
                CreatedAt = ReadMs(reader, 4, "notes.CreatedAt"),
                UpdatedAt = ReadMs(reader, 5, "notes.UpdatedAt"),
                PlainText = reader.GetString(7),
                Content = reader.GetString(8),
            });
        }
        return list;
    }

    /// <summary>All LIVE categories (v8 shape: timestamps + uuid are present).</summary>
    public static List<Category> ReadCategories(string dbPath)
    {
        using var conn = OpenReadOnly(dbPath);
        if (!TableExists(conn, "categories"))
            return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT Id, Name, IconKey, Color, SortOrder, Uuid, CreatedAt, UpdatedAt " +
            "FROM categories WHERE DeletedAt IS NULL ORDER BY SortOrder;";

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
                Uuid = reader.GetString(5),
                CreatedAt = ReadMs(reader, 6, "categories.CreatedAt"),
                UpdatedAt = ReadMs(reader, 7, "categories.UpdatedAt"),
            });
        }
        return list;
    }

    /// <summary>
    /// <c>note_images</c> rows grouped by note id (empty when the staged DB predates schema v5).
    /// </summary>
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

    /// <summary>
    /// All todo lists (schema v7+). Empty when the staged DB predates the todo
    /// domain — a v6 archive restores with the todo domain simply absent (graceful).
    /// </summary>
    public static List<TodoList> ReadTodoLists(string dbPath)
    {
        using var conn = OpenReadOnly(dbPath);
        if (!TableExists(conn, "todo_lists"))
            return [];

        // The list-options columns (2026-10-16) are probed: early v7 dev
        // archives predate them — a missing column reads as its default
        // (same idiom as CatchUpFiredAt below).
        var hasOptions = ColumnExists(conn, "todo_lists", "SortMode");
        using var cmd = conn.CreateCommand();
        // DeletedAt is NOT probed: it belongs to v8, which the caller already
        // gates on (ValidateStaging rejects pre-v8 merge). The v7 option columns
        // keep their probe — early v7 dev archives predate them.
        cmd.CommandText = "SELECT Id, Uuid, Name, Color, SortOrder, CreatedAt, UpdatedAt" +
            (hasOptions ? ", Theme, HideCompleted, SortMode " : " ") +
            "FROM todo_lists WHERE DeletedAt IS NULL ORDER BY SortOrder;";

        var list = new List<TodoList>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new TodoList
            {
                Id = reader.GetInt64(0),
                Uuid = reader.GetString(1),
                Name = reader.GetString(2),
                Color = reader.GetString(3),
                SortOrder = reader.GetInt32(4),
                CreatedAt = FromDbMs(reader.GetInt64(5)),
                UpdatedAt = FromDbMs(reader.GetInt64(6)),
                Theme = hasOptions ? reader.GetInt32(7) : 0,
                HideCompleted = hasOptions && reader.GetInt64(8) != 0,
                SortMode = hasOptions ? reader.GetInt32(9) : 0,
            });
        }
        return list;
    }

    /// <summary>
    /// All todo tasks as full records (schema v7+), including the catch-up
    /// reminder marker. Empty when the staged DB predates the todo domain. The
    /// <c>CatchUpFiredAt</c> column is probed: early v7 builds created the todo
    /// tables without it (the stage-③ heal predates any released archive, but
    /// dev archives exist) — a missing column reads as "never fired".
    /// </summary>
    public static List<TodoTask> ReadTodoTasks(string dbPath)
    {
        using var conn = OpenReadOnly(dbPath);
        if (!TableExists(conn, "todo_tasks"))
            return [];

        var hasCatchUp = ColumnExists(conn, "todo_tasks", "CatchUpFiredAt");
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT Id, Uuid, ListUuid, Title, Notes, DueAt, IsAllDay, ReminderOn, IsImportant, CompletedAt, " +
            (hasCatchUp ? "CatchUpFiredAt, " : "NULL AS CatchUpFiredAt, ") +
            "CreatedAt, UpdatedAt FROM todo_tasks WHERE DeletedAt IS NULL ORDER BY Id;";

        var list = new List<TodoTask>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new TodoTask
            {
                Id = reader.GetInt64(0),
                Uuid = reader.GetString(1),
                ListUuid = reader.GetString(2),
                Title = reader.GetString(3),
                Notes = reader.GetString(4),
                DueAt = reader.IsDBNull(5) ? null : FromDbMs(reader.GetInt64(5)),
                IsAllDay = reader.GetInt64(6) != 0,
                ReminderOn = reader.GetInt64(7) != 0,
                IsImportant = reader.GetInt64(8) != 0,
                CompletedAt = reader.IsDBNull(9) ? null : FromDbMs(reader.GetInt64(9)),
                CatchUpFiredAt = reader.IsDBNull(10) ? null : FromDbMs(reader.GetInt64(10)),
                CreatedAt = FromDbMs(reader.GetInt64(11)),
                UpdatedAt = FromDbMs(reader.GetInt64(12)),
            });
        }
        return list;
    }

    /// <summary>
    /// All todo steps (schema v7+), keyed by the STAGED task id — the merge
    /// pipeline remaps to the live task id. Empty when the staged DB predates
    /// the todo domain or its stage-⑤ tables (guarded probe, same idiom as
    /// <see cref="ReadTodoLists"/>).
    /// </summary>
    public static List<TodoStep> ReadTodoSteps(string dbPath)
    {
        using var conn = OpenReadOnly(dbPath);
        if (!TableExists(conn, "todo_steps"))
            return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT Id, Uuid, TaskId, Title, CompletedAt, SortOrder, CreatedAt, UpdatedAt " +
            "FROM todo_steps WHERE DeletedAt IS NULL ORDER BY Id;";

        var list = new List<TodoStep>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new TodoStep
            {
                Id = reader.GetInt64(0),
                Uuid = reader.GetString(1),
                TaskId = reader.GetInt64(2),
                Title = reader.GetString(3),
                CompletedAt = reader.IsDBNull(4) ? null : FromDbMs(reader.GetInt64(4)),
                SortOrder = reader.GetInt32(5),
                CreatedAt = FromDbMs(reader.GetInt64(6)),
                UpdatedAt = FromDbMs(reader.GetInt64(7)),
            });
        }
        return list;
    }

    /// <summary>
    /// All todo attachments (schema v7+), keyed by the STAGED task id — same
    /// remap contract as <see cref="ReadTodoSteps"/>. The bytes themselves live
    /// in the archive's <c>attachments/</c> entries, not the DB.
    /// </summary>
    public static List<TodoAttachment> ReadTodoAttachments(string dbPath)
    {
        using var conn = OpenReadOnly(dbPath);
        if (!TableExists(conn, "todo_attachments"))
            return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT Id, Uuid, TaskId, FileName, Sha, ByteSize, CreatedAt, UpdatedAt " +
            "FROM todo_attachments WHERE DeletedAt IS NULL ORDER BY Id;";

        var list = new List<TodoAttachment>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new TodoAttachment
            {
                Id = reader.GetInt64(0),
                Uuid = reader.GetString(1),
                TaskId = reader.GetInt64(2),
                FileName = reader.GetString(3),
                Sha = reader.GetString(4),
                ByteSize = reader.GetInt64(5),
                CreatedAt = FromDbMs(reader.GetInt64(6)),
                UpdatedAt = FromDbMs(reader.GetInt64(7)),
            });
        }
        return list;
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

/// <summary>
/// Every synced timestamp is a millisecond-epoch integer since v8 (notes,
/// categories AND the todo domain) — one shape, no type probe.
/// A TEXT value here means a pre-v8 archive slipped past the version gate; that
/// is reported as "version too old" rather than being coerced (decision 9).
/// </summary>
private static DateTimeOffset FromDbMs(long ms) =>
    DateTimeOffset.FromUnixTimeMilliseconds(ms);

private static DateTimeOffset ReadMs(DbDataReader reader, int ordinal, string column) =>
    reader.GetFieldType(ordinal) == typeof(long)
        ? FromDbMs(reader.GetInt64(ordinal))
        : throw new InvalidOperationException(
            $"Backup column '{column}' holds a non-integer timestamp ({reader.GetFieldType(ordinal).Name}); " +
            "the archive predates schema v8 and cannot be merged — use overwrite restore instead.");
}
