using Microsoft.Extensions.Logging.Abstractions;
using QNote.Models;
using QNote.Services;

namespace QNote.Tests;

/// <summary>
/// <see cref="TodoReminderCoordinator"/> battery over the real repository
/// (headless SQLite) + <see cref="FakeToastScheduler"/>: launch reconcile
/// (stale sweep, 14-day fill, idempotence), the OS-drop catch-up window
/// (24 h grace, once per due crossing, still-incomplete only), and the
/// cancel-on-mutation wiring through <see cref="TodoService"/> — including the
/// best-effort seam (scheduler failures never fail a data mutation). Time
/// anchors are UTC offsets around a pinned instant — timezone-independent.
/// </summary>
public sealed class TodoReminderCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 4, 30, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double hours) => Now.AddHours(hours);

    private sealed class Fixture : IDisposable
    {
        public TestDatabase Db { get; } = new();
        public FakeToastScheduler Scheduler { get; } = new();

        /// <summary>Mutable clock — the coordinator reads it on every call.</summary>
        public DateTimeOffset Clock { get; set; } = Now;

        public TodoReminderCoordinator Coordinator { get; }

        public TodoService NewTodoService()
        {
            var service = new TodoService(
                Db.NewTodoRepository(), NullLogger<TodoService>.Instance,
                clock: () => Clock, reminders: Coordinator);
            return service;
        }

        public Fixture()
        {
            Coordinator = new TodoReminderCoordinator(
                Db.NewTodoRepository(), Scheduler,
                NullLogger<TodoReminderCoordinator>.Instance, clock: () => Clock);
        }

        public async Task<TodoTask> SeedTaskAsync(
            string title, DateTimeOffset? due, bool reminderOn = false, bool completed = false)
        {
            var repo = Db.NewTodoRepository();
            var list = (await repo.GetListsAsync())[0];
            var stampedAt = completed ? At(-2) : At(-1);
            return await repo.CreateTaskAsync(new TodoTask
            {
                Uuid = Guid.NewGuid().ToString("N"),
                ListUuid = list.Uuid,
                Title = title,
                DueAt = due,
                ReminderOn = reminderOn,
                CompletedAt = completed ? At(-2) : null,
                CreatedAt = stampedAt,
                UpdatedAt = stampedAt,
            });
        }

        public void Dispose() => Db.Dispose();
    }

    // ---------- launch reconcile: stale sweep + 14-day fill ----------

    [Fact]
    public async Task Reconcile_FillsMissingReminders_WithinThe14DayHorizon()
    {
        using var f = new Fixture();
        var soon = await f.SeedTaskAsync("两小时后", At(2), reminderOn: true);
        var edge = await f.SeedTaskAsync("14 天边界", Now.AddDays(14), reminderOn: true);
        var far = await f.SeedTaskAsync("远期任务", At(24 * 30), reminderOn: true);

        await f.Coordinator.ReconcileAsync();

        Assert.Contains(soon.Uuid, f.Scheduler.Scheduled.Keys);
        Assert.Contains(edge.Uuid, f.Scheduler.Scheduled.Keys);
        Assert.DoesNotContain(far.Uuid, f.Scheduler.Scheduled.Keys); // horizon bounds what reconcile ADDS
        Assert.Equal(At(2), f.Scheduler.Scheduled[soon.Uuid].DueAt);
        Assert.Equal("两小时后", f.Scheduler.Scheduled[soon.Uuid].Title);
    }

    [Fact]
    public async Task Reconcile_KeepsFarFutureToast_TheOSAlreadyHolds()
    {
        using var f = new Fixture();
        var far = await f.SeedTaskAsync("远期任务", At(24 * 30), reminderOn: true);

        // The user set this reminder directly (mutation path schedules any
        // future due); reconcile must not cancel it for being far away.
        await f.Scheduler.ScheduleAsync(new TodoReminder
        {
            TaskUuid = far.Uuid, Title = far.Title, DueAt = far.DueAt!.Value, IsAllDay = false,
        });
        f.Scheduler.Calls.Clear(); // drop the seeding call itself from the log

        await f.Coordinator.ReconcileAsync();

        Assert.Contains(far.Uuid, f.Scheduler.Scheduled.Keys);
        Assert.DoesNotContain($"Schedule:{far.Uuid}", f.Scheduler.Calls); // kept, not re-asserted
    }

    [Fact]
    public async Task Reconcile_CancelsStaleScheduledEntries()
    {
        using var f = new Fixture();
        var valid = await f.SeedTaskAsync("有效", At(2), reminderOn: true);
        var completed = await f.SeedTaskAsync("已完成", At(2), reminderOn: true, completed: true);
        var reminderOff = await f.SeedTaskAsync("提醒关着", At(2), reminderOn: false);
        await f.Coordinator.SyncTaskAsync(valid);

        // Simulate entries left behind while the app was off (the exact states
        // the sweep exists for) plus a toast whose task is gone entirely.
        foreach (var task in new[] { completed, reminderOff })
        {
            await f.Scheduler.ScheduleAsync(new TodoReminder
            {
                TaskUuid = task.Uuid, Title = task.Title, DueAt = task.DueAt!.Value,
            });
        }
        await f.Scheduler.ScheduleAsync(new TodoReminder
        {
            TaskUuid = "deadbeefdeadbeefdeadbeefdeadbeef", Title = "已删除", DueAt = At(2),
        });

        await f.Coordinator.ReconcileAsync();

        Assert.Contains(valid.Uuid, f.Scheduler.Scheduled.Keys);
        Assert.DoesNotContain(completed.Uuid, f.Scheduler.Scheduled.Keys);
        Assert.DoesNotContain(reminderOff.Uuid, f.Scheduler.Scheduled.Keys);
        Assert.DoesNotContain("deadbeefdeadbeefdeadbeefdeadbeef", f.Scheduler.Scheduled.Keys);
    }

    [Fact]
    public async Task Reconcile_IsIdempotent_SecondRunReassertsNothing()
    {
        using var f = new Fixture();
        var a = await f.SeedTaskAsync("A", At(2), reminderOn: true);
        var b = await f.SeedTaskAsync("B", At(48), reminderOn: true);

        await f.Coordinator.ReconcileAsync();
        f.Scheduler.Calls.Clear();
        await f.Coordinator.ReconcileAsync();

        Assert.DoesNotContain(f.Scheduler.Calls, c => c.StartsWith("Schedule:", StringComparison.Ordinal));
        Assert.DoesNotContain(f.Scheduler.Calls, c => c.StartsWith("Cancel:", StringComparison.Ordinal));
        Assert.Equal(new[] { a.Uuid, b.Uuid }.OrderBy(u => u), f.Scheduler.Scheduled.Keys.OrderBy(u => u));
    }

    [Fact]
    public async Task Reconcile_PastDueReminderOnTask_IsNotScheduled_CatchUpOwnsIt()
    {
        using var f = new Fixture();
        // Overdue + reminder on: the scheduled toast era is over — scheduling a
        // toast in the past would throw; the catch-up path re-fires it instead.
        var overdue = await f.SeedTaskAsync("逾期", At(-1), reminderOn: true);

        await f.Coordinator.ReconcileAsync();

        Assert.DoesNotContain(overdue.Uuid, f.Scheduler.Scheduled.Keys);
        Assert.Equal(overdue.Uuid, Assert.Single(f.Scheduler.ShownNow).TaskUuid);
    }

    // ---------- catch-up window (OS 5-minute drop) ----------

    [Fact]
    public async Task CatchUp_ReFiresOnce_PerDueCrossing_OnlyWhileIncomplete()
    {
        using var f = new Fixture();
        var missed = await f.SeedTaskAsync("错过", At(-1), reminderOn: true);

        await f.Coordinator.ReconcileAsync();
        await f.Coordinator.ReconcileAsync(); // the marker must make the second run silent

        Assert.Single(f.Scheduler.ShownNow, r => r.TaskUuid == missed.Uuid);
        Assert.Equal("错过", f.Scheduler.ShownNow[0].Title);
    }

    [Fact]
    public async Task CatchUp_Skips_Completed_ReminderOff_AndBeyondGrace()
    {
        using var f = new Fixture();
        var completed = await f.SeedTaskAsync("完成了", At(-1), reminderOn: true, completed: true);
        var reminderOff = await f.SeedTaskAsync("提醒关", At(-1), reminderOn: false);
        var tooOld = await f.SeedTaskAsync("超宽限", At(-30), reminderOn: true);
        var future = await f.SeedTaskAsync("没到期", At(1), reminderOn: true);

        await f.Coordinator.ReconcileAsync();

        Assert.Empty(f.Scheduler.ShownNow);
        Assert.Contains(future.Uuid, f.Scheduler.Scheduled.Keys); // future one is scheduled, not re-fired
    }

    [Fact]
    public async Task CatchUp_FiresAgain_AfterTheTaskWasReDatedForward()
    {
        using var f = new Fixture();
        var task = await f.SeedTaskAsync("改期", At(-1), reminderOn: true);

        await f.Coordinator.ReconcileAsync();
        Assert.Single(f.Scheduler.ShownNow);

        // The user re-dates forward; the new due crosses while the app is off again.
        var service = f.NewTodoService();
        await service.SetDueAsync(task.Id, At(1), isAllDay: false);
        f.Clock = At(2);

        await f.Coordinator.ReconcileAsync();

        Assert.Equal(2, f.Scheduler.ShownNow.Count(r => r.TaskUuid == task.Uuid)); // once per due crossing
    }

    // ---------- cancel-on-mutation (TodoService seam) ----------

    [Fact]
    public async Task Mutations_SetDue_SchedulesWithThePolicyDefault()
    {
        using var f = new Fixture();
        var service = f.NewTodoService();
        var created = await service.CreateTaskAsync((await service.GetListsAsync())[0].Uuid, "任务");

        await service.SetDueAsync(created.Id, At(2), isAllDay: false);

        var scheduled = Assert.Single(f.Scheduler.Scheduled.Values);
        Assert.Equal(created.Uuid, scheduled.TaskUuid); // timed due → reminder ON by default

        var allDay = await service.CreateTaskAsync((await service.GetListsAsync())[0].Uuid, "全天");
        await service.SetDueAsync(allDay.Id, At(2), isAllDay: true);
        Assert.DoesNotContain(allDay.Uuid, f.Scheduler.Scheduled.Keys); // all-day → OFF by default
    }

    [Fact]
    public async Task Mutations_ReDate_Replaces_AndClear_Cancels()
    {
        using var f = new Fixture();
        var service = f.NewTodoService();
        var created = await service.CreateTaskAsync((await service.GetListsAsync())[0].Uuid, "任务");

        await service.SetDueAsync(created.Id, At(2), isAllDay: false);
        await service.SetDueAsync(created.Id, At(5), isAllDay: false);
        Assert.Equal(At(5), Assert.Single(f.Scheduler.Scheduled.Values).DueAt); // replace, not stack
        Assert.Single(f.Scheduler.Scheduled.Values);

        await service.SetDueAsync(created.Id, null, isAllDay: false);
        Assert.Empty(f.Scheduler.Scheduled);
    }

    [Fact]
    public async Task Mutations_CompleteCancels_UncompleteReschedules()
    {
        using var f = new Fixture();
        var service = f.NewTodoService();
        var list = (await service.GetListsAsync())[0].Uuid;
        var created = await service.CreateTaskAsync(list, "任务");
        await service.SetDueAsync(created.Id, At(2), isAllDay: false);

        await service.CompleteTaskAsync(created.Id);
        Assert.Empty(f.Scheduler.Scheduled);

        await service.UncompleteTaskAsync(created.Id);
        Assert.Equal(created.Uuid, Assert.Single(f.Scheduler.Scheduled.Keys)); // due still future → back on
    }

    [Fact]
    public async Task Mutations_ReminderToggle_CancelsAndRestores()
    {
        using var f = new Fixture();
        var service = f.NewTodoService();
        var list = (await service.GetListsAsync())[0].Uuid;
        var created = await service.CreateTaskAsync(list, "任务");
        await service.SetDueAsync(created.Id, At(2), isAllDay: false);

        await service.SetReminderAsync(created.Id, reminderOn: false);
        Assert.Empty(f.Scheduler.Scheduled);

        await service.SetReminderAsync(created.Id, reminderOn: true);
        Assert.Equal(created.Uuid, Assert.Single(f.Scheduler.Scheduled.Keys));
    }

    [Fact]
    public async Task Mutations_Retitle_RefreshesThePendingToast()
    {
        using var f = new Fixture();
        var service = f.NewTodoService();
        var list = (await service.GetListsAsync())[0].Uuid;
        var created = await service.CreateTaskAsync(list, "旧标题");
        await service.SetDueAsync(created.Id, At(2), isAllDay: false);

        await service.UpdateTaskAsync(created.Id, "新标题", notes: string.Empty);

        Assert.Equal("新标题", f.Scheduler.Scheduled[created.Uuid].Title);
        Assert.Single(f.Scheduler.Scheduled.Values); // replaced, not stacked
    }

    [Fact]
    public async Task Mutations_DeleteTask_CancelsItsToast()
    {
        using var f = new Fixture();
        var service = f.NewTodoService();
        var list = (await service.GetListsAsync())[0].Uuid;
        var created = await service.CreateTaskAsync(list, "任务");
        await service.SetDueAsync(created.Id, At(2), isAllDay: false);
        Assert.Single(f.Scheduler.Scheduled);

        await service.DeleteTaskAsync(created.Id);

        Assert.Empty(f.Scheduler.Scheduled);
    }

    [Fact]
    public async Task Mutations_DeleteList_CancelsTheCascadedToastsOnly()
    {
        using var f = new Fixture();
        var service = f.NewTodoService();
        var lists = await service.GetListsAsync();
        var work = lists[0];
        var life = lists[1];

        var w1 = await service.CreateTaskAsync(work.Uuid, "工作 1");
        var w2 = await service.CreateTaskAsync(work.Uuid, "工作 2");
        var kept = await service.CreateTaskAsync(life.Uuid, "生活");
        foreach (var t in new[] { w1, w2, kept })
            await service.SetDueAsync(t.Id, At(2), isAllDay: false);

        await service.DeleteListAsync(work.Id);

        Assert.Equal(kept.Uuid, Assert.Single(f.Scheduler.Scheduled.Keys)); // cascade canceled, other list untouched
    }

    [Fact]
    public async Task Mutations_SurviveASchedulerThatAlwaysThrows()
    {
        using var f = new Fixture();
        f.Scheduler.ThrowOnAll = true;
        var service = f.NewTodoService();
        var list = (await service.GetListsAsync())[0].Uuid;
        var created = await service.CreateTaskAsync(list, "任务");

        // None of these may throw — reminder trouble is best-effort by contract.
        await service.SetDueAsync(created.Id, At(2), isAllDay: false);
        await service.UpdateTaskAsync(created.Id, "改名", string.Empty);
        await service.CompleteTaskAsync(created.Id);
        await service.UncompleteTaskAsync(created.Id);
        await service.SetReminderAsync(created.Id, reminderOn: false);
        await service.DeleteTaskAsync(created.Id);

        // Data mutations landed despite the dead scheduler.
        Assert.Null(await f.Db.NewTodoRepository().GetTaskByIdAsync(created.Id));
    }
}
