using System.IO;
using Microsoft.Data.Sqlite;
using QNote.Data;
using QNote.Data.Schema;
using QNote.Models;
using QNote.Services;

namespace QNote.Tests;

/// <summary>
/// Schema v8 migration battery (task 10-06, multi-device sync L0): the sync shape
/// lands in one hop (notes rebuilt with epoch-ms timestamps + DeletedAt, categories
/// gain their LWW clock, every synced table gains DeletedAt, the todo children gain
/// TaskUuid, sync_state is created), soft deletes become tombstones that bump
/// UpdatedAt, and the read paths stop returning them.
/// </summary>
public sealed class SyncSchemaMigrationTests
{
    private static readonly string[] SyncedTables =
        ["notes", "categories", "todo_lists", "todo_tasks", "todo_steps", "todo_attachments"];

    // ---------- 1. fresh shape ----------

    [Fact]
    public void FreshDb_HasTheV8SyncShape()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            new SchemaInitializer(factory).EnsureCreated();

            using var conn = factory.OpenRead();
            Assert.Equal(8, GetUserVersion(conn));

            Assert.All(SyncedTables, t => Assert.True(
                ColumnExists(conn, t, "DeletedAt"), $"{t}.DeletedAt missing"));
            Assert.True(ColumnExists(conn, "categories", "CreatedAt"));
            Assert.True(ColumnExists(conn, "categories", "UpdatedAt"));
            Assert.True(ColumnExists(conn, "todo_steps", "TaskUuid"));
            Assert.True(ColumnExists(conn, "todo_attachments", "TaskUuid"));
            Assert.True(TableExists(conn, "sync_state"), "sync_state missing");
            Assert.True(TableExists(conn, "idx_categories_uuid"), "categories uuid index missing");

            // The Content-last column contract survives the rebuild.
            Assert.Equal("Content", LastColumn(conn, "notes"));
            Assert.Equal(
                ["Id", "Uuid", "Title", "Category", "CreatedAt", "UpdatedAt", "DeletedAt", "PlainText", "Content"],
                ColumnNames(conn, "notes"));

            // notes/categories timestamps are integers now.
            Assert.Equal("INTEGER", ColumnType(conn, "notes", "CreatedAt"));
            Assert.Equal("INTEGER", ColumnType(conn, "notes", "UpdatedAt"));
            Assert.Equal("INTEGER", ColumnType(conn, "categories", "UpdatedAt"));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    // ---------- 1b. built-in category identity (task 10-06, user ruling 2026-10-21) ----------

    [Fact]
    public void FreshDb_BuiltinCategories_CarryStableWellKnownUuids()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            new SchemaInitializer(factory).EnsureCreated();

            using var conn = factory.OpenRead();
            Assert.Equal(SchemaInitializer.WorkCategoryUuid, CategoryUuidByName(conn, "工作"));
            Assert.Equal(SchemaInitializer.LifeCategoryUuid, CategoryUuidByName(conn, "生活"));
            Assert.Equal(SchemaInitializer.ImportantCategoryUuid, CategoryUuidByName(conn, "重要"));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void V8Db_WithRandomBuiltinCategoryUuids_IsHealedOnNextLaunch()
    {
        // Simulates a pre-ruling v8 dev database: the seeded names carry random uuids.
        var dbPath = NewDbPath();
        try
        {
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            new SchemaInitializer(factory).EnsureCreated();
            using (var conn = factory.OpenWrite())
            using (var scramble = conn.CreateCommand())
            {
                scramble.CommandText =
                    "UPDATE categories SET Uuid = lower(hex(randomblob(16))) WHERE Name IN ('工作','生活','重要');";
                scramble.ExecuteNonQuery();
            }

            // The launch-time heal re-adopts the stable identity — and is idempotent.
            new SchemaInitializer(factory).EnsureCreated();
            new SchemaInitializer(factory).EnsureCreated();

            using var check = factory.OpenRead();
            Assert.Equal(SchemaInitializer.WorkCategoryUuid, CategoryUuidByName(check, "工作"));
            Assert.Equal(SchemaInitializer.LifeCategoryUuid, CategoryUuidByName(check, "生活"));
            Assert.Equal(SchemaInitializer.ImportantCategoryUuid, CategoryUuidByName(check, "重要"));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void RenamedBuiltinCategory_KeepsItsOwnUuid()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            new SchemaInitializer(factory).EnsureCreated();

            string renamedUuid;
            using (var conn = factory.OpenWrite())
            using (var rename = conn.CreateCommand())
            {
                // Same exact-match rule as CategoryDisplayNames: a renamed builtin
                // degrades to ordinary user data and must NOT adopt the stable id.
                rename.CommandText = "UPDATE categories SET Name = '我的工作' WHERE Name = '工作';";
                rename.ExecuteNonQuery();
                using var read = conn.CreateCommand();
                read.CommandText = "SELECT Uuid FROM categories WHERE Name = '我的工作';";
                renamedUuid = (string)read.ExecuteScalar()!;
            }

            new SchemaInitializer(factory).EnsureCreated();

            using var check = factory.OpenRead();
            using var cmd = check.CreateCommand();
            cmd.CommandText = "SELECT Uuid FROM categories WHERE Name = '我的工作';";
            Assert.Equal(renamedUuid, (string)cmd.ExecuteScalar()!);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    // ---------- 2. v7 -> v8 lossless ----------

    [Fact]
    public void V7Db_UpgradesToV8_Losslessly_ConvertingTimestampsToMs()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            CreateV7Database(factory);

            new SchemaInitializer(factory).EnsureCreated();

            using var check = factory.OpenRead();
            Assert.Equal(8, GetUserVersion(check));

            // Content survives verbatim; the row id (and therefore the FTS rowid) does too.
            Assert.Equal(1L, ScalarLong(check, "SELECT Id FROM notes WHERE Uuid = 'note-uuid-1';"));
            Assert.Equal("# 正文", Scalar(check, "SELECT Content FROM notes WHERE Uuid = 'note-uuid-1';"));
            Assert.Equal("正文", Scalar(check, "SELECT PlainText FROM notes WHERE Uuid = 'note-uuid-1';"));

            // ISO -> epoch-ms. The sub-second fraction must survive (strftime would
            // have truncated it), and the value must be a plain INTEGER.
            var expectedCreated = DateTimeOffset.Parse("2026-01-01T00:00:00.1234567Z").ToUnixTimeMilliseconds();
            var expectedUpdated = DateTimeOffset.Parse("2026-01-02T10:20:30.5000000Z").ToUnixTimeMilliseconds();
            Assert.Equal(expectedCreated, ScalarLong(check, "SELECT CreatedAt FROM notes WHERE Uuid = 'note-uuid-1';"));
            Assert.Equal(expectedUpdated, ScalarLong(check, "SELECT UpdatedAt FROM notes WHERE Uuid = 'note-uuid-1';"));

            // FTS rowids still line up with notes.Id.
            Assert.Equal(1L, ScalarLong(
                check, "SELECT COUNT(*) FROM notes WHERE Id IN (SELECT rowid FROM notes_fts);"));

            // Todo rows are untouched.
            Assert.Equal("已有任务", Scalar(check, "SELECT Title FROM todo_tasks WHERE Uuid = 'task-1';"));
            Assert.Equal(1L, ScalarLong(check, "SELECT CreatedAt FROM todo_tasks WHERE Uuid = 'task-1';"));

            // The v5 rename left note_images' FK intact (cascade still fires).
            using (var conn = factory.OpenWrite())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO note_images (note_id, sha256, created_at) " +
                    "VALUES (1, 'abc', '2026-01-01T00:00:00.0000000Z');" +
                    "DELETE FROM notes WHERE Id = 1;" +
                    "SELECT COUNT(*) FROM note_images;";
                Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
            }
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    /// <summary>
    /// The rebuild pages through notes (32 rows per page) because Content can hold
    /// RTF with inlined images — a whole-table read would pin hundreds of MB. This
    /// proves the paged copy is complete and correctly ordered across the boundary:
    /// a dropped or duplicated page would surface as a wrong row count, a gap in Ids,
    /// or a misparsed timestamp.
    /// </summary>
    [Fact]
    public void V7Db_WithMoreRowsThanOnePage_MigratesEveryRowAcrossPages()
    {
        const int rowCount = 100;
        var dbPath = NewDbPath();
        try
        {
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            CreateV7Database(factory);

            // Distinct, verifiable content per row so a shifted page is detectable.
            using (var conn = factory.OpenWrite())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    WITH RECURSIVE seq(n) AS (SELECT 2 UNION ALL SELECT n + 1 FROM seq WHERE n < 100)
                    INSERT INTO notes (Uuid, Title, Category, CreatedAt, UpdatedAt, PlainText, Content)
                    SELECT 'note-uuid-' || n, '笔记' || n, '工作',
                           '2026-01-01T00:00:' || printf('%02d', n % 60) || '.2500000Z',
                           '2026-01-02T00:00:' || printf('%02d', n % 60) || '.7500000Z',
                           '正文' || n, '#' || n
                    FROM seq;
                    """;
                cmd.ExecuteNonQuery();
            }

            new SchemaInitializer(factory).EnsureCreated();

            using var check = factory.OpenRead();

            Assert.Equal(8, GetUserVersion(check));
            Assert.Equal((long)rowCount, ScalarLong(check, "SELECT COUNT(*) FROM notes;"));

            // No Id was skipped or duplicated across page boundaries.
            Assert.Equal((long)rowCount, ScalarLong(check, "SELECT COUNT(DISTINCT Id) FROM notes;"));
            Assert.Equal((long)rowCount, ScalarLong(
                check, "SELECT MAX(Id) - MIN(Id) + 1 FROM notes;"));

            // Content and the converted timestamp survived for a row deep past the
            // first page (row 100 is in page 4).
            Assert.Equal("#100", Scalar(check, "SELECT Content FROM notes WHERE Uuid = 'note-uuid-100';"));
            Assert.Equal(
                DateTimeOffset.Parse("2026-01-02T00:00:40.7500000Z").ToUnixTimeMilliseconds(),
                ScalarLong(check, "SELECT UpdatedAt FROM notes WHERE Uuid = 'note-uuid-100';"));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    /// <summary>
    /// The read side typed its cursor as a value (AddWithValue). One declared
    /// <c>SqliteType.Integer</c>/string mix-up on <c>$after</c> makes SQLite compare
    /// <c>Id > '32'</c> as text, which skips most rows and leaves TEXT timestamps in
    /// the destination — the exact failure this pins. The rows straddle page
    /// boundaries and a mid-sized Content proves the blob does not shift the cursor.
    /// </summary>
    [Fact]
    public void V7Db_PageBoundaries_SurviveMixedContentSizes()
    {
        const int rowCount = 97;
        var dbPath = NewDbPath();
        try
        {
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            CreateV7Database(factory);
            using (var conn = factory.OpenWrite())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    WITH RECURSIVE seq(n) AS (SELECT 2 UNION ALL SELECT n + 1 FROM seq WHERE n < 97)
                    INSERT INTO notes (Uuid, Title, Category, CreatedAt, UpdatedAt, PlainText, Content)
                    SELECT 'note-uuid-' || n, '笔记' || n, '工作',
                           '2026-03-01T00:00:' || printf('%02d', n % 60) || '.0000001Z',
                           '2026-03-02T00:00:' || printf('%02d', n % 60) || '.9999999Z',
                           '正文' || n,
                           CASE WHEN n % 32 = 0 THEN '正文' || n || ' ' || printf('%.3000c', 'x') ELSE '#' || n END
                    FROM seq;
                    """;
                cmd.ExecuteNonQuery();
            }

            new SchemaInitializer(factory).EnsureCreated();

            using var check = factory.OpenRead();
            Assert.Equal((long)rowCount, ScalarLong(check, "SELECT COUNT(*) FROM notes;"));
            Assert.Equal(0L, ScalarLong(
                check, "SELECT COUNT(*) FROM notes WHERE typeof(CreatedAt) <> 'integer' OR typeof(UpdatedAt) <> 'integer';"));
            Assert.Equal(
                DateTimeOffset.Parse("2026-03-02T00:00:36.9999999Z").ToUnixTimeMilliseconds(),
                ScalarLong(check, "SELECT UpdatedAt FROM notes WHERE Uuid = 'note-uuid-96';"));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    /// <summary>
    /// A timestamp the v8 conversion cannot read must abort the migration with a
    /// message naming the row and column, not coerce it to 0 (decision 9: fail
    /// loudly). The transaction rollback then leaves the database at v7.
    /// </summary>
    [Theory]
    [InlineData("'not-a-timestamp'", "'not-a-timestamp'")]
    [InlineData("'2026-13-45T99:99:99Z'", "'2026-13-45T99:99:99Z'")]
    public void UnreadableNoteTimestamp_FailsTheMigrationLoudly_AndLeavesV7(
        string createdAtSql, string expectedFragment)
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            CreateV7Database(factory);
            using (var conn = factory.OpenWrite())
            using (var cmd = conn.CreateCommand())
            {
                // CreatedAt is TEXT NOT NULL in the v7 shape; a pre-v8 hand-built or
                // Qt-era row can still carry a non-ISO value or a REAL, which the
                // converter must refuse instead of coercing.
                cmd.CommandText = $"UPDATE notes SET CreatedAt = {createdAtSql} WHERE Uuid = 'note-uuid-1';";
                cmd.ExecuteNonQuery();
            }

            var ex = Assert.ThrowsAny<Exception>(
                () => new SchemaInitializer(factory).EnsureCreated());
            Assert.Contains("notes.CreatedAt", ex.Message);
            Assert.Contains("note 1", ex.Message);
            Assert.Contains(expectedFragment, ex.Message);

            using var check = factory.OpenRead();
            Assert.Equal(7L, GetUserVersion(check));
            Assert.False(TableExists(check, "sync_state"));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    // ---------- 3. v6 -> v8 in one hop ----------

    [Fact]
    public void V6Db_UpgradesToV8_InOneHop()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            CreateV6Database(factory);

            new SchemaInitializer(factory).EnsureCreated();

            using var check = factory.OpenRead();
            Assert.Equal(8, GetUserVersion(check));
            Assert.All(SyncedTables, t => Assert.True(ColumnExists(check, t, "DeletedAt"), $"{t}.DeletedAt missing"));
            Assert.True(TableExists(check, "sync_state"));
            Assert.True(ColumnExists(check, "categories", "CreatedAt"));

            // The v6 note's TEXT timestamp made it all the way to ms.
            var expected = DateTimeOffset.Parse("2026-01-01T00:00:00.0000000Z").ToUnixTimeMilliseconds();
            Assert.Equal(expected, ScalarLong(check, "SELECT CreatedAt FROM notes WHERE Uuid = 'note-uuid-1';"));
            Assert.Equal(2L, ScalarLong(check, "SELECT COUNT(*) FROM categories;"));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    // ---------- 4. idempotence ----------

    [Fact]
    public void Migration_IsIdempotent_SecondRunChangesNothing()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            var schema = new SchemaInitializer(factory);

            // A v7 database with real data, so a second pass would have something to
            // re-backfill if the step were not guarded.
            CreateV7Database(factory);
            schema.EnsureCreated();

            long createdAt;
            string uuid;
            using (var check = factory.OpenRead())
            {
                createdAt = ScalarLong(check, "SELECT CreatedAt FROM notes WHERE Uuid = 'note-uuid-1';");
                uuid = Scalar(check, "SELECT Uuid FROM categories WHERE Id = 1;");
            }

            schema.EnsureCreated();

            using (var check = factory.OpenRead())
            {
                Assert.Equal(8, GetUserVersion(check));
                Assert.Equal(createdAt, ScalarLong(check, "SELECT CreatedAt FROM notes WHERE Uuid = 'note-uuid-1';"));
                Assert.Equal(uuid, Scalar(check, "SELECT Uuid FROM categories WHERE Id = 1;"));
                Assert.Equal(1L, ScalarLong(check, "SELECT COUNT(*) FROM notes;"));
            }
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    // ---------- 5. categories.Uuid empty-string repair ----------

    [Fact]
    public void V7Category_WithEmptyUuid_IsRepairedAndUnique()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            CreateV7Database(factory);
            using (var conn = factory.OpenWrite())
            using (var cmd = conn.CreateCommand())
            {
                // A v7 row created through an older writer (or pre-v7 rows the v7
                // backfill missed) can still carry the DEFAULT '' identity.
                cmd.CommandText = "UPDATE categories SET Uuid = '';";
                cmd.ExecuteNonQuery();
            }

            new SchemaInitializer(factory).EnsureCreated();

            using var check = factory.OpenRead();
            var uuids = SelectCategoryUuids(check);
            Assert.Equal(2, uuids.Count);
            Assert.All(uuids, u => Assert.Matches("^[0-9a-f]{32}$", u));
            Assert.Equal(uuids.Count, uuids.Distinct().Count());

            // A duplicate identity is now rejected by the DB itself.
            using var dup = factory.OpenWrite();
            using var dupCmd = dup.CreateCommand();
            dupCmd.CommandText = "INSERT INTO categories (Name, Uuid) VALUES ('重复', $uuid);";
            dupCmd.Parameters.AddWithValue("$uuid", uuids[0]);
            Assert.Throws<SqliteException>(() => dupCmd.ExecuteNonQuery());
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    // ---------- 6. soft-delete visibility ----------

    [Fact]
    public async Task SoftDeletedNote_IsInvisible_ToListDetailAndSearch()
    {
        using var db = new TestDatabase();
        var notes = db.NewNoteService();
        var search = db.NewSearchService();

        var kept = await notes.CreateAsync("工作");
        var doomed = await notes.CreateAsync("工作");
        await notes.UpdateAsync(kept with { Title = "保留", Content = "保留正文" });
        await notes.UpdateAsync(doomed with { Title = "删除我", Content = "删除正文" });

        await notes.DeleteAsync(doomed.Id);

        Assert.Null(await notes.GetByIdAsync(doomed.Id));
        Assert.DoesNotContain(await notes.GetSummariesAsync(), s => s.Id == doomed.Id);
        Assert.DoesNotContain(await notes.GetSummariesAsync("工作"), s => s.Id == doomed.Id);
        Assert.DoesNotContain(await notes.GetAllAsync(), n => n.Id == doomed.Id);
        Assert.Empty(await search.SearchAsync("删除我"));
        Assert.Single(await search.SearchAsync("保留正文"));

        // Search reconcile counts live notes only — a tombstone must not look like
        // index drift and trigger an endless rebuild.
        await search.ReconcileAsync();
        Assert.Single(await search.SearchAsync("保留正文"));
    }

    [Fact]
    public async Task SoftDeletedCategory_IsInvisible()
    {
        using var db = new TestDatabase();
        var categories = db.NewCategoryService();
        var custom = await categories.CreateAsync("临时", "#3B82F6", "E8A5");

        await categories.DeleteAsync(custom.Id);

        Assert.DoesNotContain(await categories.GetAllAsync(), c => c.Id == custom.Id);
    }

    [Fact]
    public async Task SoftDeletedTodo_Rows_VanishFromEveryViewAndCount()
    {
        using var db = new TestDatabase();
        var todo = db.NewTodoService();
        var list = await todo.CreateListAsync("清单", "#3B82F6");
        var gone = await todo.CreateTaskAsync(list.Uuid, "要删的任务");
        var kept = await todo.CreateTaskAsync(list.Uuid, "留下的任务");
        await todo.SetDueAsync(gone.Id, DateTimeOffset.UtcNow.AddDays(-1), isAllDay: false, reminder: false);
        await todo.AddStepAsync(gone.Id, "子步骤");

        await todo.DeleteTaskAsync(gone.Id);

        Assert.Null(await todo.GetTaskAsync(gone.Id));
        Assert.Null(await todo.GetTaskDetailAsync(gone.Id));
        Assert.DoesNotContain(await todo.GetAllAsync(includeCompleted: true), t => t.Id == gone.Id);
        Assert.DoesNotContain(await todo.GetByListAsync(list.Uuid, includeCompleted: true), t => t.Id == gone.Id);
        Assert.DoesNotContain(await todo.GetPlannedAsync(includeCompleted: true), t => t.Id == gone.Id);
        Assert.DoesNotContain(await todo.GetTodayAsync(includeCompleted: true), t => t.Id == gone.Id);
        Assert.Empty(await todo.SearchByTitleAsync("要删"));
        Assert.Equal(1, (await todo.GetViewCountsAsync()).All);
        Assert.Single(await todo.GetAllAsync(includeCompleted: true));
        Assert.Equal(kept.Uuid, (await todo.GetAllAsync(includeCompleted: true))[0].Uuid);
    }

    [Fact]
    public async Task SoftDeletedList_HidesItsWholeTree()
    {
        using var db = new TestDatabase();
        var todo = db.NewTodoService();
        var list = await todo.CreateListAsync("清单", "#3B82F6");
        var task = await todo.CreateTaskAsync(list.Uuid, "任务");
        await todo.AddStepAsync(task.Id, "子步骤");

        await todo.DeleteListAsync(list.Id);

        Assert.DoesNotContain(await todo.GetListsAsync(), l => l.Id == list.Id);
        Assert.Null(await todo.GetTaskAsync(task.Id));
        Assert.Empty(await todo.GetByListAsync(list.Uuid, includeCompleted: true));
    }

    // ---------- 7. Delete = DeletedAt AND UpdatedAt, same instant ----------

    [Fact]
    public async Task SoftDelete_StampsDeletedAtAndBumpsUpdatedAtToTheSameInstant()
    {
        using var db = new TestDatabase();
        var notes = db.NewNoteService();
        var todo = db.NewTodoService();
        var list = await todo.CreateListAsync("清单", "#3B82F6");
        var task = await todo.CreateTaskAsync(list.Uuid, "任务");
        var note = await notes.CreateAsync();

        // Backdate both rows so any "now" is strictly newer.
        var longAgo = DateTimeOffset.UtcNow.AddDays(-5);
        await db.NewRepository().ImportNoteAsync(note with { UpdatedAt = longAgo });
        await db.NewTodoRepository().ImportTaskAsync(task with { UpdatedAt = longAgo });

        await notes.DeleteAsync(note.Id);
        await todo.DeleteTaskAsync(task.Id);

        foreach (var (deletedAt, updatedAt) in new[]
                 {
                     ReadNoteTimestamps(db, note.Id),
                     ReadTaskTimestamps(db, task.Id),
                 })
        {
            Assert.NotNull(deletedAt);
            Assert.Equal(deletedAt, updatedAt);
            Assert.True(updatedAt > longAgo, "a tombstone must move UpdatedAt forward or LWW resurrects it");
        }
    }

    // ---------- 8. ImportNoteAsync preserves identity and all three timestamps ----------

    [Fact]
    public async Task ImportNoteAsync_PreservesUuidAndAllTimestamps()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();

        var created = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123);
        var updated = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_100_456);
        var deleted = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_200_789);
        var import = new Note
        {
            Uuid = "synced-uuid-1",
            Title = "远端标题",
            Content = "# 远端正文",
            PlainText = "远端正文",
            Category = "工作",
            CreatedAt = created,
            UpdatedAt = updated,
            DeletedAt = deleted,
        };

        var first = await repo.ImportNoteAsync(import);
        Assert.True(first.Id > 0);

        // Re-applying the same container is idempotent (same local row, same values).
        var second = await repo.ImportNoteAsync(import);
        Assert.Equal(first.Id, second.Id);

        using (var check = db.Factory.OpenRead())
        {
            Assert.Equal("synced-uuid-1", Scalar(check, "SELECT Uuid FROM notes WHERE Id = $id;", first.Id));
            Assert.Equal(created.ToUnixTimeMilliseconds(), ScalarLong(check, "SELECT CreatedAt FROM notes WHERE Id = $id;", first.Id));
            Assert.Equal(updated.ToUnixTimeMilliseconds(), ScalarLong(check, "SELECT UpdatedAt FROM notes WHERE Id = $id;", first.Id));
            Assert.Equal(deleted.ToUnixTimeMilliseconds(), ScalarLong(check, "SELECT DeletedAt FROM notes WHERE Id = $id;", first.Id));
            // A tombstone is never searchable.
            Assert.Equal(0L, ScalarLong(check, "SELECT COUNT(*) FROM notes_fts WHERE rowid = $id;", first.Id));

            // Upserted by uuid, not inserted again.
            Assert.Equal(1L, ScalarLong(check, "SELECT COUNT(*) FROM notes;"));
        }

        // Clearing the tombstone (a remote edit that wins LWW) makes it visible again
        // and re-indexes it in the SAME transaction.
        var revived = await repo.ImportNoteAsync(import with
        {
            UpdatedAt = updated.AddMinutes(1),
            DeletedAt = null,
        });
        Assert.Equal(first.Id, revived.Id);
        Assert.NotNull(await repo.GetByIdAsync(first.Id));
        using (var check = db.Factory.OpenRead())
            Assert.Equal(1L, ScalarLong(check, "SELECT COUNT(*) FROM notes_fts WHERE rowid = $id;", first.Id));
    }

    // ---------- fixtures ----------

    /// <summary>
    /// Schema-v7 database (notes with TEXT ISO timestamps and no DeletedAt,
    /// categories with uuid but no clock, the full todo domain) — the shape the
    /// v8 step must upgrade.
    /// </summary>
    private static void CreateV7Database(DbConnectionFactory factory)
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
            CREATE VIRTUAL TABLE notes_fts USING fts5(
                title, body, content='', contentless_delete=1, tokenize='unicode61');
            CREATE TABLE settings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            CREATE TABLE categories (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Name      TEXT    NOT NULL UNIQUE,
                IconKey   TEXT    NOT NULL DEFAULT '',
                SortOrder INTEGER NOT NULL DEFAULT 0,
                Color     TEXT    NOT NULL DEFAULT '',
                Uuid      TEXT    NOT NULL DEFAULT ''
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
            CREATE TABLE todo_lists (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                Uuid          TEXT    NOT NULL UNIQUE,
                Name          TEXT    NOT NULL,
                Color         TEXT    NOT NULL DEFAULT '',
                SortOrder     INTEGER NOT NULL DEFAULT 0,
                CreatedAt     INTEGER NOT NULL,
                UpdatedAt     INTEGER NOT NULL,
                Theme         INTEGER NOT NULL DEFAULT 0,
                HideCompleted INTEGER NOT NULL DEFAULT 0,
                SortMode      INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE todo_tasks (
                Id             INTEGER PRIMARY KEY AUTOINCREMENT,
                Uuid           TEXT    NOT NULL UNIQUE,
                ListUuid       TEXT    NOT NULL,
                Title          TEXT    NOT NULL,
                Notes          TEXT    NOT NULL DEFAULT '',
                DueAt          INTEGER,
                IsAllDay       INTEGER NOT NULL DEFAULT 0,
                ReminderOn     INTEGER NOT NULL DEFAULT 0,
                IsImportant    INTEGER NOT NULL DEFAULT 0,
                CompletedAt    INTEGER,
                CatchUpFiredAt INTEGER,
                RecurrenceRule TEXT,
                CreatedAt      INTEGER NOT NULL,
                UpdatedAt      INTEGER NOT NULL
            );
            CREATE TABLE todo_steps (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                Uuid        TEXT    NOT NULL UNIQUE,
                TaskId      INTEGER NOT NULL,
                Title       TEXT    NOT NULL,
                CompletedAt INTEGER,
                SortOrder   INTEGER NOT NULL DEFAULT 0,
                CreatedAt   INTEGER NOT NULL,
                UpdatedAt   INTEGER NOT NULL
            );
            CREATE TABLE todo_attachments (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Uuid      TEXT    NOT NULL UNIQUE,
                TaskId    INTEGER NOT NULL,
                FileName  TEXT    NOT NULL,
                Sha       TEXT    NOT NULL,
                ByteSize  INTEGER NOT NULL,
                CreatedAt INTEGER NOT NULL,
                UpdatedAt INTEGER NOT NULL
            );

            INSERT INTO notes (Uuid, Title, Category, CreatedAt, UpdatedAt, PlainText, Content)
                VALUES ('note-uuid-1', '旧笔记', '工作',
                        '2026-01-01T00:00:00.1234567Z', '2026-01-02T10:20:30.5000000Z', '正文', '# 正文');
            INSERT INTO notes_fts (rowid, title, body) VALUES (1, '旧笔记', '正文');
            INSERT INTO categories (Name, IconKey, Color, SortOrder, Uuid)
                VALUES ('工作', 'E821', '#3B82F6', 0, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'),
                       ('自定义', 'E709', '#22C55E', 1, 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb');
            INSERT INTO todo_lists (Uuid, Name, Color, SortOrder, CreatedAt, UpdatedAt)
                VALUES ('list-1', '工作', '#3B82F6', 0, 1, 1);
            INSERT INTO todo_tasks (Uuid, ListUuid, Title, CreatedAt, UpdatedAt)
                VALUES ('task-1', 'list-1', '已有任务', 1, 1);
            INSERT INTO todo_steps (Uuid, TaskId, Title, SortOrder, CreatedAt, UpdatedAt)
                VALUES ('step-1', 1, '子步骤', 0, 1, 1);
            INSERT INTO todo_attachments (Uuid, TaskId, FileName, Sha, ByteSize, CreatedAt, UpdatedAt)
                VALUES ('att-1', 1, 'a.bin', 'cc', 1, 1, 1);
            PRAGMA user_version = 7;
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Schema-v6 database (pre-todo, categories without Uuid).</summary>
    private static void CreateV6Database(DbConnectionFactory factory)
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
            CREATE VIRTUAL TABLE notes_fts USING fts5(
                title, body, content='', contentless_delete=1, tokenize='unicode61');
            CREATE TABLE settings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            CREATE TABLE categories (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Name      TEXT    NOT NULL UNIQUE,
                IconKey   TEXT    NOT NULL DEFAULT '',
                SortOrder INTEGER NOT NULL DEFAULT 0,
                Color     TEXT    NOT NULL DEFAULT ''
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
            INSERT INTO categories (Name, IconKey, Color, SortOrder)
                VALUES ('工作', 'E821', '#3B82F6', 0), ('自定义', 'E709', '#22C55E', 1);
            PRAGMA user_version = 6;
            """;
        cmd.ExecuteNonQuery();
    }

    // ---------- helpers ----------

    private static (DateTimeOffset? DeletedAt, DateTimeOffset UpdatedAt) ReadNoteTimestamps(
        TestDatabase db, long id)
    {
        using var conn = db.Factory.OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DeletedAt, UpdatedAt FROM notes WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return (
            reader.IsDBNull(0) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)));
    }

    private static (DateTimeOffset? DeletedAt, DateTimeOffset UpdatedAt) ReadTaskTimestamps(
        TestDatabase db, long id)
    {
        using var conn = db.Factory.OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DeletedAt, UpdatedAt FROM todo_tasks WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return (
            reader.IsDBNull(0) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)));
    }

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), $"qnote-sync-migration-{Guid.NewGuid():N}.db");

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

    private static long GetUserVersion(SqliteConnection conn) =>
        ScalarLong(conn, "PRAGMA user_version;");

    private static string CategoryUuidByName(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Uuid FROM categories WHERE Name = $name;";
        cmd.Parameters.AddWithValue("$name", name);
        return (string)cmd.ExecuteScalar()!;
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

    private static string ColumnType(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                return reader.GetString(2);
        }
        return string.Empty;
    }

    private static IReadOnlyList<string> ColumnNames(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(1));
        return names;
    }

    private static string LastColumn(SqliteConnection conn, string table) =>
        ColumnNames(conn, table)[^1];

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

    private static string Scalar(SqliteConnection conn, string sql, long? id = null)
    {
        using var cmd = Create(conn, sql, id);
        return (string)(cmd.ExecuteScalar() ?? string.Empty);
    }

    private static long ScalarLong(SqliteConnection conn, string sql, long? id = null)
    {
        using var cmd = Create(conn, sql, id);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static SqliteCommand Create(SqliteConnection conn, string sql, long? id)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (id is { } value)
            cmd.Parameters.AddWithValue("$id", value);
        return cmd;
    }
}
