using QNote.Models;
using QNote.Services;

namespace QNote.Tests;

/// <summary>
/// Category CRUD + name-link fan-out + destructive cascade + ordering + seed
/// idempotency for <c>CategoryService</c> / <c>NoteRepository</c> (schema v4).
/// </summary>
public sealed class CategoryServiceTests
{
    [Fact]
    public async Task SchemaV4_SeedsBuiltIns_WhenEmpty()
    {
        using var db = new TestDatabase();

        var categories = await db.NewRepository().GetCategoriesAsync();

        Assert.Equal(new[] { "工作", "生活", "重要" }, categories.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, categories.Select(c => c.SortOrder).ToArray());
        Assert.All(categories, c => Assert.Matches("^#[0-9A-F]{6}$", c.Color));
        Assert.All(categories, c => Assert.False(string.IsNullOrEmpty(c.IconKey)));
    }

    [Fact]
    public async Task SchemaV4_Seed_IsIdempotent()
    {
        using var db = new TestDatabase();

        // Second EnsureCreated over the same DB must not duplicate the built-ins.
        new QNote.Data.Schema.SchemaInitializer(db.Factory).EnsureCreated();

        var categories = await db.NewRepository().GetCategoriesAsync();
        Assert.Equal(3, categories.Count);
    }

    [Fact]
    public async Task CreateAsync_AppendsAtEnd()
    {
        using var db = new TestDatabase();
        var svc = db.NewCategoryService();

        var created = await svc.CreateAsync("项目", "#F59E0B", "E8A5");

        Assert.True(created.Id > 0);
        Assert.Equal(3, created.SortOrder); // after the 3 seeded built-ins
        var all = await svc.GetAllAsync();
        Assert.Equal("项目", all[^1].Name);
    }

    [Fact]
    public async Task CreateAsync_RejectsBlankAndDuplicate()
    {
        using var db = new TestDatabase();
        var svc = db.NewCategoryService();

        var blank = await Assert.ThrowsAsync<CategoryException>(() => svc.CreateAsync("  ", "#3B82F6", "E8A5"));
        Assert.Equal(CategoryErrorKind.BlankName, blank.Kind);
        var duplicate = await Assert.ThrowsAsync<CategoryException>(() => svc.CreateAsync("工作", "#3B82F6", "E8A5"));
        Assert.Equal(CategoryErrorKind.DuplicateName, duplicate.Kind);
        var color = await Assert.ThrowsAsync<CategoryException>(() => svc.CreateAsync("项目", "blue", "E8A5"));
        Assert.Equal(CategoryErrorKind.InvalidColor, color.Kind);
    }

    [Fact]
    public async Task UpdateAsync_Rename_FansOutToNotes()
    {
        using var db = new TestDatabase();
        var svc = db.NewCategoryService();
        var repo = db.NewRepository();
        var note = await repo.CreateAsync(NewNote(category: "工作"));
        var target = (await svc.GetAllAsync()).First(c => c.Name == "工作");

        // Rename goes through a non-built-in copy path: 工作 is built-in, so create
        // a custom category first, move the note, then rename it.
        var custom = await svc.CreateAsync("临时", "#3B82F6", "E8A5");
        await svc.UpdateAsync(custom with { Name = "临时2" });

        // Fan-out check with a note actually linked by name.
        var linked = await repo.CreateAsync(NewNote(category: "临时2"));
        await svc.UpdateAsync(custom with { Name = "归档" });

        var categories = await svc.GetAllAsync();
        Assert.Contains(categories, c => c.Name == "归档");
        Assert.Equal("归档", (await repo.GetByIdAsync(linked.Id))!.Category);
        Assert.Equal("工作", (await repo.GetByIdAsync(note.Id))!.Category); // untouched
        Assert.Equal("工作", target.Name);
    }

    [Fact]
    public async Task UpdateAsync_BuiltIn_RenameRejected_ButColorAllowed()
    {
        using var db = new TestDatabase();
        var svc = db.NewCategoryService();
        var work = (await svc.GetAllAsync()).First(c => c.Name == "工作");

        var ex = await Assert.ThrowsAsync<CategoryException>(
            () => svc.UpdateAsync(work with { Name = "搬砖" }));
        Assert.Equal(CategoryErrorKind.BuiltInRename, ex.Kind);

        await svc.UpdateAsync(work with { Color = "#123456" });
        Assert.Equal("#123456", (await svc.GetAllAsync()).First(c => c.Name == "工作").Color);
    }

    [Fact]
    public async Task DeleteAsync_CascadesNotesAndFts_InOneTransaction()
    {
        using var db = new TestDatabase();
        var svc = db.NewCategoryService();
        var repo = db.NewRepository();
        var custom = await svc.CreateAsync("废弃", "#3B82F6", "E8A5");
        var doomed = await repo.CreateAsync(NewNote(title: "会消失", content: "x", category: "废弃"));
        var kept = await repo.CreateAsync(NewNote(title: "会留下", content: "x", category: "工作"));

        await svc.DeleteAsync(custom.Id);

        Assert.Null(await repo.GetByIdAsync(doomed.Id));
        Assert.NotNull(await repo.GetByIdAsync(kept.Id));
        Assert.DoesNotContain(await svc.GetAllAsync(), c => c.Name == "废弃");

        // FTS shadow row went with the note.
        await using var conn = db.Factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM notes_fts WHERE rowid = $id;";
        cmd.Parameters.AddWithValue("$id", doomed.Id);
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task DeleteAsync_BuiltIn_Rejected()
    {
        using var db = new TestDatabase();
        var svc = db.NewCategoryService();
        var work = (await svc.GetAllAsync()).First(c => c.Name == "工作");

        var ex = await Assert.ThrowsAsync<CategoryException>(() => svc.DeleteAsync(work.Id));
        Assert.Equal(CategoryErrorKind.BuiltInDelete, ex.Kind);
        Assert.Equal(3, (await svc.GetAllAsync()).Count);
    }

    [Fact]
    public async Task ReorderAsync_PersistsNewOrder()
    {
        using var db = new TestDatabase();
        var svc = db.NewCategoryService();
        var all = await svc.GetAllAsync();
        var reversed = all.Select(c => c.Id).Reverse().ToArray();

        await svc.ReorderAsync(reversed);

        var after = await svc.GetAllAsync();
        Assert.Equal(reversed, after.Select(c => c.Id).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, after.Select(c => c.SortOrder).ToArray());
    }

    [Fact]
    public async Task CountNotesAsync_GroupsByCategory()
    {
        using var db = new TestDatabase();
        var svc = db.NewCategoryService();
        var repo = db.NewRepository();
        await repo.CreateAsync(NewNote(category: "工作"));
        await repo.CreateAsync(NewNote(category: "工作"));
        await repo.CreateAsync(NewNote(category: "")); // uncategorized

        var counts = await svc.CountNotesAsync();

        Assert.Equal(2, counts["工作"]);
        Assert.Equal(1, counts[""]);
        Assert.False(counts.ContainsKey("生活"));
    }

    [Fact]
    public async Task GetSummariesAsync_FiltersByCategory()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        await repo.CreateAsync(NewNote(title: "工作笔记", category: "工作"));
        await repo.CreateAsync(NewNote(title: "生活笔记", category: "生活"));

        var work = await repo.GetSummariesAsync("工作");
        var all = await repo.GetSummariesAsync();

        Assert.Single(work);
        Assert.Equal("工作笔记", work[0].Title);
        Assert.Equal(2, all.Count);
    }

    private static Note NewNote(string title = "", string content = "", string category = "")
    {
        var now = DateTimeOffset.UtcNow;
        return new Note
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Title = title,
            Content = content,
            PlainText = content,
            Category = category,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
