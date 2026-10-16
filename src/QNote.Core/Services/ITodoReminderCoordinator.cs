using QNote.Models;

namespace QNote.Services;

/// <summary>
/// Reminder lifecycle orchestration for the todo domain (task 10-04 stage ③):
/// the single Core service that decides WHEN a task's OS toast must be
/// scheduled, replaced or cancelled. <see cref="ITodoService"/> mutations feed
/// task snapshots in (cancel-on-mutation); the launch reconcile is driven from
/// <c>App.OnLaunched</c>. Execution goes through <see cref="IToastScheduler"/>
/// (WinRT impl lives in Presentation — Core stays WinUI-free). Headless
/// testable: drive it against the real repository and a fake scheduler.
/// </summary>
public interface ITodoReminderCoordinator
{
    /// <summary>
    /// Launch reconcile (PRD §4 + research/reminder-notification-mechanism):
    /// catch-up re-fire for OS-dropped reminders, stale scheduled-toast sweep,
    /// and the 14-day fill. See <see cref="TodoReminderCoordinator"/> for the
    /// exact semantics.
    /// </summary>
    Task ReconcileAsync(CancellationToken ct = default);

    /// <summary>
    /// Post-mutation sync for one task: schedule (replace) when it wants a
    /// toast — incomplete, reminder on, due strictly in the future — else cancel
    /// any pending toast. A <c>null</c> task (mutation of a missing id) is a no-op.
    /// </summary>
    Task SyncTaskAsync(TodoTask? task, CancellationToken ct = default);

    /// <summary>Cancel the pending toast of a deleted task (by uuid).</summary>
    Task CancelTaskAsync(string taskUuid, CancellationToken ct = default);

    /// <summary>Cancel the pending toasts of many deleted tasks (list-delete cascade).</summary>
    Task CancelTasksAsync(IReadOnlyList<string> taskUuids, CancellationToken ct = default);
}
