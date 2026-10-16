using System.Collections.ObjectModel;
using QNote.Models;
using QNote.Services;
using QNote.ViewModels;

namespace QNote.Tests;

/// <summary>
/// List-options menu contracts (2026-10-16): per-list theme / hide-completed /
/// sort mode persistence (repo), the smart-view settings CSV codec, and the
/// four 排序依据 modes. All pure or repo-level — the VM actions stay covered
/// by the existing page-VM suite over the fakes.
/// </summary>
public sealed class TodoListOptionsTests
{
    // ---------- repository: per-list options ----------

    [Fact]
    public async Task UpdateListAsync_PersistsListOptions_AndReadsBack()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = (await repo.GetListsAsync())[0];

        var updated = await repo.UpdateListAsync(work with
        {
            Theme = 101,
            HideCompleted = true,
            SortMode = (int)TodoSortMode.DueDate,
        });

        Assert.NotNull(updated);
        Assert.Equal(101, updated!.Theme);
        Assert.True(updated.HideCompleted);
        Assert.Equal((int)TodoSortMode.DueDate, updated.SortMode);

        var reloaded = (await repo.GetListsAsync()).Single(l => l.Uuid == work.Uuid);
        Assert.Equal(101, reloaded.Theme);
        Assert.True(reloaded.HideCompleted);
        Assert.Equal((int)TodoSortMode.DueDate, reloaded.SortMode);
    }

    [Fact]
    public async Task UpdateListAsync_OptionsDefaultToOff_OnFreshLists()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();

        Assert.All(await repo.GetListsAsync(), l =>
        {
            Assert.Equal(0, l.Theme);
            Assert.False(l.HideCompleted);
            Assert.Equal(0, l.SortMode);
        });
    }

    // ---------- smart-view settings CSV (TodoSmartViewOptions) ----------

    [Fact]
    public void SmartViewOptions_ParseFormat_RoundTrip()
    {
        var csv = "1=5;3=2";

        var parsed = TodoSmartViewOptions.Parse(csv);
        var formatted = TodoSmartViewOptions.Format(parsed);

        Assert.Equal(csv, formatted);
        Assert.Equal(5, parsed[(int)TodoNavKind.Important]);
        Assert.Equal(2, parsed[(int)TodoNavKind.All]);
    }

    [Fact]
    public void SmartViewOptions_Parse_ToleratesGarbage()
    {
        var parsed = TodoSmartViewOptions.Parse("nope;2=x;=4;;1=1");

        Assert.Single(parsed);
        Assert.Equal(1, parsed[1]);
        Assert.Empty(TodoSmartViewOptions.Parse(null));
        Assert.Empty(TodoSmartViewOptions.Parse("   "));
    }

    [Fact]
    public void SmartViewOptions_WithEntry_SetRemoveAndDefault()
    {
        // Setting 0 = the implicit default → the entry is REMOVED, not stored.
        Assert.Empty(TodoSmartViewOptions.WithEntry("1=2", 1, 0));
        Assert.Equal("1=2;3=4", TodoSmartViewOptions.WithEntry("1=2", 3, 4));
        Assert.Equal("1=9", TodoSmartViewOptions.WithEntry("1=2", 1, 9));
        Assert.Empty(TodoSmartViewOptions.WithEntry(null, 1, 0));
    }

    [Fact]
    public void SmartViewOptions_HiddenSet_RoundTrip()
    {
        var hidden = TodoSmartViewOptions.ParseSet("1, 2 ,4");
        Assert.True(hidden.SetEquals(new[] { 1, 2, 4 }));

        Assert.Equal("1,2,4", TodoSmartViewOptions.FormatSet(hidden));
        Assert.Empty(TodoSmartViewOptions.ParseSet(null));
        Assert.Empty(TodoSmartViewOptions.ParseSet("x,y"));
    }

    // ---------- 排序依据 (TodoTaskSorter) ----------

    private static TodoTaskItemViewModel Item(
        string title, bool important = false, DateTimeOffset? due = null, DateTimeOffset? created = null) =>
        new(new TodoTaskSummary
        {
            Uuid = Guid.NewGuid().ToString("N"),
            ListUuid = "list",
            Title = title,
            IsImportant = important,
            DueAt = due,
            CreatedAt = created ?? DateTimeOffset.MinValue,
        }, static () => DateTimeOffset.UtcNow);

    [Fact]
    public void Sorter_Manual_KeepsLoadOrder()
    {
        var items = new ObservableCollection<TodoTaskItemViewModel>
        {
            Item("b"), Item("a"), Item("c"),
        };

        TodoTaskSorter.Apply(items, TodoSortMode.Manual);

        Assert.Equal(new[] { "b", "a", "c" }, items.Select(i => i.Title));
    }

    [Fact]
    public void Sorter_Importance_StarsFirstThenDue()
    {
        var items = new ObservableCollection<TodoTaskItemViewModel>
        {
            Item("plain"),
            Item("starred-no-due", important: true),
            Item("starred-due", important: true, due: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)),
        };

        TodoTaskSorter.Apply(items, TodoSortMode.Importance);

        Assert.Equal(new[] { "starred-due", "starred-no-due", "plain" }, items.Select(i => i.Title));
    }

    [Fact]
    public void Sorter_DueDate_NoDueTrails()
    {
        var items = new ObservableCollection<TodoTaskItemViewModel>
        {
            Item("no-due"),
            Item("later", due: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)),
            Item("earlier", due: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)),
        };

        TodoTaskSorter.Apply(items, TodoSortMode.DueDate);

        Assert.Equal(new[] { "earlier", "later", "no-due" }, items.Select(i => i.Title));
    }

    [Fact]
    public void Sorter_Title_OrdinalAlphabetical()
    {
        var items = new ObservableCollection<TodoTaskItemViewModel>
        {
            Item("banana"), Item("apple"), Item("Cherry"),
        };

        TodoTaskSorter.Apply(items, TodoSortMode.Title);

        Assert.Equal(new[] { "apple", "banana", "Cherry" }, items.Select(i => i.Title));
    }

    [Fact]
    public void Sorter_Created_OldestFirst()
    {
        var items = new ObservableCollection<TodoTaskItemViewModel>
        {
            Item("newest", created: new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero)),
            Item("oldest", created: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)),
            Item("middle", created: new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero)),
        };

        TodoTaskSorter.Apply(items, TodoSortMode.Created);

        Assert.Equal(new[] { "oldest", "middle", "newest" }, items.Select(i => i.Title));
    }
}
