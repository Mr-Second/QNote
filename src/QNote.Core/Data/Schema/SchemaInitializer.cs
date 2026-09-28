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
    public const long CurrentVersion = 6;

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
        if (version < 4)
            MigrateV3ToV4(conn);
        if (version < 5)
            MigrateV4ToV5(conn);
        if (version < 6)
            MigrateV5ToV6(conn);
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

    // v4: categories gain a Color column (#RRGGBB) and the built-in set is seeded
    // on first run (全部 is a synthetic UI item and never lives in the DB). The
    // CREATE TABLE IF NOT EXISTS covers legacy test/dev databases that have a
    // categories-less v1 layout; seeding is guarded on an empty table so it is
    // idempotent and never duplicates on later launches.
    private static void MigrateV3ToV4(SqliteConnection conn)
    {
        Execute(conn, """
            CREATE TABLE IF NOT EXISTS categories (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Name      TEXT    NOT NULL UNIQUE,
                IconKey   TEXT    NOT NULL DEFAULT '',
                SortOrder INTEGER NOT NULL DEFAULT 0
            );
            """);

        if (!ColumnExists(conn, "categories", "Color"))
            Execute(conn, "ALTER TABLE categories ADD COLUMN Color TEXT NOT NULL DEFAULT '';");

        // Per-name guard: seeds each built-in independently (idempotent, and a
        // user-deleted custom set never blocks the remaining built-ins).
        Execute(conn, """
            INSERT INTO categories (Name, IconKey, Color, SortOrder)
            SELECT '工作', 'E821', '#3B82F6', 0 WHERE NOT EXISTS (SELECT 1 FROM categories WHERE Name = '工作');
            """);
        Execute(conn, """
            INSERT INTO categories (Name, IconKey, Color, SortOrder)
            SELECT '生活', 'E80F', '#22C55E', 1 WHERE NOT EXISTS (SELECT 1 FROM categories WHERE Name = '生活');
            """);
        Execute(conn, """
            INSERT INTO categories (Name, IconKey, Color, SortOrder)
            SELECT '重要', 'E734', '#EF4444', 2 WHERE NOT EXISTS (SELECT 1 FROM categories WHERE Name = '重要');
            """);
    }

    // v5 (image task): inline images put a huge RTF blob in notes.Content, so Content
    // is rebuilt to be the LAST physical column — any column after an overflowing one
    // forces SQLite to walk that row's overflow page chain, which made the note-list
    // query ~1000x slower (spike sqlite_colorder_bench). Ids are preserved (Id is
    // INTEGER PRIMARY KEY = rowid, so notes_fts' contentless rowids stay valid); the
    // FTS shadow is left untouched. A new note_images table links notes to their
    // content-addressed originals on disk (D2), cascading on note delete.
    private static void MigrateV4ToV5(SqliteConnection conn)
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
        if (!ColumnExists(conn, "notes", "PlainText"))
            Execute(conn, "ALTER TABLE notes ADD COLUMN PlainText TEXT NOT NULL DEFAULT '';");

        // Rebuild notes with Content moved to the last column. DROP+ren+CREATE keeps
        // the rowids (Id preserved explicitly in the SELECT) so notes_fts rowids stay valid.
        Execute(conn, "ALTER TABLE notes RENAME TO notes_old;");
        Execute(conn, """
            CREATE TABLE notes (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Uuid      TEXT    NOT NULL UNIQUE,
                Title     TEXT    NOT NULL DEFAULT '',
                Category  TEXT    NOT NULL DEFAULT '',
                CreatedAt TEXT    NOT NULL,
                UpdatedAt TEXT    NOT NULL,
                PlainText TEXT    NOT NULL DEFAULT '',
                Content   TEXT    NOT NULL DEFAULT ''
            );
            """);
        Execute(conn, """
            INSERT INTO notes (Id, Uuid, Title, Category, CreatedAt, UpdatedAt, PlainText, Content)
            SELECT Id, Uuid, Title, Category, CreatedAt, UpdatedAt, PlainText, Content FROM notes_old;
            """);
        Execute(conn, "DROP TABLE notes_old;");

        Execute(conn, """
            CREATE TABLE IF NOT EXISTS note_images (
                note_id    INTEGER NOT NULL,
                sha256     TEXT    NOT NULL,
                ext        TEXT    NOT NULL DEFAULT '',
                byte_size  INTEGER NOT NULL DEFAULT 0,
                width      INTEGER NOT NULL DEFAULT 0,
                height     INTEGER NOT NULL DEFAULT 0,
                created_at TEXT    NOT NULL,
                PRIMARY KEY (note_id, sha256),
                FOREIGN KEY (note_id) REFERENCES notes(Id) ON DELETE CASCADE
            );
            """);
        Execute(conn, "CREATE INDEX IF NOT EXISTS idx_note_images_sha256 ON note_images (sha256);");
    }

    // v6 (Markdown storage B2): notes.Content semantics flip from RTF to Markdown —
    // NO notes DDL and no data migration (the app was never released; old RTF rows
    // simply parse as their literal text). The compressed display copy that used to
    // live inside the RTF gets a persistent home instead: note_images gains a
    // display_bytes BLOB, resolved back into {\pict} bytes when the editor renders
    // ![alt](qnote-img:<sha>) references. Nullable: legacy rows and adopted links
    // without a copy degrade to the alt-text placeholder.
    private static void MigrateV5ToV6(SqliteConnection conn)
    {
        if (!ColumnExists(conn, "note_images", "display_bytes"))
            Execute(conn, "ALTER TABLE note_images ADD COLUMN display_bytes BLOB;");
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
