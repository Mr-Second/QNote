namespace QNote.Models;

/// <summary>
/// Which domain a <see cref="SyncStateEntry"/> row describes. Storage form is a
/// lowercase snake_case TEXT in <c>sync_state.entry_kind</c> (schema v8 DDL);
/// mapping lives in <see cref="Data.SyncStateStore"/> as the single authority.
/// One row per (kind, uuid) — a note and a task may share a uuid value only by
/// coincidence and never collide because the kind partitions the keyspace.
/// </summary>
public enum SyncEntryKind
{
    Note,
    Category,
    TodoList,
    TodoTask,
    TodoStep,
    TodoAttachment,
}
