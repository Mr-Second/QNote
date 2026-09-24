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

    private static NoteService NewService(TestDatabase db) => db.NewNoteService();
}
