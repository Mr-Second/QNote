namespace QNote.Services;

/// <summary>
/// Display-name resolution for the seeded built-in todo lists. The DB rows keep
/// their zh names as data identity (never migrated, never translated at the data
/// layer); the display layer resolves through resw on an exact name match —
/// 工作/生活 ↔ Work/Life — so the built-ins read naturally in the active
/// language. A user-renamed row no longer exact-matches and degrades to plain
/// data. Copied from <see cref="CategoryDisplayNames"/> (task 10-04 stage ②).
/// </summary>
internal static class TodoListDisplayNames
{
    private static readonly Dictionary<string, string> KeysByBuiltInName = new(StringComparer.Ordinal)
    {
        ["工作"] = "TodoListWorkName",
        ["生活"] = "TodoListLifeName",
    };

    /// <summary>Localized display name for a built-in list; the name itself for anything else.</summary>
    public static string Resolve(string name) =>
        KeysByBuiltInName.TryGetValue(name, out var key) ? AppStrings.GetString(key) : name;
}
