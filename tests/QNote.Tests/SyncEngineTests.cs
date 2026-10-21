using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using QNote.Data;
using QNote.Markdown;
using QNote.Models;
using QNote.Sync;

namespace QNote.Tests;

/// <summary>
/// The sync-engine battery (task 10-06 L1 step 3, the design's test plan): fresh
/// downloads/uploads, LWW both directions, conflict → winner + loser copy, the
/// tombstone trio (+ the resurrect guard), idempotence (2nd run zero writes),
/// manifest version + E2EE-key mismatch, truncated-listing abort before
/// mutations, category round-trip, and the PreconditionFailed inline re-resolve.
/// The FakeSyncDriver is a path→bytes dictionary with an etag counter.
/// </summary>
public sealed class SyncEngineTests
{
    // ---------- downloads ----------

    [Fact]
    public async Task FreshDevice_DownloadsNote_ImagesFilesAndState()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var (imageBytes, imageSha) = Png(8, 5);
        var uuid = NewUuid();
        driver.Seed(
            $"notes/{uuid}.qnote",
            Container(uuid, "cloud title", "工作", "# hello", createdMs: 1, updatedMs: 5000,
                entries: [new QnoteEntry { Kind = QnoteEntryKind.Image, Sha256 = imageSha, FileName = "image.png", Data = imageBytes }]));

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(1, result.Downloaded);
        var repo = db.NewRepository();
        var note = Assert.Single(await repo.GetAllAsync());
        Assert.Equal(uuid, note.Uuid);
        Assert.Equal("cloud title", note.Title);
        Assert.Equal("工作", note.Category);
        Assert.Equal("# hello", note.Content);
        Assert.Equal(MarkdownText.ToPlainText("# hello"), note.PlainText);
        Assert.Equal(Unix(1), note.CreatedAt);
        Assert.Equal(Unix(5000), note.UpdatedAt);

        var image = Assert.Single(await repo.GetNoteImagesWithDisplayAsync(note.Id));
        Assert.Equal(imageSha, image.Sha256);
        Assert.Equal("png", image.Ext);
        Assert.Equal(imageBytes.LongLength, image.ByteSize);
        Assert.Equal(8, image.Width);
        Assert.Equal(5, image.Height);
        Assert.True(File.Exists(Path.Combine(db.Paths.ImagesDir, $"{imageSha}.png")));

        var state = await State(db, SyncEntryKind.Note, uuid);
        Assert.NotNull(state);
        Assert.Equal(5000, state!.LastSyncedAtMs);
    }

    [Fact]
    public async Task FreshDevice_DownloadsCategory()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var uuid = NewUuid();
        driver.SeedText(
            $"categories/{uuid}.json",
            CategoryJson(uuid, "生活", createdMs: 1, updatedMs: 700, sortOrder: 4, color: "#112233"));

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(1, result.Downloaded);
        var category = Assert.Single(await db.NewRepository().GetCategoriesAsync());
        Assert.Equal(uuid, category.Uuid);
        Assert.Equal("生活", category.Name);
        Assert.Equal(4, category.SortOrder);
        Assert.Equal("#112233", category.Color);
        Assert.Equal(Unix(700), category.UpdatedAt);
        Assert.Equal(700, (await State(db, SyncEntryKind.Category, uuid))!.LastSyncedAtMs);
    }

    [Fact]
    public async Task Download_SkipsAttachmentEntriesWithoutFailing()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var (bytes, sha) = Blob(10);
        var uuid = NewUuid();
        driver.Seed(
            $"notes/{uuid}.qnote",
            Container(uuid, "t", "", "body", 1, 100, entries:
            [
                new QnoteEntry { Kind = QnoteEntryKind.Attachment, Sha256 = sha, FileName = "file.txt", Data = bytes },
            ]));

        var result = await engine.RunAsync();

        // The todo cloud layout is undecided (PRD open question a) — skipped, not fatal.
        Assert.True(result.Ok);
        Assert.Equal(1, result.Downloaded);
        var note = Assert.Single(await db.NewRepository().GetAllAsync());
        Assert.Empty(await db.NewRepository().GetNoteImagesAsync(note.Id));
    }

    // ---------- uploads ----------

    [Fact]
    public async Task FreshCloud_UploadsNote_Category_AndManifest()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var note = await SeedNoteAsync(db, NewUuid(), "本地标题", "工作", "本地内容", updatedAtMs: 900);
        var category = await SeedCategoryAsync(db, NewUuid(), "分类", updatedAtMs: 800);

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(2, result.Uploaded);
        AssertCloudManifest(driver, expectE2ee: false);

        var container = QnoteContainerCodec.Decode(driver.Files[$"notes/{note.Uuid}.qnote"]);
        Assert.Equal(Guid.Parse(note.Uuid), container.Header.Uuid);
        Assert.Equal(900, container.Header.UpdatedAtMs);
        var (title, category_, content) = SyncNoteBody.Decode(container.Body);
        Assert.Equal("本地标题", title);
        Assert.Equal("工作", category_);
        Assert.Equal("本地内容", content);

        var catFile = System.Text.Json.JsonSerializer.Deserialize(
            driver.Files[$"categories/{category.Uuid}.json"], SyncCategoryFileJsonContext.Default.SyncCategoryFile)!;
        Assert.Equal("分类", catFile.Name);
        Assert.Equal(800, catFile.UpdatedAtMs);

        Assert.Equal(900, (await State(db, SyncEntryKind.Note, note.Uuid))!.LastSyncedAtMs);
        Assert.Equal(800, (await State(db, SyncEntryKind.Category, category.Uuid))!.LastSyncedAtMs);
    }

    [Fact]
    public async Task Upload_MissingOriginalFile_OmitsEntry()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var note = await SeedNoteAsync(db, NewUuid(), "t", "", "c", updatedAtMs: 100);
        await db.NewRepository().ImportNoteAsync(
            note with { },
            [new NoteImage { Sha256 = new string('c', 64), Ext = "png", ByteSize = 1 }]); // row without a file

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        var container = QnoteContainerCodec.Decode(driver.Files[$"notes/{note.Uuid}.qnote"]);
        Assert.Empty(container.Entries);
    }

    // ---------- LWW ----------

    [Fact]
    public async Task CloudNewer_Downloads()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var uuid = NewUuid();
        await SeedNoteAsync(db, uuid, "local", "", "local", updatedAtMs: 1000);
        driver.SeedText("manifest.json", PlainManifest);
        driver.Seed($"notes/{uuid}.qnote", Container(uuid, "cloud", "", "cloud", 1, 2000));
        await SeedStateAsync(db, SyncEntryKind.Note, uuid, etag: "stale", syncedAtMs: 1000);

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(1, result.Downloaded);
        Assert.Equal(0, result.Uploaded);
        var note = Assert.Single(await db.NewRepository().GetAllAsync());
        Assert.Equal("cloud", note.Content);
        Assert.Equal(Unix(2000), note.UpdatedAt);
        Assert.Equal(2000, (await State(db, SyncEntryKind.Note, uuid))!.LastSyncedAtMs);
    }

    [Fact]
    public async Task CloudOlder_LocalUploads()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var uuid = NewUuid();
        await SeedNoteAsync(db, uuid, "local", "", "local", updatedAtMs: 2000);
        driver.SeedText("manifest.json", PlainManifest);
        driver.Seed($"notes/{uuid}.qnote", Container(uuid, "cloud", "", "cloud", 1, 1000));
        // The state's etag matches the cloud file → the fast path infers C == S
        // (cloud unchanged since the agreement) → plain upload, no conflict.
        await SeedStateAsync(db, SyncEntryKind.Note, uuid, etag: driver.Etags[$"notes/{uuid}.qnote"], syncedAtMs: 1000);

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(1, result.Uploaded);
        Assert.Equal(0, result.ConflictCopies);
        Assert.Equal(2000, QnoteContainerCodec.ReadHeader(driver.Files[$"notes/{uuid}.qnote"]).UpdatedAtMs);
    }

    [Fact]
    public async Task Agreement_IsRecorded_WithoutRewrites()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var uuid = NewUuid();
        await SeedNoteAsync(db, uuid, "same", "", "same", updatedAtMs: 1000);
        driver.SeedText("manifest.json", PlainManifest);
        driver.Seed($"notes/{uuid}.qnote", Container(uuid, "same", "", "same", 1, 1000));

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(0, result.Uploaded);
        Assert.Equal(0, result.Downloaded);
        var state = await State(db, SyncEntryKind.Note, uuid);
        Assert.Equal(1000, state!.LastSyncedAtMs);
        Assert.Equal(driver.Etags[$"notes/{uuid}.qnote"], state.Etag);
    }

    // ---------- conflicts ----------

    [Fact]
    public async Task Conflict_LocalWins_CreatesCloudLoserCopyThenUploads()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var uuid = NewUuid();
        await SeedNoteAsync(db, uuid, "local-title", "工作", "local", updatedAtMs: 2000);
        driver.SeedText("manifest.json", PlainManifest);
        driver.Seed($"notes/{uuid}.qnote", Container(uuid, "cloud-title", "工作", "cloud", 1, 1500));
        await SeedStateAsync(db, SyncEntryKind.Note, uuid, etag: "agree", syncedAtMs: 1000);

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(1, result.ConflictCopies);
        Assert.Equal(1, result.Uploaded);

        var repo = db.NewRepository();
        var all = await repo.GetAllAsync();
        var winner = Assert.Single(all, n => n.Uuid == uuid);
        Assert.Equal("local", winner.Content);
        var copy = Assert.Single(all, n => n.Uuid != uuid);
        Assert.Equal("cloud-title (conflict)", copy.Title);
        Assert.Equal("cloud", copy.Content);
        Assert.Equal(Unix(1500), copy.UpdatedAt); // the loser's ORIGINAL stamps preserved
        Assert.Equal(Unix(1), copy.CreatedAt);

        Assert.Equal(2000, QnoteContainerCodec.ReadHeader(driver.Files[$"notes/{uuid}.qnote"]).UpdatedAtMs);
    }

    [Fact]
    public async Task Conflict_CloudWins_CreatesLocalLoserCopyThenApplies()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var uuid = NewUuid();
        await SeedNoteAsync(db, uuid, "local-title", "生活", "local", updatedAtMs: 2000);
        driver.SeedText("manifest.json", PlainManifest);
        driver.Seed($"notes/{uuid}.qnote", Container(uuid, "cloud-title", "生活", "cloud", 1, 2500));
        await SeedStateAsync(db, SyncEntryKind.Note, uuid, etag: "agree", syncedAtMs: 1000);

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(1, result.ConflictCopies);
        Assert.Equal(1, result.Downloaded);

        var repo = db.NewRepository();
        var all = await repo.GetAllAsync();
        var applied = Assert.Single(all, n => n.Uuid == uuid);
        Assert.Equal("cloud", applied.Content);
        Assert.Equal(Unix(2500), applied.UpdatedAt);
        var copy = Assert.Single(all, n => n.Uuid != uuid);
        Assert.Equal("local-title (conflict)", copy.Title);
        Assert.Equal("local", copy.Content);
        Assert.Equal(Unix(2000), copy.UpdatedAt);

        Assert.Equal(2500, QnoteContainerCodec.ReadHeader(driver.Files[$"notes/{uuid}.qnote"]).UpdatedAtMs);
    }

    [Fact]
    public async Task Conflict_CategoryIsPlainLww_NoCopy()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var uuid = NewUuid();
        await SeedCategoryAsync(db, uuid, "local-name", updatedAtMs: 2000);
        driver.SeedText("manifest.json", PlainManifest);
        driver.SeedText($"categories/{uuid}.json", CategoryJson(uuid, "cloud-name", 1, 1500));
        await SeedStateAsync(db, SyncEntryKind.Category, uuid, etag: "agree", syncedAtMs: 1000);

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(0, result.ConflictCopies);
        Assert.Equal(1, result.Uploaded);
        var category = Assert.Single(await db.NewRepository().GetCategoriesAsync());
        Assert.Equal("local-name", category.Name);
        Assert.Equal(0, result.Downloaded);
        Assert.Equal(2000, CategoryUpdatedMs(driver, uuid));
    }

    [Fact]
    public async Task ConflictCopy_TitleSuffix_IsInjectable()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db, conflictSuffix: "（冲突副本）");
        var uuid = NewUuid();
        await SeedNoteAsync(db, uuid, "local", "", "local", updatedAtMs: 2000);
        driver.SeedText("manifest.json", PlainManifest);
        driver.Seed($"notes/{uuid}.qnote", Container(uuid, "cloud", "", "cloud", 1, 1500));
        await SeedStateAsync(db, SyncEntryKind.Note, uuid, etag: "agree", syncedAtMs: 1000);

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        var copy = Assert.Single(await db.NewRepository().GetAllAsync(), n => n.Uuid != uuid);
        Assert.Equal("cloud（冲突副本）", copy.Title);
    }

    [Fact]
    public async Task PreconditionFailed_ReResolvesInline()
    {        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var uuid = NewUuid();
        await SeedNoteAsync(db, uuid, "local-title", "", "local", updatedAtMs: 2000);
        driver.SeedText("manifest.json", PlainManifest);
        driver.Seed($"notes/{uuid}.qnote", Container(uuid, "cloud-title", "", "cloud", 1, 1500));
        await SeedStateAsync(db, SyncEntryKind.Note, uuid, etag: "agree", syncedAtMs: 1000);
        // A peer writes between the engine's read and write → the first (conditional)
        // upload fails → the engine re-reads the fresh state and re-decides once.
        driver.ThrowPreconditionOnNextWrite = true;

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(1, result.ConflictCopies);
        Assert.Equal(2000, QnoteContainerCodec.ReadHeader(driver.Files[$"notes/{uuid}.qnote"]).UpdatedAtMs);
        var repo = db.NewRepository();
        Assert.Equal(2, (await repo.GetAllAsync()).Count); // winner + loser copy
    }

    // ---------- LWW ties (L1 step 4: ConflictResolver) ----------

    [Fact]
    public async Task Tie_BenignReencode_RefreshesEtagWithoutCopies()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var uuid = NewUuid();
        await SeedNoteAsync(db, uuid, "same", "", "same", updatedAtMs: 1000);
        driver.SeedText("manifest.json", PlainManifest);
        // A peer re-uploaded the IDENTICAL content: same bytes, new etag.
        var container = Container(uuid, "same", "", "same", 1, 1000);
        driver.Seed($"notes/{uuid}.qnote", container);
        await SeedStateAsync(db, SyncEntryKind.Note, uuid, etag: driver.Etags[$"notes/{uuid}.qnote"], syncedAtMs: 1000);
        driver.Seed($"notes/{uuid}.qnote", container); // etag drifts, bytes do not

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(0, result.Uploaded);
        Assert.Equal(0, result.Downloaded);
        Assert.Equal(0, result.ConflictCopies);
        Assert.Single(await db.NewRepository().GetAllAsync());
        // The agreement was refreshed with the new etag, so the next cycle is a no-op.
        Assert.Equal(driver.Etags[$"notes/{uuid}.qnote"], (await State(db, SyncEntryKind.Note, uuid))!.Etag);
    }

    [Fact]
    public async Task Tie_DifferentContent_TiebreaksByDigest()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var uuid = NewUuid();
        // Pick contents whose digests have a KNOWN order (larger digest wins), so
        // the assertions below are deterministic rather than lucky.
        var cloudContent = "cloud-body";
        var cloudDigest = DigestOf("cloud-title", "", cloudContent);
        var localContent = cloudContent;
        var localDigest = cloudDigest;
        for (var i = 0; localDigest.CompareTo(cloudDigest) <= 0; i++)
        {
            localContent = $"local-body-{i}";
            localDigest = DigestOf("local-title", "", localContent);
        }

        await SeedNoteAsync(db, uuid, "local-title", "", localContent, updatedAtMs: 1000);
        driver.SeedText("manifest.json", PlainManifest);
        driver.Seed($"notes/{uuid}.qnote", Container(uuid, "cloud-title", "", cloudContent, 1, 1000));
        await SeedStateAsync(db, SyncEntryKind.Note, uuid, etag: "agree", syncedAtMs: 1000);

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(1, result.ConflictCopies);
        Assert.Equal(1, result.Uploaded); // local wins → the winner pushes unconditionally

        var repo = db.NewRepository();
        var winner = Assert.Single(await repo.GetAllAsync(), n => n.Uuid == uuid);
        Assert.Equal(localContent, winner.Content);
        var copy = Assert.Single(await repo.GetAllAsync(), n => n.Uuid != uuid);
        Assert.Equal("cloud-title (conflict)", copy.Title);
        Assert.Equal(cloudContent, copy.Content);
        Assert.Equal(1000, QnoteContainerCodec.ReadHeader(driver.Files[$"notes/{uuid}.qnote"]).UpdatedAtMs);
    }

    [Fact]
    public async Task Tie_CloudDigestWins_AppliesCloudWithLocalLoserCopy()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var uuid = NewUuid();
        var localContent = "local-body";
        var localDigest = DigestOf("local-title", "", localContent);
        var cloudContent = localContent;
        var cloudDigest = localDigest;
        for (var i = 0; cloudDigest.CompareTo(localDigest) <= 0; i++)
        {
            cloudContent = $"cloud-body-{i}";
            cloudDigest = DigestOf("cloud-title", "", cloudContent);
        }

        await SeedNoteAsync(db, uuid, "local-title", "", localContent, updatedAtMs: 1000);
        driver.SeedText("manifest.json", PlainManifest);
        driver.Seed($"notes/{uuid}.qnote", Container(uuid, "cloud-title", "", cloudContent, 1, 1000));
        await SeedStateAsync(db, SyncEntryKind.Note, uuid, etag: "agree", syncedAtMs: 1000);

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(1, result.ConflictCopies);
        Assert.Equal(1, result.Downloaded);

        var repo = db.NewRepository();
        var applied = Assert.Single(await repo.GetAllAsync(), n => n.Uuid == uuid);
        Assert.Equal(cloudContent, applied.Content);
        var copy = Assert.Single(await repo.GetAllAsync(), n => n.Uuid != uuid);
        Assert.Equal("local-title (conflict)", copy.Title);
        Assert.Equal(localContent, copy.Content);
    }

    [Fact]
    public async Task CategoryTie_BenignRefresh_HasNoCopies()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var uuid = NewUuid();
        await SeedCategoryAsync(db, uuid, "同名", updatedAtMs: 1000);
        driver.SeedText("manifest.json", PlainManifest);
        var json = CategoryJson(uuid, "同名", 1, 1000);
        driver.SeedText($"categories/{uuid}.json", json);
        await SeedStateAsync(db, SyncEntryKind.Category, uuid, etag: driver.Etags[$"categories/{uuid}.json"], syncedAtMs: 1000);
        driver.SeedText($"categories/{uuid}.json", json); // etag drifts, content does not

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(0, result.Uploaded);
        Assert.Equal(0, result.Downloaded);
        Assert.Equal(0, result.ConflictCopies);
        Assert.Equal(driver.Etags[$"categories/{uuid}.json"], (await State(db, SyncEntryKind.Category, uuid))!.Etag);
    }

    [Fact]
    public async Task CategoryTie_DifferentContent_TiebreaksWithoutCopy()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var uuid = NewUuid();
        // Local wins here: seed a cloud name whose JSON digest is the smaller one.
        var cloudName = "云";
        var localName = cloudName;
        while (DigestJson(uuid, localName, 1000).CompareTo(DigestJson(uuid, cloudName, 1000)) <= 0)
            localName = localName + "地";
        await SeedCategoryAsync(db, uuid, localName, updatedAtMs: 1000);
        driver.SeedText("manifest.json", PlainManifest);
        driver.SeedText($"categories/{uuid}.json", CategoryJson(uuid, cloudName, 1, 1000));
        await SeedStateAsync(db, SyncEntryKind.Category, uuid, etag: "agree", syncedAtMs: 1000);

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(1, result.Uploaded);
        Assert.Equal(0, result.ConflictCopies); // categories are plain LWW — no copies
        var category = Assert.Single(await db.NewRepository().GetCategoriesAsync());
        Assert.Equal(localName, category.Name);
    }

    // ---------- tombstones ----------

    [Fact]
    public async Task LocalDelete_PushesTombstone_AndRemovesContainer()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var uuid = NewUuid();
        await SeedNoteAsync(db, uuid, "t", "", "c", updatedAtMs: 1000, deletedAtMs: 3000);
        driver.SeedText("manifest.json", PlainManifest);
        driver.Seed($"notes/{uuid}.qnote", Container(uuid, "t", "", "c", 1, 1000));

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(1, result.DeletedRemote);
        Assert.False(driver.Files.ContainsKey($"notes/{uuid}.qnote"));
        Assert.Equal(3000, TombstoneMs(driver, uuid));
        Assert.Equal(3000, (await State(db, SyncEntryKind.Note, uuid))!.LastSyncedAtMs);
    }

    [Fact]
    public async Task PeerTombstone_AppliesLocallyVerbatim()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var uuid = NewUuid();
        var note = await SeedNoteAsync(db, uuid, "t", "", "c", updatedAtMs: 1000);
        driver.SeedText("manifest.json", PlainManifest);
        driver.Seed($"tombstones/{uuid}", TombstoneBytes(2000));

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(1, result.TombstonesApplied);
        var stamp = (await db.NewRepository().GetAllUuidTimestampsAsync()).Single(s => s.Uuid == uuid);
        Assert.Equal(Unix(2000), stamp.UpdatedAt);
        Assert.Equal(Unix(2000), stamp.DeletedAt);
        Assert.Equal(2000, (await State(db, SyncEntryKind.Note, uuid))!.LastSyncedAtMs);
        Assert.Equal(0L, Scalar(db, $"SELECT COUNT(*) FROM notes_fts WHERE rowid = {note.Id};"));
    }

    [Fact]
    public async Task StaleTombstone_Removed_AndLocalEditUploaded()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var uuid = NewUuid();
        await SeedNoteAsync(db, uuid, "t", "", "edited", updatedAtMs: 3000);
        driver.SeedText("manifest.json", PlainManifest);
        driver.Seed($"tombstones/{uuid}", TombstoneBytes(1000));

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.False(driver.Files.ContainsKey($"tombstones/{uuid}"));
        Assert.Equal(3000, QnoteContainerCodec.ReadHeader(driver.Files[$"notes/{uuid}.qnote"]).UpdatedAtMs);
        Assert.Equal(1, result.DeletedRemote);
        Assert.Equal(1, result.Uploaded);
    }

    [Fact]
    public async Task EditNewerThanTombstone_ResurrectsOnTheDeletingPeer()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var uuid = NewUuid();
        await SeedNoteAsync(db, uuid, "old", "", "old", updatedAtMs: 1000, deletedAtMs: 1000);
        driver.SeedText("manifest.json", PlainManifest);
        driver.Seed($"notes/{uuid}.qnote", Container(uuid, "new", "", "new", 1, 2000));

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(0, result.DeletedRemote); // the stale delete must NOT be propagated
        Assert.Equal(1, result.Downloaded);
        var note = Assert.Single(await db.NewRepository().GetAllAsync());
        Assert.Equal("new", note.Content);
        Assert.True(driver.Files.ContainsKey($"notes/{uuid}.qnote"));
        Assert.False(driver.Files.ContainsKey($"tombstones/{uuid}"));
    }

    [Fact]
    public async Task TombstoneReconcile_LocalNewer_RewritesFile()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var uuid = NewUuid();
        await SeedNoteAsync(db, uuid, "t", "", "c", updatedAtMs: 1000, deletedAtMs: 3000);
        driver.SeedText("manifest.json", PlainManifest);
        driver.Seed($"tombstones/{uuid}", TombstoneBytes(1000));

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(3000, TombstoneMs(driver, uuid));
        Assert.Equal(1, result.Uploaded);
        Assert.Equal(0, result.DeletedRemote);
    }

    [Fact]
    public async Task TombstoneReconcile_PeerNewer_ReStampsLocalRow()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var uuid = NewUuid();
        await SeedNoteAsync(db, uuid, "t", "", "c", updatedAtMs: 1000, deletedAtMs: 1000);
        driver.SeedText("manifest.json", PlainManifest);
        driver.Seed($"tombstones/{uuid}", TombstoneBytes(3000));

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(1, result.TombstonesApplied);
        var stamp = (await db.NewRepository().GetAllUuidTimestampsAsync()).Single(s => s.Uuid == uuid);
        Assert.Equal(Unix(3000), stamp.UpdatedAt);
        Assert.Equal(Unix(3000), stamp.DeletedAt);
    }

    [Fact]
    public async Task TombstoneForUnknownEntry_IsIgnored()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        driver.SeedText("manifest.json", PlainManifest);
        driver.Seed($"tombstones/{NewUuid()}", TombstoneBytes(1));

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.Empty(driver.Writes);
        Assert.Empty(driver.Deletes);
    }

    [Fact]
    public async Task TombstonePlusContainer_WithNoLocalRow_GarbageCollectsContainer()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var uuid = NewUuid();
        driver.SeedText("manifest.json", PlainManifest);
        driver.Seed($"tombstones/{uuid}", TombstoneBytes(500));
        driver.Seed($"notes/{uuid}.qnote", Container(uuid, "t", "", "c", 1, 100));

        var result = await engine.RunAsync();

        Assert.True(result.Ok);
        Assert.True(driver.Files.ContainsKey($"tombstones/{uuid}"));
        Assert.False(driver.Files.ContainsKey($"notes/{uuid}.qnote"));
        Assert.Equal(1, result.DeletedRemote);
    }

    // ---------- idempotence ----------

    [Fact]
    public async Task SecondRun_WritesNothing()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        await SeedNoteAsync(db, NewUuid(), "t", "工作", "c", updatedAtMs: 100);
        await SeedCategoryAsync(db, NewUuid(), "分类", updatedAtMs: 200);

        await engine.RunAsync();
        var writesAfterFirst = driver.Writes.Count;
        var deletesAfterFirst = driver.Deletes.Count;

        var second = await engine.RunAsync();

        Assert.True(second.Ok);
        Assert.Equal(writesAfterFirst, driver.Writes.Count);
        Assert.Equal(deletesAfterFirst, driver.Deletes.Count);
    }

    // ---------- manifest ----------

    [Fact]
    public async Task Manifest_NewerVersion_AbortsBeforeMutations()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        await SeedNoteAsync(db, NewUuid(), "t", "", "c", updatedAtMs: 100);
        driver.SeedText("manifest.json", """{"formatVersion":99,"e2eeEnabled":false,"e2eeSalt":null}""");

        var result = await engine.RunAsync();

        Assert.Equal(SyncCycleAbortKind.UnsupportedManifest, result.AbortKind);
        Assert.Empty(driver.Writes);
        Assert.Empty(driver.Deletes);
    }

    [Fact]
    public async Task Manifest_E2eeMismatch_Aborts()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        driver.SeedText("manifest.json", EncryptedManifest);
        driver.SeedText("categories/whatever.json", "{}");

        var withoutKey = await engine.RunAsync();
        Assert.Equal(SyncCycleAbortKind.E2eeKeyMismatch, withoutKey.AbortKind);
        Assert.Empty(driver.Writes);

        driver.SeedText("manifest.json", PlainManifest);
        var withKey = await engine.RunAsync(e2eeKey: new byte[32]);
        Assert.Equal(SyncCycleAbortKind.E2eeKeyMismatch, withKey.AbortKind);
        Assert.Empty(driver.Writes);
    }

    [Fact]
    public async Task FreshCloud_WithKey_WritesSaltAndEncryptsContainers()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        var note = await SeedNoteAsync(db, NewUuid(), "secret", "", "secret", updatedAtMs: 100);
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);

        var result = await engine.RunAsync(e2eeKey: key);

        Assert.True(result.Ok);
        AssertCloudManifest(driver, expectE2ee: true);
        var container = driver.Files[$"notes/{note.Uuid}.qnote"];
        Assert.Equal(2, container[0x2C]); // encoding byte = Deflate + AES-256-GCM
        Assert.Throws<QnoteContainerException>(() => QnoteContainerCodec.Decode(container));
        var decoded = QnoteContainerCodec.Decode(container, key);
        Assert.Equal("secret", SyncNoteBody.Decode(decoded.Body).Content);
    }

    [Fact]
    public async Task TruncatedListing_Aborts_BeforeAnyMutation()
    {
        using var db = new TestDatabase();
        var (engine, driver) = NewEngine(db);
        await SeedNoteAsync(db, NewUuid(), "t", "", "c", updatedAtMs: 100);
        driver.Truncated = true;

        var result = await engine.RunAsync();

        Assert.Equal(SyncCycleAbortKind.ListingIncomplete, result.AbortKind);
        Assert.Empty(driver.Writes);
        Assert.Empty(driver.Deletes);
    }

    // ---------- display-copy callback ----------

    [Fact]
    public async Task DisplayGenerator_FillsDisplayBytes_AndFailuresDegrade()
    {
        using var db = new TestDatabase();
        var driver = new FakeSyncDriver();
        var calls = 0;
        var engine = new SyncEngine(
            db.NewRepository(), new SyncStateStore(db.Factory), driver, db.Paths,
            NullLogger<SyncEngine>.Instance,
            displayImageGenerator: (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("boom");
            });
        var (imageBytes, imageSha) = Png(4, 4);
        var uuid = NewUuid();
        driver.Seed(
            $"notes/{uuid}.qnote",
            Container(uuid, "t", "", "c", 1, 100,
                entries: [new QnoteEntry { Kind = QnoteEntryKind.Image, Sha256 = imageSha, FileName = "image.png", Data = imageBytes }]));

        var result = await engine.RunAsync();

        Assert.True(result.Ok); // a throwing generator degrades that image, never the cycle
        Assert.Equal(1, calls);
        var note = Assert.Single(await db.NewRepository().GetAllAsync());
        var image = Assert.Single(await db.NewRepository().GetNoteImagesWithDisplayAsync(note.Id));
        Assert.Null(image.DisplayBytes);
    }

    // ---------- fixtures ----------

    private const string PlainManifest = """{"formatVersion":1,"e2eeEnabled":false,"e2eeSalt":null}""";
    private const string EncryptedManifest = """{"formatVersion":1,"e2eeEnabled":true,"e2eeSalt":"AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHw8="}""";

    private static (SyncEngine Engine, FakeSyncDriver Driver) NewEngine(
        TestDatabase db,
        SyncEngine.DisplayImageGenerator? display = null,
        string? conflictSuffix = null)
    {
        // The v8 schema seeds the built-in categories (工作/生活/重要) — every engine
        // test would otherwise upload all three (and consume one-shot failure
        // injections). Deterministic tests start from an empty categories table.
        using (var conn = db.Factory.OpenWrite())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM categories;";
            cmd.ExecuteNonQuery();
        }

        var driver = new FakeSyncDriver();
        var engine = new SyncEngine(
            db.NewRepository(), new SyncStateStore(db.Factory), driver, db.Paths,
            NullLogger<SyncEngine>.Instance, display, conflictSuffix);
        return (engine, driver);
    }

    private static string NewUuid() => Guid.NewGuid().ToString("N");

    private static DateTimeOffset Unix(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    private static async Task<Note> SeedNoteAsync(
        TestDatabase db, string uuid, string title, string category, string content,
        long updatedAtMs, long? deletedAtMs = null)
    {
        var note = new Note
        {
            Uuid = uuid,
            Title = title,
            Category = category,
            Content = content,
            PlainText = MarkdownText.ToPlainText(content),
            CreatedAt = Unix(1),
            UpdatedAt = Unix(updatedAtMs),
            DeletedAt = deletedAtMs is { } d ? Unix(d) : null,
        };
        return await db.NewRepository().ImportNoteAsync(note);
    }

    private static async Task<Category> SeedCategoryAsync(TestDatabase db, string uuid, string name, long updatedAtMs)
    {
        var category = new Category
        {
            Uuid = uuid,
            Name = name,
            CreatedAt = Unix(1),
            UpdatedAt = Unix(updatedAtMs),
        };
        return await db.NewRepository().ImportCategoryAsync(category);
    }

    private static async Task SeedStateAsync(TestDatabase db, SyncEntryKind kind, string uuid, string etag, long syncedAtMs) =>
        await new SyncStateStore(db.Factory).UpsertAsync(new SyncStateEntry
        {
            EntryKind = kind,
            EntryUuid = uuid,
            RemoteId = "",
            Etag = etag,
            LastSyncedAtMs = syncedAtMs,
        });

    private static Task<SyncStateEntry?> State(TestDatabase db, SyncEntryKind kind, string uuid) =>
        new SyncStateStore(db.Factory).GetAsync(kind, uuid);

    private static byte[] Container(
        string uuid, string title, string category, string content,
        long createdMs, long updatedMs, QnoteEntry[]? entries = null, byte[]? key = null) =>
        QnoteContainerCodec.Encode(
            new QnoteHeader { Uuid = Guid.Parse(uuid), CreatedAtMs = createdMs, UpdatedAtMs = updatedMs, DeletedAtMs = 0 },
            SyncNoteBody.Encode(title, category, content),
            entries ?? [],
            key);

    private static string CategoryJson(string uuid, string name, long createdMs, long updatedMs, int sortOrder = 0, string color = "") =>
        System.Text.Json.JsonSerializer.Serialize(
            new SyncCategoryFile
            {
                Uuid = uuid,
                Name = name,
                SortOrder = sortOrder,
                Color = color,
                CreatedAtMs = createdMs,
                UpdatedAtMs = updatedMs,
            },
            SyncCategoryFileJsonContext.Default.SyncCategoryFile);

    private static byte[] TombstoneBytes(long deletedAtMs)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, deletedAtMs);
        return bytes;
    }

    /// <summary>Mirrors the engine's note tie digest: sha256 over the canonical wire envelope.</summary>
    private static string DigestOf(string title, string category, string content) =>
        Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(SyncNoteBody.Encode(title, category, content))));

    private static string DigestJson(string uuid, string name, long updatedAtMs) =>
        Convert.ToHexStringLower(SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new SyncCategoryFile { Uuid = uuid, Name = name, CreatedAtMs = 1, UpdatedAtMs = updatedAtMs },
                SyncCategoryFileJsonContext.Default.SyncCategoryFile)));

    private static long TombstoneMs(FakeSyncDriver driver, string uuid) =>
        BinaryPrimitives.ReadInt64LittleEndian(driver.Files[$"tombstones/{uuid}"]);

    private static long CategoryUpdatedMs(FakeSyncDriver driver, string uuid)
    {
        var file = System.Text.Json.JsonSerializer.Deserialize(
            driver.Files[$"categories/{uuid}.json"], SyncCategoryFileJsonContext.Default.SyncCategoryFile)!;
        return file.UpdatedAtMs;
    }

    /// <summary>Reads the cloud manifest back and asserts the E2EE mode (+ salt shape when enabled).</summary>
    private static void AssertCloudManifest(FakeSyncDriver driver, bool expectE2ee)
    {
        var manifest = System.Text.Json.JsonSerializer.Deserialize(
            driver.Files["manifest.json"], SyncManifestJsonContext.Default.SyncManifest)!;
        Assert.Equal(1, manifest.FormatVersion);
        Assert.Equal(expectE2ee, manifest.E2eeEnabled);
        if (expectE2ee)
        {
            Assert.NotNull(manifest.E2eeSalt);
            Assert.Equal(32, Convert.FromBase64String(manifest.E2eeSalt!).Length);
        }
    }

    private static long Scalar(TestDatabase db, string sql)
    {
        using var conn = db.Factory.OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>A minimal PNG-header blob (signature + IHDR dims) the payload sniffer accepts.</summary>
    private static (byte[] Bytes, string Sha) Png(int width, int height)
    {
        var bytes = new byte[33];
        bytes[0] = 0x89;
        bytes[1] = 0x50; // P
        bytes[2] = 0x4E; // N
        bytes[3] = 0x47; // G
        bytes[4] = 0x0D;
        bytes[5] = 0x0A;
        bytes[6] = 0x1A;
        bytes[7] = 0x0A;
        bytes[11] = 13; // IHDR chunk length
        bytes[12] = (byte)'I';
        bytes[13] = (byte)'H';
        bytes[14] = (byte)'D';
        bytes[15] = (byte)'R';
        bytes[16] = (byte)(width >> 24);
        bytes[17] = (byte)(width >> 16);
        bytes[18] = (byte)(width >> 8);
        bytes[19] = (byte)width;
        bytes[20] = (byte)(height >> 24);
        bytes[21] = (byte)(height >> 16);
        bytes[22] = (byte)(height >> 8);
        bytes[23] = (byte)height;
        for (var i = 24; i < bytes.Length; i++)
            bytes[i] = 0x42;
        return (bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static (byte[] Bytes, string Sha) Blob(int size)
    {
        var bytes = new byte[size];
        RandomNumberGenerator.Fill(bytes);
        return (bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}
