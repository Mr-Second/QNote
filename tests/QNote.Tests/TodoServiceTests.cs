using QNote.Models;
using QNote.Services;

namespace QNote.Tests;

/// <summary>
/// <c>TodoService</c> business rules: identity/timestamp stamping, boundary
/// validation, the PRD Decision 4 reminder default (<see cref="DueDatePolicy"/>),
/// completion semantics and the local-day 今日 boundary (pinned clock —
/// timezone-independent on any runner).
/// </summary>
public sealed class TodoServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 4, 30, 0, TimeSpan.Zero);

    private static DateTimeOffset LocalAt(int dayOffset, int hour, int minute = 0)
    {
        var tz = TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(Now, tz).Date.AddDays(dayOffset).AddHours(hour).AddMinutes(minute);
        return new DateTimeOffset(
            DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz.GetUtcOffset(local)).ToUniversalTime();
    }

    private static TodoService NewService(TestDatabase db) => db.NewTodoService(() => Now);

    private static async Task<TodoList> WorkListAsync(TodoService svc)
    {
        var lists = await svc.GetListsAsync();
        return lists.First(l => l.Name == "工作");
    }

    // ---------- lists ----------

    [Fact]
    public async Task CreateListAsync_GeneratesIdentityAndStampsClock()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);

        var created = await svc.CreateListAsync("购物", "#F59E0B");

        Assert.True(created.Id > 0);
        Assert.Matches("^[0-9a-f]{32}$", created.Uuid);
        Assert.Equal(Now, created.CreatedAt);
        Assert.Equal(Now, created.UpdatedAt);
        Assert.Equal(2, created.SortOrder); // after the two seeded lists
    }

    [Fact]
    public async Task CreateListAsync_RejectsBlankName()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.CreateListAsync("   ", "#F59E0B"));
    }

    [Fact]
    public async Task UpdateListAsync_RenamesAndReturnsUpdated_MissingIsNull()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        var work = await WorkListAsync(svc);

        var updated = await svc.UpdateListAsync(work with { Name = "搬砖" });
        var missing = await svc.UpdateListAsync(work with { Id = 9999, Name = "幽灵" });

        Assert.NotNull(updated);
        Assert.Equal("搬砖", updated!.Name);
        Assert.Null(missing);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UpdateListAsync(work with { Name = " " }));
    }

    [Fact]
    public async Task DeleteListAsync_CascadesTasks()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        var work = await WorkListAsync(svc);
        var task = await svc.CreateTaskAsync(work.Uuid, "会消失");

        await svc.DeleteListAsync(work.Id);

        Assert.Null(await svc.GetTaskAsync(task.Id));
    }

    // ---------- tasks: create / edit ----------

    [Fact]
    public async Task CreateTaskAsync_StampIdentityAndClock()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        var work = await WorkListAsync(svc);

        var created = await svc.CreateTaskAsync(work.Uuid, "  准备周会演示文稿  ");

        Assert.True(created.Id > 0);
        Assert.Matches("^[0-9a-f]{32}$", created.Uuid);
        Assert.Equal(work.Uuid, created.ListUuid);
        Assert.Equal("准备周会演示文稿", created.Title); // trimmed at the boundary
        Assert.Equal(Now, created.CreatedAt);
        Assert.False(created.IsImportant);
        Assert.Null(created.CompletedAt);
        Assert.Null(created.DueAt);
    }

    [Fact]
    public async Task CreateTaskAsync_RejectsBlankTitle()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        var work = await WorkListAsync(svc);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.CreateTaskAsync(work.Uuid, "  "));
    }

    [Fact]
    public async Task CreateTaskAsync_RejectsUnknownList()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.CreateTaskAsync("no-such-list", "任务"));
    }

    [Fact]
    public async Task UpdateTaskAsync_PersistsTitleAndNotes()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        var work = await WorkListAsync(svc);
        var task = await svc.CreateTaskAsync(work.Uuid, "原标题");

        var updated = await svc.UpdateTaskAsync(task.Id, "新标题", "含 Q3 内存优化数据两页");

        Assert.NotNull(updated);
        Assert.Equal("新标题", updated!.Title);
        Assert.Equal("含 Q3 内存优化数据两页", updated.Notes);
        Assert.Null(await svc.UpdateTaskAsync(9999, "x", "y"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UpdateTaskAsync(task.Id, "", "x"));
    }

    // ---------- completion ----------

    [Fact]
    public async Task CompleteTaskAsync_UsesClockThenUncompleteClears()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        var work = await WorkListAsync(svc);
        var task = await svc.CreateTaskAsync(work.Uuid, "提测前冒烟");

        var done = await svc.CompleteTaskAsync(task.Id);
        var reopened = await svc.UncompleteTaskAsync(task.Id);

        Assert.Equal(Now, done!.CompletedAt);
        Assert.Null(reopened!.CompletedAt);
        Assert.Null(await svc.CompleteTaskAsync(9999));
        Assert.Null(await svc.UncompleteTaskAsync(9999));
    }

    // ---------- due + reminder policy (PRD Decision 4) ----------

    [Fact]
    public async Task SetDueAsync_TimedDue_TurnsReminderOnByDefault()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        var work = await WorkListAsync(svc);
        var task = await svc.CreateTaskAsync(work.Uuid, "任务");

        var updated = await svc.SetDueAsync(task.Id, LocalAt(0, 18), isAllDay: false);

        Assert.Equal(LocalAt(0, 18), updated!.DueAt);
        Assert.False(updated.IsAllDay);
        Assert.True(updated.ReminderOn); // PRD Decision 4: timed due → reminder on
    }

    [Fact]
    public async Task SetDueAsync_AllDayDue_LeavesReminderOffByDefault()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        var work = await WorkListAsync(svc);
        var task = await svc.CreateTaskAsync(work.Uuid, "任务");

        var updated = await svc.SetDueAsync(task.Id, LocalAt(0, 0), isAllDay: true);

        Assert.True(updated!.IsAllDay);
        Assert.False(updated.ReminderOn); // all-day → no reminder by default
    }

    [Fact]
    public async Task SetDueAsync_ExplicitReminder_OverridesTheDefault()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        var work = await WorkListAsync(svc);
        var task = await svc.CreateTaskAsync(work.Uuid, "任务");

        var off = await svc.SetDueAsync(task.Id, LocalAt(0, 18), isAllDay: false, reminder: false);
        var on = await svc.SetDueAsync(task.Id, LocalAt(2, 0), isAllDay: true, reminder: true);

        Assert.False(off!.ReminderOn); // detail-pane toggle wins over the default
        Assert.True(on!.ReminderOn);
    }

    [Fact]
    public async Task SetDueAsync_ClearingDue_ForcesReminderOff()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        var work = await WorkListAsync(svc);
        var task = await svc.CreateTaskAsync(work.Uuid, "任务");
        await svc.SetDueAsync(task.Id, LocalAt(0, 18), isAllDay: false);

        var cleared = await svc.SetDueAsync(task.Id, null, isAllDay: false);

        Assert.Null(cleared!.DueAt);
        Assert.False(cleared.IsAllDay);
        Assert.False(cleared.ReminderOn);
        Assert.Null(await svc.SetDueAsync(9999, LocalAt(0, 18), isAllDay: false));
    }

    [Fact]
    public async Task SetImportantAndReminder_PersistFlags()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        var work = await WorkListAsync(svc);
        var task = await svc.CreateTaskAsync(work.Uuid, "任务");

        Assert.True((await svc.SetImportantAsync(task.Id, true))!.IsImportant);
        Assert.True((await svc.SetReminderAsync(task.Id, true))!.ReminderOn);
        Assert.Null(await svc.SetImportantAsync(9999, true));
        Assert.Null(await svc.SetReminderAsync(9999, true));
    }

    // ---------- views: local-day boundary end-to-end ----------

    [Fact]
    public async Task GetTodayAsync_UsesLocalDayBoundary()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        var work = await WorkListAsync(svc);
        var overdue = await svc.CreateTaskAsync(work.Uuid, "逾期任务");
        var today = await svc.CreateTaskAsync(work.Uuid, "今天任务");
        var tomorrow = await svc.CreateTaskAsync(work.Uuid, "明天任务");
        await svc.SetDueAsync(overdue.Id, LocalAt(-1, 18), isAllDay: false);
        await svc.SetDueAsync(today.Id, LocalAt(0, 18), isAllDay: false);
        await svc.SetDueAsync(tomorrow.Id, LocalAt(1, 0), isAllDay: true); // == boundary → excluded

        var view = await svc.GetTodayAsync();

        Assert.Equal(new[] { overdue.Id, today.Id }, view.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task GetViewCountsAsync_MatchesTheViews()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        var work = await WorkListAsync(svc);
        var overdue = await svc.CreateTaskAsync(work.Uuid, "逾期");
        var starred = await svc.CreateTaskAsync(work.Uuid, "星标");
        var plain = await svc.CreateTaskAsync(work.Uuid, "普通");
        await svc.SetDueAsync(overdue.Id, LocalAt(-1, 18), isAllDay: false);
        await svc.SetImportantAsync(starred.Id, true);

        var counts = await svc.GetViewCountsAsync();

        Assert.Equal(1, counts.Today);
        Assert.Equal(1, counts.Important);
        Assert.Equal(1, counts.Planned);
        Assert.Equal(3, counts.All);
        Assert.Equal(3, (await svc.CountIncompleteByListAsync())[work.Uuid]);
    }

    // ---------- steps (PRD Decision 9) ----------

    [Fact]
    public async Task AddStepAsync_TrimsAndStamps_RejectsBlank()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        var work = await WorkListAsync(svc);
        var task = await svc.CreateTaskAsync(work.Uuid, "带步骤");

        var step = await svc.AddStepAsync(task.Id, "  第一步  ");

        Assert.True(step.Id > 0);
        Assert.Matches("^[0-9a-f]{32}$", step.Uuid);
        Assert.Equal("第一步", step.Title);
        Assert.Equal(Now, step.CreatedAt);
        Assert.Null(step.CompletedAt);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.AddStepAsync(task.Id, "  "));
    }

    // ---------- recurrence spawn on complete (PRD Decision 10) ----------

    [Fact]
    public async Task CompleteTaskAsync_RuledTask_SpawnsTheNextOccurrence()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        var work = await WorkListAsync(svc);
        var task = await svc.CreateTaskAsync(work.Uuid, "周报");
        await svc.SetImportantAsync(task.Id, true);
        await svc.SetDueAsync(task.Id, LocalAt(0, 18), isAllDay: false); // Decision 4: reminder on
        await svc.SetRecurrenceAsync(task.Id, TodoRecurrence.Weekly);

        var done = await svc.CompleteTaskAsync(task.Id);

        // The completed row keeps its rule (history sees it repeated).
        Assert.Equal(Now, done!.CompletedAt);
        Assert.Equal(TodoRecurrence.Weekly, done.RecurrenceRule);

        var next = Assert.Single(await svc.GetByListAsync(work.Uuid), t => t.Id != task.Id);
        Assert.NotEqual(task.Uuid, next.Uuid); // fresh identity
        Assert.Equal("周报", next.Title);
        Assert.True(next.IsImportant);          // star carries
        Assert.True(next.ReminderOn);           // reminder choice carries (there was a due)
        Assert.Equal(LocalAt(7, 18), next.DueAt); // advanced from the completed due
        Assert.False(next.IsAllDay);
        Assert.Equal(TodoRecurrence.Weekly, next.RecurrenceRule);
        Assert.Equal(Now, next.CreatedAt);
    }

    [Fact]
    public async Task CompleteTaskAsync_RuledTaskWithoutDue_GainsDueFromTheCompletionInstant()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        var work = await WorkListAsync(svc);
        var task = await svc.CreateTaskAsync(work.Uuid, "无日期重复");
        await svc.SetRecurrenceAsync(task.Id, TodoRecurrence.Daily);

        await svc.CompleteTaskAsync(task.Id);

        var next = Assert.Single(await svc.GetByListAsync(work.Uuid), t => t.Id != task.Id);
        // From = the completion instant; the Advance math itself is TodoRecurrenceTests' job.
        Assert.Equal(TodoRecurrence.Advance(TodoRecurrence.Daily, Now, isAllDay: false), next.DueAt);
        Assert.True(next.ReminderOn); // first-time due takes the Decision 4 default (timed → on)
    }

    [Fact]
    public async Task CompleteTaskAsync_CustomRule_SpawnsWithTheInterval()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        var work = await WorkListAsync(svc);
        var task = await svc.CreateTaskAsync(work.Uuid, "双周会");
        await svc.SetDueAsync(task.Id, LocalAt(0, 18), isAllDay: false);
        await svc.SetRecurrenceAsync(task.Id, TodoRecurrence.MakeCustom(2, "week"));

        await svc.CompleteTaskAsync(task.Id);

        var next = Assert.Single(await svc.GetByListAsync(work.Uuid), t => t.Id != task.Id);
        Assert.Equal(LocalAt(14, 18), next.DueAt);
        Assert.Equal("custom:2:week", next.RecurrenceRule);
    }

    [Fact]
    public async Task CompleteTaskAsync_SpawnCopiesStepsUnchecked_LeavesAttachmentsBehind()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        var work = await WorkListAsync(svc);
        var task = await svc.CreateTaskAsync(work.Uuid, "发布流程");
        var openStep = await svc.AddStepAsync(task.Id, "打包");
        var doneStep = await svc.AddStepAsync(task.Id, "发公告");
        await svc.SetStepCompletedAsync(doneStep.Id, completed: true);
        await svc.SetRecurrenceAsync(task.Id, TodoRecurrence.Daily);
        // An attachment ROW on the completed task (bytes are irrelevant here):
        // To Do parity — the next occurrence does NOT inherit attachments.
        var repo = db.NewTodoRepository();
        await repo.CreateAttachmentAsync(new TodoAttachment
        {
            Uuid = "att-1", TaskId = task.Id, FileName = "包.zip",
            Sha = new string('d', 64), ByteSize = 10, CreatedAt = Now, UpdatedAt = Now,
        });

        await svc.CompleteTaskAsync(task.Id);

        var next = Assert.Single(await svc.GetByListAsync(work.Uuid), t => t.Id != task.Id);
        var steps = await repo.GetStepsAsync(next.Id);
        // Same titles in order, all UNchecked, fresh identity (To Do: the next
        // occurrence's checklist starts fresh).
        Assert.Equal(["打包", "发公告"], steps.Select(s => s.Title).ToArray());
        Assert.All(steps, s => Assert.Null(s.CompletedAt));
        Assert.DoesNotContain(steps, s => s.Uuid == openStep.Uuid || s.Uuid == doneStep.Uuid);
        Assert.Empty(await repo.GetAttachmentsAsync(next.Id));
        // The completed task keeps its own attachment row untouched.
        Assert.Single(await repo.GetAttachmentsAsync(task.Id));
    }

    [Fact]
    public async Task CompleteTaskAsync_UnruledTask_SpawnsNothing()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        var work = await WorkListAsync(svc);
        var task = await svc.CreateTaskAsync(work.Uuid, "一次性");

        await svc.CompleteTaskAsync(task.Id);

        Assert.Empty(await svc.GetByListAsync(work.Uuid));
    }

    [Fact]
    public async Task CompleteTaskAsync_InvalidStoredRule_CompletesWithoutSpawning()
    {
        // A malformed rule can only arrive by fixture surgery (the service
        // validates on SetRecurrenceAsync) — completion must still succeed and
        // simply not spawn (best-effort, never crash the mutation).
        using var db = new TestDatabase();
        var svc = NewService(db);
        var work = await WorkListAsync(svc);
        var task = await svc.CreateTaskAsync(work.Uuid, "坏规则");
        await db.NewTodoRepository().SetTaskRecurrenceAsync(task.Id, "bogus");

        var done = await svc.CompleteTaskAsync(task.Id);

        Assert.NotNull(done!.CompletedAt);
        Assert.Empty(await svc.GetByListAsync(work.Uuid));
    }

    [Fact]
    public async Task SetRecurrenceAsync_RejectsUnknownRule()
    {
        using var db = new TestDatabase();
        var svc = NewService(db);
        var work = await WorkListAsync(svc);
        var task = await svc.CreateTaskAsync(work.Uuid, "任务");

        await Assert.ThrowsAsync<ArgumentException>(() => svc.SetRecurrenceAsync(task.Id, "bogus"));
        Assert.Null((await svc.GetTaskAsync(task.Id))!.RecurrenceRule);
    }

    // ---------- attachments (PRD Decision 11; 25 MB per-task total cap) ----------

    [Fact]
    public async Task AddAttachmentAsync_StoresBytesContentAddressed_AndDedupes()
    {
        using var db = new TestDatabase();
        var svc = db.NewTodoServiceWithAttachments(() => Now);
        var work = await WorkListAsync(svc);
        var task = await svc.CreateTaskAsync(work.Uuid, "附件任务");
        byte[] bytes = [1, 2, 3, 4, 5];

        var first = await svc.AddAttachmentAsync(task.Id, "a.bin", new MemoryStream(bytes));
        var second = await svc.AddAttachmentAsync(task.Id, "b.bin", new MemoryStream(bytes));

        Assert.True(first.Id > 0);
        Assert.Matches("^[0-9a-f]{64}$", first.Sha);
        Assert.Equal(bytes.Length, first.ByteSize);
        Assert.Equal(second.Sha, first.Sha); // same bytes → same store key
        var file = Path.Combine(db.Paths.AttachmentsDir, first.Sha);
        Assert.True(File.Exists(file));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
        Assert.Equal(2, (await svc.GetTaskDetailAsync(task.Id))!.Attachments.Count);
    }

    [Fact]
    public async Task AddAttachmentAsync_OverThePerTaskCap_ThrowsWithoutStoring()
    {
        using var db = new TestDatabase();
        var svc = db.NewTodoServiceWithAttachments(() => Now);
        var work = await WorkListAsync(svc);
        var task = await svc.CreateTaskAsync(work.Uuid, "任务");
        // A sibling row puts the task near the cap WITHOUT writing a real file
        // (fixture surgery — the cap sums the rows' ByteSize).
        await db.NewTodoRepository().CreateAttachmentAsync(new TodoAttachment
        {
            Uuid = "att-big", TaskId = task.Id, FileName = "大文件.bin",
            Sha = new string('e', 64), ByteSize = TodoService.MaxTaskAttachmentBytes - 10,
            CreatedAt = Now, UpdatedAt = Now,
        });

        // The seekable pre-check fires before any bytes hit the store.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.AddAttachmentAsync(task.Id, "溢出.bin", new MemoryStream(new byte[20])));

        Assert.Single((await svc.GetTaskDetailAsync(task.Id))!.Attachments); // no row created
        Assert.False(Directory.Exists(db.Paths.AttachmentsDir)
            && Directory.EnumerateFiles(db.Paths.AttachmentsDir).Any()); // no bytes stored
    }

    [Fact]
    public async Task DeleteAttachmentAsync_LastReference_DeletesTheFile_SharedKeepsIt()
    {
        using var db = new TestDatabase();
        var svc = db.NewTodoServiceWithAttachments(() => Now);
        var work = await WorkListAsync(svc);
        var task = await svc.CreateTaskAsync(work.Uuid, "任务");
        var another = await svc.CreateTaskAsync(work.Uuid, "另一个任务");
        byte[] bytes = [9, 9, 9];
        var first = await svc.AddAttachmentAsync(task.Id, "一.bin", new MemoryStream(bytes));
        var second = await svc.AddAttachmentAsync(another.Id, "二.bin", new MemoryStream(bytes));
        var file = Path.Combine(db.Paths.AttachmentsDir, first.Sha);

        var (removed, remaining) = await svc.DeleteAttachmentAsync(first.Id);
        Assert.Equal("一.bin", removed!.FileName);
        Assert.Equal(1, remaining);
        Assert.True(File.Exists(file)); // the second row still holds the bytes alive

        await svc.DeleteAttachmentAsync(second.Id);
        Assert.False(File.Exists(file)); // last reference gone → the file goes too
    }

    [Fact]
    public async Task DeleteTaskAsync_GCsOrphanedAttachmentFiles()
    {
        using var db = new TestDatabase();
        var svc = db.NewTodoServiceWithAttachments(() => Now);
        var work = await WorkListAsync(svc);
        var task = await svc.CreateTaskAsync(work.Uuid, "带附件");
        var kept = await svc.CreateTaskAsync(work.Uuid, "共享同内容");
        byte[] bytes = [7, 7];
        var orphan = await svc.AddAttachmentAsync(task.Id, "孤儿.bin", new MemoryStream([1]));
        var shared = await svc.AddAttachmentAsync(task.Id, "共享.bin", new MemoryStream(bytes));
        await svc.AddAttachmentAsync(kept.Id, "共享副本.bin", new MemoryStream(bytes)); // same sha

        await svc.DeleteTaskAsync(task.Id);

        Assert.False(File.Exists(Path.Combine(db.Paths.AttachmentsDir, orphan.Sha))); // orphaned → GC'd
        Assert.True(File.Exists(Path.Combine(db.Paths.AttachmentsDir, shared.Sha)));  // still referenced
        Assert.Null(await svc.GetTaskAsync(task.Id));
    }
}
