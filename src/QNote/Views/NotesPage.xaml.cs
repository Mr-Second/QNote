using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using QNote.Controls;
using QNote.Services;
using QNote.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace QNote.Views;

/// <summary>
/// The three-pane notes screen. Code-behind is UI wiring only: it resolves the VM
/// from DI, forwards the ListView selection / Loaded / Ctrl+S to the VM, hosts the
/// delete-confirmation dialog (UI types stay out of the VM), and glues the
/// <see cref="WreEditorController"/> (view-side WinUIRichEditor wrapper) to the
/// VM's Markdown data flow.
/// </summary>
public sealed partial class NotesPage : Page
{
    private readonly WreEditorController _editor;
    private bool _settingsOpen;
    private bool _dialogOpen;
    private bool _dataDialogOpen;

    public NotesPageViewModel ViewModel { get; }

    public NotesPage()
    {
        ViewModel = App.Services.GetRequiredService<NotesPageViewModel>();
        InitializeComponent();

        _editor = new WreEditorController(
            Editor,
            App.Services.GetService<Microsoft.Extensions.Logging.ILogger<WreEditorController>>(),
            App.Services.GetService<IImageService>(),
            App.Services.GetService<INoteService>());
        ViewModel.EditorContentProvider = () =>
            new NotesPageViewModel.EditorSnapshot(_editor.GetMarkdown(), _editor.IsDirty);
        _editor.CurrentNoteIdProvider = () => ViewModel.SelectedNote?.Id;
        _editor.ImportFailed += OnImageError;
        // The toolbar image button stays disabled without a host picker; route it
        // through the controller's full pipeline (the raw InsertImageBlock path
        // would bypass the image store / note link / sha remap entirely).
        EditorToolbar.ImagePicker = PickAndInsertImageAsync;

        // Mirror the page's actual theme onto the editor. The appearance brushes
        // (canvas/text/caret) are pinned via ThemeResource in XAML; RequestedTheme here
        // keeps the editor's own popups (context menu, dialogs) in step, and a live
        // theme switch re-applies it.
        ApplyEditorTheme();
        ActualThemeChanged += (_, _) => ApplyEditorTheme();

        ViewModel.FocusTitleRequested += OnFocusTitleRequested;
        ViewModel.ContentReloadRequested += OnContentReloadRequested;
        ViewModel.SearchCompleted += OnSearchCompleted;
        ViewModel.SearchCleared += OnSearchCleared;
        _editor.ContentChanged += OnEditorContentChanged;
        _editor.SelectionChanged += OnEditorSelectionChanged;

        Loaded += OnLoaded;
    }

    /// <summary>
    /// Pushes the page's current theme onto the editor. The Win2D canvas does NOT follow
    /// <c>RequestedTheme</c> by itself — the three appearance brushes are pinned in XAML
    /// through <c>ThemeResource</c> (spike-verified dark-mode wiring); this only keeps the
    /// editor's own popups (context menu, dialogs) in the same theme.
    /// </summary>
    private void ApplyEditorTheme()
    {
        Editor.RequestedTheme = ActualTheme;
        EditorToolbar.RequestedTheme = ActualTheme;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await ViewModel.LoadAsync();

    /// <summary>
    /// Toolbar image-button hook: multi-select file picker routed through the
    /// controller's insert pipeline. Returns null — the controller has already
    /// inserted, so the toolbar must not run its own raw insert path.
    /// </summary>
    private async Task<byte[]?> PickAndInsertImageAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail,
        };
        foreach (var ext in WicImageNormalizer.SupportedExtensions)
            picker.FileTypeFilter.Add($".{ext}");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0)
            await _editor.InsertImageFilesAsync(files.Select(f => f.Path));
        return null;
    }

    private void OnFocusTitleRequested() => TitleBox.Focus(FocusState.Programmatic);

    // ---------- Drag & drop image files ----------

    private async void Editor_Drop(object sender, DragEventArgs e) =>
        await _editor.HandleDropAsync(e.DataView);

    // VM raised a debounced search off-thread → marshal back to the UI thread and
    // swap the note list there (ObservableCollection is UI-thread-affine).
    private void OnSearchCompleted(IReadOnlyList<QNote.Models.NoteSummary> results, string keyword)
    {
        App.DispatcherQueue.TryEnqueue(() =>
        {
            var wasSelected = ViewModel.SelectedNote;
            ViewModel.Notes.Clear();
            foreach (var s in results)
                ViewModel.Notes.Add(ViewModel.CreateItem(s, keyword));

            // Preserve selection if the same note is still in the results, else clear.
            if (wasSelected is { } prev && ViewModel.Notes.FirstOrDefault(n => n.Id == prev.Id) is { } still)
                ViewModel.SelectedNote = still;
        });
    }

    // Search box cleared → restore the full summary list on the UI thread.
    private void OnSearchCleared(IReadOnlyList<QNote.Models.NoteSummary> summaries)
    {
        App.DispatcherQueue.TryEnqueue(() =>
        {
            var wasSelected = ViewModel.SelectedNote;
            ViewModel.Notes.Clear();
            foreach (var s in summaries)
                ViewModel.Notes.Add(ViewModel.CreateItem(s));

            if (wasSelected is { } prev && ViewModel.Notes.FirstOrDefault(n => n.Id == prev.Id) is { } still)
                ViewModel.SelectedNote = still;
        });
    }

    // VM finished loading a note → render its Markdown into the editor (suppressed
    // inside the load path, so this never marks the note dirty). Async: image
    // references resolve to note_images display copies before the RTF load.
    private async void OnContentReloadRequested()
    {
        await _editor.SetMarkdownAsync(ViewModel.EditingContent);
        UpdatePlaceholder();
        UpdateEditorStatus();
    }

    private void OnEditorContentChanged()
    {
        ViewModel.NotifyContentEdited();
        UpdatePlaceholder();
        UpdateEditorStatus();
        ScheduleAutoSave();
    }

    // Auto-save: idle debounce after the last edit (interval comes from the 自动保存
    // setting, live via the settings-changed chain). A no-op while dirty is false.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autoSaveTimer;

    private void ScheduleAutoSave()
    {
        var ms = ViewModel.AutoSaveMilliseconds;
        if (ms <= 0)
        {
            _autoSaveTimer?.Stop();
            return;
        }
        if (_autoSaveTimer is null)
        {
            _autoSaveTimer = App.DispatcherQueue.CreateTimer();
            _autoSaveTimer.IsRepeating = false;
            _autoSaveTimer.Tick += async (_, _) =>
            {
                if (ViewModel.SaveCommand.CanExecute(null))
                    await ViewModel.SaveCommand.ExecuteAsync(null);
            };
        }
        _autoSaveTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void OnEditorSelectionChanged() => UpdateEditorStatus();

    // Notepads-style status bar: char count (image placeholders excluded) + last edited.
    private void UpdateEditorStatus()
    {
        var text = _editor.GetPlainText();
        int count = text.Count(c => !char.IsWhiteSpace(c) && c != '￼');
        CharCountText.Text = $"{count} 字";
        EditedTimeText.Text = ViewModel.SelectedNote is { } note ? $"编辑于 {note.TimeDisplay}" : string.Empty;
    }

    private void UpdatePlaceholder() =>
        ContentPlaceholder.Visibility = _editor.IsEmpty ? Visibility.Visible : Visibility.Collapsed;

    private async void NotesList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await ViewModel.OnSelectionChangedAsync();

    // ---------- Search-result sort (funnel flyout) ----------

    /// <summary>Mark the persisted sort with a right-aligned ✓ when the flyout opens.</summary>
    private void SearchSortFlyout_Opened(object sender, object e)
    {
        SortByRelevanceItem.KeyboardAcceleratorTextOverride = ViewModel.SearchSortIndex == 0 ? "✓" : "";
        SortByNewestItem.KeyboardAcceleratorTextOverride = ViewModel.SearchSortIndex == 1 ? "✓" : "";
        SortByOldestItem.KeyboardAcceleratorTextOverride = ViewModel.SearchSortIndex == 2 ? "✓" : "";
    }

    private void SearchSortItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && int.TryParse(el.Tag as string, out var index))
            ViewModel.SearchSortIndex = index;
    }

    // ---------- Note-item hover delete badge ----------

    private void NoteItem_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        SetItemDeleteBadgeVisible(sender, true);

    private void NoteItem_PointerExited(object sender, PointerRoutedEventArgs e) =>
        SetItemDeleteBadgeVisible(sender, false);

    private static void SetItemDeleteBadgeVisible(object sender, bool visible)
    {
        if (sender is Grid root && root.FindName("ItemDeleteButton") is Button badge)
            badge.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ItemDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NoteItemViewModel item)
            return;

        // Settings: 删除前确认 = off → delete immediately.
        if (!ViewModel.ConfirmBeforeDelete)
        {
            await ViewModel.DeleteNoteAsync(item);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            // Popups do not inherit the window root RequestedTheme — pin the dialog to it.
            RequestedTheme = ActualTheme,
            Title = "删除便签",
            Content = "确认删除这条便签?此操作无法撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.DeleteNoteAsync(item);
    }

    private async void SaveAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.SaveCommand.CanExecute(null))
            await ViewModel.SaveCommand.ExecuteAsync(null);
    }

    // ---------- Sidebar: categories ----------

    private void CategoryList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        // The synthetic "全部" row is pinned — it cannot be dragged.
        if (e.Items.OfType<CategoryItemViewModel>().Any(c => c.IsAll))
            e.Cancel = true;
    }

    private async void CategoryList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        await ViewModel.ReorderCategoriesAsync();

    private async void NewCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CategoryEditDialog("新建分类", string.Empty, "#3B82F6", QNote.Controls.IconCatalog.Options[0].Key)
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var error = await ViewModel.CreateCategoryAsync(dialog.CategoryName, dialog.ColorHex, dialog.IconKey);
        if (error is not null)
            await ShowErrorDialogAsync("新建分类", error);
    }

    private async void RenameCategoryMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CategoryItemViewModel item)
            return;

        var dialog = new CategoryEditDialog("编辑分类", item.Name, item.ColorHex, item.IconKey)
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var error = await ViewModel.UpdateCategoryAsync(item, dialog.CategoryName, dialog.ColorHex, dialog.IconKey);
        if (error is not null)
            await ShowErrorDialogAsync("编辑分类", error);
    }

    private async void DeleteCategoryMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CategoryItemViewModel item)
            return;

        var content = item.NoteCount > 0
            ? $"将删除分类「{item.Name}」及其中的 {item.NoteCount} 条便签，此操作无法撤销。"
            : $"确认删除分类「{item.Name}」？此操作无法撤销。";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            // Popups do not inherit the window root RequestedTheme — pin the dialog to it.
            RequestedTheme = ActualTheme,
            Title = "删除分类",
            Content = content,
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var error = await ViewModel.DeleteCategoryAsync(item);
        if (error is not null)
            await ShowErrorDialogAsync("删除分类", error);
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // Only one ContentDialog may be open at a time — a re-entrant click
        // (e.g. double-click) crashes ShowAsync with 0x80000019.
        if (_settingsOpen)
            return;
        _settingsOpen = true;

        // The settings panel's 数据 buttons can't open their dialog while THIS one
        // is open (the same one-dialog rule): they stash a pending request and close
        // the settings dialog; the follow-up opens after ShowAsync returns.
        var pending = PendingDataDialog.None;
        UpdateCheckResult? pendingUpdateResult = null;
        var settingsDialogOpen = true;
        try
        {
            var vm = App.Services.GetRequiredService<SettingsViewModel>();
            await vm.InitializeAsync();

            var panel = new SettingsPanel(vm);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                // Popups do not inherit the window root RequestedTheme — pin the dialog to it.
                RequestedTheme = ActualTheme,
                Title = "设置",
                Content = panel,
                CloseButtonText = "关闭",
            };
            panel.BackupRequested += () => { pending = PendingDataDialog.Backup; dialog.Hide(); };
            panel.RestoreRequested += () => { pending = PendingDataDialog.Restore; dialog.Hide(); };

            // The manual update check runs while the settings dialog is open (the
            // row button shows 检查中… via the VM). When the result lands it replaces
            // this dialog (one-dialog rule); a result arriving after the user closed
            // settings opens directly instead. Session-scoped like BackupRequested —
            // the transient VM and the handler die together.
            vm.UpdateCheckCompleted += result =>
            {
                if (!settingsDialogOpen)
                {
                    _ = ShowUpdateResultDialogAsync(result);
                    return;
                }
                settingsDialogOpen = false;
                pendingUpdateResult = result;
                dialog.Hide();
            };

            // Live-follow theme switches while the dialog is open (the settings panel
            // is where the theme gets changed — reopening to see it would be silly).
            void OnThemeChanged(FrameworkElement sender, object args) => dialog.RequestedTheme = ActualTheme;
            ActualThemeChanged += OnThemeChanged;
            try
            {
                await dialog.ShowAsync();
            }
            finally
            {
                ActualThemeChanged -= OnThemeChanged;
                settingsDialogOpen = false;
            }
        }
        finally
        {
            _settingsOpen = false;
        }

        if (pending == PendingDataDialog.Backup)
            await ShowBackupDialogAsync();
        else if (pending == PendingDataDialog.Restore)
            await ShowRestoreDialogAsync();
        else if (pendingUpdateResult is { } updateResult)
            await ShowUpdateResultDialogAsync(updateResult);
    }

    private enum PendingDataDialog { None, Backup, Restore }

    /// <summary>Backup dialog (settings panel → 数据 → 备份). Flush first so the archive captures unsaved edits.</summary>
    private async Task ShowBackupDialogAsync()
    {
        if (_dataDialogOpen)
            return;
        _dataDialogOpen = true;
        try
        {
            await ViewModel.FlushAsync();
            var dialog = new BackupDialog(App.Services.GetRequiredService<IBackupService>())
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
            };
            await dialog.ShowAsync();
            if (dialog.FinalResult is { } result)
                ShowDataResultToast(result);
        }
        finally
        {
            _dataDialogOpen = false;
        }
    }

    /// <summary>Restore dialog (settings panel → 数据 → 恢复). A completed restore invalidates every cached list.</summary>
    private async Task ShowRestoreDialogAsync()
    {
        if (_dataDialogOpen)
            return;
        _dataDialogOpen = true;
        try
        {
            // Flush BEFORE the restore: overwrite swaps the DB file, so an unsaved
            // edit would otherwise be flushed into the restored database afterwards.
            await ViewModel.FlushAsync();
            var dialog = new RestoreDialog(App.Services.GetRequiredService<IBackupService>())
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
            };
            await dialog.ShowAsync();
            if (dialog.RestoreCompleted)
                await ViewModel.ReloadAfterRestoreAsync();
            if (dialog.FinalResult is { } result)
                ShowDataResultToast(result);
        }
        finally
        {
            _dataDialogOpen = false;
        }
    }

    /// <summary>
    /// Update-check result dialog (设置 → 常规 → 检查更新, unpackaged only): newer →
    /// link to the GitHub release page (Launcher); up-to-date → info; failure →
    /// friendly zh message (the technical reason stays in the log).
    /// </summary>
    private async Task ShowUpdateResultDialogAsync(UpdateCheckResult result)
    {
        // Only one ContentDialog may be open at a time (a re-entrant ShowAsync
        // throws). The _dataDialogOpen check keeps a late check result from
        // stacking on an open backup/restore dialog; _settingsOpen covers a
        // result from a PREVIOUS settings session arriving while the user has
        // already reopened settings (the result is dropped in those corners —
        // the user's attention is on the open dialog).
        if (_dialogOpen || _dataDialogOpen || _settingsOpen)
            return;
        _dialogOpen = true;
        try
        {
            if (result.Status == UpdateCheckStatus.UpdateAvailable && result.ReleaseUrl is { } url)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    // Popups do not inherit the window root RequestedTheme — pin the dialog to it.
                    RequestedTheme = ActualTheme,
                    Title = "发现新版本",
                    Content = $"最新版本 {result.LatestVersion} 已发布，可前往 GitHub 下载。",
                    PrimaryButtonText = "前往下载",
                    CloseButtonText = "稍后再说",
                    DefaultButton = ContentDialogButton.Primary,
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary
                    && Uri.TryCreate(url, UriKind.Absolute, out var releaseUri))
                {
                    // Launch failure (no browser) has no meaningful in-dialog recovery — ignored.
                    await Launcher.LaunchUriAsync(releaseUri);
                }
            }
            else if (result.Status == UpdateCheckStatus.UpToDate)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    // Popups do not inherit the window root RequestedTheme — pin the dialog to it.
                    RequestedTheme = ActualTheme,
                    Title = "检查更新",
                    Content = "当前已是最新版本。",
                    CloseButtonText = "知道了",
                    DefaultButton = ContentDialogButton.Close,
                };
                await dialog.ShowAsync();
            }
            else
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    // Popups do not inherit the window root RequestedTheme — pin the dialog to it.
                    RequestedTheme = ActualTheme,
                    Title = "检查更新",
                    Content = "检查更新失败，请检查网络连接后重试。",
                    CloseButtonText = "知道了",
                    DefaultButton = ContentDialogButton.Close,
                };
                await dialog.ShowAsync();
            }
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    /// <summary>Surface a backup/restore final outcome as a floating page-level toast.</summary>
    private void ShowDataResultToast(OperationResult result)
    {
        if (result.Success)
            ResultTip.ShowSuccess(result.Title, result.Message);
        else
            ResultTip.ShowError(result.Title, result.Message);
    }

    private void OnImageError(string message)
    {
        // Raised from import/launch paths that may be off the UI thread — marshal the
        // dialog back to it.
        App.DispatcherQueue.TryEnqueue(() => _ = ShowErrorDialogAsync("图片", message));
    }

    private async Task ShowErrorDialogAsync(string title, string message)
    {
        // Only one ContentDialog may be open at a time (a re-entrant ShowAsync throws).
        if (_dialogOpen)
            return;
        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                // Popups do not inherit the window root RequestedTheme — pin the dialog to it.
                RequestedTheme = ActualTheme,
                Title = title,
                Content = message,
                CloseButtonText = "知道了",
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

}
