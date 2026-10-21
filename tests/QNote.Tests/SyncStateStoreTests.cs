using Microsoft.Data.Sqlite;
using QNote.Data;
using QNote.Models;

namespace QNote.Tests;

/// <summary>
/// sync_state store battery (task 10-06, multi-device sync L1). Beyond CRUD, the
/// load-bearing tests are the two transaction-composition cases: the sync state
/// row and the data write it describes must commit together and roll back
/// together — the atomicity that PRD decision 11 bought by putting sync_state
/// into the database instead of a sidecar file.
/// </summary>
public sealed class SyncStateStoreTests
{
    // ---------- 1. CRUD ----------

    [Fact]
    public async Task UpsertThenGet_RoundTripsEveryField()
    {
        using var db = new TestDatabase();
        var store = new SyncStateStore(db.Factory);
        var entry = new SyncStateEntry
        {
            EntryKind = SyncEntryKind.Note,
            EntryUuid = "note-1",
            RemoteId = "onedrive-item-abc",
            Etag = "etag-42",
            LastSyncedAtMs = 1_700_000_000_123,
        };

        await store.UpsertAsync(entry);

        var loaded = await store.GetAsync(SyncEntryKind.Note, "note-1");
        Assert.NotNull(loaded);
        Assert.Equal(SyncEntryKind.Note, loaded.EntryKind);
        Assert.Equal("note-1", loaded.EntryUuid);
        Assert.Equal("onedrive-item-abc", loaded.RemoteId);
        Assert.Equal("etag-42", loaded.Etag);
        Assert.Equal(1_700_000_000_123, loaded.LastSyncedAtMs);
    }

    [Fact]
    public async Task Upsert_IsAnOverwrite_NotADuplicate()
    {
        using var db = new TestDatabase();
        var store = new SyncStateStore(db.Factory);
        await store.UpsertAsync(State(SyncEntryKind.Note, "note-1", remoteId: "old", etag: "e1", syncedAt: 1));
        await store.UpsertAsync(State(SyncEntryKind.Note, "note-1", remoteId: "new", etag: "e2", syncedAt: 2));

        var loaded = await store.GetAsync(SyncEntryKind.Note, "note-1");
        Assert.Equal(("new", "e2", 2), (loaded!.RemoteId, loaded.Etag, loaded.LastSyncedAtMs));
        Assert.Single(await store.GetByKindAsync(SyncEntryKind.Note));
    }

    [Fact]
    public async Task EmptyRemoteIdAndEtag_AreLegal()
    {
        using var db = new TestDatabase();
        var store = new SyncStateStore(db.Factory);

        await store.UpsertAsync(State(SyncEntryKind.Category, "cat-1", remoteId: "", etag: "", syncedAt: 0));

        var loaded = await store.GetAsync(SyncEntryKind.Category, "cat-1");
        Assert.Equal(("", "", 0), (loaded!.RemoteId, loaded.Etag, loaded.LastSyncedAtMs));
    }

    [Fact]
    public async Task GetUnknownEntry_ReturnsNull()
    {
        using var db = new TestDatabase();
        var store = new SyncStateStore(db.Factory);

        Assert.Null(await store.GetAsync(SyncEntryKind.TodoTask, "never-synced"));
    }

    [Fact]
    public async Task Kinds_AreIsolated_ForTheSameUuid()
    {
        using var db = new TestDatabase();
        var store = new SyncStateStore(db.Factory);
        await store.UpsertAsync(State(SyncEntryKind.Note, "shared", remoteId: "note-remote", syncedAt: 1));
        await store.UpsertAsync(State(SyncEntryKind.TodoTask, "shared", remoteId: "task-remote", syncedAt: 2));

        Assert.Equal("note-remote", (await store.GetAsync(SyncEntryKind.Note, "shared"))!.RemoteId);
        Assert.Equal("task-remote", (await store.GetAsync(SyncEntryKind.TodoTask, "shared"))!.RemoteId);
        Assert.Equal(2, (await store.GetByKindAsync(SyncEntryKind.Note)).Count
                       + (await store.GetByKindAsync(SyncEntryKind.TodoTask)).Count);
    }

    [Fact]
    public async Task GetByKind_ReturnsOnlyThatKind_OrderedByUuid()
    {
        using var db = new TestDatabase();
        var store = new SyncStateStore(db.Factory);
        await store.UpsertAsync(State(SyncEntryKind.Note, "b", syncedAt: 1));
        await store.UpsertAsync(State(SyncEntryKind.Note, "a", syncedAt: 2));
        await store.UpsertAsync(State(SyncEntryKind.TodoStep, "a", syncedAt: 3));

        var notes = await store.GetByKindAsync(SyncEntryKind.Note);

        Assert.Equal(["a", "b"], notes.Select(e => e.EntryUuid).ToArray());
        Assert.All(notes, e => Assert.Equal(SyncEntryKind.Note, e.EntryKind));
    }

    [Fact]
    public async Task Delete_RemovesOnlyTheTargetRow()
    {
        using var db = new TestDatabase();
        var store = new SyncStateStore(db.Factory);
        await store.UpsertAsync(State(SyncEntryKind.Note, "keep", syncedAt: 1));
        await store.UpsertAsync(State(SyncEntryKind.Note, "drop", syncedAt: 2));
        await store.UpsertAsync(State(SyncEntryKind.TodoTask, "drop", syncedAt: 3));

        await store.DeleteAsync(SyncEntryKind.Note, "drop");

        Assert.Null(await store.GetAsync(SyncEntryKind.Note, "drop"));
        Assert.NotNull(await store.GetAsync(SyncEntryKind.Note, "keep"));
        Assert.NotNull(await store.GetAsync(SyncEntryKind.TodoTask, "drop"));

        // Deleting an absent row is a no-op.
        await store.DeleteAsync(SyncEntryKind.Note, "drop");
    }

    // ---------- 2. same-transaction composition (PRD decision 11) ----------

    [Fact]
    public async Task UpsertWithin_CommitsTogetherWithTheDataWrite()
    {
        using var db = new TestDatabase();
        var store = new SyncStateStore(db.Factory);

        await using (var conn = db.Factory.OpenWrite())
        await using (var tx = conn.BeginTransaction())
        {
            await InsertNoteAsync(conn, tx, "remote-note");
            await SyncStateStore.UpsertWithinAsync(
                conn, tx, State(SyncEntryKind.Note, "remote-note", remoteId: "cloud-1", syncedAt: 99));
            await tx.CommitAsync();
        }

        // Both halves of the pair are visible, or neither is.
        Assert.NotNull(await store.GetAsync(SyncEntryKind.Note, "remote-note"));
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM notes WHERE Uuid = 'remote-note';"));
    }

    [Fact]
    public async Task UpsertWithin_RollbackDiscardsTheDataWriteAndTheSyncState()
    {
        using var db = new TestDatabase();
        var store = new SyncStateStore(db.Factory);
        // A pre-existing row must survive untouched — only the tx's own writes roll back.
        await store.UpsertAsync(State(SyncEntryKind.Note, "stable", syncedAt: 1));

        await using (var conn = db.Factory.OpenWrite())
        await using (var tx = conn.BeginTransaction())
        {
            await InsertNoteAsync(conn, tx, "doomed-note");
            await SyncStateStore.UpsertWithinAsync(
                conn, tx, State(SyncEntryKind.Note, "doomed-note", syncedAt: 2));
            // No commit — dispose rolls back.
        }

        Assert.Null(await store.GetAsync(SyncEntryKind.Note, "doomed-note"));
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM notes WHERE Uuid = 'doomed-note';"));
        Assert.NotNull(await store.GetAsync(SyncEntryKind.Note, "stable"));
    }

    [Fact]
    public async Task DeleteWithin_CommitsTogetherWithTheCallerTransaction()
    {
        using var db = new TestDatabase();
        var store = new SyncStateStore(db.Factory);
        await store.UpsertAsync(State(SyncEntryKind.TodoTask, "gone", syncedAt: 1));

        await using (var conn = db.Factory.OpenWrite())
        await using (var tx = conn.BeginTransaction())
        {
            await SyncStateStore.DeleteWithinAsync(conn, tx, SyncEntryKind.TodoTask, "gone");
            await tx.CommitAsync();
        }

        Assert.Null(await store.GetAsync(SyncEntryKind.TodoTask, "gone"));
    }

    // ---------- 3. validation ----------

    [Fact]
    public async Task InvalidInput_IsRejected()
    {
        using var db = new TestDatabase();
        var store = new SyncStateStore(db.Factory);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.UpsertAsync(State((SyncEntryKind)999, "x")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.UpsertAsync(State(SyncEntryKind.Note, "")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.GetAsync(SyncEntryKind.Note, ""));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.GetAsync((SyncEntryKind)999, "x"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.DeleteAsync((SyncEntryKind)999, "x"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.DeleteAsync(SyncEntryKind.Note, ""));
    }

    // ---------- fixtures ----------

    private static SyncStateEntry State(
        SyncEntryKind kind, string uuid,
        string remoteId = "remote", string etag = "etag", long syncedAt = 42) => new()
    {
        EntryKind = kind,
        EntryUuid = uuid,
        RemoteId = remoteId,
        Etag = etag,
        LastSyncedAtMs = syncedAt,
    };

    /// <summary>Stand-in for the real data write: a note row inserted on the SAME
    /// connection + transaction the sync state is recorded on.</summary>
    private static async Task InsertNoteAsync(
        SqliteConnection conn, SqliteTransaction tx, string uuid)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO notes (Uuid, Title, CreatedAt, UpdatedAt, PlainText, Content) " +
            "VALUES ($uuid, '标题', 1, 1, '', '');";
        cmd.Parameters.AddWithValue("$uuid", uuid);
        await cmd.ExecuteNonQueryAsync();
    }

    private static long Scalar(TestDatabase db, string sql)
    {
        using var conn = db.Factory.OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }
}
