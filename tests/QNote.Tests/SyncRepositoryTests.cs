using Microsoft.Data.Sqlite;
using QNote.Data;
using QNote.Models;

namespace QNote.Tests;

/// <summary>
/// Battery for the sync-oriented repository additions (task 10-06 L1 step 3):
/// the all-rows stamp projections (tombstones included), the image+sync-state
/// extension of ImportNoteAsync (same-transaction composition), the verbatim
/// category import (no rename fan-out — each note's own container carries its
/// category name), and the tombstone re-stamp writers.
/// </summary>
public sealed class SyncRepositoryTests
{
    // ---------- stamp projections ----------

    [Fact]
    public async Task GetAllUuidTimestampsAsync_IncludesTombstonesWithExactStamps()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var live = await repo.ImportNoteAsync(Note("live", updatedAtMs: 100));
        var dead = await repo.ImportNoteAsync(Note("dead", updatedAtMs: 200, deletedAtMs: 200));

        var stamps = await repo.GetAllUuidTimestampsAsync();

        Assert.Equal(2, stamps.Count);
        var liveStamp = stamps.Single(s => s.Uuid == live.Uuid);
        Assert.True(liveStamp.IsLive);
        Assert.Equal(Unix(100), liveStamp.UpdatedAt);
        var deadStamp = stamps.Single(s => s.Uuid == dead.Uuid);
        Assert.False(deadStamp.IsLive);
        Assert.Equal(Unix(200), deadStamp.UpdatedAt);
        Assert.Equal(Unix(200), deadStamp.DeletedAt);
    }

    [Fact]
    public async Task GetAllCategoryUuidTimestampsAsync_IncludesTombstones()
    {
        using var db = new TestDatabase();
        ClearBuiltInCategories(db);
        var repo = db.NewRepository();
        await repo.ImportCategoryAsync(Category("live", updatedAtMs: 10));
        await repo.ImportCategoryAsync(Category("dead", updatedAtMs: 20, deletedAtMs: 20));

        var stamps = await repo.GetAllCategoryUuidTimestampsAsync();

        Assert.Equal(2, stamps.Count);
        Assert.Equal(1, stamps.Count(s => s.IsLive));
        Assert.Equal(1, stamps.Count(s => !s.IsLive));
    }

    // ---------- ImportNoteAsync extensions ----------

    [Fact]
    public async Task ImportNoteAsync_ImagesAndSyncState_LandInOneTransaction()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var note = Note("with-images", updatedAtMs: 500);
        var images = new[]
        {
            new NoteImage { Sha256 = new string('a', 64), Ext = "png", ByteSize = 10, Width = 4, Height = 3, DisplayBytes = [1, 2, 3] },
        };
        var state = new SyncStateEntry
        {
            EntryKind = SyncEntryKind.Note,
            EntryUuid = note.Uuid,
            RemoteId = "",
            Etag = "e1",
            LastSyncedAtMs = 500,
        };

        var imported = await repo.ImportNoteAsync(note, images, state);

        // The display blob rides the WITH-display projection (the metadata query
        // deliberately never selects display_bytes).
        var rows = await repo.GetNoteImagesWithDisplayAsync(imported.Id);
        var row = Assert.Single(rows);
        Assert.Equal(new string('a', 64), row.Sha256);
        Assert.Equal("png", row.Ext);
        Assert.Equal([1, 2, 3], row.DisplayBytes);
        var stored = await new SyncStateStore(db.Factory).GetAsync(SyncEntryKind.Note, note.Uuid);
        Assert.NotNull(stored);
        Assert.Equal("e1", stored!.Etag);
    }

    [Fact]
    public async Task ImportNoteAsync_ReimportWithNullDisplay_KeepsStoredBytes()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var note = Note("image-backfill", updatedAtMs: 500);
        var sha = new string('b', 64);
        await repo.ImportNoteAsync(note, [new NoteImage { Sha256 = sha, Ext = "png", ByteSize = 5, DisplayBytes = [9] }]);

        // The COALESCE rule: a null display copy must never wipe stored bytes.
        await repo.ImportNoteAsync(note with { Title = "updated" }, [new NoteImage { Sha256 = sha, Ext = "png", ByteSize = 5 }]);

        var updated = (await repo.GetAllAsync()).Single(n => n.Uuid == note.Uuid);
        Assert.Equal("updated", updated.Title);
        var row = Assert.Single(await repo.GetNoteImagesWithDisplayAsync(updated.Id));
        Assert.Equal([9], row.DisplayBytes);
    }

    // ---------- ImportCategoryAsync ----------

    [Fact]
    public async Task ImportCategoryAsync_UpsertIsVerbatimAndKeepsLocalId()
    {
        using var db = new TestDatabase();
        ClearBuiltInCategories(db);
        var repo = db.NewRepository();
        var original = await repo.ImportCategoryAsync(Category("work", updatedAtMs: 100, color: "#111111"));
        var localId = original.Id;

        var renamed = original with
        {
            Name = "work-renamed",
            Color = "#222222",
            SortOrder = 7,
            UpdatedAt = Unix(200),
        };
        var reimported = await repo.ImportCategoryAsync(renamed);

        Assert.Equal(localId, reimported.Id);
        var all = await repo.GetCategoriesAsync();
        var row = Assert.Single(all);
        Assert.Equal("work-renamed", row.Name);
        Assert.Equal("#222222", row.Color);
        Assert.Equal(7, row.SortOrder);
        Assert.Equal(Unix(200), row.UpdatedAt);
    }

    [Fact]
    public async Task ImportCategoryAsync_DoesNotFanARenameOutToNotes()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var category = await repo.ImportCategoryAsync(Category("cat", updatedAtMs: 100));
        var note = await repo.ImportNoteAsync(Note("n", updatedAtMs: 100, category: "cat"));

        await repo.ImportCategoryAsync(category with { Name = "cat2", UpdatedAt = Unix(200) });

        // The fan-out belongs to the notes' own sync (each container carries its
        // category name) — the local note's clock must NOT be bumped here.
        var stored = await repo.GetByIdAsync(note.Id);
        Assert.Equal("cat", stored!.Category);
        Assert.Equal(Unix(100), stored.UpdatedAt);
    }

    [Fact]
    public async Task ImportCategoryAsync_EmptyUuidIsRejected()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repo.ImportCategoryAsync(Category("", updatedAtMs: 1) with { Uuid = "" }));
    }

    // ---------- tombstone re-stamps ----------

    [Fact]
    public async Task ImportNoteTombstoneAsync_ReStampsTheRowAndDropsFts()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var note = await repo.ImportNoteAsync(Note("tombstoned", updatedAtMs: 100, deletedAtMs: 100));

        await repo.ImportNoteTombstoneAsync(
            note.Uuid, Unix(300),
            new SyncStateEntry { EntryKind = SyncEntryKind.Note, EntryUuid = note.Uuid, RemoteId = "", Etag = "e9", LastSyncedAtMs = 300 });

        // A tombstone is invisible to reads — verify the stamps via the projection.
        var stamp = (await repo.GetAllUuidTimestampsAsync()).Single(s => s.Uuid == note.Uuid);
        Assert.Equal(Unix(300), stamp.UpdatedAt);
        Assert.Equal(Unix(300), stamp.DeletedAt);
        Assert.Equal("e9", (await new SyncStateStore(db.Factory).GetAsync(SyncEntryKind.Note, note.Uuid))!.Etag);
        Assert.Equal(0L, Scalar(db, $"SELECT COUNT(*) FROM notes_fts WHERE rowid = {note.Id};"));
    }

    [Fact]
    public async Task ImportCategoryTombstoneAsync_ReStampsTheRow()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var category = await repo.ImportCategoryAsync(Category("gone", updatedAtMs: 100, deletedAtMs: 100));

        await repo.ImportCategoryTombstoneAsync(category.Uuid, Unix(400));

        var stamp = (await repo.GetAllCategoryUuidTimestampsAsync()).Single(s => s.Uuid == category.Uuid);
        Assert.Equal(Unix(400), stamp.UpdatedAt);
        Assert.Equal(Unix(400), stamp.DeletedAt);
    }

    [Fact]
    public async Task ImportTombstone_EmptyUuidIsRejected()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();

        await Assert.ThrowsAsync<ArgumentException>(() => repo.ImportNoteTombstoneAsync("", Unix(1)));
        await Assert.ThrowsAsync<ArgumentException>(() => repo.ImportCategoryTombstoneAsync("", Unix(1)));
    }

    // ---------- fixtures ----------

    /// <summary>Drops the v8-seeded built-in categories (random uuids, UpdatedAt=0)
    /// so counting assertions are deterministic.</summary>
    private static void ClearBuiltInCategories(TestDatabase db)
    {
        using var conn = db.Factory.OpenWrite();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM categories;";
        cmd.ExecuteNonQuery();
    }

    private static Note Note(string title, long updatedAtMs, long? deletedAtMs = null, string category = "") => new()
    {
        Uuid = Guid.NewGuid().ToString("N"),
        Title = title,
        Content = "content",
        PlainText = "content",
        Category = category,
        CreatedAt = Unix(1),
        UpdatedAt = Unix(updatedAtMs),
        DeletedAt = deletedAtMs is { } d ? Unix(d) : null,
    };

    private static Category Category(string name, long updatedAtMs, long? deletedAtMs = null, string color = "") => new()
    {
        Uuid = Guid.NewGuid().ToString("N"),
        Name = name,
        Color = color,
        CreatedAt = Unix(1),
        UpdatedAt = Unix(updatedAtMs),
        DeletedAt = deletedAtMs is { } d ? Unix(d) : null,
    };

    private static DateTimeOffset Unix(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    private static long Scalar(TestDatabase db, string sql)
    {
        using var conn = db.Factory.OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }
}
