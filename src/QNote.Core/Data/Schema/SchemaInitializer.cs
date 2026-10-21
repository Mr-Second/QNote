using System.Globalization;
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
    public const long CurrentVersion = 8;

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

    /// <summary>
    /// Well-known uuid of the seeded 工作 category (user ruling 2026-10-21, task
    /// 10-06): the built-in categories must carry STABLE cross-device identities
    /// exactly like the built-in lists — the v7/v8 random backfill would make two
    /// peers sync the seeded categories as distinct entries (工作×2 on every peer).
    /// Same 32-hex reserved shape as the list uuids, different block.
    /// </summary>
    public const string WorkCategoryUuid = "00000000000000000000000000010001";

    /// <summary>Well-known uuid of the seeded 生活 category (see <see cref="WorkCategoryUuid"/>).</summary>
    public const string LifeCategoryUuid = "00000000000000000000000000010002";

    /// <summary>Well-known uuid of the seeded 重要 category (see <see cref="WorkCategoryUuid"/>).</summary>
    public const string ImportantCategoryUuid = "00000000000000000000000000010003";

    private static readonly DateTimeStyles UtcStyles =
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    private readonly DbConnectionFactory _factory;

    public SchemaInitializer(DbConnectionFactory factory) => _factory = factory;

    /// <summary>Ensure the database exists and is migrated to <see cref="CurrentVersion"/>.</summary>
    public void EnsureCreated()
    {
        using var conn = _factory.OpenWrite();

        Execute(conn, null, "PRAGMA journal_mode=WAL;");

        var version = GetUserVersion(conn);
        if (version < CurrentVersion)
        {
            using var tx = conn.BeginTransaction();
            if (version < 1)
                CreateV1(conn, tx);
            if (version < 2)
                MigrateV1ToV2(conn, tx);
            if (version < 3)
                MigrateV2ToV3(conn, tx);
            if (version < 4)
                MigrateV3ToV4(conn, tx);
            if (version < 5)
                MigrateV4ToV5(conn, tx);
            if (version < 6)
                MigrateV5ToV6(conn, tx);
            if (version < 7)
                MigrateV6ToV7(conn, tx);
            if (version < 8)
                MigrateV7ToV8(conn, tx);
            SetUserVersion(conn, tx, CurrentVersion);
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
            Execute(conn, null, "ALTER TABLE todo_tasks ADD COLUMN CatchUpFiredAt INTEGER;");

        // Stage ④ heal (task 10-04): the built-in lists' identity must be STABLE
        // across installs — merge LWW keys on uuid, so the per-install random ids
        // the stage ① seed wrote would duplicate the built-ins on every
        // cross-install restore. Same launch-time guarded-probe pattern as above:
        // normalize until present, never bumping user_version. A user-DELETED
        // builtin stays deleted (the name lookup finds nothing to rewrite); a
        // user-RENAMED one degrades to ordinary user data and is left alone.
        NormalizeBuiltinListIdentity(conn);

        // Built-in CATEGORY identity heal (task 10-06, user ruling 2026-10-21):
        // dev databases already stamped v8 carry RANDOM uuids on the seeded
        // categories (the v8 fix below post-dates them). Same launch-time
        // guarded-probe pattern as the list heal; notes link categories by NAME,
        // so nothing needs relinking.
        NormalizeBuiltinCategoryUuids(conn, null);

        // Stage ⑤ heal (task 10-04): steps / attachments / RecurrenceRule joined
        // the still-UNCOMMITTED v7 shape — same guarded-probe pattern. Databases
        // stamped user_version = 7 by earlier stage builds get the new tables and
        // column on every launch until present; fresh databases get them natively
        // from the CREATE TABLE statements inside MigrateV6ToV7.
        if (!ColumnExists(conn, "todo_tasks", "RecurrenceRule"))
            Execute(conn, null, "ALTER TABLE todo_tasks ADD COLUMN RecurrenceRule TEXT;");
        EnsureTodoStepsTable(conn);
        EnsureTodoAttachmentsTable(conn);

        // List-options menu heal (2026-10-16): todo_lists.Theme / HideCompleted /
        // SortMode ride the same unshipped v7 — guarded probes only.
        if (!ColumnExists(conn, "todo_lists", "Theme"))
            Execute(conn, null, "ALTER TABLE todo_lists ADD COLUMN Theme INTEGER NOT NULL DEFAULT 0;");
        if (!ColumnExists(conn, "todo_lists", "HideCompleted"))
            Execute(conn, null, "ALTER TABLE todo_lists ADD COLUMN HideCompleted INTEGER NOT NULL DEFAULT 0;");
        if (!ColumnExists(conn, "todo_lists", "SortMode"))
            Execute(conn, null, "ALTER TABLE todo_lists ADD COLUMN SortMode INTEGER NOT NULL DEFAULT 0;");
    }

    private static void EnsureTodoStepsTable(SqliteConnection conn)
    {
        Execute(conn, null, """
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
        Execute(conn, null, "CREATE INDEX IF NOT EXISTS idx_todo_steps_task ON todo_steps (TaskId);");
    }

    private static void EnsureTodoAttachmentsTable(SqliteConnection conn)
    {
        Execute(conn, null, """
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
        Execute(conn, null, "CREATE INDEX IF NOT EXISTS idx_todo_attachments_task ON todo_attachments (TaskId);");
        Execute(conn, null, "CREATE INDEX IF NOT EXISTS idx_todo_attachments_sha ON todo_attachments (Sha);");
    }

    private static void CreateV1(SqliteConnection conn, SqliteTransaction tx)
    {
        Execute(conn, tx, """
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

        Execute(conn, tx, """
            CREATE TABLE IF NOT EXISTS categories (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Name      TEXT    NOT NULL UNIQUE,
                IconKey   TEXT    NOT NULL DEFAULT '',
                SortOrder INTEGER NOT NULL DEFAULT 0
            );
            """);

        Execute(conn, tx, """
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
        Execute(conn, tx, """
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
    private static void MigrateV1ToV2(SqliteConnection conn, SqliteTransaction tx) =>
        Execute(conn, tx, "ALTER TABLE notes ADD COLUMN PlainText TEXT NOT NULL DEFAULT '';");

    // v3: recreate notes_fts with contentless_delete=1 so DELETE works on the
    // contentless shadow (required by the FTS write path inside NoteRepository
    // transactions). Safe to drop: notes_fts holds no user data of its own; the
    // startup reconcile repopulates it on next launch (SearchService.ReconcileAsync).
    private static void MigrateV2ToV3(SqliteConnection conn, SqliteTransaction tx) =>
        Execute(conn, tx, """
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
    private static void MigrateV3ToV4(SqliteConnection conn, SqliteTransaction tx)
    {
        Execute(conn, tx, """
            CREATE TABLE IF NOT EXISTS categories (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Name      TEXT    NOT NULL UNIQUE,
                IconKey   TEXT    NOT NULL DEFAULT '',
                SortOrder INTEGER NOT NULL DEFAULT 0
            );
            """);

        if (!ColumnExists(conn, "categories", "Color"))
            Execute(conn, tx, "ALTER TABLE categories ADD COLUMN Color TEXT NOT NULL DEFAULT '';");

        // Per-name guard: seeds each built-in independently (idempotent, and a
        // user-deleted custom set never blocks the remaining built-ins).
        Execute(conn, tx, """
            INSERT INTO categories (Name, IconKey, Color, SortOrder)
            SELECT '工作', 'E821', '#3B82F6', 0 WHERE NOT EXISTS (SELECT 1 FROM categories WHERE Name = '工作');
            """);
        Execute(conn, tx, """
            INSERT INTO categories (Name, IconKey, Color, SortOrder)
            SELECT '生活', 'E80F', '#22C55E', 1 WHERE NOT EXISTS (SELECT 1 FROM categories WHERE Name = '生活');
            """);
        Execute(conn, tx, """
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
    private static void MigrateV4ToV5(SqliteConnection conn, SqliteTransaction tx)
    {
        Execute(conn, tx, """
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
            Execute(conn, tx, "ALTER TABLE notes ADD COLUMN PlainText TEXT NOT NULL DEFAULT '';");

        // Rebuild notes with Content moved to the last column. DROP+ren+CREATE keeps
        // the rowids (Id preserved explicitly in the SELECT) so notes_fts rowids stay valid.
        Execute(conn, tx, "ALTER TABLE notes RENAME TO notes_old;");
        Execute(conn, tx, """
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
        Execute(conn, tx,
            "INSERT INTO notes (Id, Uuid, Title, Category, CreatedAt, UpdatedAt, PlainText, Content) " +
            "SELECT Id, Uuid, Title, Category, CreatedAt, UpdatedAt, PlainText, Content FROM notes_old;");
        Execute(conn, tx, "DROP TABLE notes_old;");

        Execute(conn, tx, """
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
        Execute(conn, tx, "CREATE INDEX IF NOT EXISTS idx_note_images_sha256 ON note_images (sha256);");
    }

    // v6 (Markdown storage B2): notes.Content semantics flip from RTF to Markdown —
    // NO notes DDL and no data migration (the app was never released; old RTF rows
    // simply parse as their literal text). The compressed display copy that used to
    // live inside the RTF gets a persistent home instead: note_images gains a
    // display_bytes BLOB, resolved back into {\pict} bytes when the editor renders
    // ![alt](qnote-img:<sha>) references. Nullable: legacy rows and adopted links
    // without a copy degrade to the alt-text placeholder.
    private static void MigrateV5ToV6(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!ColumnExists(conn, "note_images", "display_bytes"))
            Execute(conn, tx, "ALTER TABLE note_images ADD COLUMN display_bytes BLOB;");
    }

    // v7 (todo task, 10-04): the TODO domain lands — todo_lists + todo_tasks. Their
    // timestamps are MILLISECOND-EPOCH integers (sync contract, PRD Decision 6) —
    // unlike notes, which keep TEXT ISO timestamps. categories gains a Uuid column
    // (stable identity for the future sync engine); SQLite cannot ADD COLUMN with a
    // UNIQUE constraint, so uniqueness is a writer discipline (CreateCategoryAsync
    // generates identity at the single choke point) rather than a DB constraint.
    // todo_lists seeds the default 工作/生活 lists (data identity, zh — same policy
    // as the v4 built-in note categories) with PER-ROW NOT EXISTS guards.
    private static void MigrateV6ToV7(SqliteConnection conn, SqliteTransaction tx)
    {
        // Legacy categories-less layouts (see the v4 note): heal the table first so
        // the Uuid column below can be added on every database. The shape matches
        // the ALTER path's column order exactly.
        Execute(conn, tx, """
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
            Execute(conn, tx, "ALTER TABLE categories ADD COLUMN Uuid TEXT NOT NULL DEFAULT '';");

        // Backfill: pre-v7 rows get generated identity (same 32-hex shape as
        // Guid.NewGuid().ToString("N")). Re-runs only touch still-empty rows, so
        // identity is stable across repeated migrations.
        Execute(conn, tx, "UPDATE categories SET Uuid = lower(hex(randomblob(16))) WHERE Uuid = '';");

        Execute(conn, tx, """
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
            Execute(conn, tx, "ALTER TABLE todo_lists ADD COLUMN Theme INTEGER NOT NULL DEFAULT 0;");
        if (!ColumnExists(conn, "todo_lists", "HideCompleted"))
            Execute(conn, tx, "ALTER TABLE todo_lists ADD COLUMN HideCompleted INTEGER NOT NULL DEFAULT 0;");
        if (!ColumnExists(conn, "todo_lists", "SortMode"))
            Execute(conn, tx, "ALTER TABLE todo_lists ADD COLUMN SortMode INTEGER NOT NULL DEFAULT 0;");

        // Tasks link to their list by Uuid (string link, like notes.Category — no FK
        // by schema convention). CompletedAt NULL = incomplete, non-NULL = the
        // completion instant. CatchUpFiredAt records when a launch catch-up
        // re-fired this task's OS-dropped reminder (once per due crossing — the
        // catch-up query skips tasks whose marker is not below their due); it is
        // reminder bookkeeping, deliberately never stamped into UpdatedAt (sync
        // LWW must not see it). RecurrenceRule (PRD Decision 10) is null for a
        // one-off task, else a TodoRecurrence code — completing a ruled task
        // spawns the next occurrence.
        Execute(conn, tx, """
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
            Execute(conn, tx, "ALTER TABLE todo_tasks ADD COLUMN RecurrenceRule TEXT;");

        // Steps (PRD Decision 9): lightweight checkable subtasks. CompletedAt
        // NULL = unchecked. SortOrder = insertion order (append = MAX+1).
        Execute(conn, tx, """
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
        Execute(conn, tx, "CREATE INDEX IF NOT EXISTS idx_todo_steps_task ON todo_steps (TaskId);");

        // Attachments (PRD Decision 11): reference rows — the bytes live in the
        // attachments/ store content-addressed by Sha (dedupe across tasks).
        Execute(conn, tx, """
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
        Execute(conn, tx, "CREATE INDEX IF NOT EXISTS idx_todo_attachments_task ON todo_attachments (TaskId);");
        Execute(conn, tx, "CREATE INDEX IF NOT EXISTS idx_todo_attachments_sha ON todo_attachments (Sha);");

        // Smart-view indexes: per-list listing, due-date scans over INCOMPLETE tasks
        // (今日 / 计划内 / reminder catch-up), and the 重要 view.
        Execute(conn, tx, "CREATE INDEX IF NOT EXISTS idx_todo_tasks_list ON todo_tasks (ListUuid);");
        Execute(conn, tx, "CREATE INDEX IF NOT EXISTS idx_todo_tasks_due_open ON todo_tasks (DueAt) WHERE CompletedAt IS NULL;");
        Execute(conn, tx, "CREATE INDEX IF NOT EXISTS idx_todo_tasks_important_open ON todo_tasks (IsImportant) WHERE CompletedAt IS NULL;");

        // Default lists (data identity, zh — same policy as the built-in note
        // categories; Presentation resolves display names through resw later).
        // Per-row guards (v4 lesson: a bare table-non-empty guard silently seeds
        // only the first statement). The uuids are the STABLE well-known
        // identities (see WorkListUuid) so cross-install merges never duplicate
        // the built-ins.
        Execute(conn, tx, $"""
            INSERT INTO todo_lists (Uuid, Name, Color, SortOrder, CreatedAt, UpdatedAt)
            SELECT '{WorkListUuid}', '工作', '#3B82F6', 0,
                   CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER),
                   CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)
            WHERE NOT EXISTS (SELECT 1 FROM todo_lists WHERE Name = '工作');
            """);
        Execute(conn, tx, $"""
            INSERT INTO todo_lists (Uuid, Name, Color, SortOrder, CreatedAt, UpdatedAt)
            SELECT '{LifeListUuid}', '生活', '#22C55E', 1,
                   CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER),
                   CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)
            WHERE NOT EXISTS (SELECT 1 FROM todo_lists WHERE Name = '生活');
            """);
    }

    // v8 (multi-device sync, 10-06): the sync-friendly shape. Three independent
    // changes, all non-optional for LWW correctness:
    //  1. notes/categories timestamps become INTEGER epoch-ms. The todo domain
    //     already used ms; one comparison semantics across every synced table is
    //     the only way a note-vs-category-vs-task LWW decision can be written once.
    //  2. every synced table gains DeletedAt (NULL = live). Deletes stop being
    //     physical: a tombstone is the only thing that can propagate to a peer.
    //  3. todo_steps/todo_attachments gain TaskUuid so a child can find its parent
    //     on another device (the local TaskId is meaningless there).
    // sync_state lands here too (PRD Decision 11) - it must be written in the same
    // transaction as the data it describes, so it belongs to the same migration.
    private static void MigrateV7ToV8(SqliteConnection conn, SqliteTransaction tx)
    {
        RebuildNotesV8(conn, tx);
        AddCategoryTimestampsV8(conn, tx);
        AddDeletedAtV8(conn, tx);
        AddChildTaskUuidV8(conn, tx);
        Execute(conn, tx, """
            CREATE TABLE IF NOT EXISTS sync_state (
                entry_kind     TEXT    NOT NULL,
                entry_uuid     TEXT    NOT NULL,
                remote_id      TEXT    NOT NULL DEFAULT '',
                etag           TEXT    NOT NULL DEFAULT '',
                last_synced_at INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (entry_kind, entry_uuid)
            );
            """);
    }

    /// <summary>
    /// Rebuilds <c>notes</c> into the v8 shape. A rebuild is FORCED, not chosen:
    /// the <c>Content</c>-last column-order contract (database-guidelines:171-174)
    /// forbids appending <c>DeletedAt</c> via ALTER TABLE. Ids are copied
    /// explicitly so <c>notes_fts</c> rowids (which key off notes.Id) survive.
    /// The <c>note_images</c> FK is resolved BY NAME, so it follows the new table
    /// the moment that table is renamed to <c>notes</c> — see the swap below.
    /// The TEXT-to-ms conversion runs in C#, row by row: the stored ISO strings
    /// carry 7 fractional digits, and an SQL <c>strftime</c> round-trip would
    /// silently truncate them.
    /// </summary>
    private static void RebuildNotesV8(SqliteConnection conn, SqliteTransaction tx)
    {
        // A rebuild WITHOUT renaming the old table first. The alternative — the v5
        // order, RENAME notes TO notes_old before the copy — is unreachable here:
        // on this schema SQLite (>= 3.25) rewrites note_images' DDL to
        // `REFERENCES "notes_old"(Id)` at the rename, and the final DROP then leaves
        // the FK pointing at nothing (`no such table: main.notes_old` on the next
        // note_images insert). Building the replacement under a temporary name
        // sidesteps that rewrite completely: the old table keeps its name until
        // DROP, and the rename of `notes_v8` is a pure name flip that the
        // by-name FK resolves to on the spot.
        // (Dropping `notes` also drops nothing else, because no table references it
        // yet — note_images' reference is by name and re-resolves, it is not a
        // stored row dependency.)
        Execute(conn, tx, """
            CREATE TABLE notes_v8 (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Uuid      TEXT    NOT NULL UNIQUE,
                Title     TEXT    NOT NULL DEFAULT '',
                Category  TEXT    NOT NULL DEFAULT '',
                CreatedAt INTEGER NOT NULL,
                UpdatedAt INTEGER NOT NULL,
                DeletedAt INTEGER,
                PlainText TEXT    NOT NULL DEFAULT '',
                Content   TEXT    NOT NULL DEFAULT ''
            );
            """);

        // Paged, not a single read of the whole table: Content holds RTF that can
        // inline images (that is why v5 rebuilt this table), so a library of a few
        // thousand illustrated notes is hundreds of MB. One page at a time keeps the
        // peak at one page plus the current Content blob; the whole thing still runs
        // in the ladder's single transaction, so a failure rolls back cleanly.
        // The copy is keyset-paged on the read side only (Id > lastId ORDER BY Id
        // LIMIT n) — never OFFSET, and never over the destination, so a row whose
        // Content is huge cannot shift a later page.
        long lastId = 0;
        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText =
                "INSERT INTO notes_v8 (Id, Uuid, Title, Category, CreatedAt, UpdatedAt, DeletedAt, PlainText, Content) " +
                "VALUES ($id, $uuid, $title, $category, $created, $updated, NULL, $plain, $content);";
            var pId = insert.Parameters.Add("$id", SqliteType.Integer);
            var pUuid = insert.Parameters.Add("$uuid", SqliteType.Text);
            var pTitle = insert.Parameters.Add("$title", SqliteType.Text);
            var pCategory = insert.Parameters.Add("$category", SqliteType.Text);
            var pCreated = insert.Parameters.Add("$created", SqliteType.Integer);
            var pUpdated = insert.Parameters.Add("$updated", SqliteType.Integer);
            var pPlain = insert.Parameters.Add("$plain", SqliteType.Text);
            var pContent = insert.Parameters.Add("$content", SqliteType.Text);

            while (true)
            {
                var rows = ReadNotePageV8(conn, tx, lastId, NoteMigrationPageSize);
                if (rows.Count == 0)
                    break;

                foreach (var row in rows)
                {
                    pId.Value = row.Id;
                    pUuid.Value = row.Uuid;
                    pTitle.Value = row.Title;
                    pCategory.Value = row.Category;
                    pCreated.Value = IsoToEpochMs(row.CreatedAt, row.Id, "CreatedAt");
                    pUpdated.Value = IsoToEpochMs(row.UpdatedAt, row.Id, "UpdatedAt");
                    pPlain.Value = row.PlainText;
                    pContent.Value = row.Content;
                    insert.ExecuteNonQuery();
                    lastId = row.Id;
                }
            }
        }

        // After the data is safely copied the swap is a pure name flip; note_images'
        // FK (still `REFERENCES notes(Id)`) resolves to the new table from here on.
        Execute(conn, tx, "DROP TABLE notes;");
        Execute(conn, tx, "ALTER TABLE notes_v8 RENAME TO notes;");
    }

    /// <summary>Rows per page during the v8 notes rebuild (see <see cref="RebuildNotesV8"/>).</summary>
    private const int NoteMigrationPageSize = 32;

    /// <summary>
    /// Reads the next page of pre-v8 notes ordered by Id. Keyset pagination (Id &gt;
    /// the previous page's last Id) rather than OFFSET: the cost stays flat at any
    /// depth, and the cursor advances strictly (Id is the AUTOINCREMENT rowid), so
    /// the loop cannot revisit or skip a row and terminates on the first short page.
    /// </summary>
    private static List<NoteTimestampRow> ReadNotePageV8(
        SqliteConnection conn,
        SqliteTransaction tx,
        long afterId,
        int pageSize)
    {
        var rows = new List<NoteTimestampRow>(pageSize);
        using var read = conn.CreateCommand();
        read.Transaction = tx;
        read.CommandText =
            "SELECT Id, Uuid, Title, Category, CreatedAt, UpdatedAt, PlainText, Content " +
            "FROM notes WHERE Id > $after ORDER BY Id LIMIT $limit;";
        // Both are VALUES, never identifiers — bound through the inferred-type API
        // on purpose: a declared SqliteType here is fine for $limit but would type
        // $after as a string, and `Id > '32'` silently falls back to SQLite text
        // comparison ('100' < '32'), skipping pages and re-parsing ms values as ISO.
        read.Parameters.AddWithValue("$after", afterId);
        read.Parameters.AddWithValue("$limit", pageSize);
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new NoteTimestampRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetValue(4),
                reader.GetValue(5),
                reader.GetString(6),
                reader.GetString(7)));
        }
        return rows;
    }

    private static void AddCategoryTimestampsV8(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!ColumnExists(conn, "categories", "CreatedAt"))
            Execute(conn, tx, "ALTER TABLE categories ADD COLUMN CreatedAt INTEGER NOT NULL DEFAULT 0;");
        if (!ColumnExists(conn, "categories", "UpdatedAt"))
            Execute(conn, tx, "ALTER TABLE categories ADD COLUMN UpdatedAt INTEGER NOT NULL DEFAULT 0;");

        // 0 is the honest backfill: categories never recorded a timestamp, so any
        // other value would be invented. It still loses a fair LWW against a peer
        // that has a real one, which is the correct outcome for "unknown".
        Execute(conn, tx, "UPDATE categories SET Uuid = lower(hex(randomblob(16))) WHERE Uuid = '';");

        // The built-ins get STABLE well-known uuids (user ruling 2026-10-21): the
        // random ids the backfill just wrote would duplicate them on every synced
        // peer. Notes link categories by NAME, so no relink is needed — the rows
        // are re-uuid'd in place, before the unique index below takes hold.
        NormalizeBuiltinCategoryUuids(conn, tx);

        // v7 could only enforce uuid uniqueness by writer discipline (SQLite cannot
        // ADD COLUMN ... UNIQUE). The index is creatable now, so identity becomes a
        // DB-level guarantee before any sync code trusts it.
        Execute(conn, tx, "CREATE UNIQUE INDEX IF NOT EXISTS idx_categories_uuid ON categories (Uuid);");
    }

    private static void AddDeletedAtV8(SqliteConnection conn, SqliteTransaction tx)
    {
        foreach (var table in SyncedTables)
        {
            // Every table exists by now on a real ladder run; the probe keeps the
            // step tolerant of hand-built legacy fixtures that never grew a table.
            if (TableExists(conn, table) && !ColumnExists(conn, table, "DeletedAt"))
                Execute(conn, tx, $"ALTER TABLE {table} ADD COLUMN DeletedAt INTEGER;");
        }
    }

    /// <summary>
    /// Adds the cross-device parent key to the todo children and backfills it from
    /// the local parent row. <c>TaskId</c> is deliberately KEPT: every repository
    /// query and both backup-merge relink passes still key on it, so dropping it
    /// here would drag the whole todo read/write surface into this migration.
    /// TODO(sync L1): drop TaskId once the repositories key on TaskUuid.
    /// </summary>
    private static void AddChildTaskUuidV8(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!TableExists(conn, "todo_steps") || !TableExists(conn, "todo_attachments"))
            return;

        if (!ColumnExists(conn, "todo_steps", "TaskUuid"))
            Execute(conn, tx, "ALTER TABLE todo_steps ADD COLUMN TaskUuid TEXT NOT NULL DEFAULT '';");
        if (!ColumnExists(conn, "todo_attachments", "TaskUuid"))
            Execute(conn, tx, "ALTER TABLE todo_attachments ADD COLUMN TaskUuid TEXT NOT NULL DEFAULT '';");

        Execute(conn, tx, """
            UPDATE todo_steps
            SET TaskUuid = (SELECT Uuid FROM todo_tasks WHERE Id = todo_steps.TaskId)
            WHERE TaskUuid = '';
            """);
        Execute(conn, tx, """
            UPDATE todo_attachments
            SET TaskUuid = (SELECT Uuid FROM todo_tasks WHERE Id = todo_attachments.TaskId)
            WHERE TaskUuid = '';
            """);
    }

    /// <summary>Tables that carry <c>DeletedAt</c> (every table the sync engine reads or writes).</summary>
    private static readonly string[] SyncedTables =
        ["notes", "categories", "todo_lists", "todo_tasks", "todo_steps", "todo_attachments"];

    /// <summary>
    /// v7 stored notes/categories timestamps as TEXT ISO-8601. A source row that
    /// already holds an integer is a database that passed through this step before
    /// (a re-run, or a hand-built fixture), so the value is taken as-is instead of
    /// being parsed as an ISO string. Anything else — including DBNull, which a
    /// pre-v8 hand-built or Qt-era row can carry — throws: coercing garbage to 0
    /// would make every such row look infinitely stale to a peer's LWW.
    /// </summary>
    private static long IsoToEpochMs(object? value, long id, string column) => value switch
    {
        long ms => ms,
        int ms => ms,
        string text => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, UtcStyles, out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : throw new InvalidOperationException(
                $"notes.{column} of note {id} is not an ISO-8601 timestamp ('{text}'); the database is not a v7 layout."),
        null or DBNull => throw new InvalidOperationException(
            $"notes.{column} of note {id} is NULL; the database is not a v7 layout."),
        _ => throw new InvalidOperationException(
            $"Unexpected timestamp storage type '{value.GetType().Name}' in notes.{column} of note {id}; " +
            "the database is not a v7 layout."),
    };

    private readonly record struct NoteTimestampRow(
        long Id,
        string Uuid,
        string Title,
        string Category,
        object CreatedAt,
        object UpdatedAt,
        string PlainText,
        string Content);

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

    /// <summary>
    /// Adopts the well-known builtin-CATEGORY identity for databases whose seeded
    /// categories carry random uuids (pre-ruling v8 dev databases). The OLDEST row
    /// with the exact builtin name is the seed (user-created same-name rows stay
    /// ordinary data); a user-RENAMED builtin degrades to ordinary user data; a
    /// user-DELETED one has nothing to rewrite. Notes link categories by NAME, so
    /// no relink is needed. No-op when the table/column do not exist yet.
    /// </summary>
    private static void NormalizeBuiltinCategoryUuids(SqliteConnection conn, SqliteTransaction? tx)
    {
        if (!TableExists(conn, "categories") || !ColumnExists(conn, "categories", "Uuid"))
            return;

        NormalizeBuiltinCategory(conn, tx, "工作", WorkCategoryUuid);
        NormalizeBuiltinCategory(conn, tx, "生活", LifeCategoryUuid);
        NormalizeBuiltinCategory(conn, tx, "重要", ImportantCategoryUuid);
    }

    private static void NormalizeBuiltinCategory(
        SqliteConnection conn, SqliteTransaction? tx, string name, string wellKnownUuid)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // Only the OLDEST exact-name row adopts the identity: the seed guards make
        // duplicates essentially impossible, but a user-created same-name category
        // must never collide with the reserved uuid under the unique index.
        cmd.CommandText =
            "UPDATE categories SET Uuid = $fixed WHERE Id = (" +
            "SELECT Id FROM categories WHERE Name = $name AND Uuid <> $fixed ORDER BY Id LIMIT 1);";
        cmd.Parameters.AddWithValue("$fixed", wellKnownUuid);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.ExecuteNonQuery();
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

    private static void Execute(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long GetUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static void SetUserVersion(SqliteConnection conn, SqliteTransaction tx, long version)
    {
        using var cmd = conn.CreateCommand();
        // PRAGMA values cannot be parameterized; `version` is an internal constant,
        // never user input, so this is not an injection surface.
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }
}
