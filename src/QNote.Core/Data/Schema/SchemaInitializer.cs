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
    public const long CurrentVersion = 7;

    /// <summary>
    /// Well-known uuid of the seeded 工作 list. The built-in lists carry STABLE
    /// identity across installs (unlike user lists, whose uuids are generated at
    /// creation): backup merge and the future sync engine key list identity on
    /// uuid, so per-install random ids would duplicate the built-ins on every
    /// cross-install restore (stage ④ finding). Same shape as other uuids
    /// (32-hex) but recognizably reserved.
    /// </summary>
    public const string WorkListUuid = "00000000000000000000000000000001";

    /// <summary>Well-known uuid of the seeded 生活 list (see <see cref="WorkListUuid"/>).</summary>
    public const string LifeListUuid = "00000000000000000000000000000002";

    private readonly DbConnectionFactory _factory;

    public SchemaInitializer(DbConnectionFactory factory) => _factory = factory;

    /// <summary>Ensure the database exists and is migrated to <see cref="CurrentVersion"/>.</summary>
    public void EnsureCreated()
    {
        using var conn = _factory.OpenWrite();

        Execute(conn, "PRAGMA journal_mode=WAL;");

        var version = GetUserVersion(conn);
        if (version < CurrentVersion)
        {
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
            if (version < 7)
                MigrateV6ToV7(conn);
            SetUserVersion(conn, CurrentVersion);
            tx.Commit();
        }

        // Stage ③ heal (task 10-04): the catch-up fired marker joined the still
        // UNCOMMITTED v7 shape (schema v7 shipped nothing — stage ① lives in the
        // working tree only), so this is NOT a v8 migration. Databases that were
        // already stamped user_version = 7 by the stage ① builds (dev machines)
        // never re-enter the ladder above — the guarded probe below adds the
        // column on every launch until present. Fresh databases get it natively
        // from the CREATE TABLE inside MigrateV6ToV7.
        if (!ColumnExists(conn, "todo_tasks", "CatchUpFiredAt"))
            Execute(conn, "ALTER TABLE todo_tasks ADD COLUMN CatchUpFiredAt INTEGER;");

        // Stage ④ heal (task 10-04): the built-in lists' identity must be STABLE
        // across installs — merge LWW keys on uuid, so the per-install random ids
        // the stage ① seed wrote would duplicate the built-ins on every
        // cross-install restore. Same launch-time guarded-probe pattern as above:
        // normalize until present, never bumping user_version. A user-DELETED
        // builtin stays deleted (the name lookup finds nothing to rewrite); a
        // user-RENAMED one degrades to ordinary user data and is left alone.
        NormalizeBuiltinListIdentity(conn);

        // Stage ⑤ heal (task 10-04): steps / attachments / RecurrenceRule joined
        // the still-UNCOMMITTED v7 shape — same guarded-probe pattern. Databases
        // stamped user_version = 7 by earlier stage builds get the new tables and
        // column on every launch until present; fresh databases get them natively
        // from the CREATE TABLE statements inside MigrateV6ToV7.
        if (!ColumnExists(conn, "todo_tasks", "RecurrenceRule"))
            Execute(conn, "ALTER TABLE todo_tasks ADD COLUMN RecurrenceRule TEXT;");
        EnsureTodoStepsTable(conn);
        EnsureTodoAttachmentsTable(conn);

        // List-options menu heal (2026-10-16): todo_lists.Theme / HideCompleted /
        // SortMode ride the same unshipped v7 — guarded probes only.
        if (!ColumnExists(conn, "todo_lists", "Theme"))
            Execute(conn, "ALTER TABLE todo_lists ADD COLUMN Theme INTEGER NOT NULL DEFAULT 0;");
        if (!ColumnExists(conn, "todo_lists", "HideCompleted"))
            Execute(conn, "ALTER TABLE todo_lists ADD COLUMN HideCompleted INTEGER NOT NULL DEFAULT 0;");
        if (!ColumnExists(conn, "todo_lists", "SortMode"))
            Execute(conn, "ALTER TABLE todo_lists ADD COLUMN SortMode INTEGER NOT NULL DEFAULT 0;");
    }

    private static void EnsureTodoStepsTable(SqliteConnection conn)
    {
        Execute(conn, """
            CREATE TABLE IF NOT EXISTS todo_steps (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                Uuid        TEXT    NOT NULL UNIQUE,
                TaskId      INTEGER NOT NULL,
                Title       TEXT    NOT NULL,
                CompletedAt INTEGER,
                SortOrder   INTEGER NOT NULL DEFAULT 0,
                CreatedAt   INTEGER NOT NULL,
                UpdatedAt   INTEGER NOT NULL
            );
            """);
        Execute(conn, "CREATE INDEX IF NOT EXISTS idx_todo_steps_task ON todo_steps (TaskId);");
    }

    private static void EnsureTodoAttachmentsTable(SqliteConnection conn)
    {
        Execute(conn, """
            CREATE TABLE IF NOT EXISTS todo_attachments (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Uuid      TEXT    NOT NULL UNIQUE,
                TaskId    INTEGER NOT NULL,
                FileName  TEXT    NOT NULL,
                Sha       TEXT    NOT NULL,
                ByteSize  INTEGER NOT NULL,
                CreatedAt INTEGER NOT NULL,
                UpdatedAt INTEGER NOT NULL
            );
            """);
        Execute(conn, "CREATE INDEX IF NOT EXISTS idx_todo_attachments_task ON todo_attachments (TaskId);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS idx_todo_attachments_sha ON todo_attachments (Sha);");
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

    // v7 (todo task, 10-04): the TODO domain lands — todo_lists + todo_tasks. Their
    // timestamps are MILLISECOND-EPOCH integers (sync contract, PRD Decision 6) —
    // unlike notes, which keep TEXT ISO timestamps. categories gains a Uuid column
    // (stable identity for the future sync engine); SQLite cannot ADD COLUMN with a
    // UNIQUE constraint, so uniqueness is a writer discipline (CreateCategoryAsync
    // generates identity at the single choke point) rather than a DB constraint.
    // todo_lists seeds the default 工作/生活 lists (data identity, zh — same policy
    // as the v4 built-in note categories) with PER-ROW NOT EXISTS guards.
    private static void MigrateV6ToV7(SqliteConnection conn)
    {
        // Legacy categories-less layouts (see the v4 note): heal the table first so
        // the Uuid column below can be added on every database. The shape matches
        // the ALTER path's column order exactly.
        Execute(conn, """
            CREATE TABLE IF NOT EXISTS categories (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Name      TEXT    NOT NULL UNIQUE,
                IconKey   TEXT    NOT NULL DEFAULT '',
                SortOrder INTEGER NOT NULL DEFAULT 0,
                Color     TEXT    NOT NULL DEFAULT '',
                Uuid      TEXT    NOT NULL DEFAULT ''
            );
            """);

        if (!ColumnExists(conn, "categories", "Uuid"))
            Execute(conn, "ALTER TABLE categories ADD COLUMN Uuid TEXT NOT NULL DEFAULT '';");

        // Backfill: pre-v7 rows get generated identity (same 32-hex shape as
        // Guid.NewGuid().ToString("N")). Re-runs only touch still-empty rows, so
        // identity is stable across repeated migrations.
        Execute(conn, "UPDATE categories SET Uuid = lower(hex(randomblob(16))) WHERE Uuid = '';");

        Execute(conn, """
            CREATE TABLE IF NOT EXISTS todo_lists (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Uuid      TEXT    NOT NULL UNIQUE,
                Name      TEXT    NOT NULL,
                Color     TEXT    NOT NULL DEFAULT '',
                SortOrder INTEGER NOT NULL DEFAULT 0,
                CreatedAt INTEGER NOT NULL,
                UpdatedAt INTEGER NOT NULL,
                Theme        INTEGER NOT NULL DEFAULT 0,
                HideCompleted INTEGER NOT NULL DEFAULT 0,
                SortMode     INTEGER NOT NULL DEFAULT 0
            );
            """);
        // List-options menu columns (2026-10-16): per-list theme / hide-completed
        // / sort mode. All ride inside the still-unshipped v7 — the guarded
        // ALTERs only heal local dev databases (same pattern as RecurrenceRule).
        if (!ColumnExists(conn, "todo_lists", "Theme"))
            Execute(conn, "ALTER TABLE todo_lists ADD COLUMN Theme INTEGER NOT NULL DEFAULT 0;");
        if (!ColumnExists(conn, "todo_lists", "HideCompleted"))
            Execute(conn, "ALTER TABLE todo_lists ADD COLUMN HideCompleted INTEGER NOT NULL DEFAULT 0;");
        if (!ColumnExists(conn, "todo_lists", "SortMode"))
            Execute(conn, "ALTER TABLE todo_lists ADD COLUMN SortMode INTEGER NOT NULL DEFAULT 0;");

        // Tasks link to their list by Uuid (string link, like notes.Category — no FK
        // by schema convention). CompletedAt NULL = incomplete, non-NULL = the
        // completion instant. CatchUpFiredAt records when a launch catch-up
        // re-fired this task's OS-dropped reminder (once per due crossing — the
        // catch-up query skips tasks whose marker is not below their due); it is
        // reminder bookkeeping, deliberately never stamped into UpdatedAt (sync
        // LWW must not see it). RecurrenceRule (PRD Decision 10) is null for a
        // one-off task, else a TodoRecurrence code — completing a ruled task
        // spawns the next occurrence.
        Execute(conn, """
            CREATE TABLE IF NOT EXISTS todo_tasks (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                Uuid          TEXT    NOT NULL UNIQUE,
                ListUuid      TEXT    NOT NULL,
                Title         TEXT    NOT NULL,
                Notes         TEXT    NOT NULL DEFAULT '',
                DueAt         INTEGER,
                IsAllDay      INTEGER NOT NULL DEFAULT 0,
                ReminderOn    INTEGER NOT NULL DEFAULT 0,
                IsImportant   INTEGER NOT NULL DEFAULT 0,
                CompletedAt   INTEGER,
                CatchUpFiredAt INTEGER,
                RecurrenceRule TEXT,
                CreatedAt     INTEGER NOT NULL,
                UpdatedAt     INTEGER NOT NULL
            );
            """);
        // Dev databases migrated before the rule column shipped (v7 was never
        // released, so this ALTER only ever heals local state).
        if (!ColumnExists(conn, "todo_tasks", "RecurrenceRule"))
            Execute(conn, "ALTER TABLE todo_tasks ADD COLUMN RecurrenceRule TEXT;");

        // Steps (PRD Decision 9): lightweight checkable subtasks. CompletedAt
        // NULL = unchecked. SortOrder = insertion order (append = MAX+1).
        Execute(conn, """
            CREATE TABLE IF NOT EXISTS todo_steps (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                Uuid        TEXT    NOT NULL UNIQUE,
                TaskId      INTEGER NOT NULL,
                Title       TEXT    NOT NULL,
                CompletedAt INTEGER,
                SortOrder   INTEGER NOT NULL DEFAULT 0,
                CreatedAt   INTEGER NOT NULL,
                UpdatedAt   INTEGER NOT NULL
            );
            """);
        Execute(conn, "CREATE INDEX IF NOT EXISTS idx_todo_steps_task ON todo_steps (TaskId);");

        // Attachments (PRD Decision 11): reference rows — the bytes live in the
        // attachments/ store content-addressed by Sha (dedupe across tasks).
        Execute(conn, """
            CREATE TABLE IF NOT EXISTS todo_attachments (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Uuid      TEXT    NOT NULL UNIQUE,
                TaskId    INTEGER NOT NULL,
                FileName  TEXT    NOT NULL,
                Sha       TEXT    NOT NULL,
                ByteSize  INTEGER NOT NULL,
                CreatedAt INTEGER NOT NULL,
                UpdatedAt INTEGER NOT NULL
            );
            """);
        Execute(conn, "CREATE INDEX IF NOT EXISTS idx_todo_attachments_task ON todo_attachments (TaskId);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS idx_todo_attachments_sha ON todo_attachments (Sha);");

        // Smart-view indexes: per-list listing, due-date scans over INCOMPLETE tasks
        // (今日 / 计划内 / reminder catch-up), and the 重要 view.
        Execute(conn, "CREATE INDEX IF NOT EXISTS idx_todo_tasks_list ON todo_tasks (ListUuid);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS idx_todo_tasks_due_open ON todo_tasks (DueAt) WHERE CompletedAt IS NULL;");
        Execute(conn, "CREATE INDEX IF NOT EXISTS idx_todo_tasks_important_open ON todo_tasks (IsImportant) WHERE CompletedAt IS NULL;");

        // Default lists (data identity, zh — same policy as the built-in note
        // categories; Presentation resolves display names through resw later).
        // Per-row guards (v4 lesson: a bare table-non-empty guard silently seeds
        // only the first statement). The uuids are the STABLE well-known
        // identities (see WorkListUuid) so cross-install merges never duplicate
        // the built-ins.
        Execute(conn, $"""
            INSERT INTO todo_lists (Uuid, Name, Color, SortOrder, CreatedAt, UpdatedAt)
            SELECT '{WorkListUuid}', '工作', '#3B82F6', 0,
                   CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER),
                   CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)
            WHERE NOT EXISTS (SELECT 1 FROM todo_lists WHERE Name = '工作');
            """);
        Execute(conn, $"""
            INSERT INTO todo_lists (Uuid, Name, Color, SortOrder, CreatedAt, UpdatedAt)
            SELECT '{LifeListUuid}', '生活', '#22C55E', 1,
                   CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER),
                   CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)
            WHERE NOT EXISTS (SELECT 1 FROM todo_lists WHERE Name = '生活');
            """);
    }

    /// <summary>
    /// Adopts the well-known builtin-list identity for databases seeded by the
    /// stage ① working-tree builds (random uuids). The oldest row with the
    /// builtin name is the seed (user-created duplicate names are younger);
    /// its tasks relink in the SAME transaction so no row can orphan. Skipped
    /// entirely when the well-known id is already in place, the builtin is
    /// absent (deleted / not seeded), or the todo tables do not exist yet.
    /// </summary>
    private static void NormalizeBuiltinListIdentity(SqliteConnection conn)
    {
        if (!TableExists(conn, "todo_lists"))
            return;

        NormalizeBuiltinList(conn, "工作", WorkListUuid);
        NormalizeBuiltinList(conn, "生活", LifeListUuid);
    }

    private static void NormalizeBuiltinList(SqliteConnection conn, string name, string wellKnownUuid)
    {
        using var exists = conn.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM todo_lists WHERE Uuid = $fixed;";
        exists.Parameters.AddWithValue("$fixed", wellKnownUuid);
        if (Convert.ToInt64(exists.ExecuteScalar() ?? 0L) > 0)
            return; // already normalized (fresh seed or a previous heal)

        string? oldUuid;
        using (var find = conn.CreateCommand())
        {
            find.CommandText = "SELECT Uuid FROM todo_lists WHERE Name = $name ORDER BY Id LIMIT 1;";
            find.Parameters.AddWithValue("$name", name);
            oldUuid = (string?)(find.ExecuteScalar() ?? null);
        }
        if (oldUuid is null || oldUuid == wellKnownUuid)
            return;

        using var tx = conn.BeginTransaction();
        using (var rewrite = conn.CreateCommand())
        {
            rewrite.Transaction = tx;
            rewrite.CommandText =
                "UPDATE todo_lists SET Uuid = $fixed WHERE Uuid = $old;\n" +
                "UPDATE todo_tasks SET ListUuid = $fixed WHERE ListUuid = $old;";
            rewrite.Parameters.AddWithValue("$fixed", wellKnownUuid);
            rewrite.Parameters.AddWithValue("$old", oldUuid);
            rewrite.ExecuteNonQuery();
        }
        tx.Commit();
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

    private static bool TableExists(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        cmd.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
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
