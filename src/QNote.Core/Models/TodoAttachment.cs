namespace QNote.Models;

/// <summary>
/// A todo file attachment (schema v7 / PRD Decision 11). The bytes live OUTSIDE
/// the database in the <c>attachments/</c> store, content-addressed by
/// <see cref="Sha"/> (raw bytes — no normalization, unlike note images); this
/// row is the reference that keeps the file alive. The size cap is a per-TASK
/// total (<see cref="QNote.Services.TodoService.MaxTaskAttachmentBytes"/> = 25 MB,
/// MS To Do parity — user ruling 2026-10-15, superseding the PRD's 10 MB per-file
/// cap); it is enforced by the service, not the store.
/// </summary>
public sealed record TodoAttachment
{
    public long Id { get; init; }

    /// <summary>Stable identity (backup merge / future sync), unique across attachments.</summary>
    public required string Uuid { get; init; }

    /// <summary>Owning task id (todo_tasks.Id; no FK by schema convention).</summary>
    public long TaskId { get; init; }

    /// <summary>The user-facing file name shown in the detail pane.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Content hash (SHA-256 hex) — the store's file key; dedupe across tasks.</summary>
    public string Sha { get; init; } = string.Empty;

    public long ByteSize { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// The full detail-pane aggregate for one task: the task record plus its steps
/// (ordered) and attachments (ordered) — one service call instead of three.
/// </summary>
public sealed record TodoTaskDetail(TodoTask Task, IReadOnlyList<TodoStep> Steps, IReadOnlyList<TodoAttachment> Attachments);
