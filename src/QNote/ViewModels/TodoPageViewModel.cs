using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using QNote.Models;
using QNote.Services;

namespace QNote.ViewModels;

/// <summary>
/// The todo screen (three-pane) view-model: left-pane navigation (smart views +
/// lists with incomplete badges), the middle task list (incomplete + collapsed
/// completed tail), the on-demand detail pane, quick add, debounced title/notes
/// autosave, title search and the pending-delete undo window.
///
/// Deliberately string-free: localized display text is composed by the view
/// (<c>Converters.TodoText</c> over resw), and list-CRUD failures return resw
/// KEYS for the view to resolve. This keeps the VM headless-testable (it is
/// link-compiled into the Core-only test project — no WinUI types).
///
/// Mutations delegate to <see cref="ITodoService"/> and refresh list state in
/// place; every async continuation stays on the UI thread (DispatcherQueue
/// synchronization context), which is also single-threaded-safe under xUnit.
/// </summary>
public partial class TodoPageViewModel : ObservableObject
{
    private static readonly TimeSpan DefaultSearchDebounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan DefaultDetailSaveDebounce = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan DefaultUndoWindow = TimeSpan.FromSeconds(6);

    private readonly ITodoService _todo;
    private readonly ILogger<TodoPageViewModel> _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _searchDebounce;
    private readonly TimeSpan _detailSaveDebounce;
    private readonly TimeSpan _undoWindow;
    private readonly ISettingsService? _settings;   // smart-view options; null in tests → defaults

    private bool _initializing;
    private bool _reloading;
    private bool _loadingDetail;
    private bool _suppressSearchReload;
    private TodoTask? _detailTask;                 // full record behind the detail pane
    private string _snapshotTitle = string.Empty;  // 取消 restore point: Title at selection time
    private string _snapshotNotes = string.Empty;  // 取消 restore point: Notes at selection time
    private TodoTaskItemViewModel? _pendingDelete; // staged delete, undo window open
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _detailSaveCts;
    private IReadOnlyDictionary<int, int> _smartThemes = new Dictionary<int, int>();
    private IReadOnlyDictionary<int, int> _smartHideCompleted = new Dictionary<int, int>();
    private IReadOnlyDictionary<int, int> _smartSortModes = new Dictionary<int, int>();
    private IReadOnlySet<int> _hiddenSmartKinds = new HashSet<int>();

    public TodoPageViewModel(
        ITodoService todo,
        ILogger<TodoPageViewModel> log,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? searchDebounce = null,
        TimeSpan? detailSaveDebounce = null,
        TimeSpan? undoWindow = null,
        ISettingsService? settings = null)
    {
        _todo = todo;
        _log = log;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        _searchDebounce = searchDebounce ?? DefaultSearchDebounce;
        _detailSaveDebounce = detailSaveDebounce ?? DefaultDetailSaveDebounce;
        _undoWindow = undoWindow ?? DefaultUndoWindow;
        _settings = settings;
        // Live unhide while the page runs: the settings dialog's 待办 section
        // writes the hidden-kinds CSV — rebuild the nav when it changes.
        if (_settings is not null)
            _settings.Changed += OnSettingsChanged;

        Tasks.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasCompleted));
            OnPropertyChanged(nameof(ShowCompletedToggle));
            OnPropertyChanged(nameof(ShowCompletedList));
        };
        CompletedTasks.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasCompleted));
            OnPropertyChanged(nameof(ShowCompletedToggle));
            OnPropertyChanged(nameof(ShowCompletedList));
            OnPropertyChanged(nameof(CompletedCountText));
        };
    }

    // ---------- collections ----------

    public ObservableCollection<TodoNavItemViewModel> NavItems { get; } = new();

    /// <summary>Incomplete tasks of the current view (middle column, upper list).</summary>
    public ObservableCollection<TodoTaskItemViewModel> Tasks { get; } = new();

    /// <summary>Completed tasks of the current view (collapsed tail, a4 consensus).</summary>
    public ObservableCollection<TodoTaskItemViewModel> CompletedTasks { get; } = new();

    // ---------- selection & state ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroTitleKey))]
    [NotifyPropertyChangedFor(nameof(HeroTitleRaw))]
    [NotifyPropertyChangedFor(nameof(IsTodayView))]
    public partial TodoNavItemViewModel? SelectedNav { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTask))]
    public partial TodoTaskItemViewModel? SelectedTask { get; set; }

    [ObservableProperty]
    public partial string QuickAddTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Collapsed/expanded state of the 已完成 tail.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCompletedList))]
    public partial bool IsCompletedExpanded { get; set; }

    // ---------- detail pane (edit state, TwoWay from the pane) ----------

    [ObservableProperty]
    public partial string DetailTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetailNotes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool DetailReminderOn { get; set; }

    [ObservableProperty]
    public partial bool DetailHasDue { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? DetailDueAt { get; set; }

    [ObservableProperty]
    public partial bool DetailIsAllDay { get; set; }

    [ObservableProperty]
    public partial bool DetailIsImportant { get; set; }

    /// <summary>Completion state of the selected task (drives the detail header checkbox).</summary>
    [ObservableProperty]
    public partial bool DetailIsCompleted { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset DetailCreatedAt { get; set; }

    // ---------- detail children (stage ⑤: steps / recurrence / attachments) ----------

    /// <summary>Steps of the selected task, ordered (detail pane list).</summary>
    public ObservableCollection<TodoStepItemViewModel> DetailSteps { get; } = new();

    /// <summary>Attachments of the selected task, newest-first (detail pane list).</summary>
    public ObservableCollection<TodoAttachment> DetailAttachments { get; } = new();

    /// <summary>Recurrence rule of the selected task; null = 从不.</summary>
    [ObservableProperty]
    public partial string? DetailRecurrenceRule { get; set; }

    /// <summary>重复 row trailing-✕ state (a rule is set; To Do's ClearButton).</summary>
    [ObservableProperty]
    public partial bool DetailHasRecurrence { get; set; }

    /// <summary>The inline 添加步骤 box text (cleared on Enter-add).</summary>
    [ObservableProperty]
    public partial string StepInput { get; set; } = string.Empty;

    /// <summary>Due-edit flyout: picked date (local; DatePicker is DateTimeOffset).</summary>
    [ObservableProperty]
    public partial DateTimeOffset DetailDueDate { get; set; }

    /// <summary>Due-edit flyout: picked time (hidden when all-day; non-nullable — TimePicker contract).</summary>
    [ObservableProperty]
    public partial TimeSpan DetailDueTime { get; set; } = new(9, 0, 0);

    // ---------- computed ----------

    public bool HasSelectedTask => SelectedTask is not null;

    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    public bool IsTodayView => SelectedNav?.Kind == TodoNavKind.Today;

    public bool HasCompleted => CompletedTasks.Count > 0;

    public bool ShowCompletedToggle => !IsSearching && CompletedTasks.Count > 0;

    public bool ShowCompletedList => ShowCompletedToggle && IsCompletedExpanded;

    // ---------- list-options menu state (list-options ⋯ menu, 2026-10-16) ----------

    /// <summary>High-Contrast flag, pushed by the page (AccessibilitySettings
    /// is WinRT — the VM stays WinUI-free). Suppresses the themed pane overlay.</summary>
    [ObservableProperty]
    public partial bool IsHighContrast { get; set; }

    /// <summary>Active B-pane theme id (0 = default gradient). Lists read the
    /// DB row; smart views the settings CSV. Null-safe: the x:Bind functions
    /// evaluate during page construction, BEFORE any nav item exists.</summary>
    public int ActiveThemeId => SelectedNav is not { } nav ? 0 : nav.Kind switch
    {
        TodoNavKind.List => nav.List?.Theme ?? 0,
        TodoNavKind.ListsCap => 0,
        _ => _smartThemes.TryGetValue((int)nav.Kind, out var v) ? v : 0,
    };

    /// <summary>隐藏已完成的任务 state of the current view.</summary>
    public bool ActiveHideCompleted => SelectedNav is not { } nav ? false : nav.Kind switch
    {
        TodoNavKind.List => nav.List?.HideCompleted ?? false,
        TodoNavKind.ListsCap => false,
        _ => _smartHideCompleted.TryGetValue((int)nav.Kind, out var v) && v != 0,
    };

    /// <summary>排序依据 mode of the current view (TodoSortMode).</summary>
    public int ActiveSortMode => SelectedNav is not { } nav ? 0 : nav.Kind switch
    {
        TodoNavKind.List => nav.List?.SortMode ?? 0,
        TodoNavKind.ListsCap => 0,
        _ => _smartSortModes.TryGetValue((int)nav.Kind, out var v) ? v : 0,
    };

    /// <summary>隐藏智能列表 is offered on hideable smart views only — 全部 is
    /// the fallback view and never hideable (a dead-end nav is worse than parity).</summary>
    public bool CanHideCurrentSmartList =>
        SelectedNav?.Kind is TodoNavKind.Today or TodoNavKind.Important or TodoNavKind.Planned;

    /// <summary>The currently hidden hideable smart views (drives the 显示智能列表
    /// row's visibility and its restore-all action; 全部 never appears here).</summary>
    public IReadOnlyList<TodoNavKind> HiddenSmartKinds =>
        _hiddenSmartKinds.Where(k => k is >= (int)TodoNavKind.Today and <= (int)TodoNavKind.Planned)
            .Select(k => (TodoNavKind)k)
            .OrderBy(k => k)
            .ToList();

    private void RaiseViewOptions()
    {
        OnPropertyChanged(nameof(ActiveThemeId));
        OnPropertyChanged(nameof(ActiveHideCompleted));
        OnPropertyChanged(nameof(ActiveSortMode));
        OnPropertyChanged(nameof(CanHideCurrentSmartList));
        OnPropertyChanged(nameof(HiddenSmartKinds));
    }

    private async Task LoadViewOptionsAsync()
    {
        if (_settings is null)
            return;
        try
        {
            var snapshot = await _settings.LoadAsync();
            _smartThemes = TodoSmartViewOptions.Parse(snapshot.TodoSmartThemes);
            _smartHideCompleted = TodoSmartViewOptions.Parse(snapshot.TodoSmartHideCompleted);
            _smartSortModes = TodoSmartViewOptions.Parse(snapshot.TodoSmartSortModes);
            _hiddenSmartKinds = TodoSmartViewOptions.ParseSet(snapshot.TodoHiddenSmartLists);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load the todo smart-view options.");
        }
    }

    private async Task MutateSettingsAsync(Func<AppSettings, AppSettings> transform)
    {
        if (_settings is null)
            return;
        var snapshot = await _settings.LoadAsync();
        await _settings.SaveAsync(transform(snapshot));
    }

    /// <summary>主题 pick: lists persist on their DB row, smart views on the settings CSV.</summary>
    public async Task SetCurrentThemeAsync(int themeId)
    {
        var nav = SelectedNav;
        if (nav is null)
            return;
        try
        {
            if (nav.Kind == TodoNavKind.List)
            {
                if (nav.List is not { } list)
                    return;
                if (await _todo.UpdateListAsync(list with { Theme = themeId }) is not null)
                    await RefreshListsAsync();
            }
            else if (nav.Kind != TodoNavKind.ListsCap)
            {
                await MutateSettingsAsync(s => s with
                {
                    TodoSmartThemes = TodoSmartViewOptions.WithEntry(s.TodoSmartThemes, (int)nav.Kind, themeId),
                });
                await LoadViewOptionsAsync();
            }
            RaiseViewOptions();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to set the todo view theme {Theme}.", themeId);
        }
    }

    /// <summary>隐藏已完成的任务 toggle: reload — the tail and the completion
    /// vanish behavior both follow this state.</summary>
    public async Task SetCurrentHideCompletedAsync(bool hideCompleted)
    {
        var nav = SelectedNav;
        if (nav is null || nav.Kind == TodoNavKind.ListsCap)
            return;
        try
        {
            if (nav.Kind == TodoNavKind.List)
            {
                if (nav.List is not { } list)
                    return;
                if (await _todo.UpdateListAsync(list with { HideCompleted = hideCompleted }) is not null)
                    await RefreshListsAsync();
            }
            else
            {
                await MutateSettingsAsync(s => s with
                {
                    TodoSmartHideCompleted = TodoSmartViewOptions.WithEntry(
                        s.TodoSmartHideCompleted, (int)nav.Kind, hideCompleted ? 1 : 0),
                });
                await LoadViewOptionsAsync();
            }
            RaiseViewOptions();
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to toggle hide-completed for the todo view.");
        }
    }

    /// <summary>排序依据 pick: resort the live collection (no reload).</summary>
    public async Task SetCurrentSortModeAsync(int sortMode)
    {
        var nav = SelectedNav;
        if (nav is null || nav.Kind == TodoNavKind.ListsCap)
            return;
        try
        {
            if (nav.Kind == TodoNavKind.List)
            {
                if (nav.List is not { } list)
                    return;
                if (await _todo.UpdateListAsync(list with { SortMode = sortMode }) is not null)
                    await RefreshListsAsync();
            }
            else
            {
                await MutateSettingsAsync(s => s with
                {
                    TodoSmartSortModes = TodoSmartViewOptions.WithEntry(s.TodoSmartSortModes, (int)nav.Kind, sortMode),
                });
                await LoadViewOptionsAsync();
            }
            RaiseViewOptions();
            TodoTaskSorter.Apply(Tasks, (TodoSortMode)Math.Clamp(sortMode, 0, 4));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to set the todo sort mode {Mode}.", sortMode);
        }
    }

    /// <summary>隐藏智能列表: stash the kind in the settings CSV, move to the
    /// fallback view and rebuild the nav.</summary>
    public async Task HideCurrentSmartListAsync()
    {
        var nav = SelectedNav;
        if (nav is not { Kind: TodoNavKind.Today or TodoNavKind.Important or TodoNavKind.Planned })
            return;
        try
        {
            var set = new HashSet<int>(_hiddenSmartKinds) { (int)nav.Kind };
            await MutateSettingsAsync(s => s with { TodoHiddenSmartLists = TodoSmartViewOptions.FormatSet(set) });
            _hiddenSmartKinds = set;
            // OnSelectedNavChanged reloads the new view; nav rebuild keeps ids.
            SelectedNav = NavItems.FirstOrDefault(n => n.Kind == TodoNavKind.Today && n != nav)
                ?? NavItems.FirstOrDefault(n => n.Kind == TodoNavKind.All);
            await RefreshListsAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to hide the smart list {Kind}.", nav.Kind);
        }
    }

    /// <summary>Badge text for the 已完成 toggle.</summary>
    public string CompletedCountText => CompletedTasks.Count.ToString();

    /// <summary>resw key for the 重复 row's 无 value (resolved at display time).</summary>
    public string NoneValueKey => "TodoNoneValue";

    /// <summary>Flat, null-safe hero title source (x:Bind nested-path rule).</summary>
    public string HeroTitleKey => SelectedNav?.TitleKey ?? string.Empty;

    /// <summary>Flat, null-safe hero title source (x:Bind nested-path rule).</summary>
    public string HeroTitleRaw => SelectedNav?.Title ?? string.Empty;

    // ---------- events for the view ----------

    /// <summary>Raised with the task title when a delete is staged — the view shows the undo toast.</summary>
    public event Action<string>? TaskDeleted;

    // ---------- lifecycle ----------

    /// <summary>
    /// Page teardown (Unloaded — re-navigation discards page+VM on every
    /// language/theme-mode switch): cancel the search debounce, persist any
    /// pending detail edits and commit a staged delete. Fire-and-forget: the
    /// DB writes complete on the UI thread after the page is gone.
    /// </summary>
    public void Detach()
    {
        _searchCts?.Cancel();
        if (_settings is not null)
            _settings.Changed -= OnSettingsChanged;
        _ = FlushAsync();
    }

    private async void OnSettingsChanged(AppSettings snapshot)
    {
        // Only the hidden-set concerns the live nav; other settings saves are noise.
        var hidden = TodoSmartViewOptions.ParseSet(snapshot.TodoHiddenSmartLists);
        if (hidden.SetEquals(_hiddenSmartKinds))
            return;
        _hiddenSmartKinds = hidden;
        OnPropertyChanged(nameof(HiddenSmartKinds));
        await RefreshListsAsync();
    }

    /// <summary>显示智能列表 (⋯ menu on 全部, 2026-10-16): restore every hidden
    /// hideable smart view at once; individual control stays in the settings
    /// dialog's 待办 section.</summary>
    public async Task RestoreAllSmartListsAsync()
    {
        if (_settings is null || HiddenSmartKinds.Count == 0)
            return;
        try
        {
            var set = new HashSet<int>(TodoSmartViewOptions.ParseSet(
                (await _settings.LoadAsync()).TodoHiddenSmartLists));
            set.Remove((int)TodoNavKind.Today);
            set.Remove((int)TodoNavKind.Important);
            set.Remove((int)TodoNavKind.Planned);
            await MutateSettingsAsync(s => s with { TodoHiddenSmartLists = TodoSmartViewOptions.FormatSet(set) });
            // SettingsService.Changed already rebuilt the nav; this raise keeps
            // the ⋯ row (which collapses on empty) in sync even if it didn't fire.
            await RefreshListsAsync();
            OnPropertyChanged(nameof(HiddenSmartKinds));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to restore the hidden smart lists.");
        }
    }

    /// <summary>Persist pending state: detail edits + a staged delete (mode switch / language switch / close).</summary>
    public async Task FlushAsync()
    {
        _searchCts?.Cancel();
        await SaveDetailNowAsync();
        await CommitPendingDeleteAsync();
    }

    /// <summary>Initial load: lists + badges, then the 今日 view. Called from the page's Loaded event.</summary>
    public async Task LoadAsync()
    {
        try
        {
            _initializing = true;
            await LoadViewOptionsAsync();
            await RefreshListsAsync();
            // 今日 stays the default; a hidden 今日 falls back to the first
            // visible smart view (hidden-smart-lists set, list-options menu).
            SelectedNav = NavItems.FirstOrDefault(n => n.Kind == TodoNavKind.Today)
                ?? NavItems.FirstOrDefault(n => n.Kind is not (TodoNavKind.List or TodoNavKind.ListsCap));
            _initializing = false;
            RaiseViewOptions();
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _initializing = false;
            _log.LogError(ex, "Failed to load todo data.");
        }
    }

    /// <summary>Drop all cached state and reload from disk (a restore swapped the DB).</summary>
    public async Task ReloadAfterRestoreAsync()
    {
        _suppressSearchReload = true;
        SearchText = string.Empty;
        QuickAddTitle = string.Empty;
        _suppressSearchReload = false;
        SelectedTask = null;
        ClearDetail();
        await LoadAsync();
    }

    // ---------- navigation ----------

    public void SelectNav(TodoNavItemViewModel item) => SelectedNav = item;

    /// <summary>Secondary-tile deep link (固定到开始): select a user list by
    /// uuid. The reload rides OnSelectedNavChanged.</summary>
    public void SelectListByUuid(string listUuid)
    {
        if (NavItems.FirstOrDefault(n => n.Kind == TodoNavKind.List
                && string.Equals(n.Id, listUuid, StringComparison.Ordinal)) is { } nav)
        {
            SelectedNav = nav;
        }
    }

    // ---------- toast deep link (task 10-04 stage ③) ----------

    /// <summary>
    /// Select one task by uuid (reminder toast click-through) so the detail
    /// pane opens on it. Falls back to the 全部 view — the widest one — when
    /// the current view does not show the task (a filtered smart view, an
    /// active search). Unknown uuids are a silent no-op (the task may have
    /// been deleted since the toast was shown).
    /// </summary>
    public async Task SelectTaskByUuidAsync(string taskUuid)
    {
        try
        {
            var item = FindItemByUuid(taskUuid);
            if (item is null)
            {
                var all = NavItems.FirstOrDefault(n => n.Kind == TodoNavKind.All);
                if (all is null)
                    return; // lists not loaded yet (initial load still in flight)

                // Browsing wins over search (same rule as OnSelectedNavChanged).
                if (IsSearching)
                {
                    _suppressSearchReload = true;
                    SearchText = string.Empty;
                    _suppressSearchReload = false;
                    OnPropertyChanged(nameof(IsSearching));
                }
                SelectedNav = all;
                await ReloadAsync();
                item = FindItemByUuid(taskUuid);
            }

            if (item is not null)
                SelectedTask = item; // opens the detail pane via OnSelectedTaskChanged
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Toast deep-link task selection failed for {Uuid} (best-effort).", taskUuid);
        }
    }

    private TodoTaskItemViewModel? FindItemByUuid(string taskUuid) =>
        Tasks.FirstOrDefault(t => string.Equals(t.Uuid, taskUuid, StringComparison.Ordinal))
        ?? CompletedTasks.FirstOrDefault(t => string.Equals(t.Uuid, taskUuid, StringComparison.Ordinal));

    partial void OnSelectedNavChanged(TodoNavItemViewModel? value)
    {
        SyncNavSelection();
        RaiseViewOptions();
        if (_initializing)
            return;
        // Browsing wins over search: clear the box (guarded, no reload echo), then reload.
        if (IsSearching)
        {
            _suppressSearchReload = true;
            SearchText = string.Empty;
            _suppressSearchReload = false;
            OnPropertyChanged(nameof(IsSearching));
        }
        _ = ReloadAsync();
    }

    private void SyncNavSelection()
    {
        foreach (var n in NavItems)
            n.IsSelected = n == SelectedNav;
    }

    private Task<IReadOnlyList<TodoTaskSummary>> QueryCurrentViewAsync() => SelectedNav?.Kind switch
    {
        // 隐藏已完成的任务 (list-options menu): the view keeps its completed
        // tail only when the per-view option is OFF.
        TodoNavKind.Important => _todo.GetImportantAsync(includeCompleted: !ActiveHideCompleted),
        TodoNavKind.Planned => _todo.GetPlannedAsync(includeCompleted: !ActiveHideCompleted),
        TodoNavKind.All => _todo.GetAllAsync(includeCompleted: !ActiveHideCompleted),
        TodoNavKind.List => _todo.GetByListAsync(SelectedNav.Id, includeCompleted: !ActiveHideCompleted),
        _ => _todo.GetTodayAsync(includeCompleted: !ActiveHideCompleted),
    };

    /// <summary>Reload the middle column for the current view and refresh the badges.</summary>
    public async Task ReloadAsync()
    {
        try
        {
            _reloading = true;
            var selectedId = SelectedTask?.Id;
            var summaries = await QueryCurrentViewAsync();

            Tasks.Clear();
            CompletedTasks.Clear();
            foreach (var s in summaries)
                (s.CompletedAt is null ? Tasks : CompletedTasks).Add(CreateItem(s));
            TodoTaskSorter.Apply(Tasks, (TodoSortMode)Math.Clamp(ActiveSortMode, 0, 4));

            SelectedTask = selectedId is { } id && FindItem(id) is { } still ? still : null;
            _reloading = false;

            await RefreshCountsAsync();
        }
        catch (Exception ex)
        {
            _reloading = false;
            _log.LogError(ex, "Failed to reload the task list.");
        }
    }

    private async Task RefreshCountsAsync()
    {
        try
        {
            var counts = await _todo.GetViewCountsAsync();
            var listCounts = await _todo.CountIncompleteByListAsync();
            foreach (var n in NavItems)
            {
                n.IncompleteCount = n.Kind switch
                {
                    TodoNavKind.Today => counts.Today,
                    TodoNavKind.Important => counts.Important,
                    TodoNavKind.Planned => counts.Planned,
                    TodoNavKind.All => counts.All,
                    TodoNavKind.List => listCounts.TryGetValue(n.Id, out var c) ? c : 0,
                    _ => 0,
                };
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to refresh the todo badge counts.");
        }
    }

    private async Task RefreshListsAsync()
    {
        var lists = await _todo.GetListsAsync();
        var viewCounts = await _todo.GetViewCountsAsync();
        var listCounts = await _todo.CountIncompleteByListAsync();

        var selectedId = SelectedNav?.Id;
        NavItems.Clear();
        // 隐藏智能列表 (list-options menu): hidden kinds leave the nav (restore
        // via the settings dialog's 待办 section — writes the same settings CSV).
        if (!_hiddenSmartKinds.Contains((int)TodoNavKind.Today))
            NavItems.Add(TodoNavItemViewModel.CreateSmart(TodoNavKind.Today, "TodoNavToday", "\uE823"));
        if (!_hiddenSmartKinds.Contains((int)TodoNavKind.Important))
            NavItems.Add(TodoNavItemViewModel.CreateSmart(TodoNavKind.Important, "TodoNavImportant", "\uE735"));
        if (!_hiddenSmartKinds.Contains((int)TodoNavKind.Planned))
            NavItems.Add(TodoNavItemViewModel.CreateSmart(TodoNavKind.Planned, "TodoNavPlanned", "\uE787"));
        if (!_hiddenSmartKinds.Contains((int)TodoNavKind.All))
            NavItems.Add(TodoNavItemViewModel.CreateSmart(TodoNavKind.All, "TodoNavAll", "\uE8FD"));
        NavItems.Add(TodoNavItemViewModel.CreateCap("TodoListsGroupCap"));
        foreach (var list in lists)
            NavItems.Add(new TodoNavItemViewModel(list)
            {
                IncompleteCount = listCounts.TryGetValue(list.Uuid, out var c) ? c : 0,
            });
        foreach (var n in NavItems.Where(n => n.Kind is TodoNavKind.Today or TodoNavKind.Important or TodoNavKind.Planned or TodoNavKind.All))
        {
            n.IncompleteCount = n.Kind switch
            {
                TodoNavKind.Today => viewCounts.Today,
                TodoNavKind.Important => viewCounts.Important,
                TodoNavKind.Planned => viewCounts.Planned,
                _ => viewCounts.All,
            };
        }

        SelectedNav = NavItems.FirstOrDefault(n => n.Id == selectedId) ?? NavItems[0];
        SyncNavSelection();
    }

    private TodoTaskItemViewModel CreateItem(TodoTaskSummary summary) => new(summary, _clock);

    private TodoTaskItemViewModel? FindItem(long id) =>
        Tasks.FirstOrDefault(t => t.Id == id) ?? CompletedTasks.FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// Re-raise brush-feeding properties on every card after a live theme flip
    /// (x:Bind functions do not re-run when only the theme changed).
    /// </summary>
    public void RefreshTheme()
    {
        foreach (var t in Tasks)
            t.RefreshThemeBrushes();
        foreach (var t in CompletedTasks)
            t.RefreshThemeBrushes();
    }

    // ---------- search ----------

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(ShowCompletedToggle));
        OnPropertyChanged(nameof(ShowCompletedList));
        if (_suppressSearchReload)
            return;

        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        if (string.IsNullOrWhiteSpace(value))
            _ = ReloadAfterSearchClearAsync(cts.Token);
        else
            _ = DebouncedSearchAsync(value, cts.Token);
    }

    private async Task ReloadAfterSearchClearAsync(CancellationToken ct)
    {
        try
        {
            if (_searchDebounce > TimeSpan.Zero)
                await Task.Delay(_searchDebounce, ct);
            if (!ct.IsCancellationRequested)
                await ReloadAsync();
        }
        catch (OperationCanceledException)
        {
            // debouncer cancelled this reload — expected control flow
        }
    }

    private async Task DebouncedSearchAsync(string query, CancellationToken ct)
    {
        try
        {
            await Task.Delay(_searchDebounce, ct);
            var results = await _todo.SearchByTitleAsync(query, ct);
            if (ct.IsCancellationRequested)
                return;

            _reloading = true;
            var selectedId = SelectedTask?.Id;
            Tasks.Clear();
            CompletedTasks.Clear();
            foreach (var s in results)
                Tasks.Add(CreateItem(s)); // search covers incomplete tasks only
            SelectedTask = selectedId is { } id && Tasks.FirstOrDefault(t => t.Id == id) is { } still ? still : null;
            _reloading = false;
        }
        catch (OperationCanceledException)
        {
            // debouncer cancelled this query — expected control flow
        }
        catch (Exception ex)
        {
            _reloading = false;
            _log.LogError(ex, "Task search failed.");
        }
    }

    // ---------- quick add ----------

    [RelayCommand]
    public async Task QuickAddAsync()
    {
        var title = QuickAddTitle.Trim();
        if (title.Length == 0)
            return;
        var listUuid = SelectedNav is { Kind: TodoNavKind.List } list
            ? list.Id
            : NavItems.FirstOrDefault(n => n.Kind == TodoNavKind.List)?.Id;
        if (listUuid is null)
            return; // every list was deleted — nowhere to add (seeded lists make this a corner case)

        try
        {
            var created = await _todo.CreateTaskAsync(listUuid, title);
            // Adding from the 今日 view means "a task for today" (user
            // 2026-10-15: it was confusing when the new task vanished into 全部).
            // All-day due ⇒ the reminder stays OFF by default (Decision 4), so
            // this never schedules a surprise toast.
            if (IsTodayView)
            {
                var local = DateTime.SpecifyKind(_clock().ToLocalTime().Date, DateTimeKind.Unspecified);
                var dueUtc = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
                await _todo.SetDueAsync(created.Id, dueUtc, isAllDay: true);
            }
            // Same view-context rule for 重要 (user ruling 2026-10-15): adding
            // from the starred view stars the task (To Do's flagged-view behavior).
            if (SelectedNav?.Kind == TodoNavKind.Important)
                await _todo.SetImportantAsync(created.Id, isImportant: true);
            QuickAddTitle = string.Empty; // focus stays in the box for continuous entry
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to create a task.");
        }
    }

    // ---------- task mutations (independent click targets) ----------

    public async Task ToggleCompleteAsync(TodoTaskItemViewModel item)
    {
        try
        {
            var updated = item.IsCompleted
                ? await _todo.UncompleteTaskAsync(item.Id)
                : await _todo.CompleteTaskAsync(item.Id);
            if (updated is null)
                return;

            item.Apply(updated);
            var nowCompleted = updated.CompletedAt is not null;
            // 隐藏已完成的任务: a completion in such a view makes the row
            // VANISH (real To Do behavior) instead of joining the tail.
            if (nowCompleted && Tasks.Remove(item))
            {
                if (!ActiveHideCompleted)
                    CompletedTasks.Add(item);
            }
            else if (!nowCompleted && CompletedTasks.Remove(item))
                Tasks.Insert(0, item);

            await RefreshCountsAsync();
            if (SelectedTask == item)
                ApplyDetailFrom(updated);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to toggle completion for task {Id}.", item.Id);
        }
    }

    public async Task ToggleImportantAsync(TodoTaskItemViewModel item)
    {
        try
        {
            var updated = await _todo.SetImportantAsync(item.Id, !item.IsImportant);
            if (updated is null)
                return;
            item.Apply(updated);
            await RefreshCountsAsync();
            if (SelectedTask == item)
                ApplyDetailFrom(updated);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to toggle importance for task {Id}.", item.Id);
        }
    }

    // ---------- detail pane ----------

    partial void OnSelectedTaskChanged(TodoTaskItemViewModel? value)
    {
        if (_reloading)
            return; // list surgery during a reload — selection state is re-pinned right after
        _ = LoadDetailAsync(value);
    }

    private async Task LoadDetailAsync(TodoTaskItemViewModel? item)
    {
        try
        {
            await SaveDetailNowAsync(); // flush the previous task's pending edits
            if (item is null)
            {
                ClearDetail();
                return;
            }

            var detail = await _todo.GetTaskDetailAsync(item.Id);
            if (SelectedTask != item)
                return; // selection moved on while the fetch was in flight
            if (detail is null)
            {
                ClearDetail();
                return;
            }
            ApplyDetailFrom(detail.Task);
            LoadDetailText(detail.Task);
            LoadDetailChildren(detail);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load the task detail.");
        }
    }

    /// <summary>Selection-load path: replace the steps + attachments collections.</summary>
    private void LoadDetailChildren(TodoTaskDetail detail)
    {
        DetailSteps.Clear();
        foreach (var step in detail.Steps)
            DetailSteps.Add(new(step));
        DetailAttachments.Clear();
        foreach (var attachment in detail.Attachments)
            DetailAttachments.Add(attachment);
    }

    /// <summary>
    /// Refresh the detail pane's STATE fields (due / reminder / star / completion)
    /// from a mutation result. Title/Notes are deliberately NOT touched: an
    /// instant-applied toggle must never wipe in-flight debounced text edits, and
    /// the selection-time 取消 snapshot must stay intact (see <see cref="LoadDetailText"/>).
    /// </summary>
    private void ApplyDetailFrom(TodoTask task)
    {
        _loadingDetail = true;
        _detailTask = task;

        DetailReminderOn = task.ReminderOn && task.DueAt is not null;
        DetailHasDue = task.DueAt is not null;
        DetailDueAt = task.DueAt;
        DetailIsAllDay = task.IsAllDay;
        DetailIsImportant = task.IsImportant;
        DetailIsCompleted = task.CompletedAt is not null;
        DetailCreatedAt = task.CreatedAt;
        DetailRecurrenceRule = task.RecurrenceRule;
        DetailHasRecurrence = task.RecurrenceRule is not null;

        var local = task.DueAt is { } due ? due.ToLocalTime() : _clock().ToLocalTime();
        DetailDueDate = new DateTimeOffset(local.Date);
        DetailDueTime = task.DueAt is { } d && !task.IsAllDay ? d.ToLocalTime().TimeOfDay : new TimeSpan(9, 0, 0);
        _loadingDetail = false;
    }

    /// <summary>
    /// Selection-load path only: put the fetched task's text into the editors and
    /// capture the 取消 snapshot (the values the task had when it was selected).
    /// Switching tasks re-runs this — the snapshot always tracks the CURRENT task.
    /// </summary>
    private void LoadDetailText(TodoTask task)
    {
        _loadingDetail = true;
        DetailTitle = task.Title;
        DetailNotes = task.Notes;
        _snapshotTitle = task.Title;
        _snapshotNotes = task.Notes;
        _loadingDetail = false;
    }

    private void ClearDetail()
    {
        _loadingDetail = true;
        _detailTask = null;
        _detailSaveCts?.Cancel();
        DetailTitle = string.Empty;
        DetailNotes = string.Empty;
        _snapshotTitle = string.Empty;
        _snapshotNotes = string.Empty;
        DetailReminderOn = false;
        DetailHasDue = false;
        DetailDueAt = null;
        DetailIsAllDay = false;
        DetailIsImportant = false;
        DetailIsCompleted = false;
        DetailCreatedAt = default;
        DetailRecurrenceRule = null;
        DetailHasRecurrence = false;
        DetailSteps.Clear();
        DetailAttachments.Clear();
        StepInput = string.Empty;
        DetailDueDate = _clock().ToLocalTime();
        DetailDueTime = new TimeSpan(9, 0, 0);
        _loadingDetail = false;
    }

    // ---------- detail edits (debounced autosave) ----------

    partial void OnDetailTitleChanged(string value)
    {
        if (!_loadingDetail)
            ScheduleDetailSave();
    }

    partial void OnDetailNotesChanged(string value)
    {
        if (!_loadingDetail)
            ScheduleDetailSave();
    }

    private void ScheduleDetailSave()
    {
        if (_detailTask is null)
            return;
        _detailSaveCts?.Cancel();
        var cts = _detailSaveCts = new CancellationTokenSource();
        _ = SaveDetailAfterDelayAsync(cts.Token);
    }

    private async Task SaveDetailAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            if (_detailSaveDebounce > TimeSpan.Zero)
                await Task.Delay(_detailSaveDebounce, ct);
            if (!ct.IsCancellationRequested)
                await SaveDetailNowAsync();
        }
        catch (OperationCanceledException)
        {
            // a newer edit superseded this save — expected control flow
        }
    }

    /// <summary>Persist the detail pane's title/notes if they differ from the loaded task.</summary>
    public async Task SaveDetailNowAsync()
    {
        _detailSaveCts?.Cancel();
        var task = _detailTask;
        if (task is null || _loadingDetail)
            return;
        var title = DetailTitle.Trim();
        if (title.Length == 0)
            return; // the service rejects blank titles — keep it pending
        if (title == task.Title && DetailNotes == task.Notes)
            return;

        try
        {
            var saved = await _todo.UpdateTaskAsync(task.Id, title, DetailNotes);
            if (saved is not null)
            {
                _detailTask = saved;
                FindItem(saved.Id)?.Apply(saved);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to save the task detail.");
        }
    }

    partial void OnDetailReminderOnChanged(bool value)
    {
        if (!_loadingDetail)
            _ = SetDetailReminderAsync(value);
    }

    /// <summary>提醒我 toggle / status-strip ✕. No-op without a due date (a reminder needs an instant).</summary>
    [RelayCommand]
    public async Task SetDetailReminderAsync(bool on)
    {
        var task = _detailTask;
        if (task is null || task.DueAt is null)
            return;
        try
        {
            var updated = await _todo.SetReminderAsync(task.Id, on);
            if (updated is null)
                return;
            _detailTask = updated;
            ApplyDetailFrom(updated);
            FindItem(updated.Id)?.Apply(updated);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to set the reminder for task {Id}.", task.Id);
        }
    }

    /// <summary>
    /// Quick preset (今天/明天/下周, real To Do's stage-1 popover): sets an
    /// ALL-DAY due date (all-day ⇒ reminder off by Decision 4 — a timed reminder
    /// goes through 提醒我 / 选择日期). 下周 (offset 7) = NEXT MONDAY, matching the
    /// real product's "下周 星期一 到期".
    /// </summary>
    [RelayCommand]
    public async Task ApplyDuePresetAsync(int dayOffset)
    {
        var task = _detailTask;
        if (task is null)
            return;
        try
        {
            var today = _clock().ToLocalTime().Date;
            var date = dayOffset == 7 ? NextMonday(today) : today.AddDays(dayOffset);
            var local = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
            var dueUtc = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
            var updated = await _todo.SetDueAsync(task.Id, dueUtc, isAllDay: true);
            if (updated is null)
                return;
            _detailTask = updated;
            ApplyDetailFrom(updated);
            FindItem(updated.Id)?.Apply(updated);
            await RefreshCountsAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to apply the due preset for task {Id}.", task.Id);
        }
    }

    /// <summary>Due-edit flyout's 确定: reads the picker state and applies the PRD Decision 4 default.</summary>
    [RelayCommand]
    public async Task ApplyDueEditAsync()
    {
        var task = _detailTask;
        if (task is null)
            return;
        try
        {
            var local = DateTime.SpecifyKind(
                DetailDueDate.LocalDateTime.Date + (DetailIsAllDay ? TimeSpan.Zero : DetailDueTime),
                DateTimeKind.Unspecified);
            var dueUtc = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
            var updated = await _todo.SetDueAsync(task.Id, dueUtc, DetailIsAllDay);
            if (updated is null)
                return;
            _detailTask = updated;
            ApplyDetailFrom(updated);
            FindItem(updated.Id)?.Apply(updated);
            await RefreshCountsAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to set the due date for task {Id}.", task.Id);
        }
    }

    /// <summary>
    /// Reminder stage-1 presets (real To Do): 0 = 今天晚些时候 (today 18:00),
    /// 1 = 明天 (9:00), 2 = 下周 (next Monday 9:00). Timed due ⇒ reminder on
    /// by Decision 4.
    /// </summary>
    [RelayCommand]
    public async Task ApplyReminderPresetAsync(int preset)
    {
        var task = _detailTask;
        if (task is null)
            return;
        try
        {
            var today = _clock().ToLocalTime().Date;
            var date = preset switch { 0 => today, 1 => today.AddDays(1), _ => NextMonday(today) };
            var time = preset == 0 ? new TimeSpan(18, 0, 0) : new TimeSpan(9, 0, 0);
            var local = DateTime.SpecifyKind(date + time, DateTimeKind.Unspecified);
            var dueUtc = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
            var updated = await _todo.SetDueAsync(task.Id, dueUtc, isAllDay: false);
            if (updated is null)
                return;
            _detailTask = updated;
            ApplyDetailFrom(updated);
            FindItem(updated.Id)?.Apply(updated);
            await RefreshCountsAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to apply the reminder preset for task {Id}.", task.Id);
        }
    }

    /// <summary>Due stage-2 保存: all-day due on the picked calendar date (real-To-Do due is date-only).</summary>
    [RelayCommand]
    public async Task ApplyDueDateAsync()
    {
        var task = _detailTask;
        if (task is null)
            return;
        try
        {
            var local = DateTime.SpecifyKind(DetailDueDate.LocalDateTime.Date, DateTimeKind.Unspecified);
            var dueUtc = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
            var updated = await _todo.SetDueAsync(task.Id, dueUtc, isAllDay: true);
            if (updated is null)
                return;
            _detailTask = updated;
            ApplyDetailFrom(updated);
            FindItem(updated.Id)?.Apply(updated);
            await RefreshCountsAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to set the due date for task {Id}.", task.Id);
        }
    }

    private static DateTime NextMonday(DateTime date)
    {
        var days = ((int)DayOfWeek.Monday - (int)date.DayOfWeek + 7) % 7;
        return date.AddDays(days == 0 ? 7 : days);
    }

    /// <summary>
    /// Stage-2 取消: discard unapplied picker edits by re-seeding the picker
    /// state from the loaded task (same seeding as <see cref="ApplyDetailFrom"/>).
    /// </summary>
    public void ResetDueEditState()
    {
        var task = _detailTask;
        _loadingDetail = true;
        if (task is { DueAt: { } due })
        {
            DetailIsAllDay = task.IsAllDay;
            DetailDueDate = new DateTimeOffset(due.ToLocalTime().Date);
            DetailDueTime = task.IsAllDay ? new TimeSpan(9, 0, 0) : due.ToLocalTime().TimeOfDay;
        }
        else
        {
            DetailIsAllDay = false;
            DetailDueDate = new DateTimeOffset(_clock().ToLocalTime().Date);
            DetailDueTime = new TimeSpan(9, 0, 0);
        }
        _loadingDetail = false;
    }

    [RelayCommand]
    public async Task ClearDueAsync()
    {
        var task = _detailTask;
        if (task is null)
            return;
        try
        {
            var updated = await _todo.SetDueAsync(task.Id, null, false);
            if (updated is null)
                return;
            _detailTask = updated;
            ApplyDetailFrom(updated);
            FindItem(updated.Id)?.Apply(updated);
            await RefreshCountsAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to clear the due date for task {Id}.", task.Id);
        }
    }

    [RelayCommand]
    public async Task ToggleDetailCompleteAsync()
    {
        if (SelectedTask is { } item)
            await ToggleCompleteAsync(item);
    }

    [RelayCommand]
    public async Task ToggleDetailImportantAsync()
    {
        if (SelectedTask is { } item)
            await ToggleImportantAsync(item);
    }

    // ---------- steps / recurrence / attachments (stage ⑤) ----------

    /// <summary>添加步骤 box Enter: append a step and keep the box focused.</summary>
    public async Task AddStepFromInputAsync()
    {
        var title = StepInput.Trim();
        if (title.Length == 0 || _detailTask is null)
            return;
        try
        {
            var created = await _todo.AddStepAsync(_detailTask.Id, title);
            StepInput = string.Empty;
            DetailSteps.Add(new(created));
            SyncRowSteps();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to add a step.");
        }
    }

    public async Task ToggleStepAsync(TodoStepItemViewModel step)
    {
        try
        {
            var updated = await _todo.SetStepCompletedAsync(step.Id, !step.IsCompleted);
            if (updated is null)
                return;
            step.IsCompleted = updated.CompletedAt is not null;
            SyncRowSteps();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to toggle step {Id}.", step.Id);
        }
    }

    public async Task DeleteStepAsync(TodoStepItemViewModel step)
    {
        try
        {
            await _todo.DeleteStepAsync(step.Id);
            DetailSteps.Remove(step);
            SyncRowSteps();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to delete step {Id}.", step.Id);
        }
    }

    /// <summary>重复 menu presets (real-To-Do stage-1 shape); <c>null</c> = 从不.</summary>
    public async Task SetRecurrenceAsync(string? rule)
    {
        var task = _detailTask;
        if (task is null)
            return;
        try
        {
            var updated = await _todo.SetRecurrenceAsync(task.Id, rule);
            if (updated is null)
                return;
            _detailTask = updated;
            ApplyDetailFrom(updated);
            FindItem(updated.Id)?.Apply(updated);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to set recurrence on task {Id}.", task.Id);
        }
    }

    /// <summary>View-side file picker hands each file over by name + content stream.</summary>
    public async Task AttachFileAsync(string fileName, Stream content)
    {
        var task = _detailTask;
        if (task is null)
            return;
        try
        {
            var created = await _todo.AddAttachmentAsync(task.Id, fileName, content);
            DetailAttachments.Insert(0, created);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to attach '{File}'.", fileName);
        }
    }

    public async Task DeleteAttachmentAsync(TodoAttachment attachment)
    {
        try
        {
            var (removed, _) = await _todo.DeleteAttachmentAsync(attachment.Id);
            if (removed is not null)
                DetailAttachments.Remove(attachment);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to delete attachment {Id}.", attachment.Id);
        }
    }

    /// <summary>Push the live step counts onto the selected row's N/M badge.</summary>
    private void SyncRowSteps()
    {
        if (SelectedTask is { } item)
            item.UpdateSteps(DetailSteps.Count, DetailSteps.Count(s => s.IsCompleted));
    }

    /// <summary>
    /// 保存 (footer button): flush pending debounced title/notes edits, then drop
    /// the selection — the pane hides.
    /// </summary>
    [RelayCommand]
    public async Task SaveDetailAsync()
    {
        await SaveDetailNowAsync();
        _detailTask = null;
        SelectedTask = null;
    }

    /// <summary>
    /// 取消 (footer button + Esc): drop any pending debounced save, restore the
    /// task's Title/Notes to the selection-time snapshot (through the service, so
    /// the repository + the list-card refresh run), then drop the selection.
    /// Instant-applied mutations (complete / star / reminder / due-date changes)
    /// are deliberate immediate actions and are NOT reverted by 取消.
    /// </summary>
    [RelayCommand]
    public async Task CancelDetailAsync()
    {
        _detailSaveCts?.Cancel(); // first — a pending debounced save would re-write the edited text
        var task = _detailTask;
        var title = DetailTitle.Trim();
        if (task is not null && (title != _snapshotTitle || DetailNotes != _snapshotNotes))
        {
            try
            {
                var restored = await _todo.UpdateTaskAsync(task.Id, _snapshotTitle, _snapshotNotes);
                if (restored is not null)
                    FindItem(restored.Id)?.Apply(restored); // the list card follows the restore
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to restore the task detail on cancel.");
            }
        }
        _detailTask = null;
        SelectedTask = null;
    }

    // ---------- delete with lightweight undo ----------

    /// <summary>
    /// Stage a delete: the item leaves the list immediately but the DB row is only
    /// removed after the undo window elapses — undo simply reloads (the row is
    /// still there). Only one delete may be staged at a time.
    /// </summary>
    [RelayCommand]
    public async Task DeleteTaskAsync(TodoTaskItemViewModel? item)
    {
        if (item is null)
            return; // footer button passes the (nullable) selected task
        try
        {
            await CommitPendingDeleteAsync(); // one undo window at a time
            if (SelectedTask == item)
            {
                await SaveDetailNowAsync();
                _detailTask = null;
                SelectedTask = null;
            }
            Tasks.Remove(item);
            CompletedTasks.Remove(item);
            _pendingDelete = item;
            TaskDeleted?.Invoke(item.Title);
            _ = CommitDeleteAfterWindowAsync(item);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to stage the task delete {Id}.", item.Id);
        }
    }

    /// <summary>Undo toast action: cancel the staged delete and restore the view from the DB.</summary>
    [RelayCommand]
    public async Task UndoDeleteAsync()
    {
        if (_pendingDelete is not { } item)
            return;
        _pendingDelete = null;
        await ReloadAsync();
    }

    private async Task CommitDeleteAfterWindowAsync(TodoTaskItemViewModel item)
    {
        try
        {
            await Task.Delay(_undoWindow);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_pendingDelete != item)
            return; // undone or superseded
        _pendingDelete = null;
        try
        {
            await _todo.DeleteTaskAsync(item.Id);
            await RefreshCountsAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to commit the task delete {Id}.", item.Id);
        }
    }

    /// <summary>Commit a staged delete immediately (flush / next-delete / page teardown).</summary>
    private async Task CommitPendingDeleteAsync()
    {
        var item = _pendingDelete;
        if (item is null)
            return;
        _pendingDelete = null;
        try
        {
            await _todo.DeleteTaskAsync(item.Id);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to commit the task delete {Id}.", item.Id);
        }
    }

    // ---------- list CRUD (dialogs live in the view) ----------

    /// <summary>Create a list; returns a resw KEY for the view to resolve on failure, else null.</summary>
    public async Task<string?> CreateListAsync(string name, string color)
    {
        try
        {
            var created = await _todo.CreateListAsync(name, color);
            await RefreshListsAsync();
            SelectedNav = NavItems.FirstOrDefault(n => n.Id == created.Uuid);
            await ReloadAsync();
            return null;
        }
        catch (ArgumentException)
        {
            return "TodoListNameRequired";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to create a todo list.");
            return "TodoCreateListFailed";
        }
    }

    /// <summary>Rename a list; returns a resw KEY on failure, else null.</summary>
    public async Task<string?> RenameListAsync(TodoNavItemViewModel item, string name)
    {
        if (item.List is not { } list)
            return "TodoUpdateListFailed";
        try
        {
            var updated = await _todo.UpdateListAsync(list with { Name = name });
            if (updated is null)
                return "TodoUpdateListFailed";
            await RefreshListsAsync();
            return null;
        }
        catch (ArgumentException)
        {
            return "TodoListNameRequired";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to rename todo list {Id}.", list.Id);
            return "TodoUpdateListFailed";
        }
    }

    /// <summary>Delete a list and its tasks; returns a resw KEY on failure, else null.</summary>
    public async Task<string?> DeleteListAsync(TodoNavItemViewModel item)
    {
        if (item.List is not { } list)
            return "TodoDeleteListFailed";
        try
        {
            await _todo.DeleteListAsync(list.Id);
            if (SelectedNav?.Id == item.Id)
                SelectedNav = NavItems.FirstOrDefault(n => n.Kind == TodoNavKind.Today);
            await RefreshListsAsync();
            await ReloadAsync();
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to delete todo list {Id}.", list.Id);
            return "TodoDeleteListFailed";
        }
    }
}
