using System.Data.Common;
using Microsoft.Data.Sqlite;
using QNote.Models;

namespace QNote.Data;

/// <summary>
/// SQLite-backed todo persistence over the single <c>qnote.db</c> (schema v7). All
/// queries are parameterized and every connection/command/reader is disposed via
/// <c>await using</c> (see database-guidelines). Timestamps are stored as
/// millisecond-epoch integers (sync contract, PRD Decision 6) and read back as UTC
/// instants. Focused mutations (complete/due/important/reminder) use
/// <c>UPDATE ... RETURNING</c> so one statement both writes and yields the updated
/// row — a <c>null</c> result means "id not found", never an exception.
/// </summary>
public sealed class TodoRepository : ITodoRepository
{
    private const string TaskColumns =
        "Id, Uuid, ListUuid, Title, Notes, DueAt, IsAllDay, ReminderOn, IsImportant, CompletedAt, CatchUpFiredAt, RecurrenceRule, CreatedAt, UpdatedAt";

    // Correlated step subqueries ride every summary projection (row metadata:
    // N/M + repeat glyph). Scale is fine — todo_tasks is a small table.
    // CreatedAt feeds the 排序依据/创建日期 mode (VM-side sort).
    private const string SummaryColumns =
        "Id, Uuid, ListUuid, Title, DueAt, IsAllDay, ReminderOn, IsImportant, CompletedAt, RecurrenceRule, CreatedAt, " +
        "(SELECT COUNT(*) FROM todo_steps WHERE TaskId = todo_tasks.Id), " +
        "(SELECT COUNT(*) FROM todo_steps WHERE TaskId = todo_tasks.Id AND CompletedAt IS NOT NULL)";

    private const string ListColumns =
        "Id, Uuid, Name, Color, SortOrder, CreatedAt, UpdatedAt, Theme, HideCompleted, SortMode";

    private const string StepColumns = "Id, Uuid, TaskId, Title, CompletedAt, SortOrder, CreatedAt, UpdatedAt";

    private const string AttachmentColumns = "Id, Uuid, TaskId, FileName, Sha, ByteSize, CreatedAt, UpdatedAt";

    private readonly DbConnectionFactory _factory;

    public TodoRepository(DbConnectionFactory factory) => _factory = factory;

    // ---------- lists ----------

    public async Task<IReadOnlyList<TodoList>> GetListsAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {ListColumns} FROM todo_lists ORDER BY SortOrder;";

        var list = new List<TodoList>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapList(reader));
        return list;
    }

    public async Task<TodoList> CreateListAsync(TodoList list, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO todo_lists (Uuid, Name, Color, SortOrder, CreatedAt, UpdatedAt) " +
            "VALUES ($uuid, $name, $color, (SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM todo_lists), $created, $updated) " +
            "RETURNING Id, SortOrder;";
        cmd.Parameters.AddWithValue("$uuid", list.Uuid);
        cmd.Parameters.AddWithValue("$name", list.Name);
        cmd.Parameters.AddWithValue("$color", list.Color);
        cmd.Parameters.AddWithValue("$created", ToDbMs(list.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", ToDbMs(list.UpdatedAt));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("INSERT INTO todo_lists ... RETURNING returned no row.");
        return list with { Id = reader.GetInt64(0), SortOrder = reader.GetInt32(1) };
    }

    public async Task<TodoList?> UpdateListAsync(TodoList list, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        // Also carries the list-options menu state (theme / hide-completed /
        // sort mode) — the rename dialog and the ⋯ menu share this one entry point.
        cmd.CommandText =
            "UPDATE todo_lists SET Name = $name, Color = $color, " +
            "Theme = $theme, HideCompleted = $hide, SortMode = $sortMode, UpdatedAt = $updated " +
            $"WHERE Id = $id RETURNING {ListColumns};";
        cmd.Parameters.AddWithValue("$name", list.Name);
        cmd.Parameters.AddWithValue("$color", list.Color);
        cmd.Parameters.AddWithValue("$theme", list.Theme);
        cmd.Parameters.AddWithValue("$hide", list.HideCompleted);
        cmd.Parameters.AddWithValue("$sortMode", list.SortMode);
        cmd.Parameters.AddWithValue("$updated", ToDbMs(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", list.Id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapList(reader) : null;
    }

    public async Task<(IReadOnlyList<string> TaskUuids, IReadOnlyList<string> AttachmentShas)> DeleteListAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        string? uuid;
        await using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT Uuid FROM todo_lists WHERE Id = $id;";
            read.Parameters.AddWithValue("$id", id);
            uuid = (string?)(await read.ExecuteScalarAsync(ct));
        }

        var cascaded = new List<string>();
        var shas = new List<string>();
        if (uuid is not null)
        {
            // Uuid-link cascade (same shape as the notes↔categories name-link
            // cascade). Steps and attachment rows must go BEFORE the tasks
            // (they reference todo_tasks.Id); the shas ride out so the caller
            // can GC store files that lost their last reference, and the task
            // uuids so it can cancel pending reminder toasts. One transaction.
            var taskIds = new List<long>();
            await using (var read = conn.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = "SELECT Id, Uuid FROM todo_tasks WHERE ListUuid = $uuid;";
                read.Parameters.AddWithValue("$uuid", uuid);
                await using var reader = await read.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    taskIds.Add(reader.GetInt64(0));
                    cascaded.Add(reader.GetString(1));
                }
            }
            if (taskIds.Count > 0)
            {
                var idList = string.Join(",", taskIds);
                await using (var shaCmd = conn.CreateCommand())
                {
                    shaCmd.Transaction = tx;
                    shaCmd.CommandText = $"SELECT Sha FROM todo_attachments WHERE TaskId IN ({idList});";
                    await using var reader = await shaCmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                        shas.Add(reader.GetString(0));
                }
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = $"DELETE FROM todo_steps WHERE TaskId IN ({idList}); " +
                                      $"DELETE FROM todo_attachments WHERE TaskId IN ({idList});";
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = $"DELETE FROM todo_tasks WHERE Id IN ({idList});";
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }
            await using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM todo_lists WHERE Id = $id;";
                del.Parameters.AddWithValue("$id", id);
                await del.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
        return (cascaded, shas);
    }

    // ---------- tasks ----------

    public async Task<TodoTask?> GetTaskByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {TaskColumns} FROM todo_tasks WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapTask(reader) : null;
    }

    public async Task<TodoTask?> GetTaskByUuidAsync(string uuid, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {TaskColumns} FROM todo_tasks WHERE Uuid = $uuid;";
        cmd.Parameters.AddWithValue("$uuid", uuid);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapTask(reader) : null;
    }

    public async Task<TodoTask> CreateTaskAsync(TodoTask task, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO todo_tasks (Uuid, ListUuid, Title, Notes, DueAt, IsAllDay, ReminderOn, IsImportant, CompletedAt, RecurrenceRule, CreatedAt, UpdatedAt) " +
            "VALUES ($uuid, $listUuid, $title, $notes, $due, $allDay, $reminder, $important, $completed, $rule, $created, $updated) " +
            "RETURNING Id;";
        cmd.Parameters.AddWithValue("$uuid", task.Uuid);
        cmd.Parameters.AddWithValue("$listUuid", task.ListUuid);
        cmd.Parameters.AddWithValue("$title", task.Title);
        cmd.Parameters.AddWithValue("$notes", task.Notes);
        cmd.Parameters.AddWithValue("$due", task.DueAt is { } due ? ToDbMs(due) : DBNull.Value);
        cmd.Parameters.AddWithValue("$allDay", task.IsAllDay);
        cmd.Parameters.AddWithValue("$reminder", task.ReminderOn);
        cmd.Parameters.AddWithValue("$important", task.IsImportant);
        cmd.Parameters.AddWithValue("$completed", task.CompletedAt is { } done ? ToDbMs(done) : DBNull.Value);
        cmd.Parameters.AddWithValue("$rule", (object?)task.RecurrenceRule ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", ToDbMs(task.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", ToDbMs(task.UpdatedAt));

        var result = await cmd.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("INSERT INTO todo_tasks ... RETURNING Id returned no value.");
        return task with { Id = (long)result };
    }

    public async Task<TodoTask?> UpdateTaskAsync(
        long id, string title, string notes, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE todo_tasks SET Title = $title, Notes = $notes, UpdatedAt = $updated " +
            $"WHERE Id = $id RETURNING {TaskColumns};";
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$notes", notes);
        cmd.Parameters.AddWithValue("$updated", ToDbMs(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapTask(reader) : null;
    }

    public async Task<IReadOnlyList<string>> DeleteTaskAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        // Collect the attachment shas BEFORE the rows go (the caller GCs store
        // files that lost their last reference), then cascade steps + attachments
        // + the task row in one transaction.
        var shas = new List<string>();
        await using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT Sha FROM todo_attachments WHERE TaskId = $id;";
            read.Parameters.AddWithValue("$id", id);
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                shas.Add(reader.GetString(0));
        }
        if (shas.Count == 0 && !await TaskExistsAsync(conn, tx, id))
        {
            await tx.CommitAsync(ct);
            return shas;
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM todo_steps WHERE TaskId = $id; DELETE FROM todo_attachments WHERE TaskId = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM todo_tasks WHERE Id = $id;";
            del.Parameters.AddWithValue("$id", id);
            await del.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return shas;
    }

    private static async Task<bool> TaskExistsAsync(SqliteConnection conn, SqliteTransaction tx, long id)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM todo_tasks WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        return count > 0;
    }

    public async Task<TodoTask?> CompleteTaskAsync(long id, DateTimeOffset completedAt, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        // Completion time doubles as the mutation time (sync LWW sees the change).
        var at = ToDbMs(completedAt);
        cmd.CommandText =
            "UPDATE todo_tasks SET CompletedAt = $at, UpdatedAt = $at " +
            $"WHERE Id = $id RETURNING {TaskColumns};";
        cmd.Parameters.AddWithValue("$at", at);
        cmd.Parameters.AddWithValue("$id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapTask(reader) : null;
    }

    public async Task<TodoTask?> UncompleteTaskAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE todo_tasks SET CompletedAt = NULL, UpdatedAt = $updated " +
            $"WHERE Id = $id RETURNING {TaskColumns};";
        cmd.Parameters.AddWithValue("$updated", ToDbMs(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapTask(reader) : null;
    }

    public async Task<TodoTask?> SetTaskDueAsync(
        long id, DateTimeOffset? dueUtc, bool isAllDay, bool reminderOn, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE todo_tasks SET DueAt = $due, IsAllDay = $allDay, ReminderOn = $reminder, UpdatedAt = $updated " +
            $"WHERE Id = $id RETURNING {TaskColumns};";
        cmd.Parameters.AddWithValue("$due", dueUtc is { } due ? ToDbMs(due) : DBNull.Value);
        cmd.Parameters.AddWithValue("$allDay", isAllDay);
        cmd.Parameters.AddWithValue("$reminder", reminderOn);
        cmd.Parameters.AddWithValue("$updated", ToDbMs(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapTask(reader) : null;
    }

    public async Task<TodoTask?> SetTaskImportantAsync(long id, bool isImportant, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE todo_tasks SET IsImportant = $important, UpdatedAt = $updated " +
            $"WHERE Id = $id RETURNING {TaskColumns};";
        cmd.Parameters.AddWithValue("$important", isImportant);
        cmd.Parameters.AddWithValue("$updated", ToDbMs(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapTask(reader) : null;
    }

    public async Task<TodoTask?> SetTaskReminderAsync(long id, bool reminderOn, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE todo_tasks SET ReminderOn = $reminder, UpdatedAt = $updated " +
            $"WHERE Id = $id RETURNING {TaskColumns};";
        cmd.Parameters.AddWithValue("$reminder", reminderOn);
        cmd.Parameters.AddWithValue("$updated", ToDbMs(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapTask(reader) : null;
    }

    public async Task<TodoTask?> SetTaskRecurrenceAsync(long id, string? rule, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE todo_tasks SET RecurrenceRule = $rule, UpdatedAt = $updated " +
            $"WHERE Id = $id RETURNING {TaskColumns};";
        cmd.Parameters.AddWithValue("$rule", (object?)rule ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated", ToDbMs(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapTask(reader) : null;
    }

    // ---------- steps (PRD Decision 9) ----------

    public async Task<IReadOnlyList<TodoStep>> GetStepsAsync(long taskId, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {StepColumns} FROM todo_steps WHERE TaskId = $task ORDER BY SortOrder;";
        cmd.Parameters.AddWithValue("$task", taskId);

        var list = new List<TodoStep>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapStep(reader));
        return list;
    }

    public async Task<TodoStep> CreateStepAsync(TodoStep step, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO todo_steps (Uuid, TaskId, Title, CompletedAt, SortOrder, CreatedAt, UpdatedAt) " +
            "VALUES ($uuid, $task, $title, $completed, (SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM todo_steps WHERE TaskId = $task), $created, $updated) " +
            "RETURNING Id, SortOrder;";
        cmd.Parameters.AddWithValue("$uuid", step.Uuid);
        cmd.Parameters.AddWithValue("$task", step.TaskId);
        cmd.Parameters.AddWithValue("$title", step.Title);
        cmd.Parameters.AddWithValue("$completed", step.CompletedAt is { } done ? ToDbMs(done) : DBNull.Value);
        cmd.Parameters.AddWithValue("$created", ToDbMs(step.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", ToDbMs(step.UpdatedAt));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("INSERT INTO todo_steps ... RETURNING returned no row.");
        return step with { Id = reader.GetInt64(0), SortOrder = reader.GetInt32(1) };
    }

    public async Task<TodoStep?> SetStepCompletedAsync(long id, DateTimeOffset? completedAt, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        // Same stamping rule as task completion: the completion instant doubles
        // as the mutation time; uncheck stamps write-time.
        var at = ToDbMs(DateTimeOffset.UtcNow);
        cmd.CommandText =
            "UPDATE todo_steps SET CompletedAt = $completed, UpdatedAt = $updated " +
            $"WHERE Id = $id RETURNING {StepColumns};";
        cmd.Parameters.AddWithValue("$completed", completedAt is { } done ? ToDbMs(done) : DBNull.Value);
        cmd.Parameters.AddWithValue("$updated", at);
        cmd.Parameters.AddWithValue("$id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapStep(reader) : null;
    }

    public async Task DeleteStepAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM todo_steps WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------- attachments (PRD Decision 11) ----------

    public async Task<IReadOnlyList<TodoAttachment>> GetAttachmentsAsync(long taskId, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {AttachmentColumns} FROM todo_attachments WHERE TaskId = $task ORDER BY CreatedAt DESC;";
        cmd.Parameters.AddWithValue("$task", taskId);

        var list = new List<TodoAttachment>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapAttachment(reader));
        return list;
    }

    public async Task<TodoAttachment> CreateAttachmentAsync(TodoAttachment attachment, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO todo_attachments (Uuid, TaskId, FileName, Sha, ByteSize, CreatedAt, UpdatedAt) " +
            "VALUES ($uuid, $task, $name, $sha, $size, $created, $updated) RETURNING Id;";
        cmd.Parameters.AddWithValue("$uuid", attachment.Uuid);
        cmd.Parameters.AddWithValue("$task", attachment.TaskId);
        cmd.Parameters.AddWithValue("$name", attachment.FileName);
        cmd.Parameters.AddWithValue("$sha", attachment.Sha);
        cmd.Parameters.AddWithValue("$size", attachment.ByteSize);
        cmd.Parameters.AddWithValue("$created", ToDbMs(attachment.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", ToDbMs(attachment.UpdatedAt));

        var result = await cmd.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("INSERT INTO todo_attachments ... RETURNING Id returned no value.");
        return attachment with { Id = (long)result };
    }

    public async Task<(TodoAttachment? Removed, int RemainingRefs)> DeleteAttachmentAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        string? sha = null;
        TodoAttachment? removed = null;
        await using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = $"SELECT {AttachmentColumns} FROM todo_attachments WHERE Id = $id;";
            read.Parameters.AddWithValue("$id", id);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                removed = MapAttachment(reader);
                sha = removed.Sha;
            }
        }

        if (removed is null)
        {
            await tx.CommitAsync(ct);
            return (null, 0);
        }

        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM todo_attachments WHERE Id = $id;";
            del.Parameters.AddWithValue("$id", id);
            await del.ExecuteNonQueryAsync(ct);
        }

        var refs = 0;
        await using (var count = conn.CreateCommand())
        {
            count.Transaction = tx;
            count.CommandText = "SELECT COUNT(*) FROM todo_attachments WHERE Sha = $sha;";
            count.Parameters.AddWithValue("$sha", sha!);
            refs = Convert.ToInt32((long)(await count.ExecuteScalarAsync(ct))!);
        }

        await tx.CommitAsync(ct);
        return (removed, refs);
    }

    public async Task<int> CountAttachmentRefsAsync(string sha, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM todo_attachments WHERE Sha = $sha;";
        cmd.Parameters.AddWithValue("$sha", sha);
        return Convert.ToInt32((long)(await cmd.ExecuteScalarAsync(ct))!);
    }

    public async Task<IReadOnlyList<TodoStep>> GetAllStepsAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {StepColumns} FROM todo_steps ORDER BY TaskId, SortOrder;";

        var list = new List<TodoStep>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapStep(reader));
        return list;
    }

    public async Task<IReadOnlyList<TodoAttachment>> GetAllAttachmentsAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {AttachmentColumns} FROM todo_attachments ORDER BY TaskId;";

        var list = new List<TodoAttachment>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapAttachment(reader));
        return list;
    }

    // ---------- views ----------

    public async Task<IReadOnlyList<TodoTaskSummary>> GetTodayAsync(
        DateTimeOffset todayEndUtc, bool includeCompleted = false, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT " + SummaryColumns + " FROM todo_tasks " +
            "WHERE DueAt IS NOT NULL AND DueAt < $end" +
            (includeCompleted ? " " : " AND CompletedAt IS NULL ") +
            "ORDER BY (CompletedAt IS NULL) DESC, DueAt;";
        cmd.Parameters.AddWithValue("$end", ToDbMs(todayEndUtc));
        return await ReadSummariesAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<TodoTaskSummary>> GetImportantAsync(
        bool includeCompleted = false, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT " + SummaryColumns + " FROM todo_tasks " +
            "WHERE IsImportant = 1" +
            (includeCompleted ? " " : " AND CompletedAt IS NULL ") +
            "ORDER BY (CompletedAt IS NULL) DESC, CASE WHEN DueAt IS NULL THEN 1 ELSE 0 END, DueAt, CreatedAt DESC;";
        return await ReadSummariesAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<TodoTaskSummary>> GetPlannedAsync(
        bool includeCompleted = false, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT " + SummaryColumns + " FROM todo_tasks " +
            "WHERE DueAt IS NOT NULL" +
            (includeCompleted ? " " : " AND CompletedAt IS NULL ") +
            "ORDER BY (CompletedAt IS NULL) DESC, DueAt;";
        return await ReadSummariesAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<TodoTaskSummary>> GetAllAsync(
        bool includeCompleted = false, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT " + SummaryColumns + " FROM todo_tasks " +
            (includeCompleted
                ? "ORDER BY (CompletedAt IS NULL) DESC, CreatedAt DESC;"
                : "WHERE CompletedAt IS NULL ORDER BY CreatedAt DESC;");
        return await ReadSummariesAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<TodoTaskSummary>> GetByListAsync(
        string listUuid, bool includeCompleted = false, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT " + SummaryColumns + " FROM todo_tasks " +
            "WHERE ListUuid = $list" +
            (includeCompleted
                ? " ORDER BY (CompletedAt IS NULL) DESC, CreatedAt DESC;"
                : " AND CompletedAt IS NULL ORDER BY CreatedAt DESC;");
        cmd.Parameters.AddWithValue("$list", listUuid);
        return await ReadSummariesAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<TodoTaskSummary>> SearchByTitleAsync(
        string title, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        // ESCAPE '\' + escaping makes user input match LITERALLY: %, _ and \ lose
        // their LIKE wildcard meaning (injection-safe — the pattern is parameterized).
        cmd.CommandText =
            "SELECT " + SummaryColumns + " FROM todo_tasks " +
            "WHERE CompletedAt IS NULL AND Title LIKE $pattern ESCAPE '\\' ORDER BY CreatedAt DESC;";
        cmd.Parameters.AddWithValue("$pattern", "%" + EscapeLike(title) + "%");
        return await ReadSummariesAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<TodoTaskSummary>> GetCatchUpRemindersAsync(
        DateTimeOffset minDueUtc, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        // The marker filter enforces "once per due crossing": a task whose
        // reminder was already catch-up fired for its CURRENT due instant is
        // skipped, while a re-dated task (new due above the old marker) fires again.
        cmd.CommandText =
            "SELECT " + SummaryColumns + " FROM todo_tasks " +
            "WHERE CompletedAt IS NULL AND ReminderOn = 1 AND DueAt >= $min AND DueAt < $now " +
            "AND (CatchUpFiredAt IS NULL OR CatchUpFiredAt < DueAt) " +
            "ORDER BY DueAt;";
        cmd.Parameters.AddWithValue("$min", ToDbMs(minDueUtc));
        cmd.Parameters.AddWithValue("$now", ToDbMs(nowUtc));
        return await ReadSummariesAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<TodoTaskSummary>> GetUpcomingRemindersAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT " + SummaryColumns + " FROM todo_tasks " +
            "WHERE CompletedAt IS NULL AND ReminderOn = 1 AND DueAt > $from AND DueAt <= $to " +
            "ORDER BY DueAt;";
        cmd.Parameters.AddWithValue("$from", ToDbMs(fromUtc));
        cmd.Parameters.AddWithValue("$to", ToDbMs(toUtc));
        return await ReadSummariesAsync(cmd, ct);
    }

    public async Task MarkCatchUpFiredAsync(long id, DateTimeOffset firedAtUtc, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE todo_tasks SET CatchUpFiredAt = $fired WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$fired", ToDbMs(firedAtUtc));
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------- backup-restore merge primitives ----------

    public async Task<IReadOnlyDictionary<string, (long Id, DateTimeOffset UpdatedAt)>> GetListUuidTimestampsAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Uuid, Id, UpdatedAt FROM todo_lists;";

        var map = new Dictionary<string, (long, DateTimeOffset)>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            map[reader.GetString(0)] = (reader.GetInt64(1), FromDbMs(reader.GetInt64(2)));
        return map;
    }

    public async Task<IReadOnlyDictionary<string, (long Id, DateTimeOffset UpdatedAt)>> GetTaskUuidTimestampsAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Uuid, Id, UpdatedAt FROM todo_tasks;";

        var map = new Dictionary<string, (long, DateTimeOffset)>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            map[reader.GetString(0)] = (reader.GetInt64(1), FromDbMs(reader.GetInt64(2)));
        return map;
    }

    public async Task<TodoList> ImportListAsync(TodoList list, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        // Upsert by uuid (the merge LWW decision lives in the caller): the
        // conflict branch rewrites the live row whole — keeping its Id — so the
        // archived SortOrder/timestamps AND list-options state survive exactly
        // as the backup held them.
        cmd.CommandText =
            "INSERT INTO todo_lists (Uuid, Name, Color, SortOrder, CreatedAt, UpdatedAt, Theme, HideCompleted, SortMode) " +
            "VALUES ($uuid, $name, $color, $sort, $created, $updated, $theme, $hide, $sortMode) " +
            "ON CONFLICT(Uuid) DO UPDATE SET " +
            "Name = excluded.Name, Color = excluded.Color, SortOrder = excluded.SortOrder, " +
            "CreatedAt = excluded.CreatedAt, UpdatedAt = excluded.UpdatedAt, " +
            "Theme = excluded.Theme, HideCompleted = excluded.HideCompleted, SortMode = excluded.SortMode " +
            "RETURNING Id, SortOrder;";
        cmd.Parameters.AddWithValue("$uuid", list.Uuid);
        cmd.Parameters.AddWithValue("$name", list.Name);
        cmd.Parameters.AddWithValue("$color", list.Color);
        cmd.Parameters.AddWithValue("$sort", list.SortOrder);
        cmd.Parameters.AddWithValue("$created", ToDbMs(list.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", ToDbMs(list.UpdatedAt));
        cmd.Parameters.AddWithValue("$theme", list.Theme);
        cmd.Parameters.AddWithValue("$hide", list.HideCompleted);
        cmd.Parameters.AddWithValue("$sortMode", list.SortMode);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("todo_lists import upsert returned no row.");
        return list with { Id = reader.GetInt64(0), SortOrder = reader.GetInt32(1) };
    }

    public async Task<TodoTask> ImportTaskAsync(TodoTask task, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        // Upsert by uuid (the merge LWW decision lives in the caller). The whole
        // row travels — CompletedAt for the completion state, CatchUpFiredAt so
        // a restored archive cannot re-fire an already catch-up-fired reminder,
        // and the RecurrenceRule for repeat tasks.
        cmd.CommandText =
            "INSERT INTO todo_tasks (Uuid, ListUuid, Title, Notes, DueAt, IsAllDay, ReminderOn, IsImportant, CompletedAt, CatchUpFiredAt, RecurrenceRule, CreatedAt, UpdatedAt) " +
            "VALUES ($uuid, $listUuid, $title, $notes, $due, $allDay, $reminder, $important, $completed, $catchUp, $rule, $created, $updated) " +
            "ON CONFLICT(Uuid) DO UPDATE SET " +
            "ListUuid = excluded.ListUuid, Title = excluded.Title, Notes = excluded.Notes, " +
            "DueAt = excluded.DueAt, IsAllDay = excluded.IsAllDay, ReminderOn = excluded.ReminderOn, " +
            "IsImportant = excluded.IsImportant, CompletedAt = excluded.CompletedAt, " +
            "CatchUpFiredAt = excluded.CatchUpFiredAt, RecurrenceRule = excluded.RecurrenceRule, " +
            "CreatedAt = excluded.CreatedAt, UpdatedAt = excluded.UpdatedAt " +
            "RETURNING Id;";
        cmd.Parameters.AddWithValue("$uuid", task.Uuid);
        cmd.Parameters.AddWithValue("$listUuid", task.ListUuid);
        cmd.Parameters.AddWithValue("$title", task.Title);
        cmd.Parameters.AddWithValue("$notes", task.Notes);
        cmd.Parameters.AddWithValue("$due", task.DueAt is { } due ? ToDbMs(due) : DBNull.Value);
        cmd.Parameters.AddWithValue("$allDay", task.IsAllDay);
        cmd.Parameters.AddWithValue("$reminder", task.ReminderOn);
        cmd.Parameters.AddWithValue("$important", task.IsImportant);
        cmd.Parameters.AddWithValue("$completed", task.CompletedAt is { } done ? ToDbMs(done) : DBNull.Value);
        cmd.Parameters.AddWithValue("$catchUp", task.CatchUpFiredAt is { } fired ? ToDbMs(fired) : DBNull.Value);
        cmd.Parameters.AddWithValue("$rule", (object?)task.RecurrenceRule ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", ToDbMs(task.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", ToDbMs(task.UpdatedAt));

        var result = await cmd.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("todo_tasks import upsert returned no value.");
        return task with { Id = (long)result };
    }

    public async Task<IReadOnlyDictionary<string, (long Id, DateTimeOffset UpdatedAt)>> GetStepUuidTimestampsAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Uuid, Id, UpdatedAt FROM todo_steps;";

        var map = new Dictionary<string, (long, DateTimeOffset)>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            map[reader.GetString(0)] = (reader.GetInt64(1), FromDbMs(reader.GetInt64(2)));
        return map;
    }

    public async Task<TodoStep> ImportStepAsync(TodoStep step, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO todo_steps (Uuid, TaskId, Title, CompletedAt, SortOrder, CreatedAt, UpdatedAt) " +
            "VALUES ($uuid, $task, $title, $completed, $sort, $created, $updated) " +
            "ON CONFLICT(Uuid) DO UPDATE SET " +
            "TaskId = excluded.TaskId, Title = excluded.Title, CompletedAt = excluded.CompletedAt, " +
            "SortOrder = excluded.SortOrder, CreatedAt = excluded.CreatedAt, UpdatedAt = excluded.UpdatedAt " +
            "RETURNING Id, SortOrder;";
        cmd.Parameters.AddWithValue("$uuid", step.Uuid);
        cmd.Parameters.AddWithValue("$task", step.TaskId);
        cmd.Parameters.AddWithValue("$title", step.Title);
        cmd.Parameters.AddWithValue("$completed", step.CompletedAt is { } done ? ToDbMs(done) : DBNull.Value);
        cmd.Parameters.AddWithValue("$sort", step.SortOrder);
        cmd.Parameters.AddWithValue("$created", ToDbMs(step.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", ToDbMs(step.UpdatedAt));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("todo_steps import upsert returned no row.");
        return step with { Id = reader.GetInt64(0), SortOrder = reader.GetInt32(1) };
    }

    public async Task<IReadOnlyDictionary<string, (long Id, DateTimeOffset UpdatedAt)>> GetAttachmentUuidTimestampsAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Uuid, Id, UpdatedAt FROM todo_attachments;";

        var map = new Dictionary<string, (long, DateTimeOffset)>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            map[reader.GetString(0)] = (reader.GetInt64(1), FromDbMs(reader.GetInt64(2)));
        return map;
    }

    public async Task<TodoAttachment?> GetAttachmentByUuidAsync(string uuid, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {AttachmentColumns} FROM todo_attachments WHERE Uuid = $uuid;";
        cmd.Parameters.AddWithValue("$uuid", uuid);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapAttachment(reader) : null;
    }

    public async Task<TodoAttachment> ImportAttachmentAsync(TodoAttachment attachment, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO todo_attachments (Uuid, TaskId, FileName, Sha, ByteSize, CreatedAt, UpdatedAt) " +
            "VALUES ($uuid, $task, $name, $sha, $size, $created, $updated) " +
            "ON CONFLICT(Uuid) DO UPDATE SET " +
            "TaskId = excluded.TaskId, FileName = excluded.FileName, Sha = excluded.Sha, " +
            "ByteSize = excluded.ByteSize, CreatedAt = excluded.CreatedAt, UpdatedAt = excluded.UpdatedAt " +
            "RETURNING Id;";
        cmd.Parameters.AddWithValue("$uuid", attachment.Uuid);
        cmd.Parameters.AddWithValue("$task", attachment.TaskId);
        cmd.Parameters.AddWithValue("$name", attachment.FileName);
        cmd.Parameters.AddWithValue("$sha", attachment.Sha);
        cmd.Parameters.AddWithValue("$size", attachment.ByteSize);
        cmd.Parameters.AddWithValue("$created", ToDbMs(attachment.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", ToDbMs(attachment.UpdatedAt));

        var result = await cmd.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("todo_attachments import upsert returned no value.");
        return attachment with { Id = (long)result };
    }

    // ---------- counts ----------

    public async Task<TodoViewCounts> GetViewCountsAsync(DateTimeOffset todayEndUtc, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT " +
            "COUNT(*) FILTER (WHERE CompletedAt IS NULL AND DueAt IS NOT NULL AND DueAt < $end), " +
            "COUNT(*) FILTER (WHERE CompletedAt IS NULL AND IsImportant = 1), " +
            "COUNT(*) FILTER (WHERE CompletedAt IS NULL AND DueAt IS NOT NULL), " +
            "COUNT(*) FILTER (WHERE CompletedAt IS NULL) " +
            "FROM todo_tasks;";
        cmd.Parameters.AddWithValue("$end", ToDbMs(todayEndUtc));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("Aggregate count query returned no row.");
        return new TodoViewCounts
        {
            Today = Convert.ToInt32(reader.GetInt64(0)),
            Important = Convert.ToInt32(reader.GetInt64(1)),
            Planned = Convert.ToInt32(reader.GetInt64(2)),
            All = Convert.ToInt32(reader.GetInt64(3)),
        };
    }

    public async Task<IReadOnlyDictionary<string, int>> CountIncompleteByListAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT ListUuid, COUNT(*) FROM todo_tasks WHERE CompletedAt IS NULL GROUP BY ListUuid;";

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            map[reader.GetString(0)] = Convert.ToInt32(reader.GetInt64(1));
        return map;
    }

    // ---------- helpers ----------

    private static async Task<List<TodoTaskSummary>> ReadSummariesAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<TodoTaskSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapSummary(reader));
        return list;
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static TodoList MapList(DbDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Uuid = reader.GetString(1),
        Name = reader.GetString(2),
        Color = reader.GetString(3),
        SortOrder = reader.GetInt32(4),
        CreatedAt = FromDbMs(reader.GetInt64(5)),
        UpdatedAt = FromDbMs(reader.GetInt64(6)),
        Theme = reader.GetInt32(7),
        HideCompleted = reader.GetInt64(8) != 0,
        SortMode = reader.GetInt32(9),
    };

    private static TodoTask MapTask(DbDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Uuid = reader.GetString(1),
        ListUuid = reader.GetString(2),
        Title = reader.GetString(3),
        Notes = reader.GetString(4),
        DueAt = reader.IsDBNull(5) ? null : FromDbMs(reader.GetInt64(5)),
        IsAllDay = reader.GetInt64(6) != 0,
        ReminderOn = reader.GetInt64(7) != 0,
        IsImportant = reader.GetInt64(8) != 0,
        CompletedAt = reader.IsDBNull(9) ? null : FromDbMs(reader.GetInt64(9)),
        CatchUpFiredAt = reader.IsDBNull(10) ? null : FromDbMs(reader.GetInt64(10)),
        RecurrenceRule = reader.IsDBNull(11) ? null : reader.GetString(11),
        CreatedAt = FromDbMs(reader.GetInt64(12)),
        UpdatedAt = FromDbMs(reader.GetInt64(13)),
    };

    private static TodoTaskSummary MapSummary(DbDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Uuid = reader.GetString(1),
        ListUuid = reader.GetString(2),
        Title = reader.GetString(3),
        DueAt = reader.IsDBNull(4) ? null : FromDbMs(reader.GetInt64(4)),
        IsAllDay = reader.GetInt64(5) != 0,
        ReminderOn = reader.GetInt64(6) != 0,
        IsImportant = reader.GetInt64(7) != 0,
        CompletedAt = reader.IsDBNull(8) ? null : FromDbMs(reader.GetInt64(8)),
        RecurrenceRule = reader.IsDBNull(9) ? null : reader.GetString(9),
        CreatedAt = FromDbMs(reader.GetInt64(10)),
        StepsTotal = Convert.ToInt32(reader.GetInt64(11)),
        StepsDone = Convert.ToInt32(reader.GetInt64(12)),
    };

    private static TodoStep MapStep(DbDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Uuid = reader.GetString(1),
        TaskId = reader.GetInt64(2),
        Title = reader.GetString(3),
        CompletedAt = reader.IsDBNull(4) ? null : FromDbMs(reader.GetInt64(4)),
        SortOrder = reader.GetInt32(5),
        CreatedAt = FromDbMs(reader.GetInt64(6)),
        UpdatedAt = FromDbMs(reader.GetInt64(7)),
    };

    private static TodoAttachment MapAttachment(DbDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Uuid = reader.GetString(1),
        TaskId = reader.GetInt64(2),
        FileName = reader.GetString(3),
        Sha = reader.GetString(4),
        ByteSize = reader.GetInt64(5),
        CreatedAt = FromDbMs(reader.GetInt64(6)),
        UpdatedAt = FromDbMs(reader.GetInt64(7)),
    };

    private static long ToDbMs(DateTimeOffset value) =>
        value.ToUniversalTime().ToUnixTimeMilliseconds();

    private static DateTimeOffset FromDbMs(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms);
}
