namespace QNote.Services;

/// <summary>
/// One todo reminder payload for the OS notification surface. Presentation renders
/// the localized toast from these facts; Core never touches toast XML.
/// </summary>
public sealed record TodoReminder
{
    /// <summary>Stable task identity; doubles as the OS toast Tag for precise cancel.</summary>
    public required string TaskUuid { get; init; }

    /// <summary>Task title captured at scheduling time (toast headline).</summary>
    public required string Title { get; init; }

    /// <summary>The due instant the reminder announces.</summary>
    public required DateTimeOffset DueAt { get; init; }

    /// <summary>True when the task is an all-day task (toast wording differs).</summary>
    public bool IsAllDay { get; init; }
}

/// <summary>
/// OS-level scheduled-toast surface for todo reminders (task 10-04). Core defines
/// the SEMANTICS only; the Presentation implementation (stage ③) schedules via
/// the raw WinRT API (<c>ScheduledToastNotification</c> +
/// <c>ToastNotificationManager.AddToSchedule</c>) with a hand-rolled COM
/// activator for click-through — one code path covering both the Store (MSIX)
/// and the portable (unpackaged) channel (PRD Decision 3; deviated 2026-10-10:
/// the Toolkit's <c>ToastNotificationManagerCompat</c> is NativeAOT-incompatible,
/// so <c>ToastContentBuilder</c> is used for content XML only). Core is net10.0
/// and cannot host the WinRT calls (same shape as
/// <see cref="IStartupTaskService"/>).
///
/// Contract (research/reminder-notification-mechanism.md):
/// <list type="bullet">
/// <item>Scheduled toasts are held by the OS and fire even when the app is NOT
/// running — the core selling point; no background task infrastructure needed.</item>
/// <item>Every toast carries <c>Tag = TaskUuid</c> and <c>Group = "todo"</c>, so a
/// task that is re-dated, completed or deleted gets its pending toast cancelled
/// precisely (by tag) and never fires stale.</item>
/// <item><see cref="ScheduleAsync"/> on an already-scheduled uuid REPLACES the
/// pending toast — callers never need a read-modify-write cycle.</item>
/// <item>Toast content must NOT be built with
/// <c>AddToastActivationInfo(null, ...)</c> — it swallows the activation arguments
/// the click-through handler needs to locate the task by uuid.</item>
/// <item>Launch reconcile (driven by the todo domain in stage ③): enumerate
/// <see cref="GetScheduledTaskUuidsAsync"/>, cancel toasts whose task no longer
/// needs one (completed / deleted / reminder off / re-dated), and (re)schedule
/// reminders falling within the NEXT 14 DAYS — the horizon avoids unbounded
/// schedules for far-future tasks that get re-dated anyway.</item>
/// <item>The OS DROPS a scheduled toast when the machine is off for more than ~5
/// minutes past its delivery time. The launch reconcile re-fires those via
/// <see cref="ShowNowAsync"/> — only for tasks still incomplete and overdue,
/// within a 24h grace window (candidates via
/// <c>ITodoRepository.GetCatchUpRemindersAsync</c>).</item>
/// <item>All members are best-effort: the implementation logs and swallows
/// failures (reminder trouble must never crash the app); elevated processes
/// cannot show notifications at all (WinAppSDK limitation, both channels).</item>
/// </list>
/// </summary>
public interface IToastScheduler
{
    /// <summary>Schedule (or replace) the reminder toast for one task at its due time.</summary>
    Task ScheduleAsync(TodoReminder reminder, CancellationToken ct = default);

    /// <summary>Cancel the pending scheduled toast for one task (completed / deleted / re-dated).</summary>
    Task CancelAsync(string taskUuid, CancellationToken ct = default);

    /// <summary>The task uuids with a toast currently in the OS schedule (launch reconcile input).</summary>
    Task<IReadOnlyList<string>> GetScheduledTaskUuidsAsync(CancellationToken ct = default);

    /// <summary>Show a reminder toast immediately (launch catch-up for OS-dropped reminders).</summary>
    Task ShowNowAsync(TodoReminder reminder, CancellationToken ct = default);
}
