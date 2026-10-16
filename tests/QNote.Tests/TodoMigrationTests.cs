using System.IO;
using Microsoft.Data.Sqlite;
using QNote.Data;
using QNote.Data.Schema;

namespace QNote.Tests;

/// <summary>
/// Schema v7 migration battery (task 10-04): a v6 database upgrades to v7
/// losslessly (notes untouched, categories backfilled with non-null unique
/// uuids, todo tables + default lists created), a fresh database is created at
/// v7 directly, and the migration is idempotent on re-runs.
/// </summary>
public sealed class TodoMigrationTests
{
    [Fact]
    public void FreshDb_CreatesTodoSchema_AndSeedsDefaultLists()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            new SchemaInitializer(factory).EnsureCreated();

            using var check = factory.OpenRead();
            Assert.Equal(7, GetUserVersion(check));
            Assert.True(TableExists(check, "todo_lists"), "todo_lists table missing");
            Assert.True(TableExists(check, "todo_tasks"), "todo_tasks table missing");
            Assert.True(TableExists(check, "idx_todo_tasks_list"), "todo list index missing");
            Assert.True(TableExists(check, "idx_todo_tasks_due_open"), "todo due index missing");
            Assert.True(TableExists(check, "idx_todo_tasks_important_open"), "todo important index missing");
            // Stage ③: the catch-up fired marker is part of the (unshipped) v7 shape.
            Assert.True(ColumnExists(check, "todo_tasks", "CatchUpFiredAt"), "CatchUpFiredAt column missing");
            // Stage ⑤: recurrence rule + the two child tables ride the same v7.
            Assert.True(ColumnExists(check, "todo_tasks", "RecurrenceRule"), "RecurrenceRule column missing");
            Assert.True(TableExists(check, "todo_steps"), "todo_steps table missing");
            Assert.True(TableExists(check, "todo_attachments"), "todo_attachments table missing");
            Assert.True(TableExists(check, "idx_todo_steps_task"), "todo steps index missing");
            Assert.True(TableExists(check, "idx_todo_attachments_task"), "todo attachments task index missing");
            Assert.True(TableExists(check, "idx_todo_attachments_sha"), "todo attachments sha index missing");

            Assert.Equal(new[] { "工作", "生活" }, SelectListNames(check));
            Assert.All(SelectListUuids(check), u => Assert.Matches("^[0-9a-f]{32}$", u));

            // Fresh categories (seeded at v4) are backfilled with identity too.
            var categoryUuids = SelectCategoryUuids(check);
            Assert.Equal(3, categoryUuids.Count);
            Assert.All(categoryUuids, u => Assert.Matches("^[0-9a-f]{32}$", u));
            Assert.Equal(categoryUuids.Count, categoryUuids.Distinct().Count());
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void StageOneV7Db_WithoutCatchUpMarker_HealsWithoutVersionBump()
    {
        // Dev machines that ran the stage ① working-tree builds carry a
        // user_version = 7 database WITHOUT CatchUpFiredAt (the marker joined
        // the still-uncommitted v7 shape in stage ③, so there is no v8).
        // EnsureCreated's post-ladder guarded probe must add the column on
        // every launch until present — never bumping the version.
        var dbPath = NewDbPath();
        try
        {
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            CreateStageOneV7Database(factory);

            new SchemaInitializer(factory).EnsureCreated();

            using var check = factory.OpenRead();
            Assert.Equal(7, GetUserVersion(check));
            Assert.True(ColumnExists(check, "todo_tasks", "CatchUpFiredAt"), "heal did not add CatchUpFiredAt");
            // List-options menu (2026-10-16): the three todo_lists columns heal
            // the same way — v7 is still unshipped, dev databases ride the ALTER.
            Assert.True(ColumnExists(check, "todo_lists", "Theme"), "heal did not add todo_lists.Theme");
            Assert.True(ColumnExists(check, "todo_lists", "HideCompleted"), "heal did not add todo_lists.HideCompleted");
            Assert.True(ColumnExists(check, "todo_lists", "SortMode"), "heal did not add todo_lists.SortMode");
            // Stage ⑤ heals: the recurrence column + the two child tables.
            Assert.True(ColumnExists(check, "todo_tasks", "RecurrenceRule"), "heal did not add RecurrenceRule");
            Assert.True(TableExists(check, "todo_steps"), "heal did not create todo_steps");
            Assert.True(TableExists(check, "todo_attachments"), "heal did not create todo_attachments");

            // The healed column participates in the catch-up query semantics.
            using var cmd = check.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM todo_tasks " +
                "WHERE CompletedAt IS NULL AND ReminderOn = 1 AND DueAt >= 0 AND DueAt < 100 " +
                "AND (CatchUpFiredAt IS NULL OR CatchUpFiredAt < DueAt);";
            Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar() ?? 0L));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void StageOneV7Db_BuiltinListIdentity_NormalizesAndRelinksTasks()
    {
        // Stage ④ finding: the built-in lists' identity must be STABLE across
        // installs (backup merge keys list identity on uuid — per-install random
        // ids duplicate the built-ins on every cross-install restore). Dev
        // databases seeded by the stage ① working-tree builds carry random ids;
        // the launch-time heal must adopt the well-known identity AND relink the
        // tasks in the same transaction, without bumping user_version.
        var dbPath = NewDbPath();
        try
        {
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            CreateStageOneV7Database(factory);

            new SchemaInitializer(factory).EnsureCreated();

            using var check = factory.OpenRead();
            Assert.Equal(7, GetUserVersion(check));
            Assert.Equal(SchemaInitializer.WorkListUuid, Scalar(check, "SELECT Uuid FROM todo_lists WHERE Name = '工作';"));
            // The task row relinked — no orphaned ListUuid.
            Assert.Equal(SchemaInitializer.WorkListUuid, Scalar(check, "SELECT ListUuid FROM todo_tasks WHERE Uuid = 'task-1';"));

            // Idempotent: a second launch is a no-op (identity stable).
            new SchemaInitializer(factory).EnsureCreated();
            Assert.Equal(SchemaInitializer.WorkListUuid, Scalar(check, "SELECT Uuid FROM todo_lists WHERE Name = '工作';"));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void UserDeletedBuiltin_HealDoesNotResurrectIt()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            CreateStageOneV7Database(factory);
            using (var conn = factory.OpenWrite())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM todo_lists; DELETE FROM todo_tasks;";
                cmd.ExecuteNonQuery();
            }

            new SchemaInitializer(factory).EnsureCreated();

            using var check = factory.OpenRead();
            Assert.Equal(0L, Convert.ToInt64(ScalarCount(check, "SELECT COUNT(*) FROM todo_lists;")));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void V6Db_UpgradesToV7_Losslessly()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            CreateV6Database(factory, withCategories: true);

            new SchemaInitializer(factory).EnsureCreated();

            using var check = factory.OpenRead();
            Assert.Equal(7, GetUserVersion(check));

            // Notes untouched (byte-for-byte column contract, ISO timestamps intact).
            Assert.Equal("旧笔记", Scalar(check, "SELECT Title FROM notes WHERE Uuid = 'note-uuid-1';"));
            Assert.Equal("# 正文", Scalar(check, "SELECT Content FROM notes WHERE Uuid = 'note-uuid-1';"));
            Assert.Equal("正文", Scalar(check, "SELECT PlainText FROM notes WHERE Uuid = 'note-uuid-1';"));

            // Categories intact + backfilled with non-null, distinct identity.
            Assert.Equal("工作", Scalar(check, "SELECT Name FROM categories WHERE Id = 1;"));
            var uuids = SelectCategoryUuids(check);
            Assert.All(uuids, u => Assert.Matches("^[0-9a-f]{32}$", u));
            Assert.Equal(uuids.Count, uuids.Distinct().Count());

            // Todo domain created + seeded (incl. the stage-⑤ children).
            Assert.True(TableExists(check, "todo_lists"));
            Assert.True(TableExists(check, "todo_tasks"));
            Assert.True(TableExists(check, "todo_steps"));
            Assert.True(TableExists(check, "todo_attachments"));
            Assert.True(ColumnExists(check, "todo_tasks", "RecurrenceRule"));
            Assert.Equal(new[] { "工作", "生活" }, SelectListNames(check));

            // Epoch-ms timestamps: the seeds must be plausible present-day values.
            var created = SelectLong(check, "SELECT CreatedAt FROM todo_lists WHERE Name = '工作';");
            Assert.InRange(created, 1_700_000_000_000L, 4_100_000_000_000L);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void Migration_IsIdempotent_IdentityStableOnRerun()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            var schema = new SchemaInitializer(factory);
            schema.EnsureCreated();

            string[] listUuids;
            List<string> categoryUuids;
            using (var check = factory.OpenRead())
            {
                listUuids = SelectListUuids(check).ToArray();
                categoryUuids = SelectCategoryUuids(check);
            }

            schema.EnsureCreated();

            using (var check = factory.OpenRead())
            {
                Assert.Equal(7, GetUserVersion(check));
                Assert.Equal(2, SelectListNames(check).Count); // seeds not duplicated
                Assert.Equal(listUuids, SelectListUuids(check).ToArray());
                Assert.Equal(categoryUuids, SelectCategoryUuids(check));
            }
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void CategoriesLessV6Db_StillMigrates()
    {
        // Legacy layouts without a categories table (see the v4 note in
        // SchemaInitializer) — the v7 step heals the table before adding Uuid.
        var dbPath = NewDbPath();
        try
        {
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            CreateV6Database(factory, withCategories: false);

            new SchemaInitializer(factory).EnsureCreated();

            using var check = factory.OpenRead();
            Assert.Equal(7, GetUserVersion(check));
            Assert.True(ColumnExists(check, "categories", "Uuid"));
            Assert.True(TableExists(check, "todo_lists"));
            Assert.True(TableExists(check, "todo_tasks"));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    // ---------- v6 fixture ----------

    /// <summary>
    /// Hand-builds a realistic schema-v6 database (pre-todo, categories without
    /// Uuid) so the v7 step runs against the real legacy shape.
    /// </summary>
    private static void CreateV6Database(DbConnectionFactory factory, bool withCategories)
    {
        using var conn = factory.OpenWrite();
        using var cmd = conn.CreateCommand();
        var sql = """
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
            CREATE VIRTUAL TABLE notes_fts USING fts5(
                title, body, content='', contentless_delete=1, tokenize='unicode61');
            CREATE TABLE settings (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            CREATE TABLE note_images (
                note_id       INTEGER NOT NULL,
                sha256        TEXT    NOT NULL,
                ext           TEXT    NOT NULL DEFAULT '',
                byte_size     INTEGER NOT NULL DEFAULT 0,
                width         INTEGER NOT NULL DEFAULT 0,
                height        INTEGER NOT NULL DEFAULT 0,
                created_at    TEXT    NOT NULL,
                display_bytes BLOB,
                PRIMARY KEY (note_id, sha256),
                FOREIGN KEY (note_id) REFERENCES notes(Id) ON DELETE CASCADE
            );
            INSERT INTO notes (Uuid, Title, Category, CreatedAt, UpdatedAt, PlainText, Content)
                VALUES ('note-uuid-1', '旧笔记', '工作',
                        '2026-01-01T00:00:00.0000000Z', '2026-01-02T00:00:00.0000000Z', '正文', '# 正文');
            """;
        if (withCategories)
        {
            sql += """
                CREATE TABLE categories (
                    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name      TEXT    NOT NULL UNIQUE,
                    IconKey   TEXT    NOT NULL DEFAULT '',
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    Color     TEXT    NOT NULL DEFAULT ''
                );
                INSERT INTO categories (Name, IconKey, Color, SortOrder)
                    VALUES ('工作', 'E821', '#3B82F6', 0), ('自定义', 'E709', '#22C55E', 1);
                """;
        }
        cmd.CommandText = sql + "PRAGMA user_version = 6;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Hand-builds the stage ① working-tree shape: a <c>user_version = 7</c>
    /// database whose todo_tasks predates the CatchUpFiredAt column (see
    /// <see cref="StageOneV7Db_WithoutCatchUpMarker_HealsWithoutVersionBump"/>).
    /// </summary>
    private static void CreateStageOneV7Database(DbConnectionFactory factory)
    {
        using var conn = factory.OpenWrite();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
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
            CREATE TABLE settings (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            CREATE TABLE categories (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Name      TEXT    NOT NULL UNIQUE,
                IconKey   TEXT    NOT NULL DEFAULT '',
                SortOrder INTEGER NOT NULL DEFAULT 0,
                Color     TEXT    NOT NULL DEFAULT '',
                Uuid      TEXT    NOT NULL DEFAULT ''
            );
            CREATE TABLE todo_lists (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Uuid      TEXT    NOT NULL UNIQUE,
                Name      TEXT    NOT NULL,
                Color     TEXT    NOT NULL DEFAULT '',
                SortOrder INTEGER NOT NULL DEFAULT 0,
                CreatedAt INTEGER NOT NULL,
                UpdatedAt INTEGER NOT NULL
            );
            CREATE TABLE todo_tasks (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                Uuid        TEXT    NOT NULL UNIQUE,
                ListUuid    TEXT    NOT NULL,
                Title       TEXT    NOT NULL,
                Notes       TEXT    NOT NULL DEFAULT '',
                DueAt       INTEGER,
                IsAllDay    INTEGER NOT NULL DEFAULT 0,
                ReminderOn  INTEGER NOT NULL DEFAULT 0,
                IsImportant INTEGER NOT NULL DEFAULT 0,
                CompletedAt INTEGER,
                CreatedAt   INTEGER NOT NULL,
                UpdatedAt   INTEGER NOT NULL
            );
            INSERT INTO todo_lists (Uuid, Name, Color, SortOrder, CreatedAt, UpdatedAt)
                VALUES ('work', '工作', '#3B82F6', 0, 1, 1);
            INSERT INTO todo_tasks (Uuid, ListUuid, Title, CreatedAt, UpdatedAt)
                VALUES ('task-1', 'work', '已有任务', 1, 1);
            PRAGMA user_version = 7;
            """;
        cmd.ExecuteNonQuery();
    }

    // ---------- helpers ----------

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), $"qnote-todo-migration-{Guid.NewGuid():N}.db");

    private static void Cleanup(string dbPath)
    {
        TryDelete(dbPath);
        TryDelete(dbPath + "-wal");
        TryDelete(dbPath + "-shm");
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static long GetUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static bool TableExists(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = $name;";
        cmd.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        // PRAGMA table_info cannot be parameterized; names here are test constants.
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static IReadOnlyList<string> SelectListNames(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Name FROM todo_lists ORDER BY SortOrder;";
        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
            list.Add(reader.GetString(0));
        return list;
    }

    private static IReadOnlyList<string> SelectListUuids(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Uuid FROM todo_lists ORDER BY SortOrder;";
        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
            list.Add(reader.GetString(0));
        return list;
    }

    private static List<string> SelectCategoryUuids(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Uuid FROM categories ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
            list.Add(reader.GetString(0));
        return list;
    }

    private static string Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (string)(cmd.ExecuteScalar() ?? string.Empty);
    }

    private static long ScalarCount(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static long SelectLong(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }
}
