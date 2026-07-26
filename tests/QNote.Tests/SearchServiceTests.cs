using QNote.Models;

namespace QNote.Tests;

/// <summary>
/// FTS5 + bigram search coverage: CJK recall, title weighting, multi-token AND,
/// category filter, blank-query short-circuit, write-path index sync, full rebuild,
/// and the count-only startup reconcile (parity: A-search §5-7).
/// </summary>
public sealed class SearchServiceTests
{
    [Fact]
    public async Task SearchAsync_BlankQuery_ReturnsEmpty()
    {
        using var db = new TestDatabase();
        var search = db.NewSearchService();
        await SeedNoteAsync(db, title: "机器学习", body: "深度学习入门");

        Assert.Empty(await search.SearchAsync(""));
        Assert.Empty(await search.SearchAsync("   "));
    }

    [Fact]
    public async Task SearchAsync_CjkKeyword_FindsByOverlappingBigram()
    {
        using var db = new TestDatabase();
        var search = db.NewSearchService();
        await SeedNoteAsync(db, title: "机器学习笔记", body: "今天讲深度学习");

        // "机器" matches title bigram; "学习" matches both title and body bigrams.
        var results = await search.SearchAsync("机器");
        Assert.Single(results);
        Assert.Equal("机器学习笔记", results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_TitleHit_RanksBeforeBodyOnlyHit()
    {
        using var db = new TestDatabase();
        var search = db.NewSearchService();
        // Two notes containing the same keyword; only the title of one matches.
        await SeedNoteAsync(db, title: "正常标题", body: "关键词出现在这里");
        await SeedNoteAsync(db, title: "关键词在标题", body: "无关正文");

        var results = await search.SearchAsync("关键词");

        Assert.Equal(2, results.Count);
        Assert.Equal("关键词在标题", results[0].Title); // title ×10 boost → ranks first
    }

    [Fact]
    public async Task SearchAsync_EnglishMultiToken_IsImplicitAnd()
    {
        using var db = new TestDatabase();
        var search = db.NewSearchService();
        await SeedNoteAsync(db, title: "alpha", body: "contains alpha only");
        await SeedNoteAsync(db, title: "beta alpha", body: "has both tokens");

        var results = await search.SearchAsync("alpha beta");

        // Both tokens must appear (implicit AND); only the second note has both.
        Assert.Single(results);
        Assert.Equal("beta alpha", results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_CategoryFilter_ScopesOnSqlSide()
    {
        using var db = new TestDatabase();
        var search = db.NewSearchService();
        await SeedNoteAsync(db, title: "项目笔记", body: "项目进度", category: "工作");
        await SeedNoteAsync(db, title: "项目食谱", body: "晚餐项目", category: "生活");

        var workOnly = await search.SearchAsync("项目", category: "工作");
        Assert.Single(workOnly);
        Assert.Equal("工作", workOnly[0].Category);

        var all = await search.SearchAsync("项目");
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task SearchAsync_LatinMatch_CaseInsensitive()
    {
        using var db = new TestDatabase();
        var search = db.NewSearchService();
        await SeedNoteAsync(db, title: "Shopping List", body: "MILK and eggs");

        var results = await search.SearchAsync("milk");
        Assert.Single(results);
    }

    [Fact]
    public async Task WritePath_CreateUpsertsFts_RetrievalImmediatelyReflectsNewNote()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var search = db.NewSearchService();

        await repo.CreateAsync(NewNote(title: "新建笔记", body: "新增内容"));

        var results = await search.SearchAsync("新建");
        Assert.Single(results);
    }

    [Fact]
    public async Task WritePath_UpdateUpsertsFts_RetrievalReflectsNewTitle()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var search = db.NewSearchService();
        var created = await repo.CreateAsync(NewNote(title: "原标题", body: "原内容"));

        await repo.UpdateAsync(created with { Title = "新标题", PlainText = "新内容" });

        Assert.Empty(await search.SearchAsync("原标题"));
        Assert.Single(await search.SearchAsync("新标题"));
    }

    [Fact]
    public async Task WritePath_DeleteRemovesFromFts()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var search = db.NewSearchService();
        var created = await repo.CreateAsync(NewNote(title: "待删除", body: "内容"));

        await repo.DeleteAsync(created.Id);

        Assert.Empty(await search.SearchAsync("待删除"));
    }

    [Fact]
    public async Task RebuildIndexAsync_ClearsAndRefillsFromNotesTable()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var search = db.NewSearchService();
        await repo.CreateAsync(NewNote(title: "重建测试", body: "正文"));

        // Wipe the FTS table directly to simulate drift; rebuild must restore it.
        await using (var conn = db.Factory.OpenWrite())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM notes_fts;";
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.Empty(await search.SearchAsync("重建"));

        await search.RebuildIndexAsync();

        Assert.Single(await search.SearchAsync("重建"));
    }

    [Fact]
    public async Task RebuildIndexAsync_ConcurrentCalls_SingleFlight()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var search = db.NewSearchService();
        await repo.CreateAsync(NewNote(title: "并发重建", body: "内容"));

        // Two concurrent rebuilds must not corrupt the index; the second is skipped.
        await Task.WhenAll(search.RebuildIndexAsync(), search.RebuildIndexAsync());

        Assert.Single(await search.SearchAsync("并发"));
    }

    [Fact]
    public async Task ReconcileAsync_CountsEqual_DoesNothing()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var search = db.NewSearchService();
        await repo.CreateAsync(NewNote(title: "对账", body: "内容"));

        // Counts already match; reconcile is a no-op. Drop a row from FTS to detect
        // whether reconcile wrongly rebuilds: after a no-op reconcile, the dropped
        // row must still be absent.
        await using (var conn = db.Factory.OpenWrite())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM notes_fts; INSERT INTO notes_fts(rowid,title,body) VALUES (999,'x','y');";
            await cmd.ExecuteNonQueryAsync();
        }
        // Now notes=1, fts=1 (different content but equal count) → reconcile must skip.
        await search.ReconcileAsync();

        Assert.Empty(await search.SearchAsync("对账"));
    }

    [Fact]
    public async Task ReconcileAsync_CountsDiffer_TriggersRebuild()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var search = db.NewSearchService();
        await repo.CreateAsync(NewNote(title: "对账触发", body: "内容"));

        // Make counts diverge: drop the FTS row entirely.
        await using (var conn = db.Factory.OpenWrite())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM notes_fts;";
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.Empty(await search.SearchAsync("对账触发"));

        await search.ReconcileAsync();

        Assert.Single(await search.SearchAsync("对账触发"));
    }

    [Fact]
    public async Task SearchAsync_TimeSort_OverridesRelevance()
    {
        using var db = new TestDatabase();
        var search = db.NewSearchService();
        // Same keyword in both bodies → relevance tie is possible; time sort must
        // strictly follow UpdatedAt regardless of rank.
        await SeedNoteAsync(db, title: "旧", body: "共同关键词", updated: DateTimeOffset.UtcNow.AddDays(-2));
        await SeedNoteAsync(db, title: "新", body: "共同关键词", updated: DateTimeOffset.UtcNow);

        var newest = await search.SearchAsync("共同关键词", sort: SearchSortOrder.NewestFirst);
        Assert.Equal("新", newest[0].Title);

        var oldest = await search.SearchAsync("共同关键词", sort: SearchSortOrder.OldestFirst);
        Assert.Equal("旧", oldest[0].Title);
    }

    private static async Task SeedNoteAsync(
        TestDatabase db, string title, string body, string category = "", DateTimeOffset? updated = null)
    {
        var note = NewNote(title, body, category);
        if (updated is { } u)
            note = note with { UpdatedAt = u };
        await db.NewRepository().CreateAsync(note);
    }

    private static Note NewNote(string title, string body, string category = "")
    {
        var now = DateTimeOffset.UtcNow;
        return new Note
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Title = title,
            Content = body,
            PlainText = body,
            Category = category,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
