namespace QNote.Models;

/// <summary>
/// A todo task list (left sidebar "我的清单" entry). Tasks link to their list by
/// <see cref="TodoTask.ListUuid"/> (string link, like <c>notes.Category</c> — no FK).
/// Timestamps are stored as millisecond-epoch integers (schema v7 sync contract).
/// </summary>
public sealed record TodoList
{
    public long Id { get; init; }

    /// <summary>Stable sync identity, generated eagerly at creation.</summary>
    public required string Uuid { get; init; }

    public required string Name { get; init; }

    /// <summary>Accent color as <c>#RRGGBB</c>; empty = theme accent.</summary>
    public string Color { get; init; } = string.Empty;

    public int SortOrder { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>List-options menu (2026-10-16): B-pane background theme id
    /// (0 = the default app gradient; ids resolve in the app-layer catalog).</summary>
    public int Theme { get; init; }

    /// <summary>隐藏已完成的任务: completed tasks stay out of the middle pane
    /// (completing a task makes its row vanish instead of joining the tail).</summary>
    public bool HideCompleted { get; init; }

    /// <summary>排序依据 (TodoSortMode): 0 = manual/created order.</summary>
    public int SortMode { get; init; }
}
