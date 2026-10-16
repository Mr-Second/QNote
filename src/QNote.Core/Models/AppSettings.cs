using QNote.EdgeHide;

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

    /// <summary>
    /// "system" | "zh" | "en" — UI language. "system" resolves per the OS
    /// (zh system → zh-Hans resources; everything else falls back to the neutral
    /// English file).
    /// </summary>
    public string LanguageMode { get; init; } = "system";

    public bool AlwaysOnTop { get; init; }

    // NOTE: launch-at-startup is deliberately NOT here — the OS StartupTask state is
    // the single source of truth (see IStartupTaskService). A stale "launchAtStartup"
    // row in an old DB is harmless (k/v table, ignored on load).

    public bool RememberWindowGeometry { get; init; }

    /// <summary>Launch straight to the tray without showing the main window.</summary>
    public bool StartMinimized { get; init; }

    /// <summary>
    /// Check GitHub Releases for a newer version shortly after startup and show
    /// the update dialog when one exists (1.5.1). Default ON; the dialog's
    /// 「不再提示」 checkbox turns this off (both channels — the Store build only
    /// ever links to the Store page, never an external download).
    /// </summary>
    public bool CheckUpdatesOnStartup { get; init; } = true;

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

    /// <summary>Idle debounce after which a dirty note auto-saves (milliseconds); 0 = off.</summary>
    public int AutoSaveMilliseconds { get; init; }

    /// <summary>Top-edge auto-hide master switch (Qt parity: edgeEnabled).</summary>
    public bool EdgeHideEnabled { get; init; }

    /// <summary>
    /// While edge-hidden, also remove the taskbar/Alt+Tab entry (Qt parity:
    /// hideTaskbarIconOnEdgeHide). Default ON — a hidden window should not keep a
    /// taskbar slot (user acceptance, 2026-09-25).
    /// </summary>
    public bool HideTaskbarIconOnEdgeHide { get; init; } = true;

    /// <summary>
    /// Edge-hide hotkey modifier mask (Win32 MOD_* bits, see <see cref="HotkeyFormat"/>;
    /// 0 = no modifier). Default: Win.
    /// </summary>
    public int EdgeHideHotkeyModifiers { get; init; } = HotkeyFormat.ModWin;

    /// <summary>
    /// Edge-hide hotkey virtual-key code (0 = manual hotkey disabled).
    /// Default: VK_OEM_3 (`).
    /// </summary>
    public int EdgeHideHotkeyKey { get; init; } = HotkeyFormat.VkOem3;

    /// <summary>Todo page nav-pane width in DIPs (0 = never dragged, XAML default).
    /// Persisted on splitter drag-completion; user 2026-10-15: mode switches
    /// recreate the page and used to lose the dragged widths.</summary>
    public int TodoNavPaneWidth { get; init; }

    /// <summary>Todo page detail-pane width in DIPs (0 = never dragged, XAML default).</summary>
    public int TodoDetailPaneWidth { get; init; }

    /// <summary>Smart-view B-pane theme CSV ("kind=themeId;..."), see
    /// <see cref="Services.TodoSmartViewOptions"/>. Empty/absent kind = default gradient.</summary>
    public string TodoSmartThemes { get; init; } = string.Empty;

    /// <summary>Smart-view 隐藏已完成的任务 CSV ("kind=1;...", see
    /// <see cref="Services.TodoSmartViewOptions"/>). Absent kind = show completed.</summary>
    public string TodoSmartHideCompleted { get; init; } = string.Empty;

    /// <summary>Smart-view 排序依据 CSV ("kind=sortMode;...", see
    /// <see cref="Services.TodoSmartViewOptions"/>). Absent kind = manual.</summary>
    public string TodoSmartSortModes { get; init; } = string.Empty;

    /// <summary>Hidden smart-lists kind CSV ("1,2", see
    /// <see cref="Services.TodoSmartViewOptions"/>). Empty = all visible.</summary>
    public string TodoHiddenSmartLists { get; init; } = string.Empty;
}
