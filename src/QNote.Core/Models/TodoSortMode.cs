namespace QNote.Models;

/// <summary>
/// 排序依据 modes for a todo view (list-options menu, 2026-10-16). Persisted as
/// INTEGER on <c>todo_lists.SortMode</c> and in the smart-view settings CSV; 0
/// keeps the natural load order (manual = newest first, To Do parity).
/// </summary>
public enum TodoSortMode
{
    /// <summary>Natural order (no resort; newest-created first as loaded).</summary>
    Manual = 0,

    /// <summary>重要性: starred first, then due date, then created.</summary>
    Importance = 1,

    /// <summary>到期日期: earliest due first, no-due tasks last.</summary>
    DueDate = 2,

    /// <summary>字母顺序: title, culture-invariant ordinal.</summary>
    Title = 3,

    /// <summary>创建日期: oldest first.</summary>
    Created = 4,
}
