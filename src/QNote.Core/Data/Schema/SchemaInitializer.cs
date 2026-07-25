using Microsoft.Data.Sqlite;

namespace QNote.Data.Schema;

/// <summary>
/// Creates the greenfield <c>qnote.db</c> schema on first run and stamps
/// <c>PRAGMA user_version</c>. Forward-only: future schema changes add an ordered
/// migration step and bump <see cref="CurrentVersion"/>.
/// </summary>
public sealed class SchemaInitializer
{
    /// <summary>Current schema version. Bump when adding a migration step.</summary>
    public const long CurrentVersion = 3;

    private readonly DbConnectionFactory _factory;

    public SchemaInitializer(DbConnectionFactory factory) => _factory = factory;

    /// <summary>Ensure the database exists and is migrated to <see cref="CurrentVersion"/>.</summary>
    public void EnsureCreated()
    {
        using var conn = _factory.OpenWrite();

        Execute(conn, "PRAGMA journal_mode=WAL;");

        var version = GetUserVersion(conn);
        if (version >= CurrentVersion)
            return;

        using var tx = conn.BeginTransaction();
        if (version < 1)
            CreateV1(conn);
        if (version < 2)
            MigrateV1ToV2(conn);
        if (version < 3)
            MigrateV2ToV3(conn);
        SetUserVersion(conn, CurrentVersion);
        tx.Commit();
    }

    private static void CreateV1(SqliteConnection conn)
    {
        Execute(conn, """
            CREATE TABLE IF NOT EXISTS notes (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Uuid      TEXT    NOT NULL UNIQUE,
                Title     TEXT    NOT NULL DEFAULT '',
                Content   TEXT    NOT NULL DEFAULT '',
                Category  TEXT    NOT NULL DEFAULT '',
                CreatedAt TEXT    NOT NULL,
                UpdatedAt TEXT    NOT NULL
            );
            """);

        Execute(conn, """
            CREATE TABLE IF NOT EXISTS categories (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Name      TEXT    NOT NULL UNIQUE,
                IconKey   TEXT    NOT NULL DEFAULT '',
                SortOrder INTEGER NOT NULL DEFAULT 0
            );
            """);

        Execute(conn, """
            CREATE TABLE IF NOT EXISTS settings (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """);

        // Full-text search shadow (decision ③): app-side overlapping-bigram text is
        // fed into this contentless FTS5 table; queries weight title x10 over body
        // via bm25(notes_fts, 10.0, 1.0). `contentless_delete=1` (SQLite 3.43+)
        // lifts the contentless no-DELETE restriction so the repository can maintain
        // the index row-by-row inside each notes write transaction; rebuild wiring
        // lives in SearchService.
        Execute(conn, """
            CREATE VIRTUAL TABLE IF NOT EXISTS notes_fts USING fts5(
                title,
                body,
                content='',
                contentless_delete=1,
                tokenize='unicode61'
            );
            """);
    }

    // v2: notes gain a PlainText column — the editor's plain-text projection of the
    // RTF Content, used for list previews and as the future FTS corpus.
    private static void MigrateV1ToV2(SqliteConnection conn) =>
        Execute(conn, "ALTER TABLE notes ADD COLUMN PlainText TEXT NOT NULL DEFAULT '';");

    // v3: recreate notes_fts with contentless_delete=1 so DELETE works on the
    // contentless shadow (required by the FTS write path inside NoteRepository
    // transactions). Safe to drop: notes_fts holds no user data of its own; the
    // startup reconcile repopulates it on next launch (SearchService.ReconcileAsync).
    private static void MigrateV2ToV3(SqliteConnection conn) =>
        Execute(conn, """
            DROP TABLE IF EXISTS notes_fts;
            CREATE VIRTUAL TABLE notes_fts USING fts5(
                title,
                body,
                content='',
                contentless_delete=1,
                tokenize='unicode61'
            );
            """);

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long GetUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static void SetUserVersion(SqliteConnection conn, long version)
    {
        using var cmd = conn.CreateCommand();
        // PRAGMA values cannot be parameterized; `version` is an internal constant,
        // never user input, so this is not an injection surface.
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }
}
