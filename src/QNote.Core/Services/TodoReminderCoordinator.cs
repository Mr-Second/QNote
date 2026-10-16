using Microsoft.Extensions.Logging;
using QNote.Data;
using QNote.Models;

namespace QNote.Services;

/// <summary>
/// Core reminder lifecycle owner (task 10-04 stage ③). Semantics (PRD §4 +
/// research/reminder-notification-mechanism.md):
/// <list type="bullet">
/// <item><b>Cancel-on-mutation</b> — every <see cref="ITodoService"/> mutation
/// feeds the updated task snapshot back through <see cref="SyncTaskAsync"/>:
/// schedule (Tag = uuid, replace) when the task still wants a toast, cancel
/// when it no longer does. Deletes cancel by uuid; a list delete cancels the
/// whole cascade.</item>
/// <item><b>Launch reconcile</b> — (1) catch-up: reminders the OS silently
/// dropped while the machine was off (the 5-minute window) are re-shown ONCE
/// per due crossing, only while the task is still incomplete, and only within
/// a 24 h grace window past the due instant; (2) stale sweep: scheduled toasts
/// whose task no longer wants one are cancelled; (3) 14-day fill: tasks with a
/// live reminder due within the horizon but missing from the OS schedule are
/// (re)asserted. The horizon bounds only what reconcile ADDS — toasts the OS
/// already holds for far-future tasks stay (a user-set reminder is never
/// cancelled for being far away).</item>
/// <item><b>Best-effort</b> — the scheduler impl swallows its own WinRT
/// failures; the caller (<see cref="TodoService"/> mutations,
/// <c>App.OnLaunched</c>) additionally guards so reminder trouble can never
/// fail a data mutation or startup.</item>
/// </list>
/// Reconcile cannot detect a scheduled toast whose TIME drifted (the uuid-only
/// enumeration carries no delivery time) — but re-dating always goes through
/// the mutation path which replaces eagerly, so drift requires a crash between
/// the DB write and the scheduler call; the toast then fires with the task's
/// real reminder at a stale time — accepted degradation, heals on the next
/// re-date or when the task enters the fill window.
/// </summary>
public sealed class TodoReminderCoordinator : ITodoReminderCoordinator
{
    /// <summary>Launch reconcile fill horizon: schedule the next 14 days (PRD).</summary>
    public static readonly TimeSpan ReconcileHorizon = TimeSpan.FromDays(14);

    /// <summary>Catch-up grace window for OS-dropped reminders (PRD §4: 24 h).</summary>
    public static readonly TimeSpan CatchUpGrace = TimeSpan.FromHours(24);

    private readonly ITodoRepository _repo;
    private readonly IToastScheduler _scheduler;
    private readonly ILogger<TodoReminderCoordinator> _log;
    private readonly Func<DateTimeOffset>? _clock;

    /// <param name="clock">
    /// Test hook: pins "now" for the catch-up window, the stale sweep and the
    /// fill horizon. Production leaves it null (UTC wall clock).
    /// </param>
    public TodoReminderCoordinator(
        ITodoRepository repo,
        IToastScheduler scheduler,
        ILogger<TodoReminderCoordinator> log,
        Func<DateTimeOffset>? clock = null)
    {
        _repo = repo;
        _scheduler = scheduler;
        _log = log;
        _clock = clock;
    }

    private DateTimeOffset UtcNow => _clock?.Invoke() ?? DateTimeOffset.UtcNow;

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var now = UtcNow;

        // 1. Catch-up first — the user-facing part: re-show dropped reminders
        //    while they still matter. The query enforces incomplete + reminder on
        //    + due within the grace window + not already fired for this due.
        //    Best-effort pair: ShowNow then the marker write; a process death in
        //    between only means one extra re-fire next launch.
        var missed = await _repo.GetCatchUpRemindersAsync(now - CatchUpGrace, now, ct);
        foreach (var candidate in missed)
        {
            try
            {
                await _scheduler.ShowNowAsync(ToReminder(candidate), ct);
                await _repo.MarkCatchUpFiredAsync(candidate.Id, now, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Catch-up re-fire failed for task {Uuid} (best-effort).", candidate.Uuid);
            }
        }

        // 2. Stale sweep: cancel scheduled toasts whose task no longer wants one
        //    (deleted / completed / reminder off / due cleared or already
        //    crossed). Mutations cancel eagerly; this covers the app-off window
        //    (the schedule cannot drift while the app is not running) and any
        //    eager cancel that failed.
        var scheduled = await _scheduler.GetScheduledTaskUuidsAsync(ct);
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var taskUuid in scheduled)
        {
            var task = await _repo.GetTaskByUuidAsync(taskUuid, ct);
            if (task is null || !WantsToast(task, now))
            {
                await _scheduler.CancelAsync(taskUuid, ct);
                continue;
            }
            wanted.Add(taskUuid);
        }

        // 3. 14-day fill: assert reminders for in-horizon tasks that are missing
        //    from the schedule (fresh installs, schedules lost with a moved
        //    portable folder, fills that failed on an earlier launch).
        var filled = 0;
        var upcoming = await _repo.GetUpcomingRemindersAsync(now, now + ReconcileHorizon, ct);
        foreach (var task in upcoming)
        {
            if (wanted.Contains(task.Uuid))
                continue;
            await _scheduler.ScheduleAsync(ToReminder(task), ct);
            filled++;
        }

        _log.LogInformation(
            "Todo reminder reconcile: {Scheduled} were scheduled, {Cancelled} stale cancelled, {Filled} filled, {Missed} catch-up re-fired.",
            scheduled.Count, scheduled.Count - wanted.Count, filled, missed.Count);
    }

    public async Task SyncTaskAsync(TodoTask? task, CancellationToken ct = default)
    {
        if (task is null)
            return; // mutation of a missing id — the data contract treats it as a no-op

        if (WantsToast(task, UtcNow))
            await _scheduler.ScheduleAsync(ToReminder(task), ct);
        else
            await _scheduler.CancelAsync(task.Uuid, ct);
    }

    public Task CancelTaskAsync(string taskUuid, CancellationToken ct = default) =>
        _scheduler.CancelAsync(taskUuid, ct);

    public async Task CancelTasksAsync(IReadOnlyList<string> taskUuids, CancellationToken ct = default)
    {
        foreach (var taskUuid in taskUuids)
            await _scheduler.CancelAsync(taskUuid, ct);
    }

    /// <summary>Whether the task currently wants a scheduled toast: incomplete, reminder on, due strictly in the future.</summary>
    private static bool WantsToast(TodoTask task, DateTimeOffset now) =>
        task.CompletedAt is null && task.ReminderOn && task.DueAt is { } due && due > now;

    private static TodoReminder ToReminder(TodoTask task) => new()
    {
        TaskUuid = task.Uuid,
        Title = task.Title,
        DueAt = task.DueAt!.Value, // WantsToast guarantees a due instant on every caller
        IsAllDay = task.IsAllDay,
    };

    private static TodoReminder ToReminder(TodoTaskSummary summary) => new()
    {
        TaskUuid = summary.Uuid,
        Title = summary.Title,
        DueAt = summary.DueAt!.Value, // the reminder queries all filter DueAt IS NOT NULL
        IsAllDay = summary.IsAllDay,
    };
}
