namespace QNote.Models;

/// <summary>
/// A todo task (schema v7). <see cref="DueAt"/> carries the due instant; for an
/// all-day task the caller stores local midnight of the due date (the UTC instant
/// of that wall-clock time). <see cref="CompletedAt"/> is <c>null</c> while the
/// task is incomplete and the completion instant once checked off. Timestamps are
/// millisecond-epoch integers in storage (sync contract, PRD Decision 6) and
/// UTC <see cref="DateTimeOffset"/> values in memory.
/// </summary>
public sealed record TodoTask
{
    public long Id { get; init; }

    /// <summary>Stable sync identity; also the OS toast Tag for reminder cancel.</summary>
    public required string Uuid { get; init; }

    /// <summary>Owning list identity (matches <see cref="TodoList.Uuid"/>; no FK).</summary>
    public required string ListUuid { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>Long-form note body (detail pane's large notes area).</summary>
    public string Notes { get; init; } = string.Empty;

    /// <summary>Due instant, or <c>null</c> when the task has no due date.</summary>
    public DateTimeOffset? DueAt { get; init; }

    /// <summary>True when <see cref="DueAt"/> is a whole-day marker rather than a time.</summary>
    public bool IsAllDay { get; init; }

    /// <summary>Reminder toggle (PRD Decision 4: timed due → on by default, all-day → off).</summary>
    public bool ReminderOn { get; init; }

    /// <summary>Importance star (boolean; multi-level priority is post-MVP).</summary>
    public bool IsImportant { get; init; }

    /// <summary>Completion instant; <c>null</c> = incomplete.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// When the launch catch-up last re-fired this task's OS-dropped reminder
    /// (once per due crossing). Reminder bookkeeping that deliberately never
    /// stamps <see cref="UpdatedAt"/> — and travels with the row on backup
    /// merge so a restored archive cannot re-fire an already-fired reminder.
    /// </summary>
    public DateTimeOffset? CatchUpFiredAt { get; init; }

    /// <summary>
    /// Recurrence rule (PRD Decision 10): null = never repeats; otherwise one of
    /// <see cref="QNote.Services.TodoRecurrence"/>.Daily / Weekdays / Weekly /
    /// Monthly / Yearly. Completing a task with a rule spawns the next
    /// occurrence (real-To-Do semantics).
    /// </summary>
    public string? RecurrenceRule { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
