using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using QNote.Controls;
using QNote.Services;
using QNote.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace QNote.Views;

/// <summary>
/// The three-pane notes screen. Code-behind is UI wiring only: it resolves the VM
/// from DI, forwards the ListView selection / Loaded / Ctrl+S to the VM, hosts the
/// delete-confirmation dialog (UI types stay out of the VM), and glues the
/// <see cref="RichTextEditorController"/> (view-side RichEditBox wrapper) to the VM's
/// RTF data flow.
/// </summary>
public sealed partial class NotesPage : Page
{
    private readonly RichTextEditorController _editor;
    private bool _syncingToolbar;
    private bool _settingsOpen;
    private bool _dialogOpen;
    private bool _dataDialogOpen;

    public NotesPageViewModel ViewModel { get; }

    public NotesPage()
    {
        ViewModel = App.Services.GetRequiredService<NotesPageViewModel>();
        InitializeComponent();

        _editor = new RichTextEditorController(
            ContentEditor,
            App.Services.GetService<Microsoft.Extensions.Logging.ILogger<RichTextEditorController>>(),
            App.Services.GetService<IImageService>(),
            App.Services.GetService<INoteService>());
        ViewModel.EditorContentProvider = () =>
            new NotesPageViewModel.EditorSnapshot(_editor.GetRtf(), _editor.GetPlainText(), _editor.IsRtfChangedFromBaseline());
        _editor.CurrentNoteIdProvider = () => ViewModel.SelectedNote?.Id;
        _editor.OpenImageRequested += OnOpenImageRequested;
        _editor.ImportFailed += OnImageError;
        _editor.ImageOpenFailed += OnImageError;

        ViewModel.FocusTitleRequested += OnFocusTitleRequested;
        ViewModel.ContentReloadRequested += OnContentReloadRequested;
        ViewModel.SearchCompleted += OnSearchCompleted;
        ViewModel.SearchCleared += OnSearchCleared;
        _editor.ContentChanged += OnEditorContentChanged;
        _editor.SelectionChanged += UpdateToolbarState;

        FontFamilyBox.ItemsSource = FontCatalog.Families;
        FontSizeBox.ItemsSource = FontCatalog.SizesPx;
        ColorGrid.ItemsSource = ColorPalette.Colors;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await ViewModel.LoadAsync();

    private void OnFocusTitleRequested() => TitleBox.Focus(FocusState.Programmatic);

    // ---------- Images ----------

    private async void InsertImageButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                ViewMode = PickerViewMode.Thumbnail,
            };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".gif");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".tif");
            picker.FileTypeFilter.Add(".tiff");
            picker.FileTypeFilter.Add(".webp");

            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            if (await picker.PickSingleFileAsync() is { } file)
                await _editor.InsertImageFromFileAsync(file.Path);
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync("插入图片", $"插入图片失败：{ex.Message}");
        }
    }

    private void OnOpenImageRequested(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            OnImageError($"无法打开原图：{ex.Message}");
        }
    }

    // ---------- Drag & drop image files ----------

    private void ContentEditor_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
        e.DragUIOverride.Caption = "插入图片";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void ContentEditor_Drop(object sender, DragEventArgs e) =>
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

    // VM finished loading a note → push its RTF into the editor (suppressed inside
    // SetRtf, so this never marks the note dirty).
    private void OnContentReloadRequested()
    {
        _editor.SetRtf(ViewModel.EditingContentRtf);
        UpdatePlaceholder();
        UpdateToolbarState();
    }

    private void OnEditorContentChanged()
    {
        ViewModel.NotifyContentEdited();
        UpdatePlaceholder();
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

    // ---------- Toolbar ----------

    private void BoldButton_Click(object sender, RoutedEventArgs e) => ApplyFormat(_editor.ToggleBold);

    private void ItalicButton_Click(object sender, RoutedEventArgs e) => ApplyFormat(_editor.ToggleItalic);

    private void UnderlineButton_Click(object sender, RoutedEventArgs e) => ApplyFormat(_editor.ToggleUnderline);

    private void StrikethroughButton_Click(object sender, RoutedEventArgs e) => ApplyFormat(_editor.ToggleStrikethrough);

    private void AlignLeftButton_Click(object sender, RoutedEventArgs e) =>
        ApplyFormat(() => _editor.SetAlignment(ParagraphAlignment.Left));

    private void AlignCenterButton_Click(object sender, RoutedEventArgs e) =>
        ApplyFormat(() => _editor.SetAlignment(ParagraphAlignment.Center));

    private void AlignRightButton_Click(object sender, RoutedEventArgs e) =>
        ApplyFormat(() => _editor.SetAlignment(ParagraphAlignment.Right));

    private void BulletListButton_Click(object sender, RoutedEventArgs e) =>
        ApplyFormat(() => _editor.ToggleList(MarkerType.Bullet));

    private void NumberListButton_Click(object sender, RoutedEventArgs e) =>
        ApplyFormat(() => _editor.ToggleList(MarkerType.Arabic)); // decimal 1. 2. 3.

    private void FontFamilyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingToolbar || FontFamilyBox.SelectedItem is not FontOption font)
            return;
        ApplyFormat(() => _editor.SetFontFamily(font.Family));
    }

    private void FontSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingToolbar || FontSizeBox.SelectedItem is not int px)
            return;
        ApplyFormat(() => _editor.SetFontSizePx(px));
    }

    private void ColorGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PaletteColor color)
            return;
        ColorButton.Flyout.Hide();
        ApplyFormat(() => _editor.SetForegroundColor(color.Color));
    }

    private void ApplyFormat(Action apply)
    {
        apply();
        UpdateToolbarState();
        _editor.Focus(); // keep typing in the editor after a toolbar click
    }

    // Current selection's format → toolbar highlight / dropdown selection.
    private void UpdateToolbarState()
    {
        if (_syncingToolbar)
            return;

        _syncingToolbar = true;
        try
        {
            var s = _editor.GetSelectionState();

            BoldButton.IsChecked = s.Bold;
            ItalicButton.IsChecked = s.Italic;
            UnderlineButton.IsChecked = s.Underline;
            StrikethroughButton.IsChecked = s.Strikethrough;
            BulletListButton.IsChecked = s.BulletedList;
            NumberListButton.IsChecked = s.NumberedList;

            AlignLeftButton.IsChecked = s.Alignment == ParagraphAlignment.Left;
            AlignCenterButton.IsChecked = s.Alignment == ParagraphAlignment.Center;
            AlignRightButton.IsChecked = s.Alignment == ParagraphAlignment.Right;

            FontFamilyBox.SelectedItem = s.FontFamily is null
                ? null
                : FontCatalog.Families.FirstOrDefault(f => string.Equals(f.Family, s.FontFamily, StringComparison.OrdinalIgnoreCase));
            FontSizeBox.SelectedItem = s.FontSizePx is int px && FontCatalog.SizesPx.Contains(px) ? px : null;

            if (s.ForegroundColor is { } color)
                ColorButtonIcon.Foreground = new SolidColorBrush(color);
        }
        finally
        {
            _syncingToolbar = false; // a throw must not permanently freeze toolbar sync
        }
    }

    // The stock CommandBar template star-sizes the (empty) content column and lets
    // the Auto-sized command column (buttons + "...") hug the RIGHT edge. Collapse
    // the content column — the sibling of the PrimaryItemsControl inside its
    // parent grid — so the command cluster hugs the LEFT instead. Arrange-only
    // change: the items control keeps its own column/sizing, so the dynamic-
    // overflow measurement is unaffected. Runs on SizeChanged, not Loaded: the
    // editor grid starts Collapsed (no note selected), so the bar's template is
    // only applied when it first shows. Retries until the template exists.
    private bool _formatBarAligned;

    private void FormatBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_formatBarAligned)
            return;

        ItemsControl? primaryItems = null;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(FormatBar);
        while (queue.Count > 0 && primaryItems is null)
        {
            var d = queue.Dequeue();
            if (d is ItemsControl { Name: "PrimaryItemsControl" } items)
            {
                primaryItems = items;
                break;
            }
            int count = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < count; i++)
                queue.Enqueue(VisualTreeHelper.GetChild(d, i));
        }

        if (primaryItems is null ||
            VisualTreeHelper.GetParent(primaryItems) is not Grid { ColumnDefinitions.Count: >= 2 } parentGrid)
        {
            App.Services.GetService<ILogger<NotesPage>>()
                ?.LogWarning("FormatBar left-align fix: PrimaryItemsControl not found yet");
            return; // template not applied yet — the next SizeChanged retries
        }

        // Zero the star-sized sibling content column so the cluster starts at the
        // left edge; the MoreButton (in the outer Auto column) follows the cluster.
        int itemsColumn = Grid.GetColumn(primaryItems);
        if (itemsColumn > 0)
            parentGrid.ColumnDefinitions[0].Width = GridLength.Auto;
        primaryItems.HorizontalAlignment = HorizontalAlignment.Left;
        _formatBarAligned = true;
    }

    // The CommandBar overflow menu renders in a popup, which does not inherit the
    // window root's RequestedTheme (same rule as dialogs / flyouts) — pin it to the
    // current theme every time the overflow opens.
    private void FormatBar_Opened(object sender, object e)
    {
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
            if (popup.Child is FrameworkElement child)
                child.RequestedTheme = ActualTheme;
    }
}
