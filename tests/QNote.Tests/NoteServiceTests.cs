using QNote.Models;
using QNote.Services;

namespace QNote.Tests;

/// <summary>Business rules for <c>NoteService</c>: identity + timestamp stamping.</summary>
public sealed class NoteServiceTests
{
    [Fact]
    public async Task CreateAsync_GeneratesUuid_AndEqualTimestamps()
    {
        using var db = new TestDatabase();
        var service = NewService(db);

        var created = await service.CreateAsync(category: "生活");

        Assert.True(created.Id > 0);
        Assert.False(string.IsNullOrWhiteSpace(created.Uuid));
        Assert.Equal("生活", created.Category);
        Assert.Equal(string.Empty, created.Title);
        Assert.Equal(created.CreatedAt, created.UpdatedAt); // equal at the moment of creation
    }

    [Fact]
    public async Task UpdateAsync_ReStampsUpdatedAt_IgnoringStaleValue()
    {
        using var db = new TestDatabase();
        var service = NewService(db);
        var created = await service.CreateAsync();

        // Pass a deliberately stale UpdatedAt; the service must re-stamp it to now.
        var stale = created with { Title = "改", Content = "改文", UpdatedAt = created.UpdatedAt.AddDays(-5) };
        var saved = await service.UpdateAsync(stale);

        Assert.True(saved.UpdatedAt > stale.UpdatedAt);
        var loaded = await service.GetByIdAsync(created.Id);
        Assert.Equal("改", loaded!.Title);
        Assert.Equal("改文", loaded.Content);
    }

    [Fact]
    public async Task DeleteAsync_RemovesNoteFromSummaries()
    {
        using var db = new TestDatabase();
        var service = NewService(db);
        var created = await service.CreateAsync();

        await service.DeleteAsync(created.Id);

        Assert.Empty(await service.GetSummariesAsync());
    }

    /// <summary>
    /// RichEdit strips the qnote: alt on RTF reload (2026-09-26), so a reloaded note
    /// saves with picts that carry no link. Sync must NOT unlink/prune then — the
    /// originals are still visibly in the note.
    /// </summary>
    [Fact]
    public async Task SyncNoteImagesAsync_PictWithoutAlt_KeepsRowAndOriginal()
    {
        using var db = new TestDatabase();
        var service = NewService(db);
        var images = db.NewImageService();
        var note = await service.CreateAsync();
        var imported = await images.ImportAsync([1, 2, 3], "png", [4, 5, 6], "pngblip", 1, 1);
        await service.AddNoteImagesAsync(note.Id,
            [new NoteImage { NoteId = note.Id, Sha256 = imported.Sha256, Ext = "png", ByteSize = 3, CreatedAt = DateTimeOffset.UtcNow }]);

        // One pict whose alt was rewritten to "Image" by a RichEdit reload strip.
        var strippedRtf = @"{\rtf1 {\pict{\*\picprop{\sp{\sn wzDescription}{\sv Image}}}\pngblip\picw1\pich1 89504e47}}";
        await service.SyncNoteImagesAsync(note.Id, strippedRtf);

        var rows = await db.NewRepository().GetNoteImagesAsync(note.Id);
        Assert.Contains(rows, r => r.Sha256 == imported.Sha256);
        Assert.True(File.Exists(imported.OriginalPath));
    }

    /// <summary>When every pict still carries its alt, unreferenced originals prune as before.</summary>
    [Fact]
    public async Task SyncNoteImagesAsync_AllPictsLinked_PrunesUnreferenced()
    {
        using var db = new TestDatabase();
        var service = NewService(db);
        var images = db.NewImageService();
        var note = await service.CreateAsync();
        var stale = await images.ImportAsync([1, 2, 3], "png", [4, 5, 6], "pngblip", 1, 1);
        var kept = await images.ImportAsync([7, 8, 9], "png", [10, 11, 12], "pngblip", 1, 1);
        await service.AddNoteImagesAsync(note.Id,
            [new NoteImage { NoteId = note.Id, Sha256 = stale.Sha256, Ext = "png", ByteSize = 3, CreatedAt = DateTimeOffset.UtcNow }]);

        var linkedRtf = @"{\rtf1 {\pict{\*\picprop{\sp{\sn wzDescription}{\sv qnote:" + kept.Sha256 + @"}}}\pngblip\picw1\pich1 89504e47}}";
        await service.SyncNoteImagesAsync(note.Id, linkedRtf);

        var rows = await db.NewRepository().GetNoteImagesAsync(note.Id);
        Assert.DoesNotContain(rows, r => r.Sha256 == stale.Sha256);
        Assert.False(File.Exists(stale.OriginalPath));
        Assert.Contains(rows, r => r.Sha256 == kept.Sha256);
    }

    private static NoteService NewService(TestDatabase db) => db.NewNoteService();
}
