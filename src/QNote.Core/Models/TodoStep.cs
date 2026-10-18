namespace QNote.Models;

/// <summary>
/// A todo step (subtask, schema v7 / PRD Decision 9). A lightweight checkable
/// child of a <see cref="TodoTask"/>: checking every step does NOT complete the
/// parent (real-To-Do semantics). Timestamps are millisecond-epoch integers in
/// storage and UTC values in memory; <c>CompletedAt</c> is null while unchecked.
/// </summary>
public sealed record TodoStep
{
    public long Id { get; init; }

    /// <summary>Stable identity (backup merge / future sync), unique across steps.</summary>
    public required string Uuid { get; init; }

    /// <summary>Owning task id (todo_tasks.Id; no FK by schema convention).</summary>
    public long TaskId { get; init; }

    /// <summary>
    /// Cross-device parent key (schema v8): the parent task's <see cref="TodoTask.Uuid"/>.
    /// <see cref="TaskId"/> stays authoritative locally; TaskUuid is what a synced
    /// row carries, because a peer's local id means nothing here.
    /// </summary>
    public string TaskUuid { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>Completion instant; <c>null</c> = unchecked.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Insertion order within the task (append = MAX+1).</summary>
    public int SortOrder { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Soft-delete tombstone (schema v8): <c>null</c> = live.</summary>
    public DateTimeOffset? DeletedAt { get; init; }
}
