using QNote.Models;

namespace QNote.Data;

/// <summary>
/// Persistence for the TODO domain (todo_lists + todo_tasks, schema v7) over the
/// single <c>qnote.db</c>. Timestamps are millisecond-epoch integers in storage and
/// UTC <see cref="DateTimeOffset"/> values in memory. Tasks link to lists by
/// <c>ListUuid</c> (string link — no FK, mirroring the notes↔categories name link);
/// deleting a list cascades its tasks inside one transaction.
/// Services depend on this interface, never on raw connections.
/// </summary>
public interface ITodoRepository
{
    /// <summary>Preserves a draft as a copy when remote text/deletion superseded its baseline.</summary>
    Task<TodoTask> SaveTaskDraftAsync(TodoTaskDetail baseline, string title, string notes,
        string conflictSuffix, CancellationToken ct = default);
    // ---------- lists ----------

    /// <summary>All lists ordered by <see cref="TodoList.SortOrder"/>.</summary>
    Task<IReadOnlyList<TodoList>> GetListsAsync(CancellationToken ct = default);

    /// <summary>
    /// Inserts a list with <c>SortOrder = MAX(SortOrder)+1</c> and returns it with
    /// the DB-assigned <see cref="TodoList.Id"/> (identity/timestamps come from the
    /// caller — the service owns them).
    /// </summary>
    Task<TodoList> CreateListAsync(TodoList list, CancellationToken ct = default);

    /// <summary>
    /// Updates Name/Color of a list and stamps UpdatedAt. Returns the updated row,
    /// or <c>null</c> when the id does not exist (not-found is not an exception).
    /// </summary>
    Task<TodoList?> UpdateListAsync(TodoList list, CancellationToken ct = default);

    /// <summary>
    /// Deletes a list AND all of its tasks (destructive — the caller confirms) in
    /// one transaction, cascading their steps and attachments. Returns the uuids
    /// of the cascaded tasks (reminder cancel) and the shas of every cascaded
    /// attachment (the caller deletes store files that lost their last
    /// reference). No-op (empty result) when the id does not exist.
    /// </summary>
    Task<(IReadOnlyList<string> TaskUuids, IReadOnlyList<string> AttachmentShas)> DeleteListAsync(long id, CancellationToken ct = default);

    // ---------- tasks ----------

    /// <summary>The full task (incl. Notes) by id, or <c>null</c> when missing.</summary>
    Task<TodoTask?> GetTaskByIdAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// The full task by uuid — the toast-activation deep-link path (OS toast Tag
    /// carries the task uuid) and the future backup-merge identity lookup.
    /// </summary>
    Task<TodoTask?> GetTaskByUuidAsync(string uuid, CancellationToken ct = default);

    /// <summary>
    /// Inserts a task (all fields from the caller; the service owns identity and
    /// timestamps) and returns it with the DB-assigned <see cref="TodoTask.Id"/>.
    /// </summary>
    Task<TodoTask> CreateTaskAsync(TodoTask task, CancellationToken ct = default);

    /// <summary>
    /// Updates Title/Notes of a task and stamps UpdatedAt. Completion state and
    /// due/reminder/important flags deliberately do NOT pass through here — they
    /// have their own focused operations below. Returns the updated row, or
    /// <c>null</c> when the id does not exist.
    /// </summary>
    Task<TodoTask?> UpdateTaskAsync(long id, string title, string notes, CancellationToken ct = default);

    /// <summary>
    /// Deletes a task by id, cascading its steps and attachment rows in one
    /// transaction. Returns the shas of the task's attachments (the caller
    /// deletes store files that lost their last reference). No-op (empty) when
    /// it does not exist.
    /// </summary>
    Task<IReadOnlyList<string>> DeleteTaskAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Marks a task complete: <c>CompletedAt = completedAt</c> (also stamped as
    /// UpdatedAt — the mutation time is the completion time). Returns the updated
    /// row, or <c>null</c> when the id does not exist.
    /// </summary>
    Task<TodoTask?> CompleteTaskAsync(long id, DateTimeOffset completedAt, CancellationToken ct = default);

    /// <summary>
    /// Marks a task incomplete again (<c>CompletedAt = NULL</c>, UpdatedAt stamped
    /// at write time). Returns the updated row, or <c>null</c> when the id does not
    /// exist.
    /// </summary>
    Task<TodoTask?> UncompleteTaskAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Sets the due date (or clears it with <c>null</c>), all-day flag and reminder
    /// in one statement; UpdatedAt stamped at write time. Reminder policy is decided
    /// by the caller (service-level <c>DueDatePolicy</c>). Returns the updated row,
    /// or <c>null</c> when the id does not exist.
    /// </summary>
    Task<TodoTask?> SetTaskDueAsync(
        long id, DateTimeOffset? dueUtc, bool isAllDay, bool reminderOn, CancellationToken ct = default);

    /// <summary>Sets the importance star; UpdatedAt stamped at write time.</summary>
    Task<TodoTask?> SetTaskImportantAsync(long id, bool isImportant, CancellationToken ct = default);

    /// <summary>Sets the reminder toggle; UpdatedAt stamped at write time.</summary>
    Task<TodoTask?> SetTaskReminderAsync(long id, bool reminderOn, CancellationToken ct = default);

    /// <summary>
    /// Sets the recurrence rule (PRD Decision 10; <c>null</c> = never repeats —
    /// pass <c>null</c> for 从不). UpdatedAt stamped at write time. Rule codes are
    /// validated by the service (<see cref="Services.TodoRecurrence"/>), not here.
    /// </summary>
    Task<TodoTask?> SetTaskRecurrenceAsync(long id, string? rule, CancellationToken ct = default);

    // ---------- steps (PRD Decision 9) ----------

    /// <summary>The task's steps ordered by <see cref="TodoStep.SortOrder"/> (insertion order).</summary>
    Task<IReadOnlyList<TodoStep>> GetStepsAsync(long taskId, CancellationToken ct = default);

    /// <summary>
    /// Appends a step (<c>SortOrder = MAX(SortOrder)+1</c>); identity/timestamps
    /// from the caller (the service owns them). Returns the row with the live Id.
    /// </summary>
    Task<TodoStep> CreateStepAsync(TodoStep step, CancellationToken ct = default);

    /// <summary>
    /// Checks a step (<paramref name="completedAt"/> = the completion instant, also
    /// stamped as UpdatedAt) or unchecks it (<c>null</c>). Returns the updated row,
    /// or <c>null</c> when the id does not exist.
    /// </summary>
    Task<TodoStep?> SetStepCompletedAsync(long id, DateTimeOffset? completedAt, CancellationToken ct = default);

    /// <summary>Deletes a step by id (no-op when it does not exist).</summary>
    Task DeleteStepAsync(long id, CancellationToken ct = default);

    // ---------- attachments (PRD Decision 11) ----------

    /// <summary>The task's attachments ordered newest-first (detail-pane list).</summary>
    Task<IReadOnlyList<TodoAttachment>> GetAttachmentsAsync(long taskId, CancellationToken ct = default);

    /// <summary>
    /// Inserts an attachment reference (the store file was already written by the
    /// caller). Identity/timestamps from the caller. Returns the row with the live Id.
    /// </summary>
    Task<TodoAttachment> CreateAttachmentAsync(TodoAttachment attachment, CancellationToken ct = default);

    /// <summary>
    /// Deletes an attachment row, returning the removed record and how many OTHER
    /// rows still reference its sha (0 → the caller may delete the store file).
    /// <c>null</c> removal = id not found.
    /// </summary>
    Task<(TodoAttachment? Removed, int RemainingRefs)> DeleteAttachmentAsync(long id, CancellationToken ct = default);

    /// <summary>How many attachment rows reference <paramref name="sha"/> (file-GC check after task deletion).</summary>
    Task<int> CountAttachmentRefsAsync(string sha, CancellationToken ct = default);

    /// <summary>Every step row across all tasks (backup writer).</summary>
    Task<IReadOnlyList<TodoStep>> GetAllStepsAsync(CancellationToken ct = default);

    /// <summary>Every attachment row across all tasks (backup writer).</summary>
    Task<IReadOnlyList<TodoAttachment>> GetAllAttachmentsAsync(CancellationToken ct = default);

    // ---------- views ----------

    /// <summary>
    /// 今日: incomplete tasks due before <paramref name="todayEndUtc"/> — the UTC
    /// instant of the NEXT LOCAL midnight after "now" (the service owns the
    /// timezone policy; the boundary covers overdue, timed-today and all-day-today
    /// tasks alike). With <paramref name="includeCompleted"/> the completed tail
    /// follows (locked mockup a4 shape). Ordered most-overdue first.
    /// </summary>
    Task<IReadOnlyList<TodoTaskSummary>> GetTodayAsync(
        DateTimeOffset todayEndUtc, bool includeCompleted = false, CancellationToken ct = default);

    /// <summary>重要: starred tasks, incomplete by default, due-first (nulls last).</summary>
    Task<IReadOnlyList<TodoTaskSummary>> GetImportantAsync(
        bool includeCompleted = false, CancellationToken ct = default);

    /// <summary>计划内: tasks with a due date, incomplete by default, chronological.</summary>
    Task<IReadOnlyList<TodoTaskSummary>> GetPlannedAsync(
        bool includeCompleted = false, CancellationToken ct = default);

    /// <summary>全部: every task across all lists, incomplete by default, newest-created first.</summary>
    Task<IReadOnlyList<TodoTaskSummary>> GetAllAsync(
        bool includeCompleted = false, CancellationToken ct = default);

    /// <summary>
    /// One list's tasks, incomplete by default (quick-add parity: newest first);
    /// <paramref name="includeCompleted"/> appends the completed tail (mockup a4).
    /// </summary>
    Task<IReadOnlyList<TodoTaskSummary>> GetByListAsync(
        string listUuid, bool includeCompleted = false, CancellationToken ct = default);

    /// <summary>
    /// Title LIKE search over incomplete tasks. The query text is parameterized and
    /// its LIKE wildcards (<c>%</c>, <c>_</c>, <c>\</c>) escaped so user input
    /// matches literally (MVP scope — FTS integration is post-MVP, PRD Decision 8).
    /// </summary>
    Task<IReadOnlyList<TodoTaskSummary>> SearchByTitleAsync(string title, CancellationToken ct = default);

    /// <summary>
    /// Launch catch-up candidates (PRD §4): incomplete tasks with reminders whose
    /// due instant falls in <c>[minDueUtc, nowUtc)</c> — reminders the OS may have
    /// dropped while the machine was off (5-minute window) — whose due crossing
    /// has not been catch-up fired yet (<c>CatchUpFiredAt</c> is NULL or below the
    /// task's due: once per due crossing, so a re-dated task can fire again). The
    /// caller derives the 24h grace lower bound; ordered most-overdue first.
    /// </summary>
    Task<IReadOnlyList<TodoTaskSummary>> GetCatchUpRemindersAsync(
        DateTimeOffset minDueUtc, DateTimeOffset nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Incomplete tasks with a live reminder due strictly within
    /// <c>(fromUtc, toUtc]</c> — the launch reconcile's 14-day fill window (the
    /// horizon bounds what reconcile ADDS; toasts the OS already holds for
    /// far-future tasks are left alone). Ordered soonest-first.
    /// </summary>
    Task<IReadOnlyList<TodoTaskSummary>> GetUpcomingRemindersAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default);

    /// <summary>
    /// Records that the OS-dropped reminder for this task was catch-up fired at
    /// <paramref name="firedAtUtc"/>. Deliberately does NOT stamp UpdatedAt: the
    /// marker is reminder bookkeeping, not a data mutation (sync LWW must not
    /// see it, and GetCatchUpRemindersAsync already excludes fired tasks).
    /// </summary>
    Task MarkCatchUpFiredAsync(long id, DateTimeOffset firedAtUtc, CancellationToken ct = default);

    /// <summary>
    /// Uuid → (Id, UpdatedAt) for every list. Used by the backup restore
    /// pipeline for conflict analysis and merge decisions (lists carry no other
    /// identity across databases — same shape as the notes primitive).
    /// </summary>
    Task<IReadOnlyDictionary<string, (long Id, DateTimeOffset UpdatedAt)>> GetListUuidTimestampsAsync(CancellationToken ct = default);

    /// <summary>
    /// Uuid → (Id, UpdatedAt) for every task. Used by the backup restore
    /// pipeline for conflict analysis and merge decisions.
    /// </summary>
    Task<IReadOnlyDictionary<string, (long Id, DateTimeOffset UpdatedAt)>> GetTaskUuidTimestampsAsync(CancellationToken ct = default);

    /// <summary>
    /// Backup-restore import: upserts a full list row keyed by uuid, preserving
    /// every archived field (Name/Color/SortOrder/CreatedAt/UpdatedAt — the
    /// LWW/ImportNew decision was already made by the caller; the live row keeps
    /// its Id on the conflict branch). Returns the row with the live Id.
    /// </summary>
    Task<TodoList> ImportListAsync(TodoList list, CancellationToken ct = default);

    /// <summary>
    /// Backup-restore import: upserts a full task row keyed by uuid, preserving
    /// every archived field — including CompletedAt and the CatchUpFiredAt
    /// reminder marker, which travels with the row. Returns the row with the
    /// live Id.
    /// </summary>
    Task<TodoTask> ImportTaskAsync(TodoTask task, CancellationToken ct = default);

    /// <summary>Uuid → (Id, UpdatedAt) for every step (backup merge conflict analysis).</summary>
    Task<IReadOnlyDictionary<string, (long Id, DateTimeOffset UpdatedAt)>> GetStepUuidTimestampsAsync(CancellationToken ct = default);

    /// <summary>Backup-restore import: upserts a full step row keyed by uuid (the LWW decision was made by the caller).</summary>
    Task<TodoStep> ImportStepAsync(TodoStep step, CancellationToken ct = default);

    /// <summary>Uuid → (Id, UpdatedAt) for every attachment (backup merge conflict analysis).</summary>
    Task<IReadOnlyDictionary<string, (long Id, DateTimeOffset UpdatedAt)>> GetAttachmentUuidTimestampsAsync(CancellationToken ct = default);

    /// <summary>
    /// One attachment row by uuid (null when absent). The restore merge uses it
    /// to compare the losing row's sha before an LWW overwrite, so a replaced
    /// file can be pruned when its last reference went with it.
    /// </summary>
    Task<TodoAttachment?> GetAttachmentByUuidAsync(string uuid, CancellationToken ct = default);

    /// <summary>Backup-restore import: upserts a full attachment row keyed by uuid (the LWW decision was made by the caller).</summary>
    Task<TodoAttachment> ImportAttachmentAsync(TodoAttachment attachment, CancellationToken ct = default);

    // ---------- counts ----------

    /// <summary>Incomplete counts for the four smart views in one aggregate query.</summary>
    Task<TodoViewCounts> GetViewCountsAsync(DateTimeOffset todayEndUtc, CancellationToken ct = default);

    /// <summary>Incomplete task count per list uuid (sidebar badges; lists with zero open tasks are absent).</summary>
    Task<IReadOnlyDictionary<string, int>> CountIncompleteByListAsync(CancellationToken ct = default);
}
