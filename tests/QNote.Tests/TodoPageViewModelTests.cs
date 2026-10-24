using Microsoft.Extensions.Logging.Abstractions;
using QNote.Services;
using QNote.ViewModels;

namespace QNote.Tests;

/// <summary>
/// <see cref="TodoPageViewModel"/> logic over a fake <see cref="ITodoService"/>:
/// view switching, badge counts, quick-add, completion moves, the PRD Decision 4
/// due-policy flow, search, and the staged-delete undo window. The VM family is
/// link-compiled into this assembly (WinUI-free by design); every await in the
/// VM completes synchronously against the fake, keeping tests deterministic.
/// </summary>
public sealed class TodoPageViewModelTests
{
    [Fact]
    public async Task ThemeRefresh_RebindsNavigationAndDetailWithoutDiscardingDraftOrSelection()
    {
        var env = new Env(detailSaveDebounce: TimeSpan.FromHours(1));
        var task = env.AddTask(FakeTodoService.WorkUuid, "theme test");
        await env.Service.AddStepAsync(task.Id, "step");
        await env.Vm.LoadAsync();
        await env.Vm.SelectTaskByUuidAsync(task.Uuid);
        var selectedNav = env.Vm.SelectedNav;
        var selectedTask = env.Vm.SelectedTask;
        env.Vm.DetailTitle = "unsaved draft";
        var navChanges = new Dictionary<TodoNavItemViewModel, List<string?>>();
        foreach (var nav in env.Vm.NavItems)
        {
            var changes = new List<string?>();
            navChanges[nav] = changes;
            nav.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        }
        var stepChanges = new List<string?>();
        Assert.Single(env.Vm.DetailSteps).PropertyChanged += (_, args) => stepChanges.Add(args.PropertyName);
        var detailChanges = new List<string?>();
        env.Vm.PropertyChanged += (_, args) => detailChanges.Add(args.PropertyName);

        env.Vm.RefreshTheme();

        Assert.All(navChanges.Values, changes =>
        {
            Assert.Contains(nameof(TodoNavItemViewModel.IsSelected), changes);
            Assert.Contains(nameof(TodoNavItemViewModel.ColorHex), changes);
        });
        Assert.Contains(nameof(TodoStepItemViewModel.IsCompleted), stepChanges);
        Assert.Contains(nameof(TodoPageViewModel.DetailIsCompleted), detailChanges);
        Assert.Contains(nameof(TodoPageViewModel.DetailIsImportant), detailChanges);
        Assert.Same(selectedNav, env.Vm.SelectedNav);
        Assert.Same(selectedTask, env.Vm.SelectedTask);
        Assert.Equal("unsaved draft", env.Vm.DetailTitle);
        Assert.Equal("theme test", (await env.Service.GetTaskAsync(task.Id))!.Title);
        env.Vm.Detach();
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 9, 4, 30, 0, TimeSpan.Zero);

    /// <summary>Local-time instant relative to the pinned Now (timezone-independent, TodoServiceTests pattern).</summary>
    private static DateTimeOffset LocalAt(int dayOffset, int hour, int minute = 0)
    {
        var tz = TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(Now, tz).Date.AddDays(dayOffset).AddHours(hour).AddMinutes(minute);
        return new DateTimeOffset(
            DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz.GetUtcOffset(local)).ToUniversalTime();
    }

    private sealed class Env
    {
        public FakeTodoService Service { get; }
        public TodoPageViewModel Vm { get; }

        public Env(TimeSpan? searchDebounce = null, TimeSpan? detailSaveDebounce = null, TimeSpan? undoWindow = null)
        {
            Service = new FakeTodoService(() => Now);
            Vm = new TodoPageViewModel(
                Service,
                NullLogger<TodoPageViewModel>.Instance,
                clock: () => Now,
                searchDebounce,
                detailSaveDebounce,
                undoWindow);
        }

        public QNote.Models.TodoTask AddTask(
            string listUuid,
            string title,
            DateTimeOffset? due = null,
            bool allDay = false,
            bool important = false,
            bool completed = false,
            string notes = "") => Service.SeedTask(listUuid, title, due, allDay, important, completed, notes);

        /// <summary>Switch to the 全部 view (no-due tasks are absent from 今日 by definition).</summary>
        public void SelectAll() => Vm.SelectNav(Vm.NavItems.First(n => n.Kind == TodoNavKind.All));
    }

    // ---------- load & nav ----------

    [Fact]
    public async Task LoadAsync_PopulatesNavBadgesAndTodayView()
    {
        var env = new Env();
        var today = env.AddTask(FakeTodoService.WorkUuid, "今天开会", LocalAt(0, 18));
        var overdue = env.AddTask(FakeTodoService.WorkUuid, "逾期修复", LocalAt(-1, 9));
        env.AddTask(FakeTodoService.LifeUuid, "明天买菜", LocalAt(1, 10));
        env.AddTask(FakeTodoService.LifeUuid, "重要无期限", important: true);
        env.AddTask(FakeTodoService.WorkUuid, "已完成", due: LocalAt(0, 10), completed: true);

        await env.Vm.LoadAsync();

        // nav: 4 smart views + cap + 2 seeded lists
        Assert.Equal(7, env.Vm.NavItems.Count);
        Assert.Equal(TodoNavKind.Today, env.Vm.SelectedNav?.Kind);

        Assert.Equal(2, env.Vm.NavItems.First(n => n.Kind == TodoNavKind.Today).IncompleteCount);      // due today + overdue
        Assert.Equal(1, env.Vm.NavItems.First(n => n.Kind == TodoNavKind.Important).IncompleteCount);  // the starred task
        Assert.Equal(3, env.Vm.NavItems.First(n => n.Kind == TodoNavKind.Planned).IncompleteCount);    // today + overdue + tomorrow
        Assert.Equal(4, env.Vm.NavItems.First(n => n.Kind == TodoNavKind.All).IncompleteCount);        // every incomplete task

        var workNav = env.Vm.NavItems.First(n => n.Kind == TodoNavKind.List && n.Title == "工作");
        var lifeNav = env.Vm.NavItems.First(n => n.Kind == TodoNavKind.List && n.Title == "生活");
        Assert.Equal(2, workNav.IncompleteCount); // 今天开会 + 逾期修复
        Assert.Equal(2, lifeNav.IncompleteCount); // 明天买菜 + 重要无期限

        // 今日 view: incomplete chronological (overdue first), completed in the tail
        Assert.Equal(
            new[] { overdue.Id, today.Id },
            env.Vm.Tasks.Select(t => t.Id).ToArray());
        Assert.Equal("已完成", Assert.Single(env.Vm.CompletedTasks).Title);
        Assert.True(env.Vm.Tasks.Single(t => t.Id == overdue.Id).IsOverdue);
        Assert.False(env.Vm.Tasks.Single(t => t.Id == today.Id).IsOverdue);
    }

    [Fact]
    public async Task SelectNav_List_ShowsOnlyThatList()
    {
        var env = new Env();
        env.AddTask(FakeTodoService.WorkUuid, "工作任务");
        env.AddTask(FakeTodoService.LifeUuid, "生活任务");
        await env.Vm.LoadAsync();

        var workNav = env.Vm.NavItems.First(n => n.Kind == TodoNavKind.List && n.Title == "工作");
        env.Vm.SelectNav(workNav);

        Assert.Equal("工作任务", Assert.Single(env.Vm.Tasks).Title);
        Assert.True(workNav.IsSelected);
    }

    [Fact]
    public async Task SelectNav_Important_ShowsStarredIncomplete()
    {
        var env = new Env();
        env.AddTask(FakeTodoService.WorkUuid, "普通");
        env.AddTask(FakeTodoService.WorkUuid, "星标", important: true);
        env.AddTask(FakeTodoService.WorkUuid, "完成的星标", important: true, completed: true);
        await env.Vm.LoadAsync();

        env.Vm.SelectNav(env.Vm.NavItems.First(n => n.Kind == TodoNavKind.Important));

        Assert.Equal("星标", Assert.Single(env.Vm.Tasks).Title);
    }

    // ---------- quick add ----------

    [Fact]
    public async Task QuickAdd_CreatesInSelectedList_AndClearsBox()
    {
        var env = new Env();
        await env.Vm.LoadAsync();
        var workNav = env.Vm.NavItems.First(n => n.Kind == TodoNavKind.List && n.Title == "工作");
        env.Vm.SelectNav(workNav);

        env.Vm.QuickAddTitle = "  新任务  ";
        await env.Vm.QuickAddAsync();

        Assert.Contains($"CreateTask:{FakeTodoService.WorkUuid}:新任务", env.Service.Calls);
        Assert.Equal(string.Empty, env.Vm.QuickAddTitle);
        Assert.Contains(env.Vm.Tasks, t => t.Title == "新任务");
    }

    [Fact]
    public async Task QuickAdd_FromSmartView_UsesFirstList()
    {
        var env = new Env();
        await env.Vm.LoadAsync(); // 今日 selected

        env.Vm.QuickAddTitle = "无上下文任务";
        await env.Vm.QuickAddAsync();

        Assert.Contains($"CreateTask:{FakeTodoService.WorkUuid}:无上下文任务", env.Service.Calls);
    }

    [Fact]
    public async Task QuickAdd_BlankTitle_IsNoOp()
    {
        var env = new Env();
        await env.Vm.LoadAsync();

        env.Vm.QuickAddTitle = "   ";
        await env.Vm.QuickAddAsync();

        Assert.DoesNotContain(env.Service.Calls, c => c.StartsWith("CreateTask:", StringComparison.Ordinal));
    }

    // ---------- completion & importance ----------

    [Fact]
    public async Task ToggleComplete_MovesBetweenTailAndList_AndUpdatesCounts()
    {
        var env = new Env();
        var task = env.AddTask(FakeTodoService.WorkUuid, "待勾选");
        await env.Vm.LoadAsync();
        env.SelectAll();

        var item = env.Vm.Tasks.Single(t => t.Id == task.Id);
        env.Vm.SelectedTask = item; // open the detail pane too
        await env.Vm.ToggleCompleteAsync(item);

        Assert.DoesNotContain(item, env.Vm.Tasks);
        Assert.Contains(item, env.Vm.CompletedTasks);
        Assert.True(item.IsCompleted);
        Assert.True(env.Vm.DetailIsCompleted); // detail header checkbox follows
        Assert.Equal(0, env.Vm.NavItems.First(n => n.Kind == TodoNavKind.All).IncompleteCount);

        await env.Vm.ToggleCompleteAsync(item);
        Assert.Contains(item, env.Vm.Tasks);
        Assert.DoesNotContain(item, env.Vm.CompletedTasks);
        Assert.False(item.IsCompleted);
        Assert.False(env.Vm.DetailIsCompleted);
    }

    [Fact]
    public async Task ToggleImportant_UpdatesBadgeAndItem()
    {
        var env = new Env();
        var task = env.AddTask(FakeTodoService.WorkUuid, "普通任务");
        await env.Vm.LoadAsync();
        env.SelectAll();

        var item = env.Vm.Tasks.Single(t => t.Id == task.Id);
        await env.Vm.ToggleImportantAsync(item);

        Assert.True(item.IsImportant);
        Assert.Equal(1, env.Vm.NavItems.First(n => n.Kind == TodoNavKind.Important).IncompleteCount);
        Assert.Contains($"SetImportant:{task.Id}:True", env.Service.Calls);
    }

    // ---------- detail / due policy (PRD Decision 4) ----------

    [Fact]
    public async Task Detail_OpenLoadsTask_TimedDueAppliesReminderDefaultOn()
    {
        var env = new Env();
        var task = env.AddTask(FakeTodoService.WorkUuid, "带时间任务", due: LocalAt(0, 18), notes: "原始备注");
        await env.Vm.LoadAsync();

        var item = env.Vm.Tasks.Single(t => t.Id == task.Id);
        env.Vm.SelectedTask = item;

        Assert.True(env.Vm.HasSelectedTask);
        Assert.Equal("带时间任务", env.Vm.DetailTitle);
        Assert.Equal("原始备注", env.Vm.DetailNotes);
        Assert.True(env.Vm.DetailHasDue);

        // Re-applying a TIMED due keeps the reminder ON by policy.
        env.Vm.DetailDueDate = new DateTimeOffset(
            DateTime.SpecifyKind(LocalAt(0, 18).ToLocalTime().Date, DateTimeKind.Unspecified));
        env.Vm.DetailDueTime = new TimeSpan(18, 0, 0);
        env.Vm.DetailIsAllDay = false;
        await env.Vm.ApplyDueEditAsync();

        Assert.True(env.Vm.DetailReminderOn);
        Assert.True(item.HasDue);
        Assert.Contains($"SetDue:{task.Id}:", Assert.Single(env.Service.Calls, c => c.StartsWith($"SetDue:{task.Id}:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ApplyDueEdit_AllDayDue_TurnsReminderOffByPolicy()
    {
        var env = new Env();
        var task = env.AddTask(FakeTodoService.WorkUuid, "全天任务");
        await env.Vm.LoadAsync();
        env.SelectAll();
        var item = env.Vm.Tasks.Single(t => t.Id == task.Id);
        env.Vm.SelectedTask = item;

        env.Vm.DetailIsAllDay = true;
        env.Vm.DetailDueTime = new TimeSpan(9, 0, 0);
        await env.Vm.ApplyDueEditAsync();

        Assert.False(env.Vm.DetailReminderOn); // Decision 4: all-day defaults to no reminder
        Assert.True(item.HasDue);
        Assert.True(item.IsAllDay);
        Assert.Contains($"SetDue:{task.Id}:", Assert.Single(env.Service.Calls, c => c.StartsWith($"SetDue:{task.Id}:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ApplyDuePreset_SetsAllDayDue_ReminderOffByPolicy()
    {
        var env = new Env();
        var task = env.AddTask(FakeTodoService.WorkUuid, "预设任务");
        await env.Vm.LoadAsync();
        env.SelectAll();
        var item = env.Vm.Tasks.Single(t => t.Id == task.Id);
        env.Vm.SelectedTask = item;

        await env.Vm.ApplyDuePresetAsync(1); // 明天

        Assert.True(env.Vm.DetailHasDue);
        Assert.True(env.Vm.DetailIsAllDay);
        Assert.False(env.Vm.DetailReminderOn); // Decision 4: all-day preset ⇒ no reminder
        Assert.True(item.HasDue);
        Assert.True(item.IsAllDay);
        Assert.Contains($"SetDue:{task.Id}:", Assert.Single(env.Service.Calls, c => c.StartsWith($"SetDue:{task.Id}:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ResetDueEditState_ReSeedsPickersFromLoadedTask()
    {
        var env = new Env();
        var task = env.AddTask(FakeTodoService.WorkUuid, "取消恢复", due: LocalAt(2, 14));
        await env.Vm.LoadAsync();
        env.SelectAll();
        var item = env.Vm.Tasks.Single(t => t.Id == task.Id);
        env.Vm.SelectedTask = item;

        // unapplied fiddling in the stage-2 picker
        env.Vm.DetailDueDate = new DateTimeOffset(
            DateTime.SpecifyKind(LocalAt(9, 0).ToLocalTime().Date, DateTimeKind.Unspecified));
        env.Vm.DetailDueTime = new TimeSpan(23, 30, 0);
        env.Vm.DetailIsAllDay = true;

        env.Vm.ResetDueEditState(); // 取消

        Assert.Equal(LocalAt(2, 14).ToLocalTime().Date, env.Vm.DetailDueDate.LocalDateTime.Date);
        Assert.Equal(new TimeSpan(14, 0, 0), env.Vm.DetailDueTime);
        Assert.False(env.Vm.DetailIsAllDay);
    }

    [Fact]
    public async Task ApplyReminderPreset_LaterToday_SetsTimedDue_ReminderOn()
    {
        var env = new Env();
        var task = env.AddTask(FakeTodoService.WorkUuid, "提醒预设");
        await env.Vm.LoadAsync();
        env.SelectAll();
        var item = env.Vm.Tasks.Single(t => t.Id == task.Id);
        env.Vm.SelectedTask = item;

        await env.Vm.ApplyReminderPresetAsync(0); // 今天晚些时候 = today 18:00

        Assert.True(env.Vm.DetailHasDue);
        Assert.False(env.Vm.DetailIsAllDay);
        Assert.True(env.Vm.DetailReminderOn); // timed due ⇒ reminder on (Decision 4)
        Assert.True(item.HasDue);
        Assert.Contains($"SetDue:{task.Id}:", Assert.Single(env.Service.Calls, c => c.StartsWith($"SetDue:{task.Id}:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ApplyDueDate_Stage2Calendar_SetsAllDayDue_ReminderOff()
    {
        var env = new Env();
        var task = env.AddTask(FakeTodoService.WorkUuid, "日历选日期");
        await env.Vm.LoadAsync();
        env.SelectAll();
        var item = env.Vm.Tasks.Single(t => t.Id == task.Id);
        env.Vm.SelectedTask = item;

        env.Vm.DetailDueDate = new DateTimeOffset(
            DateTime.SpecifyKind(LocalAt(5, 0).ToLocalTime().Date, DateTimeKind.Unspecified));
        await env.Vm.ApplyDueDateAsync();

        Assert.True(env.Vm.DetailHasDue);
        Assert.True(env.Vm.DetailIsAllDay);
        Assert.False(env.Vm.DetailReminderOn); // all-day due ⇒ no reminder (Decision 4)
        Assert.True(item.IsAllDay);
    }

    [Fact]
    public async Task ApplyDuePreset_NextWeek_LandsOnNextMonday()
    {
        var env = new Env();
        var task = env.AddTask(FakeTodoService.WorkUuid, "下周任务");
        await env.Vm.LoadAsync();
        env.SelectAll();
        var item = env.Vm.Tasks.Single(t => t.Id == task.Id);
        env.Vm.SelectedTask = item;

        await env.Vm.ApplyDuePresetAsync(7); // 下周

        // Pinned clock: 2026-10-09 (Friday) → next Monday = 10-12 (real-To-Do semantics).
        Assert.True(env.Vm.DetailIsAllDay);
        Assert.NotNull(env.Vm.DetailDueAt);
        var due = env.Vm.DetailDueAt.Value.ToLocalTime();
        Assert.Equal(DayOfWeek.Monday, due.DayOfWeek);
        Assert.Equal(LocalAt(3, 0).ToLocalTime().Date, due.Date);
    }

    [Fact]
    public async Task ClearDue_RemovesDueAndReminder()
    {
        var env = new Env();
        var task = env.AddTask(FakeTodoService.WorkUuid, "会清期的任务", due: LocalAt(0, 18));
        await env.Vm.LoadAsync();
        var item = env.Vm.Tasks.Single(t => t.Id == task.Id);
        env.Vm.SelectedTask = item;

        await env.Vm.ClearDueAsync();

        Assert.False(env.Vm.DetailHasDue);
        Assert.False(env.Vm.DetailReminderOn);
        Assert.False(item.HasDue);
        Assert.Contains($"SetDue:{task.Id}:null:False", env.Service.Calls);
    }

    [Fact]
    public async Task SetDetailReminder_ExplicitToggle_WinsOverDefault()
    {
        var env = new Env();
        var task = env.AddTask(FakeTodoService.WorkUuid, "显式提醒", due: LocalAt(0, 18));
        await env.Vm.LoadAsync();
        env.Vm.SelectedTask = env.Vm.Tasks.Single(t => t.Id == task.Id);

        await env.Vm.SetDetailReminderAsync(false); // user turns the default off
        Assert.False(env.Vm.DetailReminderOn);

        await env.Vm.SetDetailReminderAsync(true); // and back on
        Assert.True(env.Vm.DetailReminderOn);
        Assert.Contains($"SetReminder:{task.Id}:False", env.Service.Calls);
    }

    // ---------- search ----------

    [Fact]
    public async Task Search_FiltersByTitle_ClearRestoresView()
    {
        var env = new Env(searchDebounce: TimeSpan.Zero);
        env.AddTask(FakeTodoService.WorkUuid, "修复搜索偏移");
        env.AddTask(FakeTodoService.WorkUuid, "准备周会");
        env.AddTask(FakeTodoService.LifeUuid, "接口联调", due: LocalAt(0, 18));
        await env.Vm.LoadAsync(); // today view: only 接口联调 has a due date

        env.Vm.SearchText = "接口";
        Assert.Equal("接口联调", Assert.Single(env.Vm.Tasks).Title);
        Assert.True(env.Vm.IsSearching);

        env.Vm.SearchText = "";
        Assert.False(env.Vm.IsSearching);
        Assert.Equal("接口联调", Assert.Single(env.Vm.Tasks).Title); // back to the 今日 view
    }

    // ---------- delete with undo ----------

    [Fact]
    public async Task DeleteTask_StagesUndo_CommitsAfterWindow()
    {
        var env = new Env(undoWindow: TimeSpan.FromMilliseconds(50));
        var task = env.AddTask(FakeTodoService.WorkUuid, "将被删除");
        await env.Vm.LoadAsync();
        env.SelectAll();

        string? deletedTitle = null;
        env.Vm.TaskDeleted += t => deletedTitle = t;

        var item = env.Vm.Tasks.Single(t => t.Id == task.Id);
        await env.Vm.DeleteTaskAsync(item);

        Assert.Equal("将被删除", deletedTitle);
        Assert.Empty(env.Vm.Tasks); // gone from the list at once
        Assert.DoesNotContain(env.Service.Calls, c => c.StartsWith($"DeleteTask:{task.Id}", StringComparison.Ordinal)); // still in the DB

        await Task.Delay(250); // undo window elapses
        Assert.Contains($"DeleteTask:{task.Id}", env.Service.Calls); // committed
    }

    [Fact]
    public async Task UndoDelete_RestoresTaskWithoutDbRoundTrip()
    {
        var env = new Env(undoWindow: TimeSpan.FromSeconds(10));
        var task = env.AddTask(FakeTodoService.WorkUuid, "会撤销的");
        await env.Vm.LoadAsync();
        env.SelectAll();

        var item = env.Vm.Tasks.Single(t => t.Id == task.Id);
        await env.Vm.DeleteTaskAsync(item);
        await env.Vm.UndoDeleteAsync();

        Assert.DoesNotContain(env.Service.Calls, c => c.StartsWith($"DeleteTask:{task.Id}", StringComparison.Ordinal));
        Assert.Contains(env.Vm.Tasks, t => t.Id == task.Id); // restored into the view
    }

    [Fact]
    public async Task FlushAsync_CommitsPendingDeleteImmediately()
    {
        var env = new Env(undoWindow: TimeSpan.FromSeconds(10));
        var task = env.AddTask(FakeTodoService.WorkUuid, "待提交删除");
        await env.Vm.LoadAsync();
        env.SelectAll();

        await env.Vm.DeleteTaskAsync(env.Vm.Tasks.Single(t => t.Id == task.Id));
        await env.Vm.FlushAsync();

        Assert.Contains($"DeleteTask:{task.Id}", env.Service.Calls);
    }

    // ---------- debounced autosave ----------

    [Fact]
    public async Task DetailEdits_PersistThroughFlush_NotBefore()
    {
        var env = new Env(detailSaveDebounce: TimeSpan.FromSeconds(10));
        var task = env.AddTask(FakeTodoService.WorkUuid, "原标题", notes: "原备注");
        await env.Vm.LoadAsync();
        env.SelectAll();
        env.Vm.SelectedTask = env.Vm.Tasks.Single(t => t.Id == task.Id);

        env.Vm.DetailTitle = "新标题";
        env.Vm.DetailNotes = "新备注";
        Assert.DoesNotContain(env.Service.Calls, c => c.StartsWith($"UpdateTask:{task.Id}", StringComparison.Ordinal)); // still pending

        await env.Vm.FlushAsync();

        Assert.Contains($"UpdateTask:{task.Id}:新标题", env.Service.Calls);
        Assert.Equal("新标题", env.Vm.Tasks.Single(t => t.Id == task.Id).Title); // card follows
    }

    // ---------- detail pane 保存 / 取消 ----------

    [Fact]
    public async Task SaveDetail_FlushesPendingEdit_AndClosesPane()
    {
        var env = new Env(detailSaveDebounce: TimeSpan.FromSeconds(10));
        var task = env.AddTask(FakeTodoService.WorkUuid, "原标题", notes: "原备注");
        await env.Vm.LoadAsync();
        env.SelectAll();
        env.Vm.SelectedTask = env.Vm.Tasks.Single(t => t.Id == task.Id);

        env.Vm.DetailTitle = "保存后的标题";
        await env.Vm.SaveDetailAsync();

        Assert.Contains($"UpdateTask:{task.Id}:保存后的标题", env.Service.Calls);
        Assert.Null(env.Vm.SelectedTask);
        Assert.False(env.Vm.HasSelectedTask);
    }

    [Fact]
    public async Task CancelDetail_RestoresSnapshotTitleAndNotes_AndCloses()
    {
        var env = new Env(detailSaveDebounce: TimeSpan.FromSeconds(10));
        var task = env.AddTask(FakeTodoService.WorkUuid, "原标题", notes: "原备注");
        await env.Vm.LoadAsync();
        env.SelectAll();
        env.Vm.SelectedTask = env.Vm.Tasks.Single(t => t.Id == task.Id);

        env.Vm.DetailTitle = "改过的标题";
        env.Vm.DetailNotes = "改过的备注";
        await env.Vm.CancelDetailAsync();

        // Restore goes through the service with the selection-time values.
        Assert.Contains($"UpdateTask:{task.Id}:原标题", env.Service.Calls);
        var stored = await env.Service.GetTaskAsync(task.Id);
        Assert.Equal("原标题", stored!.Title);
        Assert.Equal("原备注", stored.Notes);
        // The list card follows the restore, and the pane closes.
        Assert.Equal("原标题", env.Vm.Tasks.Single(t => t.Id == task.Id).Title);
        Assert.False(env.Vm.HasSelectedTask);
    }

    [Fact]
    public async Task CancelDetail_NoPendingEdit_PerformsNoRestoreWrite()
    {
        var env = new Env();
        var task = env.AddTask(FakeTodoService.WorkUuid, "稳定的任务");
        await env.Vm.LoadAsync();
        env.SelectAll();
        env.Vm.SelectedTask = env.Vm.Tasks.Single(t => t.Id == task.Id);

        await env.Vm.CancelDetailAsync();

        Assert.DoesNotContain(env.Service.Calls, c => c.StartsWith("UpdateTask:", StringComparison.Ordinal));
        Assert.Null(env.Vm.SelectedTask);
    }

    [Fact]
    public async Task CancelDetail_SnapshotSwapsWhenSwitchingTasks()
    {
        var env = new Env(detailSaveDebounce: TimeSpan.FromSeconds(10));
        var a = env.AddTask(FakeTodoService.WorkUuid, "任务A");
        var b = env.AddTask(FakeTodoService.LifeUuid, "任务B");
        await env.Vm.LoadAsync();
        env.SelectAll();

        env.Vm.SelectedTask = env.Vm.Tasks.Single(t => t.Id == a.Id);
        env.Vm.DetailTitle = "A 被改";
        env.Vm.SelectedTask = env.Vm.Tasks.Single(t => t.Id == b.Id); // switch: A's edit flushes first

        Assert.Contains($"UpdateTask:{a.Id}:A 被改", env.Service.Calls); // flush-on-switch kept
        env.Vm.DetailTitle = "B 被改";
        await env.Vm.CancelDetailAsync();

        // B is restored to B's OWN selection-time snapshot — never A's values —
        // and A's flushed edit stays saved.
        Assert.Contains($"UpdateTask:{b.Id}:任务B", env.Service.Calls);
        Assert.Equal("任务B", (await env.Service.GetTaskAsync(b.Id))!.Title);
        Assert.Equal("A 被改", (await env.Service.GetTaskAsync(a.Id))!.Title);
    }

    // ---------- list CRUD ----------

    [Fact]
    public async Task CreateList_AddsNavRowAndSelectsIt()
    {
        var env = new Env();
        await env.Vm.LoadAsync();

        var error = await env.Vm.CreateListAsync("购物", "#F59E0B");

        Assert.Null(error);
        var created = env.Vm.NavItems.Single(n => n.Title == "购物");
        Assert.Equal(created, env.Vm.SelectedNav);
        Assert.Equal(TodoNavKind.List, created.Kind);
    }

    [Fact]
    public async Task CreateList_BlankName_ReturnsErrorKey()
    {
        var env = new Env();
        await env.Vm.LoadAsync();

        Assert.Equal("TodoListNameRequired", await env.Vm.CreateListAsync("   ", "#F59E0B"));
    }

    [Fact]
    public async Task DeleteList_CascadesTasks_AndFallsBackToToday()
    {
        var env = new Env();
        await env.Vm.CreateListAsync("临时清单", "#F59E0B");
        var listNav = env.Vm.SelectedNav!;
        env.AddTask(listNav.Id, "清单内任务");
        env.AddTask(FakeTodoService.WorkUuid, "工作保留", due: LocalAt(0, 18));
        await env.Vm.LoadAsync();
        env.Vm.SelectNav(listNav);
        Assert.Single(env.Vm.Tasks);

        var error = await env.Vm.DeleteListAsync(listNav);

        Assert.Null(error);
        Assert.DoesNotContain(env.Vm.NavItems, n => n.Title == "临时清单");
        Assert.Equal(TodoNavKind.Today, env.Vm.SelectedNav?.Kind);
        Assert.Equal("工作保留", Assert.Single(env.Vm.Tasks).Title); // cascade removed the list's task
    }

    // ---------- toast deep link (task 10-04 stage ③) ----------

    [Fact]
    public async Task SelectTaskByUuid_SelectsTaskInCurrentView_AndOpensDetail()
    {
        var env = new Env();
        var task = env.AddTask(FakeTodoService.WorkUuid, "今天开会", LocalAt(0, 18));
        await env.Vm.LoadAsync(); // 今日 view contains the task

        await env.Vm.SelectTaskByUuidAsync(task.Uuid);

        Assert.Equal(task.Id, env.Vm.SelectedTask?.Id);
        Assert.True(env.Vm.HasSelectedTask);
        Assert.Equal("今天开会", env.Vm.DetailTitle); // detail pane loaded the full record
    }

    [Fact]
    public async Task SelectTaskByUuid_NotInCurrentView_FallsBackToAllView()
    {
        var env = new Env();
        var noDue = env.AddTask(FakeTodoService.WorkUuid, "无期限任务"); // absent from 今日
        await env.Vm.LoadAsync();

        await env.Vm.SelectTaskByUuidAsync(noDue.Uuid);

        Assert.Equal(TodoNavKind.All, env.Vm.SelectedNav?.Kind);
        Assert.Equal(noDue.Id, env.Vm.SelectedTask?.Id);
    }

    [Fact]
    public async Task SelectTaskByUuid_ActiveSearch_ClearedAndFoundInAllView()
    {
        var env = new Env(searchDebounce: TimeSpan.Zero);
        var a = env.AddTask(FakeTodoService.WorkUuid, "买牛奶");
        env.AddTask(FakeTodoService.WorkUuid, "买咖啡");
        await env.Vm.LoadAsync();

        env.Vm.SearchText = "买咖啡";
        await Task.Yield(); // let the zero-debounce search run
        Assert.Single(env.Vm.Tasks); // search filtered

        await env.Vm.SelectTaskByUuidAsync(a.Uuid);

        Assert.Equal(TodoNavKind.All, env.Vm.SelectedNav?.Kind);
        Assert.False(env.Vm.IsSearching); // browsing wins over search
        Assert.Equal(a.Id, env.Vm.SelectedTask?.Id);
    }

    [Fact]
    public async Task SelectTaskByUuid_UnknownUuid_IsASilentNoOp()
    {
        var env = new Env();
        env.AddTask(FakeTodoService.WorkUuid, "今天开会", LocalAt(0, 18));
        await env.Vm.LoadAsync();

        await env.Vm.SelectTaskByUuidAsync("0123456789abcdef0123456789abcdef");

        Assert.Null(env.Vm.SelectedTask); // task deleted since the toast was shown
        Assert.Equal(TodoNavKind.All, env.Vm.SelectedNav?.Kind); // fell back, found nothing, kept going
    }
}
