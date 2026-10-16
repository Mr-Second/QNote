using Microsoft.Extensions.Logging;
using QNote.Data;
using QNote.Models;

namespace QNote.Services;

/// <summary>
/// TODO business logic. Owns creation identity (Uuid), timestamp stamping, the
/// PRD Decision 4 reminder default and the local-day boundary for the 今日 view;
/// delegates all SQL to <see cref="ITodoRepository"/>. Mutations return the
/// updated record (or <c>null</c> for a missing id — not-found is not an
/// exception, see error-handling). Reminder orchestration (task 10-04 stage ③):
/// every reminder-relevant mutation feeds the updated snapshot to
/// <see cref="ITodoReminderCoordinator"/> — best-effort, a reminder failure is
/// logged and never fails the data mutation.
/// </summary>
public sealed class TodoService : ITodoService
{
    /// <summary>Per-TASK TOTAL attachment cap — MS To Do parity ("添加到任务的总附件
    /// 大小不能超过 25 MB"; user ruling 2026-10-15, superseding the PRD's original
    /// 10 MB per-file cap). Public: the todo page pre-checks the picker result
    /// against it so over-cap files fail fast with a visible toast instead of the
    /// silent service-side exception path.</summary>
    public const long MaxTaskAttachmentBytes = 25 * 1024 * 1024;

    private readonly ITodoRepository _repo;
    private readonly ILogger<TodoService> _log;
    private readonly ITodoReminderCoordinator? _reminders;
    private readonly ITodoAttachmentStore? _attachments;
    private readonly Func<DateTimeOffset>? _clock;

    /// <param name="clock">
    /// Test hook: pins "now" for the local-day boundary math and creation
    /// timestamps. Production leaves it null (UTC wall clock).
    /// </param>
    /// <param name="reminders">
    /// Reminder lifecycle owner; optional so pre-stage-③ tests construct the
    /// service without one (headless CRUD tests stay focused). Production DI
    /// always injects the coordinator — it is registered before this service.
    /// </param>
    /// <param name="attachments">
    /// Attachment byte store (stage ⑤); optional for the same headless-test reason.
    /// AddAttachmentAsync throws without it (the UI always has DI's instance).
    /// </param>
    public TodoService(
        ITodoRepository repo,
        ILogger<TodoService> log,
        Func<DateTimeOffset>? clock = null,
        ITodoReminderCoordinator? reminders = null,
        ITodoAttachmentStore? attachments = null)
    {
        _repo = repo;
        _log = log;
        _clock = clock;
        _reminders = reminders;
        _attachments = attachments;
    }

    private DateTimeOffset UtcNow => _clock?.Invoke() ?? DateTimeOffset.UtcNow;

    public Task<IReadOnlyList<TodoList>> GetListsAsync(CancellationToken ct = default) =>
        _repo.GetListsAsync(ct);

    public async Task<TodoList> CreateListAsync(string name, string color, CancellationToken ct = default)
    {
        name = ValidateName(name);
        var now = UtcNow;
        var created = await _repo.CreateListAsync(new TodoList
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Name = name,
            Color = color,
            CreatedAt = now,
            UpdatedAt = now,
        }, ct);
        _log.LogInformation("Created todo list '{Name}' ({Id})", created.Name, created.Id);
        return created;
    }

    public async Task<TodoList?> UpdateListAsync(TodoList list, CancellationToken ct = default)
    {
        var updated = await _repo.UpdateListAsync(list with { Name = ValidateName(list.Name) }, ct);
        if (updated is not null)
            _log.LogInformation("Updated todo list {Id}", list.Id);
        return updated;
    }

    public async Task DeleteListAsync(long id, CancellationToken ct = default)
    {
        var (taskUuids, attachmentShas) = await _repo.DeleteListAsync(id, ct);
        await CancelRemindersAsync(taskUuids, ct);
        await DeleteOrphanFilesAsync(attachmentShas, ct);
        _log.LogInformation("Deleted todo list {Id} ({Count} tasks cascaded)", id, taskUuids.Count);
    }

    public async Task<TodoTask> CreateTaskAsync(string listUuid, string title, CancellationToken ct = default)
    {
        title = ValidateTitle(title);
        await EnsureListExistsAsync(listUuid, ct);
        var now = UtcNow;
        var created = await _repo.CreateTaskAsync(new TodoTask
        {
            Uuid = Guid.NewGuid().ToString("N"),
            ListUuid = listUuid,
            Title = title,
            CreatedAt = now,
            UpdatedAt = now,
        }, ct);
        _log.LogInformation("Created todo task {Id} in list {ListUuid}", created.Id, listUuid);
        return created;
    }

    public Task<TodoTask?> GetTaskAsync(long id, CancellationToken ct = default) =>
        _repo.GetTaskByIdAsync(id, ct);

    public async Task<TodoTaskDetail?> GetTaskDetailAsync(long id, CancellationToken ct = default)
    {
        var task = await _repo.GetTaskByIdAsync(id, ct);
        if (task is null)
            return null;
        var steps = await _repo.GetStepsAsync(id, ct);
        var attachments = await _repo.GetAttachmentsAsync(id, ct);
        return new TodoTaskDetail(task, steps, attachments);
    }

    public async Task<TodoTask?> UpdateTaskAsync(long id, string title, string notes, CancellationToken ct = default)
    {
        var updated = await _repo.UpdateTaskAsync(id, ValidateTitle(title), notes, ct);
        if (updated is not null)
        {
            _log.LogInformation("Updated todo task {Id}", id);
            // The toast headline is the task title — a retitle refreshes the
            // pending toast (Schedule replaces by tag).
            await SyncReminderAsync(updated, ct);
        }
        return updated;
    }

    public async Task DeleteTaskAsync(long id, CancellationToken ct = default)
    {
        // Read before delete: the reminder cancel keys on the uuid, which the
        // delete itself does not return. The delete returns the attachment shas
        // so store files that lost their last reference go too.
        var task = await _repo.GetTaskByIdAsync(id, ct);
        var shas = await _repo.DeleteTaskAsync(id, ct);
        if (task is not null)
            await CancelReminderAsync(task.Uuid, ct);
        await DeleteOrphanFilesAsync(shas, ct);
        _log.LogInformation("Deleted todo task {Id}", id);
    }

    public async Task<TodoTask?> CompleteTaskAsync(long id, CancellationToken ct = default)
    {
        var before = await _repo.GetTaskByIdAsync(id, ct);
        if (before is null)
            return null;

        var updated = await _repo.CompleteTaskAsync(id, UtcNow, ct);
        if (updated is null)
            return null;
        await SyncReminderAsync(updated, ct); // completed → cancel the pending toast

        // Recurrence (PRD Decision 10): completing a ruled task spawns the NEXT
        // incomplete occurrence — same payload, due advanced from the completed
        // occurrence's due (or the completion instant when it had none), steps
        // copied UNchecked, fresh identity. The completed row keeps its rule so
        // history/analytics can see it repeated.
        if (updated.RecurrenceRule is { } rule && TodoRecurrence.IsValid(rule))
        {
            var next = await SpawnNextOccurrenceAsync(updated, rule, ct);
            if (next is not null)
                await SyncReminderAsync(next, ct); // next due may already be future-reminder-worthy
        }
        return updated;
    }

    private async Task<TodoTask?> SpawnNextOccurrenceAsync(TodoTask completed, string rule, CancellationToken ct)
    {
        try
        {
            var now = UtcNow;
            // PRD Decision 10: next due = the completed occurrence's due advanced
            // by the rule — or, when it had none, the completion instant advanced
            // (a repeat-without-due task GAINS a due on its first completion).
            var from = completed.DueAt ?? now;
            var nextDue = TodoRecurrence.Advance(rule, from, completed.IsAllDay);
            var next = await _repo.CreateTaskAsync(new TodoTask
            {
                Uuid = Guid.NewGuid().ToString("N"),
                ListUuid = completed.ListUuid,
                Title = completed.Title,
                Notes = completed.Notes,
                DueAt = nextDue,
                IsAllDay = completed.IsAllDay,
                // Carry the reminder choice when there was a due; a first-time due
                // (no-due repeat) takes the Decision 4 default.
                ReminderOn = completed.DueAt is not null
                    ? completed.ReminderOn
                    : DueDatePolicy.DefaultReminder(completed.IsAllDay),
                IsImportant = completed.IsImportant,
                RecurrenceRule = rule,
                CreatedAt = now,
                UpdatedAt = now,
            }, ct);

            // Steps copy over UNchecked (To Do semantics: the next occurrence's
            // checklist starts fresh). Attachments do NOT travel (files belong
            // to the completed instance — To Do parity).
            foreach (var step in await _repo.GetStepsAsync(completed.Id, ct))
            {
                await _repo.CreateStepAsync(new TodoStep
                {
                    Uuid = Guid.NewGuid().ToString("N"),
                    TaskId = next.Id,
                    Title = step.Title,
                    CreatedAt = now,
                    UpdatedAt = now,
                }, ct);
            }

            _log.LogInformation("Spawned recurrence {Rule} occurrence {Uuid} for completed task {OldUuid}", rule, next.Uuid, completed.Uuid);
            return next;
        }
        catch (Exception ex)
        {
            // The completion itself already succeeded — a spawn failure must not
            // roll it back (best-effort, logged).
            _log.LogWarning(ex, "Recurrence spawn failed for task {Uuid} (rule {Rule}).", completed.Uuid, rule);
            return null;
        }
    }

    public async Task<TodoTask?> UncompleteTaskAsync(long id, CancellationToken ct = default)
    {
        var updated = await _repo.UncompleteTaskAsync(id, ct);
        if (updated is not null)
            await SyncReminderAsync(updated, ct); // due still future → re-schedule
        return updated;
    }

    public async Task<TodoTask?> SetDueAsync(
        long id, DateTimeOffset? dueUtc, bool isAllDay, bool? reminder = null, CancellationToken ct = default)
    {
        // Clearing the due date forces the all-day flag off and the reminder off (a
        // reminder without a due instant is meaningless); otherwise the DueDatePolicy
        // default applies unless the detail-pane toggle passed an explicit choice.
        var allDay = dueUtc is not null && isAllDay;
        var reminderOn = dueUtc is not null && (reminder ?? DueDatePolicy.DefaultReminder(allDay));
        var updated = await _repo.SetTaskDueAsync(id, dueUtc, allDay, reminderOn, ct);
        if (updated is not null)
            await SyncReminderAsync(updated, ct); // re-dated → replace; cleared → cancel
        return updated;
    }

    public Task<TodoTask?> SetImportantAsync(long id, bool isImportant, CancellationToken ct = default) =>
        // Not reminder-synced: importance is not part of the toast payload
        // (title + due), so a pending toast stays valid across a star toggle.
        _repo.SetTaskImportantAsync(id, isImportant, ct);

    public async Task<TodoTask?> SetReminderAsync(long id, bool reminderOn, CancellationToken ct = default)
    {
        var updated = await _repo.SetTaskReminderAsync(id, reminderOn, ct);
        if (updated is not null)
            await SyncReminderAsync(updated, ct); // the toggle itself reschedules/cancels
        return updated;
    }

    public async Task<TodoTask?> SetRecurrenceAsync(long id, string? rule, CancellationToken ct = default)
    {
        if (rule is not null && !TodoRecurrence.IsValid(rule))
            throw new ArgumentException($"Unknown recurrence rule '{rule}'.", nameof(rule));
        var updated = await _repo.SetTaskRecurrenceAsync(id, rule, ct);
        if (updated is not null)
            _log.LogInformation("Set recurrence '{Rule}' on task {Id}", rule ?? "never", id);
        return updated;
    }

    // ---------- steps (PRD Decision 9) ----------

    public async Task<TodoStep> AddStepAsync(long taskId, string title, CancellationToken ct = default)
    {
        title = ValidateTitle(title);
        var now = UtcNow;
        var created = await _repo.CreateStepAsync(new TodoStep
        {
            Uuid = Guid.NewGuid().ToString("N"),
            TaskId = taskId,
            Title = title,
            CreatedAt = now,
            UpdatedAt = now,
        }, ct);
        _log.LogInformation("Added step {StepId} to task {TaskId}", created.Id, taskId);
        return created;
    }

    public Task<TodoStep?> SetStepCompletedAsync(long stepId, bool completed, CancellationToken ct = default) =>
        _repo.SetStepCompletedAsync(stepId, completed ? UtcNow : null, ct);

    public Task DeleteStepAsync(long stepId, CancellationToken ct = default) =>
        _repo.DeleteStepAsync(stepId, ct);

    // ---------- attachments (PRD Decision 11) ----------

    public async Task<TodoAttachment> AddAttachmentAsync(
        long taskId, string fileName, Stream content, CancellationToken ct = default)
    {
        if (_attachments is null)
            throw new InvalidOperationException("No attachment store configured (DI must register ITodoAttachmentStore).");
        fileName = fileName.Trim();
        if (fileName.Length == 0)
            throw new ArgumentException("Attachment file name cannot be empty.", nameof(fileName));

        // Per-task TOTAL cap (25 MB, MS To Do parity): fail fast for seekable
        // streams before any bytes hit the store; the post-write check below
        // covers non-seekable streams (defense in depth, same constant).
        var existingBytes = (await _repo.GetAttachmentsAsync(taskId, ct)).Sum(a => a.ByteSize);
        if (content.CanSeek && existingBytes + content.Length > MaxTaskAttachmentBytes)
            throw new ArgumentException(
                $"Task attachments would exceed the 25 MB total cap (existing {existingBytes} + incoming {content.Length} bytes).",
                nameof(content));

        var (sha, size) = await _attachments.SaveAsync(content, ct);
        if (existingBytes + size > MaxTaskAttachmentBytes)
        {
            // The oversized file never got a reference row — undo the store write.
            await _attachments.TryDeleteAsync(sha, ct);
            throw new ArgumentException(
                $"Task attachments would exceed the 25 MB total cap (existing {existingBytes} + incoming {size} bytes).",
                nameof(content));
        }

        var now = UtcNow;
        var created = await _repo.CreateAttachmentAsync(new TodoAttachment
        {
            Uuid = Guid.NewGuid().ToString("N"),
            TaskId = taskId,
            FileName = fileName,
            Sha = sha,
            ByteSize = size,
            CreatedAt = now,
            UpdatedAt = now,
        }, ct);
        _log.LogInformation("Attached '{File}' ({Size} B, {Sha}) to task {TaskId}", fileName, size, sha[..12], taskId);
        return created;
    }

    public async Task<(TodoAttachment? Removed, int RemainingRefs)> DeleteAttachmentAsync(
        long attachmentId, CancellationToken ct = default)
    {
        var (removed, remaining) = await _repo.DeleteAttachmentAsync(attachmentId, ct);
        if (removed is not null && remaining == 0 && _attachments is not null)
            await _attachments.TryDeleteAsync(removed.Sha, ct);
        return (removed, remaining);
    }

    /// <summary>Deletes store files whose sha lost its last DB reference (task/list deletion path).</summary>
    private async Task DeleteOrphanFilesAsync(IReadOnlyList<string> shas, CancellationToken ct)
    {
        if (_attachments is null)
            return;
        foreach (var sha in shas.Distinct(StringComparer.Ordinal))
        {
            if (await _repo.CountAttachmentRefsAsync(sha, ct) == 0)
                await _attachments.TryDeleteAsync(sha, ct);
        }
    }

    public Task<IReadOnlyList<TodoTaskSummary>> GetTodayAsync(
        bool includeCompleted = false, CancellationToken ct = default) =>
        _repo.GetTodayAsync(LocalNextMidnightUtc(UtcNow), includeCompleted, ct);

    public Task<IReadOnlyList<TodoTaskSummary>> GetImportantAsync(
        bool includeCompleted = false, CancellationToken ct = default) =>
        _repo.GetImportantAsync(includeCompleted, ct);

    public Task<IReadOnlyList<TodoTaskSummary>> GetPlannedAsync(
        bool includeCompleted = false, CancellationToken ct = default) =>
        _repo.GetPlannedAsync(includeCompleted, ct);

    public Task<IReadOnlyList<TodoTaskSummary>> GetAllAsync(
        bool includeCompleted = false, CancellationToken ct = default) =>
        _repo.GetAllAsync(includeCompleted, ct);

    public Task<IReadOnlyList<TodoTaskSummary>> GetByListAsync(
        string listUuid, bool includeCompleted = false, CancellationToken ct = default) =>
        _repo.GetByListAsync(listUuid, includeCompleted, ct);

    public Task<IReadOnlyList<TodoTaskSummary>> SearchByTitleAsync(string title, CancellationToken ct = default) =>
        _repo.SearchByTitleAsync(title, ct);

    public Task<TodoViewCounts> GetViewCountsAsync(CancellationToken ct = default) =>
        _repo.GetViewCountsAsync(LocalNextMidnightUtc(UtcNow), ct);

    public Task<IReadOnlyDictionary<string, int>> CountIncompleteByListAsync(CancellationToken ct = default) =>
        _repo.CountIncompleteByListAsync(ct);

    private async Task EnsureListExistsAsync(string listUuid, CancellationToken ct)
    {
        var lists = await _repo.GetListsAsync(ct);
        if (!lists.Any(l => string.Equals(l.Uuid, listUuid, StringComparison.Ordinal)))
            throw new ArgumentException($"No todo list with uuid '{listUuid}' exists.", nameof(listUuid));
    }

    // ---------- reminder orchestration seam (stage ③) ----------
    //
    // The scheduler impl already swallows its own WinRT failures; these guards
    // additionally keep a coordinator bug from EVER failing the data mutation —
    // reminder trouble is best-effort by contract (PRD: never crash the app).

    private async Task SyncReminderAsync(TodoTask task, CancellationToken ct)
    {
        if (_reminders is null)
            return;
        try
        {
            await _reminders.SyncTaskAsync(task, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reminder sync failed for task {Uuid} (best-effort).", task.Uuid);
        }
    }

    private async Task CancelReminderAsync(string taskUuid, CancellationToken ct)
    {
        if (_reminders is null)
            return;
        try
        {
            await _reminders.CancelTaskAsync(taskUuid, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reminder cancel failed for task {Uuid} (best-effort).", taskUuid);
        }
    }

    private async Task CancelRemindersAsync(IReadOnlyList<string> taskUuids, CancellationToken ct)
    {
        if (_reminders is null || taskUuids.Count == 0)
            return;
        try
        {
            await _reminders.CancelTasksAsync(taskUuids, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reminder cancel failed for {Count} cascaded tasks (best-effort).", taskUuids.Count);
        }
    }

    /// <summary>
    /// The UTC instant of the NEXT local midnight after <paramref name="now"/> — the
    /// exclusive upper bound of the 今日 view. Derived from the local timezone on
    /// every call so a DST transition mid-session cannot pin a stale boundary.
    /// </summary>
    private static DateTimeOffset LocalNextMidnightUtc(DateTimeOffset now)
    {
        var tz = TimeZoneInfo.Local;
        var localNow = TimeZoneInfo.ConvertTime(now, tz);
        var nextMidnight = DateTime.SpecifyKind(localNow.Date.AddDays(1), DateTimeKind.Unspecified);
        return new DateTimeOffset(nextMidnight, tz.GetUtcOffset(nextMidnight)).ToUniversalTime();
    }

    private static string ValidateName(string name)
    {
        name = name.Trim();
        return name.Length == 0
            ? throw new ArgumentException("List name cannot be empty.", nameof(name))
            : name;
    }

    private static string ValidateTitle(string title)
    {
        title = title.Trim();
        return title.Length == 0
            ? throw new ArgumentException("Task title cannot be empty.", nameof(title))
            : title;
    }
}
