using QNote.Models;

namespace QNote.Services;

/// <summary>
/// TODO business logic (task 10-04): lists + tasks CRUD, completion semantics, the
/// PRD Decision 4 reminder default, the smart views (今日 / 重要 / 计划内 / 全部),
/// title search and sidebar counts. Validation lives here at the service boundary;
/// SQL lives in <see cref="Data.ITodoRepository"/>.
/// </summary>
public interface ITodoService
{
    // ---------- lists ----------

    /// <summary>All lists ordered by <see cref="TodoList.SortOrder"/> (工作/生活 seeded first).</summary>
    Task<IReadOnlyList<TodoList>> GetListsAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates a list (appended at the end of the order) with generated identity.
    /// Rejects a blank name. Duplicate names are allowed (tasks link by uuid, not
    /// by name — Microsoft To Do parity).
    /// </summary>
    Task<TodoList> CreateListAsync(string name, string color, CancellationToken ct = default);

    /// <summary>
    /// Renames / recolors a list (Uuid and SortOrder are never rewritten). Returns
    /// the updated list, or <c>null</c> when the id does not exist.
    /// </summary>
    Task<TodoList?> UpdateListAsync(TodoList list, CancellationToken ct = default);

    /// <summary>
    /// Deletes a list AND all of its tasks (destructive — the caller confirms;
    /// no built-in protection for the seeded lists, To Do parity).
    /// </summary>
    Task DeleteListAsync(long id, CancellationToken ct = default);

    // ---------- tasks ----------

    /// <summary>
    /// Quick-add: creates a task titled <paramref name="title"/> in the list
    /// <paramref name="listUuid"/> (generated identity, no due date, not
    /// important). Rejects blank titles and unknown list uuids.
    /// </summary>
    Task<TodoTask> CreateTaskAsync(string listUuid, string title, CancellationToken ct = default);

    /// <summary>The full task (incl. Notes) by id, or <c>null</c> when missing.</summary>
    Task<TodoTask?> GetTaskAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// The detail-pane aggregate: the task plus its steps (ordered) and
    /// attachments (newest first) in one call, or <c>null</c> when missing.
    /// </summary>
    Task<TodoTaskDetail?> GetTaskDetailAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Persists detail-pane title/notes edits (blank titles rejected). Completion,
    /// due, reminder and importance have their own focused operations. Returns the
    /// updated task, or <c>null</c> when the id does not exist.
    /// </summary>
    Task<TodoTask?> UpdateTaskAsync(long id, string title, string notes, CancellationToken ct = default);

    /// <summary>Deletes a task by id (no-op when it does not exist).</summary>
    Task DeleteTaskAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Marks a task complete at the current time. Returns the updated task, or
    /// <c>null</c> when the id does not exist.
    /// </summary>
    Task<TodoTask?> CompleteTaskAsync(long id, CancellationToken ct = default);

    /// <summary>Marks a task incomplete again; same return contract.</summary>
    Task<TodoTask?> UncompleteTaskAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Sets (or clears) the due date. The reminder default follows
    /// <see cref="DueDatePolicy"/> (PRD Decision 4: timed → on, all-day → off,
    /// clearing → off) unless <paramref name="reminder"/> carries the detail-pane
    /// toggle's explicit choice. Returns the updated task, or <c>null</c> when the
    /// id does not exist.
    /// </summary>
    Task<TodoTask?> SetDueAsync(
        long id, DateTimeOffset? dueUtc, bool isAllDay, bool? reminder = null, CancellationToken ct = default);

    /// <summary>Sets the importance star; same return contract.</summary>
    Task<TodoTask?> SetImportantAsync(long id, bool isImportant, CancellationToken ct = default);

    /// <summary>Sets the reminder toggle; same return contract.</summary>
    Task<TodoTask?> SetReminderAsync(long id, bool reminderOn, CancellationToken ct = default);

    /// <summary>
    /// Sets the recurrence rule (PRD Decision 10; <c>null</c> = 从不). Rejects
    /// unknown rule codes (<see cref="TodoRecurrence"/>). Returns the updated
    /// task, or <c>null</c> when the id does not exist.
    /// </summary>
    Task<TodoTask?> SetRecurrenceAsync(long id, string? rule, CancellationToken ct = default);

    // ---------- steps (PRD Decision 9) ----------

    /// <summary>Appends a step to the task (blank titles rejected).</summary>
    Task<TodoStep> AddStepAsync(long taskId, string title, CancellationToken ct = default);

    /// <summary>
    /// Checks/unchecks a step (completing every step does NOT complete the
    /// parent task — To Do semantics). Returns the updated step, or <c>null</c>
    /// when the id does not exist.
    /// </summary>
    Task<TodoStep?> SetStepCompletedAsync(long stepId, bool completed, CancellationToken ct = default);

    /// <summary>Deletes a step by id (no-op when it does not exist).</summary>
    Task DeleteStepAsync(long stepId, CancellationToken ct = default);

    // ---------- attachments (PRD Decision 11) ----------

    /// <summary>
    /// Stores <paramref name="content"/> (raw bytes, ≤10 MB, sha-256-addressed)
    /// and attaches it to the task under <paramref name="fileName"/>. The store
    /// file is written BEFORE the row; a DB failure leaves an orphan file that
    /// the next store sweep reclaims (orphan files are harmless).
    /// </summary>
    Task<TodoAttachment> AddAttachmentAsync(long taskId, string fileName, Stream content, CancellationToken ct = default);

    /// <summary>
    /// Deletes an attachment row. When the removed row was the last reference
    /// to its content (<c>RemainingRefs == 0</c>), the store file is deleted too.
    /// </summary>
    Task<(TodoAttachment? Removed, int RemainingRefs)> DeleteAttachmentAsync(long attachmentId, CancellationToken ct = default);

    // ---------- views ----------

    /// <summary>
    /// 今日: incomplete tasks due before the end of the local day (incl. overdue).
    /// The day boundary derives from the local timezone; the optional test clock
    /// pins "now".
    /// </summary>
    Task<IReadOnlyList<TodoTaskSummary>> GetTodayAsync(
        bool includeCompleted = false, CancellationToken ct = default);

    /// <summary>重要: starred tasks, incomplete by default, due-first (nulls last).</summary>
    Task<IReadOnlyList<TodoTaskSummary>> GetImportantAsync(
        bool includeCompleted = false, CancellationToken ct = default);

    /// <summary>计划内: tasks with a due date, incomplete by default, chronological.</summary>
    Task<IReadOnlyList<TodoTaskSummary>> GetPlannedAsync(
        bool includeCompleted = false, CancellationToken ct = default);

    /// <summary>全部: every task across all lists, incomplete by default, newest-created first.</summary>
    Task<IReadOnlyList<TodoTaskSummary>> GetAllAsync(
        bool includeCompleted = false, CancellationToken ct = default);

    /// <summary>One list's tasks, incomplete by default (quick-add parity: newest first).</summary>
    Task<IReadOnlyList<TodoTaskSummary>> GetByListAsync(
        string listUuid, bool includeCompleted = false, CancellationToken ct = default);

    /// <summary>Title LIKE search over incomplete tasks (wildcards matched literally).</summary>
    Task<IReadOnlyList<TodoTaskSummary>> SearchByTitleAsync(string title, CancellationToken ct = default);

    // ---------- counts ----------

    /// <summary>Incomplete counts for the four smart views (计数 = 未完成数, PRD §5).</summary>
    Task<TodoViewCounts> GetViewCountsAsync(CancellationToken ct = default);

    /// <summary>Incomplete task count per list uuid (sidebar badges).</summary>
    Task<IReadOnlyDictionary<string, int>> CountIncompleteByListAsync(CancellationToken ct = default);
}
