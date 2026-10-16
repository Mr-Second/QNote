namespace QNote.Models;

/// <summary>
/// Incomplete counts for the todo smart views (sidebar badges; 计数 = 未完成数,
/// PRD §5). Computed by the repository in a single aggregate query.
/// </summary>
public sealed record TodoViewCounts
{
    /// <summary>今日: incomplete tasks due before the end of the local day (incl. overdue).</summary>
    public int Today { get; init; }

    /// <summary>重要: incomplete tasks carrying the star.</summary>
    public int Important { get; init; }

    /// <summary>计划内: incomplete tasks with any due date.</summary>
    public int Planned { get; init; }

    /// <summary>全部: every incomplete task across all lists.</summary>
    public int All { get; init; }
}
