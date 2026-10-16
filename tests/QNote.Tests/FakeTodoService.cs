using QNote.Models;
using QNote.Services;

namespace QNote.Tests;

/// <summary>
/// In-memory <see cref="ITodoService"/> for headless view-model tests: seeds the
/// 工作/生活 lists with fixed uuids, mirrors the repository's view semantics
/// (今日 boundary, orderings, DueDatePolicy default) and records mutating calls.
/// Purely deterministic — the clock is injected.
/// </summary>
internal sealed class FakeTodoService : ITodoService
{
    public const string WorkUuid = "list-work";
    public const string LifeUuid = "list-life";

    private readonly List<TodoList> _lists = [];
    private readonly List<TodoTask> _tasks = [];
    private readonly List<TodoStep> _steps = [];
    private readonly List<TodoAttachment> _attachments = [];
    private long _nextListId = 1;
    private long _nextTaskId = 1;
    private long _nextStepId = 1;
    private long _nextAttachmentId = 1;

    public FakeTodoService(Func<DateTimeOffset> clock)
    {
        Clock = clock;
        var now = clock();
        _lists.Add(new TodoList
        {
            Id = _nextListId++, Uuid = WorkUuid, Name = "工作", Color = "#3B82F6",
            SortOrder = 0, CreatedAt = now, UpdatedAt = now,
        });
        _lists.Add(new TodoList
        {
            Id = _nextListId++, Uuid = LifeUuid, Name = "生活", Color = "#22C55E",
            SortOrder = 1, CreatedAt = now, UpdatedAt = now,
        });
    }

    public Func<DateTimeOffset> Clock { get; }

    /// <summary>Recorded method names + key args, for call-order assertions.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Insert a task directly with full control over its state (test fixture).</summary>
    public QNote.Models.TodoTask SeedTask(
        string listUuid,
        string title,
        DateTimeOffset? due = null,
        bool allDay = false,
        bool important = false,
        bool completed = false,
        string notes = "",
        string? recurrenceRule = null)
    {
        var now = Now;
        var task = new QNote.Models.TodoTask
        {
            Id = _nextTaskId++,
            Uuid = $"task-{_nextTaskId}",
            ListUuid = listUuid,
            Title = title,
            Notes = notes,
            DueAt = due,
            IsAllDay = allDay && due is not null,
            ReminderOn = due is not null && !allDay, // mirrors DueDatePolicy.DefaultReminder
            IsImportant = important,
            CompletedAt = completed ? now : null,
            RecurrenceRule = recurrenceRule,
            CreatedAt = now.AddMinutes(-_nextTaskId), // staggered for newest-first orderings
            UpdatedAt = now,
        };
        _tasks.Add(task);
        return task;
    }

    /// <summary>Insert a step directly (test fixture).</summary>
    public TodoStep SeedStep(long taskId, string title, bool completed = false)
    {
        var now = Now;
        var step = new TodoStep
        {
            Id = _nextStepId++,
            Uuid = $"step-{_nextStepId}",
            TaskId = taskId,
            Title = title,
            CompletedAt = completed ? now : null,
            SortOrder = _steps.Count(s => s.TaskId == taskId),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _steps.Add(step);
        return step;
    }

    private DateTimeOffset Now => Clock();

    // ---------- lists ----------

    public Task<IReadOnlyList<TodoList>> GetListsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<TodoList> result = _lists.OrderBy(l => l.SortOrder).ToList();
        return Task.FromResult(result);
    }

    public Task<TodoList> CreateListAsync(string name, string color, CancellationToken ct = default)
    {
        Calls.Add($"CreateList:{name}");
        name = Validate(name, "name");
        var now = Now;
        var created = new TodoList
        {
            Id = _nextListId++,
            Uuid = $"list-{_nextListId}",
            Name = name,
            Color = color,
            SortOrder = _lists.Count == 0 ? 0 : _lists.Max(l => l.SortOrder) + 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _lists.Add(created);
        return Task.FromResult(created);
    }

    public Task<TodoList?> UpdateListAsync(TodoList list, CancellationToken ct = default)
    {
        Calls.Add($"UpdateList:{list.Id}");
        var index = _lists.FindIndex(l => l.Id == list.Id);
        if (index < 0)
            return Task.FromResult<TodoList?>(null);
        var updated = list with { Name = Validate(list.Name, "name"), UpdatedAt = Now };
        _lists[index] = updated;
        return Task.FromResult<TodoList?>(updated);
    }

    public Task DeleteListAsync(long id, CancellationToken ct = default)
    {
        Calls.Add($"DeleteList:{id}");
        var list = _lists.FirstOrDefault(l => l.Id == id);
        if (list is null)
            return Task.CompletedTask;
        _lists.RemoveAll(l => l.Id == id);
        var taskIds = _tasks.Where(t => t.ListUuid == list.Uuid).Select(t => t.Id).ToList();
        _tasks.RemoveAll(t => t.ListUuid == list.Uuid);
        _steps.RemoveAll(s => taskIds.Contains(s.TaskId));
        _attachments.RemoveAll(a => taskIds.Contains(a.TaskId));
        return Task.CompletedTask;
    }

    // ---------- tasks ----------

    public Task<TodoTask> CreateTaskAsync(string listUuid, string title, CancellationToken ct = default)
    {
        Calls.Add($"CreateTask:{listUuid}:{title}");
        if (!_lists.Any(l => l.Uuid == listUuid))
            throw new ArgumentException($"No todo list with uuid '{listUuid}' exists.", nameof(listUuid));
        var now = Now;
        var created = new TodoTask
        {
            Id = _nextTaskId++,
            Uuid = $"task-{_nextTaskId}",
            ListUuid = listUuid,
            Title = Validate(title, "title"),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _tasks.Add(created);
        return Task.FromResult(created);
    }

    public Task<TodoTask?> GetTaskAsync(long id, CancellationToken ct = default) =>
        Task.FromResult(_tasks.FirstOrDefault(t => t.Id == id));

    public Task<TodoTaskDetail?> GetTaskDetailAsync(long id, CancellationToken ct = default)
    {
        var task = _tasks.FirstOrDefault(t => t.Id == id);
        if (task is null)
            return Task.FromResult<TodoTaskDetail?>(null);
        IReadOnlyList<TodoStep> steps = _steps.Where(s => s.TaskId == id).OrderBy(s => s.SortOrder).ToList();
        IReadOnlyList<TodoAttachment> attachments = _attachments.Where(a => a.TaskId == id).OrderByDescending(a => a.CreatedAt).ToList();
        return Task.FromResult<TodoTaskDetail?>(new TodoTaskDetail(task, steps, attachments));
    }

    public Task<TodoTask?> UpdateTaskAsync(long id, string title, string notes, CancellationToken ct = default)
    {
        Calls.Add($"UpdateTask:{id}:{title}");
        var index = _tasks.FindIndex(t => t.Id == id);
        if (index < 0)
            return Task.FromResult<TodoTask?>(null);
        var updated = _tasks[index] with
        {
            Title = Validate(title, "title"),
            Notes = notes,
            UpdatedAt = Now,
        };
        _tasks[index] = updated;
        return Task.FromResult<TodoTask?>(updated);
    }

    public Task DeleteTaskAsync(long id, CancellationToken ct = default)
    {
        Calls.Add($"DeleteTask:{id}");
        _tasks.RemoveAll(t => t.Id == id);
        _steps.RemoveAll(s => s.TaskId == id);
        _attachments.RemoveAll(a => a.TaskId == id);
        return Task.CompletedTask;
    }

    public Task<TodoTask?> CompleteTaskAsync(long id, CancellationToken ct = default)
    {
        Calls.Add($"Complete:{id}");
        var updated = MutateSync(id, t => t with { CompletedAt = Now, UpdatedAt = Now });
        // Recurrence mirror (TodoService semantics): completing a ruled task
        // spawns the next occurrence with the due advanced and steps copied UNchecked.
        if (updated is { RecurrenceRule: { } rule } && TodoRecurrence.IsValid(rule))
        {
            var now = Now;
            var from = updated.DueAt ?? now;
            var nextDue = TodoRecurrence.Advance(rule, from, updated.IsAllDay);
            var next = new TodoTask
            {
                Id = _nextTaskId++,
                Uuid = $"task-{_nextTaskId}",
                ListUuid = updated.ListUuid,
                Title = updated.Title,
                Notes = updated.Notes,
                DueAt = nextDue,
                IsAllDay = updated.IsAllDay,
                ReminderOn = updated.DueAt is not null
                    ? updated.ReminderOn
                    : DueDatePolicy.DefaultReminder(updated.IsAllDay),
                IsImportant = updated.IsImportant,
                RecurrenceRule = rule,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _tasks.Add(next);
            foreach (var step in _steps.Where(s => s.TaskId == id).ToList())
            {
                _steps.Add(step with
                {
                    Id = _nextStepId++,
                    Uuid = $"step-{_nextStepId}",
                    TaskId = next.Id,
                    CompletedAt = null,
                    SortOrder = step.SortOrder,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }
        return Task.FromResult(updated);
    }

    public Task<TodoTask?> UncompleteTaskAsync(long id, CancellationToken ct = default)
    {
        Calls.Add($"Uncomplete:{id}");
        return Mutate(id, t => t with { CompletedAt = null, UpdatedAt = Now });
    }

    public Task<TodoTask?> SetDueAsync(
        long id, DateTimeOffset? dueUtc, bool isAllDay, bool? reminder = null, CancellationToken ct = default)
    {
        Calls.Add($"SetDue:{id}:{dueUtc?.ToString("o") ?? "null"}:{isAllDay}");
        var allDay = dueUtc is not null && isAllDay;
        var reminderOn = dueUtc is not null && (reminder ?? DueDatePolicy.DefaultReminder(allDay));
        return Mutate(id, t => t with
        {
            DueAt = dueUtc,
            IsAllDay = allDay,
            ReminderOn = reminderOn,
            UpdatedAt = Now,
        });
    }

    public Task<TodoTask?> SetImportantAsync(long id, bool isImportant, CancellationToken ct = default)
    {
        Calls.Add($"SetImportant:{id}:{isImportant}");
        return Mutate(id, t => t with { IsImportant = isImportant, UpdatedAt = Now });
    }

    public Task<TodoTask?> SetReminderAsync(long id, bool reminderOn, CancellationToken ct = default)
    {
        Calls.Add($"SetReminder:{id}:{reminderOn}");
        return Mutate(id, t => t with { ReminderOn = reminderOn, UpdatedAt = Now });
    }

    public Task<TodoTask?> SetRecurrenceAsync(long id, string? rule, CancellationToken ct = default)
    {
        if (rule is not null && !TodoRecurrence.IsValid(rule))
            throw new ArgumentException($"Unknown recurrence rule '{rule}'.", nameof(rule));
        Calls.Add($"SetRecurrence:{id}:{rule ?? "null"}");
        return Mutate(id, t => t with { RecurrenceRule = rule, UpdatedAt = Now });
    }

    // ---------- steps ----------

    public Task<TodoStep> AddStepAsync(long taskId, string title, CancellationToken ct = default)
    {
        Calls.Add($"AddStep:{taskId}:{title}");
        var now = Now;
        var created = new TodoStep
        {
            Id = _nextStepId++,
            Uuid = $"step-{_nextStepId}",
            TaskId = taskId,
            Title = Validate(title, "title"),
            SortOrder = _steps.Count(s => s.TaskId == taskId),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _steps.Add(created);
        return Task.FromResult(created);
    }

    public Task<TodoStep?> SetStepCompletedAsync(long stepId, bool completed, CancellationToken ct = default)
    {
        Calls.Add($"SetStepCompleted:{stepId}:{completed}");
        var index = _steps.FindIndex(s => s.Id == stepId);
        if (index < 0)
            return Task.FromResult<TodoStep?>(null);
        var updated = _steps[index] with { CompletedAt = completed ? Now : null, UpdatedAt = Now };
        _steps[index] = updated;
        return Task.FromResult<TodoStep?>(updated);
    }

    public Task DeleteStepAsync(long stepId, CancellationToken ct = default)
    {
        Calls.Add($"DeleteStep:{stepId}");
        _steps.RemoveAll(s => s.Id == stepId);
        return Task.CompletedTask;
    }

    // ---------- attachments ----------

    public Task<TodoAttachment> AddAttachmentAsync(long taskId, string fileName, Stream content, CancellationToken ct = default)
    {
        Calls.Add($"AddAttachment:{taskId}:{fileName}");
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var now = Now;
        var created = new TodoAttachment
        {
            Id = _nextAttachmentId++,
            Uuid = $"attach-{_nextAttachmentId}",
            TaskId = taskId,
            FileName = Validate(fileName, "fileName"),
            Sha = $"sha-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()[..12]}",
            ByteSize = bytes.LongLength,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _attachments.Add(created);
        return Task.FromResult(created);
    }

    public Task<(TodoAttachment? Removed, int RemainingRefs)> DeleteAttachmentAsync(long attachmentId, CancellationToken ct = default)
    {
        Calls.Add($"DeleteAttachment:{attachmentId}");
        var removed = _attachments.FirstOrDefault(a => a.Id == attachmentId);
        if (removed is null)
            return Task.FromResult(((TodoAttachment?)null, 0));
        _attachments.RemoveAll(a => a.Id == attachmentId);
        var remaining = _attachments.Count(a => a.Sha == removed.Sha);
        return Task.FromResult<(TodoAttachment?, int)>((removed, remaining));
    }

    private Task<TodoTask?> Mutate(long id, Func<TodoTask, TodoTask> change) =>
        Task.FromResult(MutateSync(id, change));

    private TodoTask? MutateSync(long id, Func<TodoTask, TodoTask> change)
    {
        var index = _tasks.FindIndex(t => t.Id == id);
        if (index < 0)
            return null;
        var updated = change(_tasks[index]);
        _tasks[index] = updated;
        return updated;
    }

    // ---------- views (repository semantics mirrored) ----------

    public Task<IReadOnlyList<TodoTaskSummary>> GetTodayAsync(
        bool includeCompleted = false, CancellationToken ct = default) =>
        Task.FromResult(View(
            t => t.DueAt is not null && t.DueAt < LocalNextMidnightUtc(Now),
            includeCompleted,
            thenBy: t => t.DueAt!.Value,
            descending: false));

    public Task<IReadOnlyList<TodoTaskSummary>> GetImportantAsync(
        bool includeCompleted = false, CancellationToken ct = default)
    {
        // incomplete first, DueAt (nulls last), CreatedAt DESC
        var query = _tasks.Where(t => t.IsImportant);
        if (!includeCompleted)
            query = query.Where(t => t.CompletedAt is null);
        IReadOnlyList<TodoTaskSummary> result = query
            .OrderBy(t => t.CompletedAt is null ? 0 : 1)
            .ThenBy(t => t.DueAt is null ? 1 : 0)
            .ThenBy(t => t.DueAt)
            .ThenByDescending(t => t.CreatedAt)
            .Select(ToSummary)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<TodoTaskSummary>> GetPlannedAsync(
        bool includeCompleted = false, CancellationToken ct = default) =>
        Task.FromResult(View(
            t => t.DueAt is not null,
            includeCompleted,
            thenBy: t => t.DueAt!.Value,
            descending: false));

    public Task<IReadOnlyList<TodoTaskSummary>> GetAllAsync(
        bool includeCompleted = false, CancellationToken ct = default) =>
        Task.FromResult(View(_ => true, includeCompleted, thenBy: t => t.CreatedAt, descending: true));

    public Task<IReadOnlyList<TodoTaskSummary>> GetByListAsync(
        string listUuid, bool includeCompleted = false, CancellationToken ct = default) =>
        Task.FromResult(View(t => t.ListUuid == listUuid, includeCompleted, thenBy: t => t.CreatedAt, descending: true));

    public Task<IReadOnlyList<TodoTaskSummary>> SearchByTitleAsync(string title, CancellationToken ct = default)
    {
        IReadOnlyList<TodoTaskSummary> result = _tasks
            .Where(t => t.CompletedAt is null && t.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.CreatedAt)
            .Select(ToSummary)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<TodoViewCounts> GetViewCountsAsync(CancellationToken ct = default)
    {
        var end = LocalNextMidnightUtc(Now);
        var counts = new TodoViewCounts
        {
            Today = _tasks.Count(t => t.CompletedAt is null && t.DueAt is not null && t.DueAt < end),
            Important = _tasks.Count(t => t.CompletedAt is null && t.IsImportant),
            Planned = _tasks.Count(t => t.CompletedAt is null && t.DueAt is not null),
            All = _tasks.Count(t => t.CompletedAt is null),
        };
        return Task.FromResult(counts);
    }

    public Task<IReadOnlyDictionary<string, int>> CountIncompleteByListAsync(CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, int> map = _tasks
            .Where(t => t.CompletedAt is null)
            .GroupBy(t => t.ListUuid)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return Task.FromResult(map);
    }

    /// <summary>Ordering mirror: incomplete first, then the secondary key (repository SQL).</summary>
    private IReadOnlyList<TodoTaskSummary> View(
        Func<TodoTask, bool> where,
        bool includeCompleted,
        Func<TodoTask, DateTimeOffset> thenBy,
        bool descending)
    {
        var query = _tasks.Where(where);
        if (!includeCompleted)
            query = query.Where(t => t.CompletedAt is null);

        var incomplete = descending
            ? query.Where(t => t.CompletedAt is null).OrderByDescending(thenBy)
            : query.Where(t => t.CompletedAt is null).OrderBy(thenBy);
        var completed = includeCompleted
            ? (descending
                ? query.Where(t => t.CompletedAt is not null).OrderByDescending(thenBy)
                : query.Where(t => t.CompletedAt is not null).OrderBy(thenBy))
            : Enumerable.Empty<TodoTask>();

        return incomplete.Concat(completed).Select(ToSummary).ToList();
    }

    private TodoTaskSummary ToSummary(TodoTask t) => new()
    {
        Id = t.Id,
        Uuid = t.Uuid,
        ListUuid = t.ListUuid,
        Title = t.Title,
        DueAt = t.DueAt,
        IsAllDay = t.IsAllDay,
        ReminderOn = t.ReminderOn,
        IsImportant = t.IsImportant,
        CompletedAt = t.CompletedAt,
        RecurrenceRule = t.RecurrenceRule,
        StepsTotal = _steps.Count(s => s.TaskId == t.Id),
        StepsDone = _steps.Count(s => s.TaskId == t.Id && s.CompletedAt is not null),
    };

    private static DateTimeOffset LocalNextMidnightUtc(DateTimeOffset now)
    {
        var tz = TimeZoneInfo.Local;
        var localNow = TimeZoneInfo.ConvertTime(now, tz);
        var nextMidnight = DateTime.SpecifyKind(localNow.Date.AddDays(1), DateTimeKind.Unspecified);
        return new DateTimeOffset(nextMidnight, tz.GetUtcOffset(nextMidnight)).ToUniversalTime();
    }

    private static string Validate(string value, string paramName)
    {
        value = value.Trim();
        return value.Length == 0
            ? throw new ArgumentException($"Empty {paramName}.", paramName)
            : value;
    }
}
