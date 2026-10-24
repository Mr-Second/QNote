using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using QNote.Controls;
using QNote.Services;
using QNote.Sync;
using QNote.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace QNote.Views;

/// <summary>
/// The three-pane notes screen. Code-behind is UI wiring only: it resolves the VM
/// from DI, forwards the ListView selection / Loaded / Ctrl+S to the VM, hosts the
/// delete-confirmation dialog (UI types stay out of the VM), glues the
/// <see cref="WreEditorController"/> (view-side WinUIRichEditor wrapper) to the
/// VM's Markdown data flow, and drives the header's manual-sync button
/// (readiness + spin + result toast — UI orchestration stays out of the VM).
/// </summary>
public sealed partial class NotesPage : Page
{
    private readonly WreEditorController _editor;
    private readonly SyncPollingScheduler _syncPolling;
    private readonly SyncService _syncService;
    private bool _pairingPromptOpen;
    private bool _pairingPromptDismissed;
    private bool _syncSubscribed;
    private int _syncRefreshQueued;
    private Task _editorLoadTask = Task.CompletedTask;
    private int _editorLoadVersion;
    private bool _pairingSyncRunning;
    private bool _settingsOpen;
    private bool _dialogOpen;
    private bool _dataDialogOpen;
    private bool _syncReady;
    private bool _syncSpinning;
    private Storyboard? _syncSpinStoryboard;

    public NotesPageViewModel ViewModel { get; }

    public NotesPage()
    {
        ViewModel = App.Services.GetRequiredService<NotesPageViewModel>();
        _syncPolling = App.Services.GetRequiredService<SyncPollingScheduler>();
        _syncService = App.Services.GetRequiredService<SyncService>();
        InitializeComponent();

        _editor = new WreEditorController(
            Editor,
            App.Services.GetService<Microsoft.Extensions.Logging.ILogger<WreEditorController>>(),
            App.Services.GetService<IImageService>(),
            App.Services.GetService<INoteService>());
        // Flush snapshot: adopt unlinked pasted images into the open note first
        // (cross-note copies / external bitmaps — task 10-03), then emit. The
        // controller's combined call snapshots from the SAME document it adopted, so
        // a note switch racing the adoption's DB awaits cannot swap the content
        // underneath this flush.
        ViewModel.EditorContentProvider = async () =>
        {
            await _editorLoadTask;
            var (markdown, changed) = await _editor.AdoptAndSnapshotAsync();
            return new NotesPageViewModel.EditorSnapshot(markdown, changed);
        };
        // Image links and display-copy loads must target the note whose content the
        // editor holds — SelectedNote diverges from it during the switch flush window.
        _editor.CurrentNoteIdProvider = () => ViewModel.LoadedNoteId;
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

        // Find/replace: the engine raises FindRequested on Ctrl+F / Ctrl+H (editor focus
        // domain); the floating bar is QNote's host-side find UI (the RichEditorView one is
        // excluded with the rest of that chrome). Subscribing also arms the engine's own
        // F3 / Shift+F3 (FindAgain). Without a note loaded the editor column is collapsed,
        // so Ctrl+F cannot fire — the guard is defensive only.
        FindBar.Target = Editor;
        Editor.FindRequested += (_, withReplace) =>
        {
            if (ViewModel.HasSelection) FindBar.Show(withReplace);
        };
        // Replace-all result toast (page-level AppToast, same as backup/restore outcomes);
        // the count message uses the editor localization table's ReplacedFormat key.
        FindBar.ReplaceAllCompleted += count => ResultTip.ShowSuccess(
            WinUIRichEditor.RichEditorLocalization.GetString("ReplaceAll"),
            string.Format(WinUIRichEditor.RichEditorLocalization.GetString("ReplacedFormat"), count));

        Loaded += OnLoaded;
        // A language switch re-navigates the root frame and discards this page
        // (no page caching). Tear down page-owned sources that outlive it: the
        // singleton settings subscription (via the VM) and the auto-save timer.
        Unloaded += OnPageUnloaded;
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _syncPolling.CycleCompleted -= OnSyncCycleCompleted;
        _syncSubscribed = false;
        _autoSaveTimer?.Stop();
        StopSyncSpin();
        ViewModel.Detach();
    }

    // ---------- mode switch (segmented control, shared with TodoPage) ----------

    private void ModeSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // Navigates the root frame to TodoPage (flushes the dirty note first —
        // MainWindow.SwitchToMode owns the choreography). No-op on echo ticks.
        if (App.Window is MainWindow window)
            window.SwitchToMode(sender.SelectedItem == TodoModeItem);
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

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_syncSubscribed)
        {
            _syncPolling.CycleCompleted += OnSyncCycleCompleted;
            _syncSubscribed = true;
        }
        // Sync readiness (local MSAL cache probe, no network) before the VM load.
        await RefreshSyncButtonStateAsync();
        await ViewModel.LoadAsync();
    }

    // ---------- manual sync (header icon button, task 10-06) ----------

    /// <summary>
    /// Refreshes the header sync button's readiness: enabled iff a sync account is
    /// configured (OneDrive signed in or WebDAV saved). Destination metadata and
    /// MSAL account enumeration are local reads, without network traffic. The
    /// churn point is the settings dialog (sign-in/out), so this also runs when it
    /// closes; sign-in from TodoPage's settings is picked up on the next page load.
    /// </summary>
    private async Task RefreshSyncButtonStateAsync()
    {
        try
        {
            var destination = await _syncService.GetDestinationAsync();
            _syncReady = destination.IsReady;
            var detail = SyncOutcomeText.DestinationDetail(destination);
            ToolTipService.SetToolTip(SyncNowButton, AppStrings.GetFormat("SyncNowTarget", detail));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                SyncNowButton, AppStrings.GetFormat("SyncNowTarget", detail));
        }
        catch
        {
            // Same call the settings VM logs; here a failure just means "not ready".
            _syncReady = false;
            ToolTipService.SetToolTip(SyncNowButton, AppStrings.GetString("SyncConfigurationUnreadable"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                SyncNowButton, AppStrings.GetString("SyncNotStarted"));
        }
        SyncNowButton.IsEnabled = _syncReady && !_syncSpinning;
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        if (_syncSpinning)
            return;
        _syncSpinning = true;
        SyncNowButton.IsEnabled = false;
        StartSyncSpin();
        try
        {
            await ViewModel.FlushAsync();
            // bypass: explicit user intent. The scheduler single-flights — clicking
            // while a background cycle runs awaits THAT cycle.
            var cycle = _syncPolling.TriggerNow(bypassThrottle: true);
            if (cycle is null)
            {
                ResultTip.ShowError(AppStrings.GetString("SyncNotStarted"), "");
                return;
            }
            var result = await cycle;
            if (result?.AbortKind == SyncCycleAbortKind.PairingRequired)
            {
                result = await ShowSyncPairingAsync(result.PairingTarget, result.Destination);
                if (result is null)
                    return;
            }
            var outcome = SyncOutcomeText.Describe(result);
            if (outcome.Success)
                ResultTip.ShowSuccess(outcome.Title, outcome.Message);
            else
                ResultTip.ShowError(outcome.Title, outcome.Message);
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<NotesPage>>().LogError(ex, "Manual sync failed.");
            ResultTip.ShowError(AppStrings.GetString("SyncToastFailed"), AppStrings.GetString("SyncAbortGeneric"));
        }
        finally
        {
            StopSyncSpin();
            _syncSpinning = false;
            await RefreshSyncButtonStateAsync();
        }
    }

    private void OnSyncCycleCompleted(SyncCycleResult? result)
    {
        if (result?.AbortKind != SyncCycleAbortKind.PairingRequired)
        {
            if (result is null || result.Downloaded + result.TombstonesApplied + result.ConflictCopies == 0
                || Interlocked.Exchange(ref _syncRefreshQueued, 1) != 0)
                return;
            if (App.DispatcherQueue?.TryEnqueue(async () =>
            {
                Interlocked.Exchange(ref _syncRefreshQueued, 0);
                if (IsLoaded)
                    await ViewModel.RefreshAfterSyncAsync();
            }) != true)
                Interlocked.Exchange(ref _syncRefreshQueued, 0);
            return;
        }
        App.DispatcherQueue?.TryEnqueue(async () =>
        {
            if (!IsLoaded || _pairingPromptDismissed || _syncSpinning || _pairingPromptOpen
                || _settingsOpen || _dataDialogOpen || _dialogOpen)
                return;
            try
            {
                var outcome = await ShowSyncPairingAsync(result.PairingTarget, result.Destination);
                if (outcome is not null)
                {
                    var text = SyncOutcomeText.Describe(outcome);
                    if (text.Success) ResultTip.ShowSuccess(text.Title, text.Message);
                    else ResultTip.ShowError(text.Title, text.Message);
                }
            }
            catch (Exception ex)
            {
                App.Services.GetRequiredService<ILogger<NotesPage>>().LogError(ex, "The first-pairing dialog failed.");
                ResultTip.ShowError(AppStrings.GetString("SyncToastFailed"), AppStrings.GetString("SyncAbortGeneric"));
            }
        });
    }

    private async Task<SyncCycleResult?> ShowSyncPairingAsync(string? target, SyncDestination? destination)
    {
        if (_pairingPromptOpen)
            return null;
        _pairingPromptOpen = true;
        try
        {
            var (dialog, choices) = SyncPairingDialog.Create(this, destination);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                _pairingPromptDismissed = true;
                return null;
            }
            // Replacement may remove the source rows/images entirely. Finish the
            // existing draft, then prevent a new one during this explicit operation.
            _pairingSyncRunning = true;
            TitleBox.IsEnabled = Editor.IsEnabled = EditorToolbar.IsEnabled = false;
            try
            {
                await ViewModel.FlushAsync();
                if (ViewModel.IsDirty)
                    return null;
                var choice = (SyncPairingChoice)choices.SelectedIndex;
                var result = await Task.Run(() => _syncService.RunAsync(choice, pairingTarget: target));
                if (result is not null)
                    await ViewModel.RefreshAfterSyncAsync();
                return result;
            }
            finally
            {
                _pairingSyncRunning = false;
                TitleBox.IsEnabled = true;
                Editor.IsEnabled = EditorToolbar.IsEnabled = _editorLoadTask.IsCompleted;
            }
        }
        finally { _pairingPromptOpen = false; }
    }

    private void StartSyncSpin()
    {
        var spin = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromSeconds(1)),
        };
        Storyboard.SetTarget(spin, SyncNowRotate);
        Storyboard.SetTargetProperty(spin, "Angle");
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        sb.Children.Add(spin);
        sb.Begin();
        _syncSpinStoryboard = sb;
    }

    private void StopSyncSpin()
    {
        _syncSpinStoryboard?.Stop();
        _syncSpinStoryboard = null;
        SyncNowRotate.Angle = 0;
    }

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
    private void OnContentReloadRequested() =>
        _editorLoadTask = ReloadEditorAsync(++_editorLoadVersion);

    private async Task ReloadEditorAsync(int version)
    {
        Editor.IsEnabled = false;
        EditorToolbar.IsEnabled = false;
        try
        {
            await _editor.SetMarkdownAsync(ViewModel.EditingContent);
            if (version != _editorLoadVersion)
                return;
            UpdatePlaceholder();
            UpdateEditorStatus();
            FindBar.Refresh();
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<NotesPage>>().LogError(ex, "Loading the editor document failed.");
        }
        finally
        {
            if (version == _editorLoadVersion)
            {
                Editor.IsEnabled = !_pairingSyncRunning;
                EditorToolbar.IsEnabled = !_pairingSyncRunning;
            }
        }
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
        CharCountText.Text = AppStrings.GetFormat("CharsFormat", count);
        EditedTimeText.Text = ViewModel.SelectedNote is { } note
            ? AppStrings.GetFormat("EditedAtFormat", note.TimeDisplay)
            : string.Empty;
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
            Title = AppStrings.GetString("DeleteNoteTitle"),
            Content = AppStrings.GetString("DeleteNoteContent"),
            PrimaryButtonText = AppStrings.GetString("DeleteButton"),
            CloseButtonText = AppStrings.GetString("CancelButton"),
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
        var dialog = new CategoryEditDialog(AppStrings.GetString("NewCategoryTitle"), string.Empty, "#3B82F6", QNote.Controls.IconCatalog.Options[0].Key)
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var error = await ViewModel.CreateCategoryAsync(dialog.CategoryName, dialog.ColorHex, dialog.IconKey);
        if (error is not null)
            ShowErrorToast(AppStrings.GetString("NewCategoryTitle"), error);
    }

    private async void RenameCategoryMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CategoryItemViewModel item)
            return;

        var dialog = new CategoryEditDialog(AppStrings.GetString("EditCategoryTitle"), item.Name, item.ColorHex, item.IconKey)
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var error = await ViewModel.UpdateCategoryAsync(item, dialog.CategoryName, dialog.ColorHex, dialog.IconKey);
        if (error is not null)
            ShowErrorToast(AppStrings.GetString("EditCategoryTitle"), error);
    }

    private async void DeleteCategoryMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CategoryItemViewModel item)
            return;

        var content = item.NoteCount > 0
            ? AppStrings.GetFormat("DeleteCategoryWithNotesContent", item.Name, item.NoteCount)
            : AppStrings.GetFormat("DeleteCategoryContent", item.Name);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            // Popups do not inherit the window root RequestedTheme — pin the dialog to it.
            RequestedTheme = ActualTheme,
            Title = AppStrings.GetString("DeleteCategoryTitle"),
            Content = content,
            PrimaryButtonText = AppStrings.GetString("DeleteButton"),
            CloseButtonText = AppStrings.GetString("CancelButton"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var error = await ViewModel.DeleteCategoryAsync(item);
        if (error is not null)
            ShowErrorToast(AppStrings.GetString("DeleteCategoryTitle"), error);
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
            var panel = new SettingsPanel(vm);
            await vm.InitializeAsync();
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                // Popups do not inherit the window root RequestedTheme — pin the dialog to it.
                RequestedTheme = ActualTheme,
                Title = AppStrings.GetString("SettingsTitle"),
                Content = panel,
                CloseButtonText = AppStrings.GetString("CloseButton"),
            };
            panel.BackupRequested += () => { pending = PendingDataDialog.Backup; dialog.Hide(); };
            panel.RestoreRequested += () => { pending = PendingDataDialog.Restore; dialog.Hide(); };
            panel.AboutRequested += () => { pending = PendingDataDialog.About; dialog.Hide(); };

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
            using var dialogTheme = new DialogThemeScope(dialog, (FrameworkElement)XamlRoot.Content);
            try
            {
                await dialog.ShowAsync();
            }
            finally
            {
                settingsDialogOpen = false;
            }
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<NotesPage>>().LogError(ex, "Opening settings failed.");
            ResultTip.ShowError(AppStrings.GetString("SettingsOperationFailedTitle"), AppStrings.GetString("SettingsOpenFailed"));
        }
        finally
        {
            _settingsOpen = false;
        }

        // The dialog is where sign-in/out happens — re-read the sync button's
        // readiness whatever way it closed (follow-up dialogs run after this).
        await RefreshSyncButtonStateAsync();

        if (pending == PendingDataDialog.Backup)
            await ShowBackupDialogAsync();
        else if (pending == PendingDataDialog.Restore)
            await ShowRestoreDialogAsync();
        else if (pending == PendingDataDialog.About)
            await ShowAboutDialogAsync();
        else if (pendingUpdateResult is { } updateResult)
            await ShowUpdateResultDialogAsync(updateResult);
    }

    private enum PendingDataDialog { None, Backup, Restore, About }

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

    /// <summary>About dialog (settings panel → 关于 → 关于…). Read-only — no
    /// flush or guards beyond the one-dialog choreography in
    /// <see cref="SettingsButton_Click"/> are needed.</summary>
    private async Task ShowAboutDialogAsync()
    {
        var dialog = new AboutDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        await dialog.ShowAsync();
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
    /// A newer release requests a download decision. Routine update-check
    /// outcomes stay in the settings panel's toast.
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
                    Title = AppStrings.GetString("UpdateAvailableTitle"),
                    Content = AppStrings.GetFormat("UpdateAvailableContent", result.LatestVersion),
                    PrimaryButtonText = AppStrings.GetString("GoDownloadButton"),
                    CloseButtonText = AppStrings.GetString("LaterButton"),
                    DefaultButton = ContentDialogButton.Primary,
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary
                    && Uri.TryCreate(url, UriKind.Absolute, out var releaseUri))
                {
                    // Launch failure (no browser) has no meaningful in-dialog recovery — ignored.
                    await Launcher.LaunchUriAsync(releaseUri);
                }
            }
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    /// <summary>
    /// Startup update dialog (1.5.1): fed by MainWindow ~5 s after the first
    /// activation when <see cref="IStartupUpdateCheck"/> found a newer
    /// version. Same one-dialog guards as every other page dialog — a
    /// collision (backup/settings/about open at the moment) silently drops
    /// this session's notice; the next session checks again.
    /// </summary>
    public async Task ShowStartupUpdateAsync(StartupUpdateInfo info)
    {
        if (_dialogOpen || _dataDialogOpen || _settingsOpen)
            return;
        _dialogOpen = true;
        try
        {
            var dialog = new UpdateDialog(info)
            {
                XamlRoot = XamlRoot,
                // Popups do not inherit the window root RequestedTheme — pin the dialog to it.
                RequestedTheme = ActualTheme,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await Launcher.LaunchUriAsync(info.Target);

            // 「不再提示」 works regardless of which button closed the dialog:
            // the user's "stop nagging me" is the strongest signal in the room.
            if (dialog.MuteRequested)
                await DisableStartupUpdateCheckAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    /// <summary>
    /// Turn the setting off (the checkbox path AND the settings-row path share
    /// the same stored flag). Loaded-modified-saved via the service — the
    /// SettingsViewModel's own snapshot is not touched from here (its panel is
    /// closed by construction in every path that reaches this method).
    /// </summary>
    private async Task DisableStartupUpdateCheckAsync()
    {
        try
        {
            var settings = App.Services.GetRequiredService<ISettingsService>();
            var snapshot = await settings.LoadAsync();
            if (snapshot.CheckUpdatesOnStartup)
                await settings.SaveAsync(snapshot with { CheckUpdatesOnStartup = false });
        }
        catch (Exception ex)
        {
            // The dialog already closed — a failed persist only means the next
            // session offers the notice once more.
            App.Services.GetRequiredService<ILogger<NotesPage>>()
                .LogWarning(ex, "Failed to persist CheckUpdatesOnStartup = false.");
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
        // toast back to it.
        App.DispatcherQueue.TryEnqueue(
            () => ShowErrorToast(AppStrings.GetString("ImageErrorTitle"), message));
    }

    private void ShowErrorToast(string title, string message) => ResultTip.ShowError(title, message);

}
