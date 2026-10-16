namespace QNote.Services;

/// <summary>
/// Smart-view (今日/重要/计划内/全部) per-view state that has no DB row to live
/// on: theme, hide-completed, sort mode and the hidden-smart-lists set
/// (list-options menu, 2026-10-16). Stored as settings k/v CSV strings in the
/// shape <c>"kind=value;kind=value"</c> (kind = <c>(int)TodoNavKind</c> for the
/// first three; the hidden set is a plain kind CSV). Pure + WinUI-free so the
/// headless test project covers the parse/format contract.
/// </summary>
public static class TodoSmartViewOptions
{
    /// <summary>Parse "kind=value;..." into a map. Malformed pairs are skipped
    /// (settings strings are user-invisible but corruption-tolerant reads win).</summary>
    public static IReadOnlyDictionary<int, int> Parse(string? csv)
    {
        var map = new Dictionary<int, int>();
        if (string.IsNullOrWhiteSpace(csv))
            return map;
        foreach (var pair in csv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2
                && int.TryParse(parts[0], out var kind)
                && int.TryParse(parts[1], out var value))
            {
                map[kind] = value;
            }
        }
        return map;
    }

    /// <summary>Inverse of <see cref="Parse"/>; deterministic key order so the
    /// stored string only changes when the content does.</summary>
    public static string Format(IReadOnlyDictionary<int, int> map)
    {
        var parts = map.Keys.OrderBy(k => k).Select(k => $"{k}={map[k]}");
        return string.Join(";", parts);
    }

    /// <summary>Set or remove one entry and return the new CSV (absent kind =
    /// default 0 is stored as REMOVED — 0 is the implicit default everywhere).</summary>
    public static string WithEntry(string? csv, int kind, int value)
    {
        var map = new Dictionary<int, int>(Parse(csv));
        if (value == 0)
            map.Remove(kind);
        else
            map[kind] = value;
        return Format(map);
    }

    /// <summary>Parse the hidden-smart-lists kind CSV ("0,2").</summary>
    public static IReadOnlySet<int> ParseSet(string? csv)
    {
        var set = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(csv))
            return set;
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out var kind))
                set.Add(kind);
        }
        return set;
    }

    /// <summary>Inverse of <see cref="ParseSet"/>.</summary>
    public static string FormatSet(IEnumerable<int> kinds) =>
        string.Join(",", kinds.OrderBy(k => k).Select(k => k.ToString()));
}
