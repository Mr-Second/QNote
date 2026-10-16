using QNote.Models;
using QNote.Services;

namespace QNote.Tests;

/// <summary>
/// Repository contract for the todo domain (schema v7): list CRUD + cascade,
/// task CRUD, completion semantics, focused due/important/reminder updates,
/// smart-view boundaries, LIKE escaping, and counts. Timezone-independent:
/// all day boundaries derive from <see cref="TimeZoneInfo.Local"/> around a
/// fixed instant, so the suite is green on UTC runners and UTC+8 dev machines
/// alike (CI lesson, project-context §Build/run).
/// </summary>
public sealed class TodoRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 4, 30, 0, TimeSpan.Zero);

    /// <summary>A UTC instant on the local day <paramref name="dayOffset"/> at wall-clock hour/minute.</summary>
    private static DateTimeOffset LocalAt(int dayOffset, int hour, int minute = 0)
    {
        var tz = TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(Now, tz).Date.AddDays(dayOffset).AddHours(hour).AddMinutes(minute);
        return new DateTimeOffset(
            DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz.GetUtcOffset(local)).ToUniversalTime();
    }

    private static async Task<TodoList> GetWorkListAsync(TestDatabase db) =>
        (await db.NewTodoRepository().GetListsAsync())[0];

    private static TodoTask NewTask(
        string listUuid, string title,
        DateTimeOffset? due = null, bool allDay = false,
        bool important = false, bool reminder = false)
    {
        var now = DateTimeOffset.UtcNow;
        return new TodoTask
        {
            Uuid = Guid.NewGuid().ToString("N"),
            ListUuid = listUuid,
            Title = title,
            DueAt = due,
            IsAllDay = allDay,
            IsImportant = important,
            ReminderOn = reminder,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    // ---------- lists ----------

    [Fact]
    public async Task GetListsAsync_ReturnsSeededLists_InOrder()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();

        var lists = await repo.GetListsAsync();

        Assert.Equal(new[] { "工作", "生活" }, lists.Select(l => l.Name).ToArray());
        Assert.Equal(new[] { 0, 1 }, lists.Select(l => l.SortOrder).ToArray());
        Assert.All(lists, l => Assert.Matches("^[0-9a-f]{32}$", l.Uuid));
        Assert.All(lists, l => Assert.True(l.CreatedAt > DateTimeOffset.MinValue));
    }

    [Fact]
    public async Task CreateListAsync_AppendsAtEnd()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var now = DateTimeOffset.UtcNow;

        var created = await repo.CreateListAsync(new TodoList
        {
            Uuid = "list-uuid-new", Name = "购物", Color = "#F59E0B", CreatedAt = now, UpdatedAt = now,
        });

        Assert.True(created.Id > 0);
        Assert.Equal(2, created.SortOrder);
        Assert.Equal("购物", (await repo.GetListsAsync())[^1].Name);
    }

    [Fact]
    public async Task UpdateListAsync_RenamesAndRecolors_MissingReturnsNull()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);

        var updated = await repo.UpdateListAsync(work with { Name = "搬砖", Color = "#123456" });

        Assert.NotNull(updated);
        Assert.Equal("搬砖", updated!.Name);
        Assert.Equal("#123456", updated.Color);
        Assert.Equal(work.Uuid, updated.Uuid); // identity never rewritten
        Assert.True(updated.UpdatedAt > work.UpdatedAt);

        Assert.Null(await repo.UpdateListAsync(work with { Id = 9999 }));
    }

    [Fact]
    public async Task DeleteListAsync_CascadesTasks_WithinTheList()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        var life = (await repo.GetListsAsync())[1];
        var doomed = await repo.CreateTaskAsync(NewTask(work.Uuid, "会消失"));
        var kept = await repo.CreateTaskAsync(NewTask(life.Uuid, "会留下"));

        await repo.DeleteListAsync(work.Id);

        Assert.Null(await repo.GetTaskByIdAsync(doomed.Id));
        Assert.NotNull(await repo.GetTaskByIdAsync(kept.Id));
        Assert.DoesNotContain(await repo.GetListsAsync(), l => l.Id == work.Id);
    }

    [Fact]
    public async Task DeleteListAsync_MissingId_IsNoOp()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var before = await repo.GetListsAsync();

        await repo.DeleteListAsync(9999);

        Assert.Equal(before.Count, (await repo.GetListsAsync()).Count);
    }

    // ---------- tasks: CRUD ----------

    [Fact]
    public async Task CreateTaskAsync_AssignsId_AndRoundTripsByIdAndUuid()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);

        var created = await repo.CreateTaskAsync(NewTask(work.Uuid, "准备周会演示文稿"));

        Assert.True(created.Id > 0);
        var byId = await repo.GetTaskByIdAsync(created.Id);
        var byUuid = await repo.GetTaskByUuidAsync(created.Uuid);
        Assert.NotNull(byId);
        Assert.NotNull(byUuid);
        Assert.Equal(created.Uuid, byId!.Uuid);
        Assert.Equal(byId.Id, byUuid!.Id);
        Assert.Equal("准备周会演示文稿", byId.Title);
    }

    [Fact]
    public async Task GetTask_Missing_ReturnsNull()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        Assert.Null(await repo.GetTaskByIdAsync(9999));
        Assert.Null(await repo.GetTaskByUuidAsync("no-such-uuid"));
    }

    [Fact]
    public async Task UpdateTaskAsync_PersistsTitleNotes_KeepsCompletionAndFlags()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        var task = await repo.CreateTaskAsync(NewTask(work.Uuid, "原标题"));
        await repo.CompleteTaskAsync(task.Id, Now);
        await repo.SetTaskImportantAsync(task.Id, true);

        var updated = await repo.UpdateTaskAsync(task.Id, "新标题", "补充说明");

        Assert.NotNull(updated);
        Assert.Equal("新标题", updated!.Title);
        Assert.Equal("补充说明", updated.Notes);
        Assert.NotNull(updated.CompletedAt); // completion not clobbered
        Assert.True(updated.IsImportant);      // flags not clobbered
        Assert.Null(await repo.UpdateTaskAsync(9999, "x", "y"));
    }

    [Fact]
    public async Task DeleteTaskAsync_RemovesTask()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        var task = await repo.CreateTaskAsync(NewTask(work.Uuid, "临时"));

        await repo.DeleteTaskAsync(task.Id);

        Assert.Null(await repo.GetTaskByIdAsync(task.Id));
    }

    // ---------- tasks: completion ----------

    [Fact]
    public async Task CompleteTaskAsync_SetsCompletionInstant_UncompleteClears()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        var task = await repo.CreateTaskAsync(NewTask(work.Uuid, "提测前冒烟"));

        var done = await repo.CompleteTaskAsync(task.Id, Now);

        Assert.NotNull(done);
        Assert.Equal(Now, done!.CompletedAt);
        Assert.Equal(Now, done.UpdatedAt); // completion time doubles as the mutation time
        Assert.Null(await repo.CompleteTaskAsync(9999, Now));

        var reopened = await repo.UncompleteTaskAsync(task.Id);

        Assert.NotNull(reopened);
        Assert.Null(reopened!.CompletedAt);
        Assert.True(reopened.UpdatedAt >= Now);
        Assert.Null(await repo.UncompleteTaskAsync(9999));
    }

    // ---------- tasks: focused updates ----------

    [Fact]
    public async Task SetTaskDueAsync_PersistsDueAllDayAndReminder()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        var task = await repo.CreateTaskAsync(NewTask(work.Uuid, "整理评审反馈"));

        var updated = await repo.SetTaskDueAsync(task.Id, LocalAt(0, 18), isAllDay: false, reminderOn: true);

        Assert.NotNull(updated);
        Assert.Equal(LocalAt(0, 18), updated!.DueAt);
        Assert.False(updated.IsAllDay);
        Assert.True(updated.ReminderOn);

        var cleared = await repo.SetTaskDueAsync(task.Id, null, isAllDay: false, reminderOn: false);
        Assert.Null(cleared!.DueAt);
        Assert.False(cleared.ReminderOn);
    }

    [Fact]
    public async Task SetTaskImportantAndReminder_PersistFlags_MissingReturnsNull()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        var task = await repo.CreateTaskAsync(NewTask(work.Uuid, "星标任务"));

        Assert.True((await repo.SetTaskImportantAsync(task.Id, true))!.IsImportant);
        Assert.True((await repo.SetTaskReminderAsync(task.Id, true))!.ReminderOn);
        Assert.False((await repo.SetTaskImportantAsync(task.Id, false))!.IsImportant);
        Assert.Null(await repo.SetTaskImportantAsync(9999, true));
        Assert.Null(await repo.SetTaskReminderAsync(9999, true));
    }

    // ---------- smart views ----------

    [Fact]
    public async Task GetTodayAsync_IncludesOverdueTodayAndAllDay_ExcludesTomorrowAndCompleted()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        var overdue = await repo.CreateTaskAsync(NewTask(work.Uuid, "逾期", due: LocalAt(-1, 18)));
        var allDayToday = await repo.CreateTaskAsync(NewTask(work.Uuid, "全天", due: LocalAt(0, 0), allDay: true));
        var timedToday = await repo.CreateTaskAsync(NewTask(work.Uuid, "今天", due: LocalAt(0, 18)));
        var tomorrow = await repo.CreateTaskAsync(NewTask(work.Uuid, "明天", due: LocalAt(1, 9)));
        var allDayTomorrow = await repo.CreateTaskAsync(NewTask(work.Uuid, "明天全天", due: LocalAt(1, 0), allDay: true));
        var noDue = await repo.CreateTaskAsync(NewTask(work.Uuid, "无日期"));
        var completed = await repo.CreateTaskAsync(NewTask(work.Uuid, "已完成", due: LocalAt(0, 10)));
        await repo.CompleteTaskAsync(completed.Id, Now);

        var today = await repo.GetTodayAsync(LocalAt(1, 0));

        // Most overdue first; the day boundary is exclusive (all-day-tomorrow == boundary → out).
        Assert.Equal(
            new[] { overdue.Id, allDayToday.Id, timedToday.Id },
            today.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task GetTodayAsync_WithCompleted_AppendsCompletedTail()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        var open = await repo.CreateTaskAsync(NewTask(work.Uuid, "开着", due: LocalAt(0, 18)));
        var done = await repo.CreateTaskAsync(NewTask(work.Uuid, "关了", due: LocalAt(0, 10)));
        await repo.CompleteTaskAsync(done.Id, Now);

        var view = await repo.GetTodayAsync(LocalAt(1, 0), includeCompleted: true);

        Assert.Equal(new[] { open.Id, done.Id }, view.Select(t => t.Id).ToArray());
        Assert.NotNull(view[^1].CompletedAt);
    }

    [Fact]
    public async Task GetImportantAsync_StarsIncompleteFirst_DueOrdered()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        var starredLater = await repo.CreateTaskAsync(NewTask(work.Uuid, "星标晚", due: LocalAt(2, 9), important: true));
        var starredOverdue = await repo.CreateTaskAsync(NewTask(work.Uuid, "星标逾", due: LocalAt(-1, 8), important: true));
        var starredDone = await repo.CreateTaskAsync(NewTask(work.Uuid, "星标完", important: true));
        await repo.CompleteTaskAsync(starredDone.Id, Now);
        var plain = await repo.CreateTaskAsync(NewTask(work.Uuid, "普通"));

        var important = await repo.GetImportantAsync();

        Assert.Equal(new[] { starredOverdue.Id, starredLater.Id }, important.Select(t => t.Id).ToArray());
        Assert.DoesNotContain(important, t => t.Id == plain.Id || t.Id == starredDone.Id);
    }

    [Fact]
    public async Task GetPlannedAsync_AllIncompleteWithDue_Chronological()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        var far = await repo.CreateTaskAsync(NewTask(work.Uuid, "远期", due: LocalAt(5, 9)));
        var near = await repo.CreateTaskAsync(NewTask(work.Uuid, "近期", due: LocalAt(0, 18)));
        var noDue = await repo.CreateTaskAsync(NewTask(work.Uuid, "无日期"));
        var done = await repo.CreateTaskAsync(NewTask(work.Uuid, "已完成", due: LocalAt(0, 8)));
        await repo.CompleteTaskAsync(done.Id, Now);

        var planned = await repo.GetPlannedAsync();

        Assert.Equal(new[] { near.Id, far.Id }, planned.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task GetAllAsync_IncompleteOnly_NewestCreatedFirst()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        var old = await repo.CreateTaskAsync(NewTask(work.Uuid, "旧", due: null));
        var mid = await repo.CreateTaskAsync(NewTask(work.Uuid, "中", due: null));
        var done = await repo.CreateTaskAsync(NewTask(work.Uuid, "完成"));
        await repo.CompleteTaskAsync(done.Id, Now);

        var all = await repo.GetAllAsync();

        Assert.Equal(new[] { mid.Id, old.Id }, all.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task GetByListAsync_ScopesAndOrders_CompletedLast()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var lists = await repo.GetListsAsync();
        var work = lists[0];
        var life = lists[1];
        var w1 = await repo.CreateTaskAsync(NewTask(work.Uuid, "工作1"));
        var w2 = await repo.CreateTaskAsync(NewTask(work.Uuid, "工作2"));
        var wDone = await repo.CreateTaskAsync(NewTask(work.Uuid, "工作完成"));
        await repo.CompleteTaskAsync(wDone.Id, Now);
        var other = await repo.CreateTaskAsync(NewTask(life.Uuid, "生活1"));

        var openOnly = await repo.GetByListAsync(work.Uuid);
        var withDone = await repo.GetByListAsync(work.Uuid, includeCompleted: true);

        Assert.Equal(new[] { w2.Id, w1.Id }, openOnly.Select(t => t.Id).ToArray());
        Assert.Equal(new[] { w2.Id, w1.Id, wDone.Id }, withDone.Select(t => t.Id).ToArray());
        Assert.DoesNotContain(withDone, t => t.Id == other.Id);
    }

    // ---------- search ----------

    [Fact]
    public async Task SearchByTitleAsync_MatchesLiteralWildcards()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        await repo.CreateTaskAsync(NewTask(work.Uuid, "50% 进度_a"));
        await repo.CreateTaskAsync(NewTask(work.Uuid, "axb"));
        await repo.CreateTaskAsync(NewTask(work.Uuid, "xa"));
        await repo.CreateTaskAsync(NewTask(work.Uuid, "普通任务"));

        var percent = await repo.SearchByTitleAsync("0% 进度");
        Assert.Single(percent);
        Assert.Equal("50% 进度_a", percent[0].Title);

        var underscore = await repo.SearchByTitleAsync("_a"); // literal _, not any-char
        Assert.Single(underscore);
        Assert.Equal("50% 进度_a", underscore[0].Title); // "xa" only matches an unescaped wildcard

        var none = await repo.SearchByTitleAsync("任务"); // plain text
        Assert.Single(none);
    }

    [Fact]
    public async Task SearchByTitleAsync_ExcludesCompleted()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        var done = await repo.CreateTaskAsync(NewTask(work.Uuid, "验收"));
        await repo.CompleteTaskAsync(done.Id, Now);

        Assert.Empty(await repo.SearchByTitleAsync("验收"));
    }

    // ---------- catch-up candidates ----------

    [Fact]
    public async Task GetCatchUpRemindersAsync_OverdueWithinGrace_WithReminderOnly()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        var candidate = await repo.CreateTaskAsync(
            NewTask(work.Uuid, "关机错过", due: Now.AddHours(-2), reminder: true));
        var tooOld = await repo.CreateTaskAsync(
            NewTask(work.Uuid, "太久以前", due: Now.AddHours(-30), reminder: true));
        var noReminder = await repo.CreateTaskAsync(NewTask(work.Uuid, "没提醒", due: Now.AddHours(-2)));
        var done = await repo.CreateTaskAsync(
            NewTask(work.Uuid, "已完成", due: Now.AddHours(-2), reminder: true));
        await repo.CompleteTaskAsync(done.Id, Now);
        var future = await repo.CreateTaskAsync(
            NewTask(work.Uuid, "还没到", due: Now.AddHours(2), reminder: true));

        var candidates = await repo.GetCatchUpRemindersAsync(Now.AddHours(-24), Now);

        Assert.Equal(new[] { candidate.Id }, candidates.Select(t => t.Id).ToArray());
        Assert.DoesNotContain(candidates, t =>
            t.Id == tooOld.Id || t.Id == noReminder.Id || t.Id == done.Id || t.Id == future.Id);
    }

    // ---------- counts ----------

    [Fact]
    public async Task GetViewCountsAsync_AggregatesAllFourViews()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        await repo.CreateTaskAsync(NewTask(work.Uuid, "逾期", due: LocalAt(-1, 18)));
        await repo.CreateTaskAsync(NewTask(work.Uuid, "今天", due: LocalAt(0, 18)));
        await repo.CreateTaskAsync(NewTask(work.Uuid, "星标", important: true));
        var done = await repo.CreateTaskAsync(NewTask(work.Uuid, "已完成", important: true));
        await repo.CompleteTaskAsync(done.Id, Now);

        var counts = await repo.GetViewCountsAsync(LocalAt(1, 0));

        Assert.Equal(2, counts.Today);
        Assert.Equal(1, counts.Important); // the completed starred task is excluded
        Assert.Equal(2, counts.Planned);
        Assert.Equal(3, counts.All);
    }

    [Fact]
    public async Task CountIncompleteByListAsync_GroupsByListUuid()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var lists = await repo.GetListsAsync();
        var work = lists[0];
        var life = lists[1];
        await repo.CreateTaskAsync(NewTask(work.Uuid, "1"));
        await repo.CreateTaskAsync(NewTask(work.Uuid, "2"));
        await repo.CreateTaskAsync(NewTask(work.Uuid, "3"));
        var done = await repo.CreateTaskAsync(NewTask(work.Uuid, "4"));
        await repo.CompleteTaskAsync(done.Id, Now);
        await repo.CreateTaskAsync(NewTask(life.Uuid, "5"));

        var counts = await repo.CountIncompleteByListAsync();

        Assert.Equal(2, counts.Count);
        Assert.Equal(3, counts[work.Uuid]);
        Assert.Equal(1, counts[life.Uuid]);
    }

    // ---------- steps (stage ⑤, PRD Decision 9) ----------

    [Fact]
    public async Task CreateStepAsync_AppendsSortOrder_GetStepsAsync_OrdersWithinTask()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        var task = await repo.CreateTaskAsync(NewTask(work.Uuid, "带步骤任务"));
        var other = await repo.CreateTaskAsync(NewTask(work.Uuid, "另一个任务"));

        var first = await repo.CreateStepAsync(Step(task.Id, "step-1", "第一步"));
        var second = await repo.CreateStepAsync(Step(task.Id, "step-2", "第二步"));
        await repo.CreateStepAsync(Step(other.Id, "step-3", "别的任务的步骤"));

        Assert.Equal(0, first.SortOrder);
        Assert.Equal(1, second.SortOrder);
        var steps = await repo.GetStepsAsync(task.Id);
        Assert.Equal(["第一步", "第二步"], steps.Select(s => s.Title).ToArray());
        Assert.All(steps, s => Assert.Equal(task.Id, s.TaskId)); // scoping by task
    }

    [Fact]
    public async Task SetStepCompletedAsync_TogglesCompletionAndStamps_MissingReturnsNull()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        var task = await repo.CreateTaskAsync(NewTask(work.Uuid, "任务"));
        var step = await repo.CreateStepAsync(Step(task.Id, "step-1", "唯一步骤"));

        var done = await repo.SetStepCompletedAsync(step.Id, Now);

        Assert.NotNull(done);
        Assert.Equal(Now, done!.CompletedAt);
        Assert.True(done.UpdatedAt >= Now);

        var reopened = await repo.SetStepCompletedAsync(step.Id, null);
        Assert.Null(reopened!.CompletedAt);

        Assert.Null(await repo.SetStepCompletedAsync(9999, Now));
    }

    [Fact]
    public async Task DeleteStepAsync_RemovesRow()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        var task = await repo.CreateTaskAsync(NewTask(work.Uuid, "任务"));
        var step = await repo.CreateStepAsync(Step(task.Id, "step-1", "会删除"));

        await repo.DeleteStepAsync(step.Id);

        Assert.Empty(await repo.GetStepsAsync(task.Id));
    }

    // ---------- recurrence column (stage ⑤, PRD Decision 10) ----------

    [Fact]
    public async Task SetTaskRecurrenceAsync_SetsClearsAndRoundTrips_MissingReturnsNull()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        var task = await repo.CreateTaskAsync(NewTask(work.Uuid, "重复任务"));

        var set = await repo.SetTaskRecurrenceAsync(task.Id, TodoRecurrence.Weekly);
        var custom = await repo.SetTaskRecurrenceAsync(task.Id, "custom:2:week");
        var cleared = await repo.SetTaskRecurrenceAsync(task.Id, null);

        Assert.Equal(TodoRecurrence.Weekly, set!.RecurrenceRule);
        Assert.Equal("custom:2:week", custom!.RecurrenceRule);
        Assert.Null(cleared!.RecurrenceRule);
        Assert.Null(await repo.SetTaskRecurrenceAsync(9999, TodoRecurrence.Daily));
    }

    [Fact]
    public async Task SummaryProjection_CarriesRecurrenceRuleAndStepCounts()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        var task = await repo.CreateTaskAsync(NewTask(work.Uuid, "有元数据"));
        await repo.SetTaskRecurrenceAsync(task.Id, TodoRecurrence.Monthly);
        await repo.CreateStepAsync(Step(task.Id, "s1", "未完成"));
        var doneStep = await repo.CreateStepAsync(Step(task.Id, "s2", "已完成"));
        await repo.SetStepCompletedAsync(doneStep.Id, Now);

        var summary = (await repo.GetByListAsync(work.Uuid, includeCompleted: true)).Single(t => t.Id == task.Id);

        Assert.Equal(TodoRecurrence.Monthly, summary.RecurrenceRule);
        Assert.Equal(2, summary.StepsTotal);
        Assert.Equal(1, summary.StepsDone);
    }

    // ---------- attachments (stage ⑤, PRD Decision 11) ----------

    [Fact]
    public async Task AttachmentCrud_RoundTripsRowAndCountsRefs()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        var task = await repo.CreateTaskAsync(NewTask(work.Uuid, "带附件任务"));
        var sha = new string('a', 64);

        var created = await repo.CreateAttachmentAsync(new TodoAttachment
        {
            Uuid = "att-1", TaskId = task.Id, FileName = "脚本.zip",
            Sha = sha, ByteSize = 12, CreatedAt = Now, UpdatedAt = Now,
        });
        await repo.CreateAttachmentAsync(new TodoAttachment
        {
            Uuid = "att-2", TaskId = task.Id, FileName = "同一内容.zip",
            Sha = sha, ByteSize = 12, CreatedAt = Now, UpdatedAt = Now,
        });

        Assert.True(created.Id > 0);
        Assert.Equal(2, (await repo.GetAttachmentsAsync(task.Id)).Count);
        Assert.Equal(2, await repo.CountAttachmentRefsAsync(sha)); // dedupe: 2 rows, 1 file

        var byUuid = await repo.GetAttachmentByUuidAsync("att-2");
        Assert.NotNull(byUuid);
        Assert.Equal("同一内容.zip", byUuid!.FileName);

        var (removed, remaining) = await repo.DeleteAttachmentAsync(created.Id);
        Assert.Equal("脚本.zip", removed!.FileName);
        Assert.Equal(1, remaining); // the second row still holds the file alive
        // Missing id: no row removed (the tuple itself is a value type — assert the payload).
        Assert.Null((await repo.DeleteAttachmentAsync(9999)).Removed);
        Assert.Equal(1, await repo.CountAttachmentRefsAsync(sha));
    }

    [Fact]
    public async Task DeleteTaskAsync_CascadesStepsAndAttachments_ReturnsShasForGc()
    {
        using var db = new TestDatabase();
        var repo = db.NewTodoRepository();
        var work = await GetWorkListAsync(db);
        var task = await repo.CreateTaskAsync(NewTask(work.Uuid, "级联任务"));
        var kept = await repo.CreateTaskAsync(NewTask(work.Uuid, "保留任务"));
        await repo.CreateStepAsync(Step(task.Id, "s1", "会消失的步骤"));
        var sha = new string('b', 64);
        await repo.CreateAttachmentAsync(new TodoAttachment
        {
            Uuid = "att-1", TaskId = task.Id, FileName = "a.bin",
            Sha = sha, ByteSize = 1, CreatedAt = Now, UpdatedAt = Now,
        });
        var keptSha = new string('c', 64);
        await repo.CreateAttachmentAsync(new TodoAttachment
        {
            Uuid = "att-2", TaskId = kept.Id, FileName = "b.bin",
            Sha = keptSha, ByteSize = 1, CreatedAt = Now, UpdatedAt = Now,
        });

        var shas = await repo.DeleteTaskAsync(task.Id);

        Assert.Equal([sha], shas); // only the deleted task's sha goes for GC
        Assert.Empty(await repo.GetStepsAsync(task.Id));
        Assert.Empty(await repo.GetAttachmentsAsync(task.Id));
        Assert.Single(await repo.GetAttachmentsAsync(kept.Id));
    }

    private static TodoStep Step(long taskId, string uuid, string title)
    {
        var now = DateTimeOffset.UtcNow;
        return new TodoStep
        {
            Uuid = uuid,
            TaskId = taskId,
            Title = title,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
