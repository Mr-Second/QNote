using Microsoft.Data.Sqlite;
using QNote.Models;

namespace QNote.Data;

public sealed partial class TodoRepository
{
    public async Task<TodoTask> SaveTaskDraftAsync(TodoTaskDetail baseline, string title, string notes,
        string conflictSuffix, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();
        TodoTask? current = null;
        await using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = $"SELECT {TaskColumns} FROM todo_tasks WHERE Uuid = $uuid;";
            read.Parameters.AddWithValue("$uuid", baseline.Task.Uuid);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) current = MapTask(reader);
        }
        var conflict = current is null || current.DeletedAt is not null
            || current.Title != baseline.Task.Title || current.Notes != baseline.Task.Notes;
        var source = conflict ? baseline.Task : current!;
        var now = Math.Max(await NextDeletionClockAsync(conn, tx, source.Id, null, ct), source.UpdatedAt.ToUnixTimeMilliseconds() + 1);
        var draft = source with
        {
            Uuid = conflict ? Guid.NewGuid().ToString("N") : source.Uuid,
            Title = title + (conflict ? conflictSuffix : ""), Notes = notes,
            UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(now), DeletedAt = null,
        };
        var saved = await ImportTaskWithinAsync(conn, tx, draft, ct);
        if (conflict)
        {
            foreach (var step in baseline.Steps)
                await ImportStepWithinAsync(conn, tx, step with
                { Uuid = Guid.NewGuid().ToString("N"), TaskId = saved.Id, TaskUuid = saved.Uuid }, ct);
            foreach (var attachment in baseline.Attachments)
                await ImportAttachmentWithinAsync(conn, tx, attachment with
                { Uuid = Guid.NewGuid().ToString("N"), TaskId = saved.Id, TaskUuid = saved.Uuid }, ct);
        }
        await tx.CommitAsync(ct);
        return saved;
    }

    public async Task<TodoSyncSnapshot> ReadSyncSnapshotAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var tx = conn.BeginTransaction(deferred: true);
        var lists = await ReadAsync("todo_lists", ListColumns, MapList);
        var tasks = await ReadAsync("todo_tasks", TaskColumns, MapTask);
        var steps = await ReadAsync("todo_steps", StepColumns, MapStep);
        var attachments = await ReadAsync("todo_attachments", AttachmentColumns, MapAttachment);
        await tx.CommitAsync(ct);
        var stepsByTask = steps.ToLookup(s => s.TaskId);
        var attachmentsByTask = attachments.ToLookup(a => a.TaskId);
        // Pre-integration databases may contain child changes newer than their parent.
        // Include all child clocks, even deleted children, in that task's effective clock.
        var details = tasks.Select(task =>
        {
            var children = stepsByTask[task.Id].ToArray();
            var files = attachmentsByTask[task.Id].ToArray();
            var updated = children.Select(s => s.UpdatedAt).Concat(files.Select(a => a.UpdatedAt))
                .Append(task.UpdatedAt).Max();
            return new TodoTaskDetail(task with { UpdatedAt = updated },
                children.Where(s => s.DeletedAt is null).OrderBy(s => s.SortOrder).ThenBy(s => s.Uuid, StringComparer.Ordinal).ToArray(),
                files.Where(a => a.DeletedAt is null).OrderBy(a => a.Uuid, StringComparer.Ordinal).ToArray());
        }).ToArray();
        return new TodoSyncSnapshot(lists, details);

        async Task<List<T>> ReadAsync<T>(string table, string columns, Func<System.Data.Common.DbDataReader, T> map)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT {columns} FROM {table} ORDER BY Uuid;";
            var rows = new List<T>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(map(reader));
            return rows;
        }
    }

    public async Task ApplySyncListAsync(TodoList list, SyncStateEntry? state, CancellationToken ct = default, long? expectedUpdatedMs = null)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();
        await CheckSyncVersionAsync(conn, tx, SyncEntryKind.TodoList, list.Uuid, expectedUpdatedMs, ct);
        await ImportListWithinAsync(conn, tx, list, ct);
        if (state is not null) await SyncStateStore.UpsertWithinAsync(conn, tx, state, ct);
        await tx.CommitAsync(ct);
    }

    public async Task ApplySyncTaskAsync(TodoTaskDetail detail, SyncStateEntry? state, CancellationToken ct = default, long? expectedUpdatedMs = null)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();
        await CheckSyncVersionAsync(conn, tx, SyncEntryKind.TodoTask, detail.Task.Uuid, expectedUpdatedMs, ct);
        foreach (var step in detail.Steps)
            await CheckChildParentAsync(conn, tx, "todo_steps", step.Uuid, detail.Task.Uuid, ct);
        foreach (var attachment in detail.Attachments)
            await CheckChildParentAsync(conn, tx, "todo_attachments", attachment.Uuid, detail.Task.Uuid, ct);
        // A task owns its complete child snapshot. Deleting absent children here is
        // part of applying this winning container, not inferring a remote deletion.
        var task = await ImportTaskWithinAsync(conn, tx, detail.Task, ct);
        await using (var clear = conn.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM todo_steps WHERE TaskId = $id; DELETE FROM todo_attachments WHERE TaskId = $id;";
            clear.Parameters.AddWithValue("$id", task.Id);
            await clear.ExecuteNonQueryAsync(ct);
        }
        foreach (var step in detail.Steps)
            await ImportStepWithinAsync(conn, tx, step with { TaskId = task.Id, TaskUuid = task.Uuid }, ct);
        foreach (var attachment in detail.Attachments)
            await ImportAttachmentWithinAsync(conn, tx, attachment with { TaskId = task.Id, TaskUuid = task.Uuid }, ct);
        if (state is not null) await SyncStateStore.UpsertWithinAsync(conn, tx, state, ct);
        await tx.CommitAsync(ct);
    }

    public async Task ApplySyncDeletionAsync(SyncEntryKind kind, string uuid, long deletedMs,
        SyncStateEntry state, CancellationToken ct = default, long? expectedUpdatedMs = null)
    {
        var table = kind switch
        {
            SyncEntryKind.TodoList => "todo_lists",
            SyncEntryKind.TodoTask => "todo_tasks",
            _ => throw new ArgumentException("Expected a task or list deletion.", nameof(kind)),
        };
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();
        await CheckSyncVersionAsync(conn, tx, kind, uuid, expectedUpdatedMs, ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // Individual task tombstones handle list cascades and their own LWW clocks.
        cmd.CommandText = $"UPDATE {table} SET DeletedAt = $at, UpdatedAt = $at WHERE Uuid = $uuid;";
        if (kind == SyncEntryKind.TodoTask)
            cmd.CommandText += "UPDATE todo_steps SET DeletedAt = $at, UpdatedAt = $at WHERE TaskUuid = $uuid; " +
                "UPDATE todo_attachments SET DeletedAt = $at, UpdatedAt = $at WHERE TaskUuid = $uuid;";
        cmd.Parameters.AddWithValue("$uuid", uuid);
        cmd.Parameters.AddWithValue("$at", deletedMs);
        await cmd.ExecuteNonQueryAsync(ct);
        await SyncStateStore.UpsertWithinAsync(conn, tx, state, ct);
        await tx.CommitAsync(ct);
    }

    private static async Task AdvanceParentClockAsync(SqliteConnection conn, SqliteTransaction tx,
        long taskId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE todo_tasks SET UpdatedAt = " + TaskMutationClock + " WHERE Id = $id AND DeletedAt IS NULL;";
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$id", taskId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CheckSyncVersionAsync(SqliteConnection conn, SqliteTransaction tx,
        SyncEntryKind kind, string uuid, long? expected, CancellationToken ct)
    {
        if (expected is null) return;
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = kind == SyncEntryKind.TodoList
            ? "SELECT UpdatedAt FROM todo_lists WHERE Uuid = $uuid;"
            : "SELECT MAX(UpdatedAt, COALESCE((SELECT MAX(UpdatedAt) FROM todo_steps WHERE TaskUuid = $uuid), UpdatedAt), " +
                "COALESCE((SELECT MAX(UpdatedAt) FROM todo_attachments WHERE TaskUuid = $uuid), UpdatedAt)) FROM todo_tasks WHERE Uuid = $uuid;";
        cmd.Parameters.AddWithValue("$uuid", uuid);
        var actual = await cmd.ExecuteScalarAsync(ct);
        if ((actual is long at ? at : -1) != expected.Value)
            throw new InvalidOperationException("The local TODO entry changed during sync; retry with a fresh snapshot.");
    }

    private static async Task CheckChildParentAsync(SqliteConnection conn, SqliteTransaction tx,
        string table, string uuid, string parent, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT TaskUuid FROM {table} WHERE Uuid = $uuid;";
        cmd.Parameters.AddWithValue("$uuid", uuid);
        var previous = await cmd.ExecuteScalarAsync(ct) as string;
        if (previous is not null && previous != parent)
            throw new InvalidDataException("A TODO child belongs to another task.");
    }

    private static async Task<long> NextDeletionClockAsync(SqliteConnection conn, SqliteTransaction tx,
        long id, string? listUuid, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        var predicate = listUuid is null ? "t.Id = $id" : "t.ListUuid = $uuid";
        cmd.CommandText = "SELECT MAX(at) FROM (" +
            $"SELECT t.UpdatedAt AS at FROM todo_tasks t WHERE {predicate} UNION ALL " +
            $"SELECT c.UpdatedAt FROM todo_steps c JOIN todo_tasks t ON t.Id = c.TaskId WHERE {predicate} UNION ALL " +
            $"SELECT c.UpdatedAt FROM todo_attachments c JOIN todo_tasks t ON t.Id = c.TaskId WHERE {predicate}" +
            (listUuid is null ? ");" : " UNION ALL SELECT UpdatedAt FROM todo_lists WHERE Id = $id);");
        cmd.Parameters.AddWithValue("$id", id);
        if (listUuid is not null) cmd.Parameters.AddWithValue("$uuid", listUuid);
        var newest = await cmd.ExecuteScalarAsync(ct);
        return Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), newest is long value ? checked(value + 1) : 0);
    }
}
