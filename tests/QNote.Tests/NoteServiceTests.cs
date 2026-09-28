using QNote.Markdown;
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
    /// PlainText is derived from the Markdown content on save — one authority, so
    /// previews and the FTS corpus never see syntax markers.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_DerivesPlainTextFromMarkdown()
    {
        using var db = new TestDatabase();
        var service = NewService(db);
        var created = await service.CreateAsync();

        var saved = await service.UpdateAsync(created with
        {
            Title = "t",
            Content = "# 标题\n**粗** *斜* ~~删~~ 正文\n- 列表项",
        });

        var loaded = await service.GetByIdAsync(created.Id);
        Assert.Equal("标题\n粗 斜 删 正文\n列表项", loaded!.PlainText);
        Assert.Equal("标题\n粗 斜 删 正文\n列表项", saved.PlainText);
    }

    /// <summary>A Markdown reference keeps the row and the original on disk.</summary>
    [Fact]
    public async Task SyncNoteImagesAsync_MarkdownReference_KeepsRowAndOriginal()
    {
        using var db = new TestDatabase();
        var service = NewService(db);
        var images = db.NewImageService();
        var note = await service.CreateAsync();
        var imported = await images.ImportAsync([1, 2, 3], "png", [4, 5, 6], "pngblip", 1, 1);
        await service.AddNoteImagesAsync(note.Id,
            [new NoteImage { NoteId = note.Id, Sha256 = imported.Sha256, Ext = "png", ByteSize = 3, CreatedAt = DateTimeOffset.UtcNow }]);

        var md = $"![图]({MarkdownParser.ImageSchemePrefix}{imported.Sha256})";
        await service.SyncNoteImagesAsync(note.Id, md);

        var rows = await db.NewRepository().GetNoteImagesAsync(note.Id);
        Assert.Contains(rows, r => r.Sha256 == imported.Sha256);
        Assert.True(File.Exists(imported.OriginalPath));
    }

    /// <summary>References removed from the Markdown prune the row and the original.</summary>
    [Fact]
    public async Task SyncNoteImagesAsync_RemovedReference_PrunesUnreferenced()
    {
        using var db = new TestDatabase();
        var service = NewService(db);
        var images = db.NewImageService();
        var note = await service.CreateAsync();
        var stale = await images.ImportAsync([1, 2, 3], "png", [4, 5, 6], "pngblip", 1, 1);
        var kept = await images.ImportAsync([7, 8, 9], "png", [10, 11, 12], "pngblip", 1, 1);
        await service.AddNoteImagesAsync(note.Id,
            [new NoteImage { NoteId = note.Id, Sha256 = stale.Sha256, Ext = "png", ByteSize = 3, CreatedAt = DateTimeOffset.UtcNow }]);

        var md = $"![]({MarkdownParser.ImageSchemePrefix}{kept.Sha256})";
        await service.SyncNoteImagesAsync(note.Id, md);

        var rows = await db.NewRepository().GetNoteImagesAsync(note.Id);
        Assert.DoesNotContain(rows, r => r.Sha256 == stale.Sha256);
        Assert.False(File.Exists(stale.OriginalPath));
        Assert.Contains(rows, r => r.Sha256 == kept.Sha256);
    }

    /// <summary>Null/empty Markdown references nothing — every row is a prune candidate.</summary>
    [Fact]
    public async Task SyncNoteImagesAsync_EmptyMarkdown_PrunesAll()
    {
        using var db = new TestDatabase();
        var service = NewService(db);
        var images = db.NewImageService();
        var note = await service.CreateAsync();
        var imported = await images.ImportAsync([1, 2, 3], "png", [4, 5, 6], "pngblip", 1, 1);
        await service.AddNoteImagesAsync(note.Id,
            [new NoteImage { NoteId = note.Id, Sha256 = imported.Sha256, Ext = "png", ByteSize = 3, CreatedAt = DateTimeOffset.UtcNow }]);

        await service.SyncNoteImagesAsync(note.Id, null);

        Assert.Empty(await db.NewRepository().GetNoteImagesAsync(note.Id));
        Assert.False(File.Exists(imported.OriginalPath));
    }

    private static NoteService NewService(TestDatabase db) => db.NewNoteService();
}
