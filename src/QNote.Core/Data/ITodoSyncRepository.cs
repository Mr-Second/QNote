using QNote.Models;

namespace QNote.Data;

/// <summary>A consistent snapshot includes deletion clocks; children travel with their task.</summary>
public sealed record TodoSyncSnapshot(
    IReadOnlyList<TodoList> Lists, IReadOnlyList<TodoTaskDetail> Tasks);

/// <summary>Sync imports bypass user-mutation hooks and atomically record agreement.</summary>
public interface ITodoSyncRepository
{
    Task<TodoSyncSnapshot> ReadSyncSnapshotAsync(CancellationToken ct = default);
    Task ApplySyncListAsync(TodoList list, SyncStateEntry? state, CancellationToken ct = default, long? expectedUpdatedMs = null);
    Task ApplySyncTaskAsync(TodoTaskDetail detail, SyncStateEntry? state, CancellationToken ct = default, long? expectedUpdatedMs = null);
    Task ApplySyncDeletionAsync(SyncEntryKind kind, string uuid, long deletedMs,
        SyncStateEntry state, CancellationToken ct = default, long? expectedUpdatedMs = null);
}
