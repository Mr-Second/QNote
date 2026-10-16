namespace QNote.Models;

/// <summary>
/// Lean list projection of a <see cref="TodoTask"/> for the middle-column task
/// cards, smart views and search results. Excludes <see cref="TodoTask.Notes"/>
/// (potentially long text) so list loading stays cheap; the detail pane fetches
/// the full record via <c>ITodoRepository.GetTaskByIdAsync</c> on selection.
/// </summary>
public sealed record TodoTaskSummary
{
    public long Id { get; init; }

    /// <summary>Stable sync identity (mirrors <see cref="TodoTask.Uuid"/>).</summary>
    public required string Uuid { get; init; }

    /// <summary>Owning list identity (smart views group or badge by this).</summary>
    public required string ListUuid { get; init; }

    public string Title { get; init; } = string.Empty;

    public DateTimeOffset? DueAt { get; init; }

    public bool IsAllDay { get; init; }

    public bool ReminderOn { get; init; }

    public bool IsImportant { get; init; }

    /// <summary>Completion instant; <c>null</c> = incomplete.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Recurrence rule; null = never repeats (row metadata glyph).</summary>
    public string? RecurrenceRule { get; init; }

    /// <summary>Step count on the task (0 = no steps; row shows "N/M" when non-zero).</summary>
    public int StepsTotal { get; init; }

    /// <summary>Checked step count.</summary>
    public int StepsDone { get; init; }

    /// <summary>Creation instant (排序依据/创建日期 sorts on it).</summary>
    public DateTimeOffset CreatedAt { get; init; }
}
