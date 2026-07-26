namespace QNote.Models;

/// <summary>Which field drives the browse-mode note-list ordering.</summary>
public enum NoteSortOrder
{
    /// <summary>Recently updated first (Qt default).</summary>
    Updated = 0,

    /// <summary>Recently created first.</summary>
    Created = 1,

    /// <summary>Alphabetical by title (display title, current culture).</summary>
    Title = 2,
}

/// <summary>Timestamp style on the note-list card.</summary>
public enum NoteTimeFormat
{
    /// <summary>刚刚 / N分钟前 / N小时前 / N天前 / older → MM-dd.</summary>
    Relative = 0,

    /// <summary>Tiered (default): today → HH:mm, this year → MM-dd HH:mm, older → yyyy-MM-dd.</summary>
    Tiered = 1,

    /// <summary>Always yyyy-MM-dd HH:mm.</summary>
    Full = 2,
}

/// <summary>Note-list row density.</summary>
public enum NoteListDensity
{
    Compact = 0,
    Standard = 1,
    Comfortable = 2,
}

/// <summary>Search-results ordering — independent of the browse-mode <see cref="NoteSortOrder"/>.</summary>
public enum SearchSortOrder
{
    /// <summary>bm25 relevance, title ×10 (default).</summary>
    Relevance = 0,

    /// <summary>UpdatedAt DESC.</summary>
    NewestFirst = 1,

    /// <summary>UpdatedAt ASC.</summary>
    OldestFirst = 2,
}

/// <summary>
/// Typed snapshot of app settings. Persisted as key/value TEXT rows in the
/// <c>settings</c> table; enums serialize as their int value, bools as
/// "true"/"false" (defaults mirror the Qt SettingsManager).
/// </summary>
public sealed record AppSettings
{
    /// <summary>"system" | "light" | "dark".</summary>
    public string ThemeMode { get; init; } = "system";

    public bool AlwaysOnTop { get; init; }

    public bool LaunchAtStartup { get; init; }

    public bool RememberWindowGeometry { get; init; }

    /// <summary>Last window position (-1 = never persisted / centered by the OS).</summary>
    public int WindowX { get; init; } = -1;

    public int WindowY { get; init; } = -1;

    public int WindowWidth { get; init; } = 940;

    public int WindowHeight { get; init; } = 620;

    public NoteListDensity ListDensity { get; init; } = NoteListDensity.Standard;

    public NoteTimeFormat TimeFormat { get; init; } = NoteTimeFormat.Tiered;

    public NoteSortOrder NoteSortOrder { get; init; } = NoteSortOrder.Updated;

    public bool ConfirmBeforeDelete { get; init; } = true;

    public SearchSortOrder SearchSortOrder { get; init; } = SearchSortOrder.Relevance;
}
