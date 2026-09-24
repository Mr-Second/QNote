using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using QNote.Models;
using QNote.Services;
using QNote.Text;

namespace QNote.ViewModels;

/// <summary>
/// The notes screen (three-pane) view-model. Owns the summary list, the current
/// selection, and the editor data flow. The editor itself is a RichEditBox driven by
/// a view-side controller (the VM never touches UI types — mvvm-guidelines), so RTF
/// content crosses the boundary as plain strings: <see cref="EditingContentRtf"/>
/// carries a freshly loaded note's RTF to the view (via <see cref="ContentReloadRequested"/>),
/// and <see cref="EditorContentProvider"/> lets the VM pull current RTF/plain text from
/// the view at flush time. Save model: flush on navigate-away / close / Ctrl+S,
/// surfaced by <see cref="IsDirty"/>.
///
/// Search state lives here too: <see cref="SearchText"/> feeds a debounced query to
/// <see cref="ISearchService"/>; while searching, <see cref="Notes"/> mirrors the
/// ranked results (with <see cref="NoteItemViewModel.Keyword"/> set for highlight),
/// and clearing the box restores the full summary list.
/// </summary>
public partial class NotesPageViewModel : ObservableObject
{
    private const int PreviewLength = 120;
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(300);

    private readonly INoteService _notes;
    private readonly ISearchService _search;
    private readonly ICategoryService _categories;
    private readonly ISettingsService _settings;
    private readonly ILogger<NotesPageViewModel> _log;

    private Note? _loaded;        // full note currently shown in the editor
    private bool _suppressDirty;  // true while loading editor fields programmatically
    private bool _initializing;   // true during LoadAsync (category selection must not double-reload)
    private bool _loadingSettings; // true while applying a settings snapshot (no echo-save)

    private AppSettings _settingsSnapshot = new();

    private CancellationTokenSource? _searchCts;

    public NotesPageViewModel(INoteService notes, ISearchService search, ICategoryService categories, ISettingsService settings, ILogger<NotesPageViewModel> log)
    {
        _notes = notes;
        _search = search;
        _categories = categories;
        _settings = settings;
        _log = log;
        Notes.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(IsEmpty));
        };
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Raised when a freshly created note wants keyboard focus in the title box.</summary>
    public event Action? FocusTitleRequested;

    /// <summary>
    /// Raised after the editor fields were (re)loaded — the view should push
    /// <see cref="EditingContentRtf"/> into the RichEditBox.
    /// </summary>
    public event Action? ContentReloadRequested;

    /// <summary>
    /// Raised off the UI thread when a debounced search resolves. The view marshals
    /// back to the UI thread and replaces the note list with the ranked results.
    /// Carries the raw keyword (for highlight) and the summaries.
    /// </summary>
    public event Action<IReadOnlyList<NoteSummary>, string>? SearchCompleted;

    /// <summary>
    /// Raised off the UI thread when the search box is cleared. The view restores
    /// the full summary list.
    /// </summary>
    public event Action<IReadOnlyList<NoteSummary>>? SearchCleared;

    /// <summary>Editor content captured by the view for a flush.</summary>
    /// <param name="Rtf">Current RTF (persisted to <c>notes.Content</c>).</param>
    /// <param name="Plain">Current plain text (persisted to <c>notes.PlainText</c>).</param>
    /// <param name="RtfChanged">
    /// True when the editor's RTF differs from the post-load baseline. Catches
    /// format-only and image-only edits the plain-text compare cannot see, while
    /// staying immune to RichEdit's load normalization noise.
    /// </param>
    public readonly record struct EditorSnapshot(string Rtf, string Plain, bool RtfChanged);

    /// <summary>
    /// Set by the view: returns the editor's current state so flushes always persist
    /// what is on screen, no matter who triggered them.
    /// </summary>
    public Func<EditorSnapshot>? EditorContentProvider { get; set; }

    public ObservableCollection<NoteItemViewModel> Notes { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial NoteItemViewModel? SelectedNote { get; set; }

    [ObservableProperty]
    public partial string EditingTitle { get; set; } = string.Empty;

    /// <summary>RTF of the note currently loaded in the editor (set on load; read by the view).</summary>
    public string EditingContentRtf { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsDirty { get; set; }

    /// <summary>Live search box content. Debounced ~300ms before querying FTS.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching))]
    public partial string SearchText { get; set; } = string.Empty;

    // ---------- Settings-driven display state ----------

    /// <summary>True while the search box has text → the search-sort dropdown shows.</summary>
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>Settings: 删除前确认 (read by the view's delete-badge handler).</summary>
    [ObservableProperty]
    public partial bool ConfirmBeforeDelete { get; set; } = true;

    /// <summary>
    /// Settings: search-results ordering as a ComboBox index (0 匹配度 / 1 最新 / 2 最早).
    /// Persisted on change and re-runs the active search immediately.
    /// </summary>
    [ObservableProperty]
    public partial int SearchSortIndex { get; set; }

    /// <summary>Build a list item with the current display settings applied.</summary>
    public NoteItemViewModel CreateItem(NoteSummary summary, string keyword = "") => new(summary)
    {
        Keyword = keyword,
        TimeFormat = _settingsSnapshot.TimeFormat,
        Density = _settingsSnapshot.ListDensity,
    };

    // ---------- Categories ----------

    /// <summary>Sidebar categories: synthetic "全部" pinned at index 0, then DB rows.</summary>
    public ObservableCollection<CategoryItemViewModel> Categories { get; } = new();

    /// <summary>Selected sidebar category; the synthetic "全部" item = no filter.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentCategoryTitle))]
    public partial CategoryItemViewModel? SelectedCategory { get; set; }

    /// <summary>Title of the middle column ("全部" or the category name).</summary>
    public string CurrentCategoryTitle => SelectedCategory is { IsAll: false } c ? c.Name : "全部";

    /// <summary>Current scope as a category name, or <c>null</c> for "全部" (no WHERE).</summary>
    public string? CurrentCategoryName => SelectedCategory is { IsAll: false } c ? c.Name : null;

    public bool HasSelection => SelectedNote is not null;

    public bool IsEmpty => Notes.Count == 0;

    public string CountText => Notes.Count > 0 ? $"共 {Notes.Count} 条" : "暂无便签";

    /// <summary>Marks the note dirty — called by the view on editor text changes.</summary>
    public void NotifyContentEdited() => IsDirty = true;

    /// <summary>Initial load of categories + the summary list (called from the page's Loaded event).</summary>
    public async Task LoadAsync()
    {
        try
        {
            _initializing = true;
            ApplySettings(await _settings.LoadAsync());
            await RefreshCategoriesAsync(selectedId: null); // → SelectedCategory = null ("全部")
            _initializing = false;
            await ReloadListAsync();
        }
        catch (Exception ex)
        {
            _initializing = false;
            _log.LogError(ex, "加载便签列表失败");
        }
    }

    // ---------- Settings application ----------

    /// <summary>SettingsService.Changed → re-apply display settings live (UI thread).</summary>
    private void OnSettingsChanged(AppSettings settings) => ApplySettings(settings);

    private void ApplySettings(AppSettings settings)
    {
        _loadingSettings = true;
        _settingsSnapshot = settings;
        ConfirmBeforeDelete = settings.ConfirmBeforeDelete;
        SearchSortIndex = (int)settings.SearchSortOrder;
        _loadingSettings = false;

        foreach (var item in Notes)
        {
            item.TimeFormat = settings.TimeFormat;
            item.Density = settings.ListDensity;
        }
        ResortList();

        // Theme may have switched: the category rows' fallback text brush is
        // theme-dependent — force the converter to re-run (see HexToBrushConverter).
        foreach (var c in Categories)
            c.RefreshActiveColor();
    }

    /// <summary>Comparer for the browse-mode list per settings: 更新/创建/标题 (all newest/first = index 0).</summary>
    private int CompareItems(NoteItemViewModel a, NoteItemViewModel b) => _settingsSnapshot.NoteSortOrder switch
    {
        NoteSortOrder.Created => b.CreatedAt.CompareTo(a.CreatedAt),
        NoteSortOrder.Title => string.Compare(a.DisplayTitle, b.DisplayTitle, StringComparison.CurrentCulture),
        _ => b.UpdatedAt.CompareTo(a.UpdatedAt),
    };

    /// <summary>Insert at the sorted position (browse mode; search results stay service-ordered).</summary>
    private void InsertSorted(NoteItemViewModel item)
    {
        var index = 0;
        while (index < Notes.Count && CompareItems(Notes[index], item) <= 0)
            index++;
        Notes.Insert(index, item);
    }

    /// <summary>Re-sort the current list in place, preserving selection (settings changed).</summary>
    private void ResortList()
    {
        if (Notes.Count < 2 || IsSearching)
            return; // search results keep their service-side ordering

        var sorted = Notes.OrderBy(n => n, Comparer<NoteItemViewModel>.Create(CompareItems)).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var currentIndex = Notes.IndexOf(sorted[i]);
            if (currentIndex != i)
                Notes.Move(currentIndex, i);
        }
    }

    /// <summary>Sort freshly loaded summaries so the view adds them in display order.</summary>
    private IEnumerable<NoteSummary> SortSummaries(IEnumerable<NoteSummary> summaries) =>
        _settingsSnapshot.NoteSortOrder switch
        {
            NoteSortOrder.Created => summaries.OrderByDescending(s => s.CreatedAt),
            NoteSortOrder.Title => summaries.OrderBy(s => string.IsNullOrWhiteSpace(s.Title) ? "新便签" : s.Title, StringComparer.CurrentCulture),
            _ => summaries.OrderByDescending(s => s.UpdatedAt),
        };

    /// <summary>Reload the note list for the current scope (category filter, no search).</summary>
    private async Task ReloadListAsync()
    {
        try
        {
            var summaries = await _notes.GetSummariesAsync(CurrentCategoryName);
            var wasSelectedId = SelectedNote?.Id;
            Notes.Clear();
            foreach (var summary in SortSummaries(summaries))
                Notes.Add(CreateItem(summary));

            if (wasSelectedId is { } id && Notes.FirstOrDefault(n => n.Id == id) is { } still)
                SelectedNote = still;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "刷新便签列表失败");
        }
    }

    /// <summary>On selection change: flush the previous note, then lazy-load the new one.</summary>
    public async Task OnSelectionChangedAsync()
    {
        try
        {
            await FlushAsync();

            var selected = SelectedNote;
            if (selected is null)
            {
                SetEditor(null, string.Empty, string.Empty);
                return;
            }

            var full = await _notes.GetByIdAsync(selected.Id);
            SetEditor(full, full?.Title ?? string.Empty, full?.Content ?? string.Empty);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "切换便签失败");
        }
    }

    /// <summary>Persist the current editor content if dirty (navigate-away / close / Ctrl+S).</summary>
    public async Task FlushAsync()
    {
        if (_loaded is null || !IsDirty)
            return;

        try
        {
            var snapshot = EditorContentProvider?.Invoke() ?? new EditorSnapshot(EditingContentRtf, string.Empty, false);

            // No-op guard. Previously this compared only title + plain text, so a
            // format-only edit (bold/colour/alignment — which leaves the plain text
            // identical) was silently dropped, and an image-only edit could be lost
            // too. The editor now supplies an RTF-vs-baseline flag: it is true for any
            // real content or formatting change and false for RichEdit's load/render
            // normalization (which refreshes the baseline instead of flagging dirty).
            if (!NoteEditComparer.HasChanges(
                    _loaded.Title, EditingTitle,
                    _loaded.PlainText, snapshot.Plain,
                    snapshot.RtfChanged))
            {
                IsDirty = false;
                return;
            }

            var saved = await _notes.UpdateAsync(_loaded with
            {
                Title = EditingTitle,
                Content = snapshot.Rtf,
                PlainText = snapshot.Plain,
            });
            _loaded = saved;
            IsDirty = false;

            // Keep note_images in step with the images the note actually contains:
            // images dropped from the note are unlinked (and their originals pruned
            // when no note references them any more).
            try
            {
                await _notes.SyncNoteImagesAsync(saved.Id, saved.Content);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "同步便签图片关联失败");
            }

            var item = Notes.FirstOrDefault(n => n.Id == saved.Id);
            if (item is not null)
            {
                item.Apply(saved.Title, MakePreview(saved.PlainText), saved.UpdatedAt);
                if (!IsSearching)
                {
                    // Bumped UpdatedAt may change the item's rank under any sort mode.
                    Notes.Remove(item);
                    InsertSorted(item);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "保存便签失败");
        }
    }

    /// <summary>
    /// Delete a specific note from its list-item badge (the View confirms first).
    /// Selection is left untouched unless the deleted item was the selected one.
    /// </summary>
    public async Task DeleteNoteAsync(NoteItemViewModel item)
    {
        try
        {
            if (_loaded?.Id == item.Id)
            {
                _loaded = null;   // don't let the follow-up selection change flush a deleted note
                IsDirty = false;
            }

            await _notes.DeleteAsync(item.Id);
            Notes.Remove(item); // removing the selected item clears selection → editor empty state
            await RefreshCategoryCountsAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "删除便签失败");
        }
    }

    [RelayCommand]
    private async Task NewNoteAsync()
    {
        try
        {
            await FlushAsync();
            var created = await _notes.CreateAsync(CurrentCategoryName ?? string.Empty);
            var item = CreateItem(new NoteSummary
            {
                Id = created.Id,
                Uuid = created.Uuid,
                Title = created.Title,
                Category = created.Category,
                CreatedAt = created.CreatedAt,
                UpdatedAt = created.UpdatedAt,
            });
            InsertSorted(item);
            SelectedNote = item; // → OnSelectionChangedAsync loads the blank note into the editor
            await RefreshCategoryCountsAsync();
            FocusTitleRequested?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "新建便签失败");
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => FlushAsync();

    private bool CanSave() => IsDirty;

    // ---------- Category CRUD (dialogs live in the view; these are the VM endpoints) ----------

    /// <summary>Create a category; returns an error message on validation failure, else null.</summary>
    public async Task<string?> CreateCategoryAsync(string name, string color, string iconKey)
    {
        try
        {
            var created = await _categories.CreateAsync(name, color, iconKey);
            await RefreshCategoriesAsync(created.Id);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ex.Message;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "新建分类失败");
            return "新建分类失败，请查看日志";
        }
    }

    /// <summary>Rename / restyle a category; returns an error message on failure, else null.</summary>
    public async Task<string?> UpdateCategoryAsync(CategoryItemViewModel item, string name, string color, string iconKey)
    {
        try
        {
            await _categories.UpdateAsync(new Category
            {
                Id = item.Id,
                Name = name,
                Color = color,
                IconKey = iconKey,
            });
            // The rename may have changed the current scope's name — rebuild and reload.
            await RefreshCategoriesAsync(item.Id);
            await ReloadListAsync();
            if (_loaded is not null)
                _loaded = await _notes.GetByIdAsync(_loaded.Id);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ex.Message;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "更新分类失败");
            return "更新分类失败，请查看日志";
        }
    }

    /// <summary>Delete a category and all its notes (the view confirmed first).</summary>
    public async Task<string?> DeleteCategoryAsync(CategoryItemViewModel item)
    {
        try
        {
            var wasSelected = SelectedCategory?.Id == item.Id;
            var deletedLoaded = _loaded?.Category == item.Name;

            await _categories.DeleteAsync(item.Id);

            if (deletedLoaded)
            {
                _loaded = null;
                IsDirty = false;
                SelectedNote = null;
                SetEditor(null, string.Empty, string.Empty);
            }
            if (wasSelected)
                SelectedCategory = null; // falls back to "全部"

            await RefreshCategoriesAsync(SelectedCategory?.Id);
            await ReloadListAsync();
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ex.Message;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "删除分类失败");
            return "删除分类失败，请查看日志";
        }
    }

    /// <summary>Persist the current sidebar order (after a drag-reorder).</summary>
    public async Task ReorderCategoriesAsync()
    {
        try
        {
            // The synthetic "全部" must stay pinned at the top even if the user
            // drops another row above it; only DB rows persist their order.
            if (Categories.Count > 0 && !Categories[0].IsAll)
            {
                var all = Categories.First(c => c.IsAll);
                Categories.Move(Categories.IndexOf(all), 0);
            }
            await _categories.ReorderAsync(Categories.Where(c => !c.IsAll).Select(c => c.Id).ToArray());
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "分类重排失败");
        }
    }

    // ---------- Category internals ----------

    /// <summary>Rebuild the sidebar list from the DB, preserving / setting the selection by id.</summary>
    private async Task RefreshCategoriesAsync(long? selectedId)
    {
        var categories = await _categories.GetAllAsync();
        var counts = await _categories.CountNotesAsync();

        Categories.Clear();
        Categories.Add(CategoryItemViewModel.CreateAll(counts.Values.Sum()));
        foreach (var c in categories)
        {
            counts.TryGetValue(c.Name, out var count);
            Categories.Add(new CategoryItemViewModel(c, _categories.IsBuiltIn(c.Name), count));
        }

        SelectedCategory = selectedId is { } id
            ? Categories.FirstOrDefault(c => c.Id == id)
            : Categories[0]; // "全部"
        SyncCategorySelection();
    }

    /// <summary>Update count badges in place (after note add/delete/move).</summary>
    private async Task RefreshCategoryCountsAsync()
    {
        try
        {
            var counts = await _categories.CountNotesAsync();
            foreach (var c in Categories)
                c.NoteCount = c.IsAll ? counts.Values.Sum() : counts.TryGetValue(c.Name, out var n) ? n : 0;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "刷新分类计数失败");
        }
    }

    partial void OnSelectedCategoryChanged(CategoryItemViewModel? value)
    {
        SyncCategorySelection();
        if (_initializing)
            return;
        _ = OnScopeChangedAsync();
    }

    private void SyncCategorySelection()
    {
        foreach (var c in Categories)
            c.IsSelected = c == SelectedCategory;
    }

    /// <summary>Scope switched: re-run the active search within the new scope, else reload the list.</summary>
    private async Task OnScopeChangedAsync()
    {
        if (!string.IsNullOrWhiteSpace(SearchText))
            OnSearchTextChanged(SearchText); // re-debounce with the new category filter
        else
            await ReloadListAsync();
    }

    /// <summary>
    /// Debounced search: each keystroke cancels the previous pending query; after
    /// ~300ms of quiet the query fires off-thread and raises <see cref="SearchCompleted"/>
    /// / <see cref="SearchCleared"/> for the view to swap the note list (empty keyword
    /// restores the full summary list - Qt parity: blank = no search, not "all match").
    /// </summary>
    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();

        if (string.IsNullOrWhiteSpace(value))
        {
            _ = RestoreFullListAsync(cts.Token);
            return;
        }

        _ = DebouncedSearchAsync(value, cts.Token);
    }

    /// <summary>
    /// Search-sort dropdown changed: persist the choice, then re-run the active
    /// search so the results re-rank immediately (no-op while settings load).
    /// </summary>
    partial void OnSearchSortIndexChanged(int value)
    {
        if (_loadingSettings)
            return;

        _settingsSnapshot = _settingsSnapshot with { SearchSortOrder = (SearchSortOrder)value };
        _ = _settings.SaveAsync(_settingsSnapshot);
        if (IsSearching)
            OnSearchTextChanged(SearchText); // re-debounce with the new ORDER BY
    }

    private async Task DebouncedSearchAsync(string query, CancellationToken ct)
    {
        try
        {
            await Task.Delay(SearchDebounce, ct);
            var results = await _search.SearchAsync(query, category: CurrentCategoryName, _settingsSnapshot.SearchSortOrder, ct);
            if (!ct.IsCancellationRequested)
                SearchCompleted?.Invoke(results, query);
        }
        catch (OperationCanceledException)
        {
            // debouncer cancelled this query - expected, not an error
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "搜索失败");
        }
    }

    private async Task RestoreFullListAsync(CancellationToken ct)    {
        try
        {
            var summaries = await _notes.GetSummariesAsync(CurrentCategoryName, ct);
            if (!ct.IsCancellationRequested)
                SearchCleared?.Invoke(SortSummaries(summaries).ToList());
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "恢复便签列表失败");
        }
    }

    private void SetEditor(Note? loaded, string title, string contentRtf)
    {
        _loaded = loaded;
        _suppressDirty = true;
        EditingTitle = title;
        EditingContentRtf = contentRtf;
        _suppressDirty = false;
        IsDirty = false;
        ContentReloadRequested?.Invoke();
    }

    partial void OnEditingTitleChanged(string value)
    {
        if (!_suppressDirty)
            IsDirty = true;
    }

    private static string MakePreview(string plainText)
    {
        var oneLine = plainText.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= PreviewLength ? oneLine : oneLine[..PreviewLength];
    }
}
