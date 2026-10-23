using Microsoft.Extensions.Logging.Abstractions;
using QNote.Models;
using QNote.Services;
using QNote.Sync;

namespace QNote.Tests;

public sealed class NoteDraftSyncTests
{
    [Fact]
    public async Task MatchingBaseline_UpdatesOriginalAndQueuesItsStoredIdentity()
    {
        using var db = new TestDatabase();
        var changes = new List<string>();
        var service = Service(db, changes);
        var baseline = await service.CreateAsync();
        changes.Clear();

        var saved = await service.SaveDraftAsync(baseline with { Title = "saved", Content = "**draft**" }, baseline);

        Assert.Equal(baseline.Uuid, saved.Uuid);
        Assert.Equal("draft", saved.PlainText);
        Assert.True(saved.UpdatedAt > baseline.UpdatedAt);
        Assert.Equal(saved, await service.GetByIdAsync(saved.Id));
        Assert.Equal(new[] { saved.Uuid }, changes);
        Assert.Single(await service.GetAllAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedOrDeletedBaseline_PreservesOriginalAndOneImageBearingCopy(bool deleted)
    {
        using var db = new TestDatabase();
        var changes = new List<string>();
        var service = Service(db, changes);
        var baseline = await service.CreateAsync();
        var image = await db.NewImageService().ImportAsync([1, 2, 3], "png", [4, 5, 6], "pngblip", 1, 1);
        await service.AddNoteImagesAsync(baseline.Id,
            [new NoteImage { NoteId = baseline.Id, Sha256 = image.Sha256, Ext = "png", DisplayBytes = [4, 5, 6] }]);
        if (deleted) await db.NewRepository().DeleteAsync(baseline.Id);
        else await db.NewRepository().ImportNoteAsync(baseline with { Title = "cloud", Content = "cloud body" });
        changes.Clear();

        var copy = await service.SaveDraftAsync(baseline with
        {
            Title = "draft", Content = $"![image](qnote-img:{image.Sha256})",
        }, baseline);

        Assert.NotEqual(baseline.Uuid, copy.Uuid);
        Assert.Equal("draft (saved version)", copy.Title);
        Assert.Equal(new[] { copy.Uuid }, changes);
        var copiedImage = Assert.Single(await service.GetNoteImagesWithDisplayAsync(copy.Id));
        Assert.Equal(image.Sha256, copiedImage.Sha256);
        Assert.Equal(new byte[] { 4, 5, 6 }, copiedImage.DisplayBytes);
        Assert.True(File.Exists(image.OriginalPath));
        if (deleted)
        {
            Assert.Null(await service.GetByIdAsync(baseline.Id));
            Assert.Single(await service.GetAllAsync());
        }
        else
        {
            Assert.Equal("cloud body", (await service.GetByIdAsync(baseline.Id))!.Content);
            Assert.Equal(2, (await service.GetAllAsync()).Count);
        }
    }

    [Fact]
    public async Task RemovedCategory_DoesNotHideThePreservedDraft()
    {
        using var db = new TestDatabase();
        var category = await db.NewCategoryService().CreateAsync("removed", "#112233", "");
        var service = Service(db, []);
        var baseline = await service.CreateAsync(category.Name);
        await db.NewCategoryService().DeleteAsync(category.Id);

        var copy = await service.SaveDraftAsync(baseline with { Content = "keep me" }, baseline);

        Assert.Equal("", copy.Category);
        Assert.Equal(copy.Id, Assert.Single(await service.GetSummariesAsync()).Id);
    }

    [Fact]
    public async Task MissingRow_CreatesCopyWithoutRecreatingTheOriginalIdentity()
    {
        using var db = new TestDatabase();
        var baseline = new Note { Id = 100, Uuid = Guid.NewGuid().ToString("N"), Title = "missing" };
        var copy = await Service(db, []).SaveDraftAsync(baseline with { Content = "draft" }, baseline);

        Assert.NotEqual(baseline.Uuid, copy.Uuid);
        Assert.Equal(copy.Uuid, Assert.Single(await db.NewRepository().GetAllAsync()).Uuid);
    }

    private static NoteService Service(TestDatabase db, List<string> changes) => new(db.NewRepository(),
        db.NewImageService(), NullLogger<NoteService>.Instance,
        (kind, uuid) => { Assert.Equal(SyncEntryKind.Note, kind); changes.Add(uuid); }, " (saved version)");
}
