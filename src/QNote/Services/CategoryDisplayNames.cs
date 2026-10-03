namespace QNote.Services;

/// <summary>
/// Display-name resolution for the seeded built-in categories. The DB rows keep
/// their zh names as data identity (never migrated, never translated at the data
/// layer); the display layer resolves through resw on an exact name match —
/// 工作/生活/重要 ↔ Work/Life/Important — so built-ins read naturally in the
/// active language (zh-Hans resw values are the original zh names, byte-
/// identical to the data). A user-renamed row no longer exact-matches and
/// degrades to plain data — no special-casing. Mirrors the synthetic "全部"
/// display handling (<c>CategoryAll</c>) in <see cref="AppStrings"/>.
/// </summary>
internal static class CategoryDisplayNames
{
    private static readonly Dictionary<string, string> KeysByBuiltInName = new(StringComparer.Ordinal)
    {
        ["工作"] = "CategoryWorkName",
        ["生活"] = "CategoryLifeName",
        ["重要"] = "CategoryImportantName",
    };

    /// <summary>Localized display name for a built-in category; the name itself for anything else.</summary>
    public static string Resolve(string name) =>
        KeysByBuiltInName.TryGetValue(name, out var key) ? AppStrings.GetString(key) : name;
}
