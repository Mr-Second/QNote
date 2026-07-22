using QNote.Models;

namespace QNote.Tests;

/// <summary>CRUD + projection + timestamp round-trip for <c>NoteRepository</c>.</summary>
public sealed class NoteRepositoryTests
{
    [Fact]
    public async Task CreateAsync_AssignsId_AndGetByIdReturnsSameNote()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();

        var created = await repo.CreateAsync(NewNote(title: "标题", content: "正文内容", category: "工作"));

        Assert.True(created.Id > 0);
        var loaded = await repo.GetByIdAsync(created.Id);
        Assert.NotNull(loaded);
        Assert.Equal(created.Uuid, loaded!.Uuid);
        Assert.Equal("标题", loaded.Title);
        Assert.Equal("正文内容", loaded.Content);
        Assert.Equal("工作", loaded.Category);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenMissing()
    {
        using var db = new TestDatabase();
        Assert.Null(await db.NewRepository().GetByIdAsync(9999));
    }

    [Fact]
    public async Task GetSummariesAsync_TruncatesPreview_AndOrdersByUpdatedAtDesc()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();

        var older = await repo.CreateAsync(NewNote(title: "旧", content: "旧内容",
            created: DaysAgo(2), updated: DaysAgo(2)));
        var newer = await repo.CreateAsync(NewNote(title: "新", content: new string('x', 500),
            created: DaysAgo(1), updated: DaysAgo(1)));

        var summaries = await repo.GetSummariesAsync();

        Assert.Equal(2, summaries.Count);
        Assert.Equal(newer.Id, summaries[0].Id); // newest-updated first
        Assert.Equal(older.Id, summaries[1].Id);
        Assert.Equal(120, summaries[0].Preview.Length); // truncated to PreviewLength
        Assert.Equal("旧内容", summaries[1].Preview);
    }

    [Fact]
    public async Task UpdateAsync_PersistsEdits()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var created = await repo.CreateAsync(NewNote(title: "原", content: "原文"));

        await repo.UpdateAsync(created with { Title = "改", Content = "改文", UpdatedAt = DateTimeOffset.UtcNow });

        var loaded = await repo.GetByIdAsync(created.Id);
        Assert.Equal("改", loaded!.Title);
        Assert.Equal("改文", loaded.Content);
    }

    [Fact]
    public async Task DeleteAsync_RemovesNote()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var created = await repo.CreateAsync(NewNote());

        await repo.DeleteAsync(created.Id);

        Assert.Null(await repo.GetByIdAsync(created.Id));
        Assert.Empty(await repo.GetSummariesAsync());
    }

    [Fact]
    public async Task Timestamps_RoundTripAsUtc()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var when = DateTimeOffset.UtcNow.AddMinutes(-30);

        var created = await repo.CreateAsync(NewNote(created: when, updated: when));
        var loaded = await repo.GetByIdAsync(created.Id);

        Assert.Equal(when, loaded!.CreatedAt);            // exact instant preserved
        Assert.Equal(TimeSpan.Zero, loaded.CreatedAt.Offset); // read back as UTC
    }

    private static Note NewNote(
        string title = "",
        string content = "",
        string category = "",
        DateTimeOffset? created = null,
        DateTimeOffset? updated = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new Note
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Title = title,
            Content = content,
            Category = category,
            CreatedAt = created ?? now,
            UpdatedAt = updated ?? now,
        };
    }

    private static DateTimeOffset DaysAgo(int days) => DateTimeOffset.UtcNow.AddDays(-days);
}
