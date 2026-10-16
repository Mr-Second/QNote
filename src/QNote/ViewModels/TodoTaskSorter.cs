using System.Collections.ObjectModel;
using QNote.Models;

namespace QNote.ViewModels;

/// <summary>
/// In-place 排序依据 (list-options menu, 2026-10-16) over the middle-column
/// collection. WinUI-free pure logic (link-compiled into the test project).
/// Manual mode keeps the load order (repo: incomplete first, then created
/// DESC — MS To Do parity); the other modes re-arrange via
/// <see cref="ObservableCollection{T}.Move"/> so item containers survive.
/// </summary>
public static class TodoTaskSorter
{
    public static void Apply(ObservableCollection<TodoTaskItemViewModel> items, TodoSortMode mode)
    {
        if (mode == TodoSortMode.Manual || items.Count < 2)
            return;

        var ordered = mode switch
        {
            // 重要性: starred first, then due (no-due last), then newest.
            TodoSortMode.Importance => items
                .OrderByDescending(t => t.IsImportant)
                .ThenBy(t => t.DueAt ?? DateTimeOffset.MaxValue)
                .ThenByDescending(t => t.CreatedAt),
            // 到期日期: earliest first; no-due tasks trail, newest first within ties.
            TodoSortMode.DueDate => items
                .OrderBy(t => t.DueAt ?? DateTimeOffset.MaxValue)
                .ThenByDescending(t => t.CreatedAt),
            // 字母顺序: culture-invariant ordinal; ties fall back to newest.
            TodoSortMode.Title => items
                .OrderBy(t => t.Title, StringComparer.InvariantCulture)
                .ThenByDescending(t => t.CreatedAt),
            // 创建日期: oldest first.
            TodoSortMode.Created => items
                .OrderBy(t => t.CreatedAt),
            _ => (IOrderedEnumerable<TodoTaskItemViewModel>)items.OrderBy(t => t.Id),
        };

        var snapshot = ordered.ToList();
        for (var i = 0; i < snapshot.Count; i++)
        {
            var from = items.IndexOf(snapshot[i]);
            if (from != i)
                items.Move(from, i);
        }
    }
}
