using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using QNote.Controls;
using QNote.Converters;
using QNote.Models;
using QNote.Services;
using QNote.ViewModels;
using Windows.System;
using Windows.UI.StartScreen;
using Windows.UI.ViewManagement;

namespace QNote.Views;

/// <summary>
/// The three-pane todo screen (MS To Do style, a4 mockup). Code-behind is UI
/// wiring only: it resolves the VM from DI, forwards clicks/keys to it, hosts
/// the list dialogs and the SAME settings surface NotesPage uses (the settings
/// choreography is intentionally duplicated here — stage ② keeps the notes
/// domain byte-stable per the zero-regression acceptance; consolidation is
/// deferred to the stage ③④ settings rework), and shows the delete-undo toast.
/// </summary>
public sealed partial class TodoPage : Page
{
    private bool _settingsOpen;
    private bool _dialogOpen;
    private bool _dataDialogOpen;
    private readonly Dictionary<int, FontIcon> _themeChecks = new();
    private readonly Dictionary<int, FontIcon> _sortChecks = new();

    /// <summary>
    /// Completes when the initial <see cref="ViewModel.LoadAsync"/> finished —
    /// the toast deep-link path (task 10-04 stage ③) can land right after the
    /// mode-switch Navigate, before Loaded ever fired; it awaits this before
    /// looking the task up in the (then populated) collections.
    /// </summary>
    private readonly TaskCompletionSource _initialLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TodoPageViewModel ViewModel { get; }

    public TodoPage()
    {
        ViewModel = App.Services.GetRequiredService<TodoPageViewModel>();
        InitializeComponent();

        Loaded += OnLoaded;
        // A language/theme-mode switch re-navigates the root frame and discards
        // this page (no caching) — tear down page-owned sources.
        Unloaded += OnPageUnloaded;

        ViewModel.TaskDeleted += OnTaskDeleted;

        // The detail pane's FIXED initial width (360) can overflow the window
        // on first show (the ✕ ends half-cut at the right edge) — apply the
        // same dynamic clamp the splitter drag uses at show time and on every
        // root resize, so the pane always fits the available layout width.
        RootGrid.SizeChanged += OnRootResized;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // x:Bind functions do not re-run when only the theme changed — re-raise
        // the brush-feeding properties on every card (see TodoTaskItemViewModel).
        ActualThemeChanged += (_, _) => ViewModel.RefreshTheme();

        // List-options ⋯ menu (user 2026-10-16): swatch grid + sort rows are
        // built in code-behind (15 tiny cells beat a templated ItemsRepeater;
        // their active checks follow the VM and initialize when the flyout opens).
        BuildThemeSwatchGrid();
        BuildSortRows();

        // High Contrast gates the themed pane overlay (AccessibilitySettings is
        // WinRT — the VM stays WinUI-free, so the page pushes the flag in).
        // Best-effort: the EVENT subscription throws 0x80070490 on some
        // sessions — the flag is still read once; a mid-session flip then only
        // takes effect after a restart (verified crash 2026-10-16).
        try
        {
            var accessibility = new AccessibilitySettings();
            ViewModel.IsHighContrast = accessibility.HighContrast;
            accessibility.HighContrastChanged += (_, _) => ViewModel.IsHighContrast = accessibility.HighContrast;
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<TodoPage>>()
                .LogWarning(ex, "High-Contrast tracking unavailable; the pane overlay is gated by the startup value only.");
        }
    }

    /// <summary>主题 grid: the default-gradient cell + the catalog (5 per row).
    /// Ids ride the button Tag; active checks follow ActiveThemeId changes.</summary>
    private void BuildThemeSwatchGrid()
    {
        var options = new[] { new TodoTheme.ThemeOption(0, string.Empty, null) }
            .Concat(TodoTheme.Options);
        var perRow = 5;
        var index = 0;
        foreach (var option in options)
        {
            if (index % perRow == 0)
                ThemeSwatchGrid.RowDefinitions.Add(new RowDefinition());
            var check = new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 14,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                Visibility = Visibility.Collapsed,
            };
            _themeChecks[option.Id] = check;
            var cell = new Button
            {
                Tag = option.Id,
                Width = 44, // fixed size: the grid lives in a vertical StackPanel,
                Height = 44, // where Star columns get NO width to distribute
                Margin = new Thickness(2), // (2026-10-16: stretch collapsed all 15
                Padding = new Thickness(0), // cells into one sliver — user report)
                CornerRadius = new CornerRadius(6),
                Style = (Style)Resources["TodoIconButtonStyle"],
                Content = new Grid
                {
                    Children =
                    {
                        new Border
                        {
                            // The icon-button template CENTER-aligns content and a
                            // plain-background Border desires 0x0 (blank cells —
                            // user 2026-10-16); the preview must carry the FULL
                            // cell size itself (MS To Do: color fills the cell).
                            Width = 44,
                            Height = 44,
                            CornerRadius = new CornerRadius(6),
                            Background = TodoTheme.CellPreviewBrush(option.Id),
                        },
                        check,
                    },
                },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(cell, $"TodoTheme{option.Id}");
            cell.Click += ThemeCell_Click;
            Grid.SetRow(cell, index / perRow);
            Grid.SetColumn(cell, index % perRow);
            ThemeSwatchGrid.Children.Add(cell);
            index++;
        }
        for (var c = 0; c < perRow; c++)
            ThemeSwatchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    }

    /// <summary>排序依据 flyout rows: importance / due date / title / created.</summary>
    private void BuildSortRows()
    {
        var defs = new (int Mode, string LabelKey, string Glyph)[]
        {
            ((int)TodoSortMode.Importance, "TodoSortByImportance", "\uF53D"),
            ((int)TodoSortMode.DueDate, "TodoSortByDueDate", "\uF53C"),
            ((int)TodoSortMode.Title, "TodoSortByTitle", "\uF53E"),
            ((int)TodoSortMode.Created, "TodoSortByCreated", "\uF53B"),
        };
        foreach (var (mode, labelKey, glyph) in defs)
        {
            var check = new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 13,
                // Theme-dictionary brushes resolve ONLY via the merged-dictionary
                // walk (a direct Resources[key] Lookup throws KeyNotFound —
                // learned 2026-10-16: TodoPage construction died on Navigate).
                Foreground = HexToBrushConverter.ResolveThemeBrush("BrandAccentBrush"),
                Visibility = Visibility.Collapsed,
            };
            _sortChecks[mode] = check;
            var icon = new FontIcon
            {
                Glyph = glyph,
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Resources["QNoteIconsFont"],
                FontSize = 15,
                Foreground = HexToBrushConverter.ResolveThemeBrush("TextFillColorSecondaryBrush"),
            };
            var label = new TextBlock
            {
                Text = AppStrings.GetString(labelKey),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
            };
            var content = new Grid { ColumnSpacing = 10 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.Children.Add(check);
            content.Children.Add(icon);
            content.Children.Add(label);
            Grid.SetColumn(check, 0);
            Grid.SetColumn(icon, 1);
            Grid.SetColumn(label, 2);

            var row = new Button
            {
                Tag = mode,
                MinHeight = 40,
                Padding = new Thickness(8, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Style = (Style)Resources["TodoRowButtonStyle"],
                Content = content,
            };
            row.Click += SortMode_Click;
            SortFlyoutPanel.Children.Add(row);
        }
    }

    private void OnRootResized(object sender, SizeChangedEventArgs e) => ClampDetailWidth();

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TodoPageViewModel.ActiveThemeId))
            RefreshThemeChecks();

        if (e.PropertyName != nameof(TodoPageViewModel.HasSelectedTask))
            return;
        if (ViewModel.HasSelectedTask)
        {
            ClampDetailWidth();
        }
        else
        {
            // The pane hides by collapsing ITS COLUMN (ColumnDefinition has no
            // Visibility) — the remembered width returns on the next show.
            DetailColumn.Width = new GridLength(0);
        }
    }

    /// <summary>Keep the detail column within the layout's reach (nav + 2 splitters + the middle list's MinWidth must always fit). The extra 8 px reserve compensates the frameless window's invisible right resize border, so the pane keeps a visible right margin like the left panes.</summary>
    private void ClampDetailWidth()
    {
        if (!ViewModel.HasSelectedTask)
            return;
        var maxByLayout = RootGrid.ActualWidth - NavColumn.ActualWidth - 16 - 320 - 8;
        App.Services.GetRequiredService<ILogger<TodoPage>>().LogInformation(
            "ClampDetailWidth: root={Root} nav={Nav} max={Max} current={Current}",
            RootGrid.ActualWidth, NavColumn.ActualWidth, maxByLayout, _detailWidth);
        _detailWidth = Math.Clamp(_detailWidth, 300, Math.Max(300, Math.Min(520, maxByLayout)));
        DetailColumn.Width = new GridLength(_detailWidth);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.TaskDeleted -= OnTaskDeleted;
        RootGrid.SizeChanged -= OnRootResized;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Detach();
        ResultTip.Dismiss();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await RestorePaneWidthsAsync();
            await ViewModel.LoadAsync();
        }
        finally
        {
            _initialLoad.TrySetResult();
        }
    }

    // ---------- pane width persistence (user 2026-10-15: mode switches recreate
    // the page — dragged widths must survive the round trip AND app restarts) ----------

    /// <summary>Apply the persisted splitter widths (0 = never dragged → XAML
    /// defaults). Clamps match the drag handlers; OnRootResized re-clamps
    /// against the live layout afterwards.</summary>
    private async Task RestorePaneWidthsAsync()
    {
        try
        {
            var snapshot = await App.Services.GetRequiredService<ISettingsService>().LoadAsync();
            App.Services.GetRequiredService<ILogger<TodoPage>>().LogInformation(
                "Restoring todo pane widths: nav={Nav} detail={Detail}.",
                snapshot.TodoNavPaneWidth, snapshot.TodoDetailPaneWidth);
            if (snapshot.TodoNavPaneWidth > 0)
                NavColumn.Width = new GridLength(Math.Clamp(snapshot.TodoNavPaneWidth, 240, 420));
            if (snapshot.TodoDetailPaneWidth > 0)
            {
                // Prime the REMEMBERED width only — the detail pane starts
                // COLLAPSED (its column is 0). Restoring the column width here
                // would reserve 360px of blank space right of the middle pane
                // until a task opens; ClampDetailWidth applies it at show time.
                _detailWidth = Math.Clamp(snapshot.TodoDetailPaneWidth, 300, 520);
            }
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<TodoPage>>()
                .LogWarning(ex, "Failed to restore the todo pane widths; using defaults.");
        }
    }

    /// <summary>Drag-completion persist hook (both splitters; DragDelta would
    /// hammer the settings store).</summary>
    private async void PaneSplitter_DragCompleted(object? sender, EventArgs e)
    {
        try
        {
            var settings = App.Services.GetRequiredService<ISettingsService>();
            var snapshot = await settings.LoadAsync();
            await settings.SaveAsync(snapshot with
            {
                TodoNavPaneWidth = (int)Math.Round(NavColumn.ActualWidth),
                TodoDetailPaneWidth = (int)Math.Round(_detailWidth),
            });
            App.Services.GetRequiredService<ILogger<TodoPage>>().LogInformation(
                "Persisted todo pane widths: nav={Nav} detail={Detail}.",
                (int)Math.Round(NavColumn.ActualWidth), (int)Math.Round(_detailWidth));
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<TodoPage>>()
                .LogWarning(ex, "Failed to persist the todo pane widths.");
        }
    }

    /// <summary>
    /// Toast deep-link target: select one task by uuid so the detail pane opens
    /// on it. Awaits the initial load — see <see cref="_initialLoad"/>.
    /// </summary>
    public async Task SelectTaskByUuidAsync(string taskUuid)
    {
        await _initialLoad.Task;
        await ViewModel.SelectTaskByUuidAsync(taskUuid);
    }

    // ---------- undo toast ----------

    private void OnTaskDeleted(string title) =>
        ResultTip.ShowSuccess(
            AppStrings.GetString("TodoDeletedToastTitle"),
            title,
            AppStrings.GetString("TodoUndoAction"),
            () => _ = ViewModel.UndoDeleteAsync());

    // ---------- mode switch (segmented control, shared with NotesPage) ----------

    private void ModeSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (App.Window is MainWindow window)
            window.SwitchToMode(sender.SelectedItem == TodoModeItem);
    }

    // ---------- left pane: nav rows + list CRUD ----------

    private static T? FromContext<T>(object sender) where T : class =>
        (sender as FrameworkElement)?.DataContext as T ?? (sender as FrameworkElement)?.Tag as T;

    private void NavRow_Click(object sender, RoutedEventArgs e)
    {
        if (FromContext<TodoNavItemViewModel>(sender) is { } nav)
            ViewModel.SelectNav(nav);
    }

    private async void NewListButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TodoListDialog(AppStrings.GetString("TodoNewListTitle"), string.Empty, "#3B82F6")
        {
            XamlRoot = XamlRoot,
            // Popups do not inherit the window root RequestedTheme — pin the dialog to it.
            RequestedTheme = ActualTheme,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var error = await ViewModel.CreateListAsync(dialog.ListName, dialog.ColorHex);
        if (error is not null)
            await ShowErrorDialogAsync(AppStrings.GetString("TodoNewListTitle"), AppStrings.GetString(error));
    }

    private async void RenameListMenu_Click(object sender, RoutedEventArgs e)
    {
        if (FromContext<TodoNavItemViewModel>(sender) is not { } nav)
            return;

        var dialog = new TodoListDialog(
            AppStrings.GetString("TodoRenameListTitle"), nav.Title, nav.ColorHex.Length > 0 ? nav.ColorHex : "#3B82F6")
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var error = await ViewModel.RenameListAsync(nav, dialog.ListName);
        if (error is not null)
            await ShowErrorDialogAsync(AppStrings.GetString("TodoRenameListTitle"), AppStrings.GetString(error));
    }

    private async void DeleteListMenu_Click(object sender, RoutedEventArgs e)
    {
        if (FromContext<TodoNavItemViewModel>(sender) is not { } nav)
            return;

        // Task count for the warning = the row's badge (live, incomplete count).
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = AppStrings.GetString("TodoDeleteListTitle"),
            Content = AppStrings.GetFormat("TodoDeleteListContentFormat", nav.Title, nav.IncompleteCount),
            PrimaryButtonText = AppStrings.GetString("DeleteButton"),
            CloseButtonText = AppStrings.GetString("CancelButton"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var error = await ViewModel.DeleteListAsync(nav);
        if (error is not null)
            await ShowErrorDialogAsync(AppStrings.GetString("TodoDeleteListTitle"), AppStrings.GetString(error));
    }

    // ---------- task cards: independent click targets (checkbox / star / body) ----------

    private void TaskCheck_Click(object sender, RoutedEventArgs e)
    {
        if (FromContext<TodoTaskItemViewModel>(sender) is { } item)
            _ = ViewModel.ToggleCompleteAsync(item);
    }

    private void TaskStar_Click(object sender, RoutedEventArgs e)
    {
        if (FromContext<TodoTaskItemViewModel>(sender) is { } item)
            _ = ViewModel.ToggleImportantAsync(item);
    }

    private void TaskDeleteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (FromContext<TodoTaskItemViewModel>(sender) is { } item)
            _ = ViewModel.DeleteTaskAsync(item);
    }

    private void CompletedTask_Click(object sender, RoutedEventArgs e)
    {
        if (FromContext<TodoTaskItemViewModel>(sender) is { } item)
            ViewModel.SelectedTask = item; // tail list is SelectionMode=None — body click opens the detail pane
    }

    /// <summary>
    /// OneWay selection bridge (2026-10-15 bug #3): SelectedTask can hold a
    /// COMPLETED row, which is NOT in TaskList's ItemsSource — the old TwoWay
    /// binding coerced the ListView's SelectedItem to null and wrote null back
    /// into the VM, closing the pane the click had just opened. The ListView
    /// now pushes real user picks only; its programmatic nulls (view reloads,
    /// off-list selections coerced away) never clear the VM selection.
    /// </summary>
    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TaskList.SelectedItem is TodoTaskItemViewModel item)
            ViewModel.SelectedTask = item;
    }

    // ---------- list-options ⋯ menu (user 2026-10-16, real-To-Do parity) ----------

    /// <summary>Secondary-tile deep link (固定到开始): select the list by uuid
    /// after the initial load (NavItems may still be filling at cold start).</summary>
    public async Task SelectListByUuidAsync(string listUuid)
    {
        await _initialLoad.Task;
        ViewModel.SelectListByUuid(listUuid);
    }

    private void ListOptionsFlyout_Opening(object sender, object e) => RefreshThemeChecks();

    private void RefreshThemeChecks()
    {
        foreach (var (id, check) in _themeChecks)
            check.Visibility = id == ViewModel.ActiveThemeId ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ThemeCell_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is int id)
            _ = ViewModel.SetCurrentThemeAsync(id); // flyout stays open for more picks
    }

    private void SortRow_Click(object sender, RoutedEventArgs e) => SortFlyout.ShowAt(SortRowButton);

    private void SortFlyout_Opening(object sender, object e)
    {
        foreach (var (mode, check) in _sortChecks)
            check.Visibility = mode == ViewModel.ActiveSortMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SortMode_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is int mode)
            _ = ViewModel.SetCurrentSortModeAsync(mode);
        SortFlyout.Hide();
    }

    private void HideCompletedRow_Click(object sender, RoutedEventArgs e) =>
        _ = ViewModel.SetCurrentHideCompletedAsync(!ViewModel.ActiveHideCompleted);

    private void HideSmartList_Click(object sender, RoutedEventArgs e)
    {
        ListOptionsFlyout.Hide();
        _ = ViewModel.HideCurrentSmartListAsync();
        // No confirmation toast (user 2026-10-16): the RESTORE affordances are
        // the 显示智能列表 row on 全部 and the 设置 → 待办 switches.
    }

    /// <summary>显示智能列表 (⋯ on 全部): restore-all; the durable per-view
    /// control lives in 设置 → 待办.</summary>
    private void RestoreSmartLists_Click(object sender, RoutedEventArgs e) =>
        _ = ViewModel.RestoreAllSmartListsAsync();

    /// <summary>Plain-text export of the current view (email deep link shares it).</summary>
    private (string Title, string Body) BuildListExport()
    {
        var title = TodoText.NavTitleText(ViewModel.HeroTitleKey, ViewModel.HeroTitleRaw);
        var builder = new StringBuilder();
        foreach (var t in ViewModel.Tasks)
            builder.Append("☐ ").AppendLine(t.Title);
        if (ViewModel.CompletedTasks.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine(AppStrings.GetString("TodoCompletedLabel") + ":");
            foreach (var t in ViewModel.CompletedTasks)
                builder.Append("☑ ").AppendLine(t.Title);
        }
        return (title, builder.ToString().TrimEnd());
    }

    private async void EmailList_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (title, body) = BuildListExport();
            var uri = new Uri($"mailto:?subject={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}");
            await Launcher.LaunchUriAsync(uri); // default mail client; absence is a silent no-op
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<TodoPage>>()
                .LogWarning(ex, "Failed to hand the todo list to the mail client.");
        }
    }

    private async void PinList_Click(object sender, RoutedEventArgs e)
    {
        var nav = ViewModel.SelectedNav;
        if (nav?.Kind != TodoNavKind.List || nav.List is not { } list)
            return;
        try
        {
            var tileId = "qnote-todo-list-" + list.Uuid;
            // Anchor for the OS confirmation flyout: under the ⋯ menu row.
            var anchor = (FrameworkElement)sender;
            var point = anchor.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, anchor.ActualHeight));
            foreach (var existing in await SecondaryTile.FindAllAsync())
            {
                if (existing.TileId == tileId)
                {
                    // Toggle: unpin an already-pinned list.
                    await existing.RequestDeleteAsync(point);
                    return;
                }
            }

            var tile = new SecondaryTile(
                tileId,
                TodoListDisplayNames.Resolve(list.Name),
                "todo-list:" + list.Uuid,
                new Uri("ms-appx:///Assets/Square150x150Logo.scale-200.png"),
                TileSize.Square150x150);
            await tile.RequestCreateAsync(point);
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<TodoPage>>()
                .LogWarning(ex, "Failed to pin/unpin the todo list {Uuid} to Start.", list.Uuid);
        }
    }

    // ---------- keyboard ----------

    private void QuickAdd_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        _ = ViewModel.QuickAddAsync(); // TextBox keeps focus for continuous entry
    }

    private void TaskList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Space/Enter toggle the selected task; Esc = save & close (real To Do:
        // autosave is the only channel, Esc mirrors the ✕ button). Skips presses
        // originating on a button inside the row (its own Click wins).
        if (e.OriginalSource is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
            return;
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            _ = ViewModel.SaveDetailAsync();
        }
        else if ((e.Key is VirtualKey.Enter or VirtualKey.Space) && ViewModel.SelectedTask is { } item)
        {
            e.Handled = true;
            _ = ViewModel.ToggleCompleteAsync(item);
        }
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
            return;
        e.Handled = true;
        _ = ViewModel.SaveDetailAsync();
    }

    // ---------- detail pane ----------

    private void DetailCheck_Click(object sender, RoutedEventArgs e) => _ = ViewModel.ToggleDetailCompleteAsync();

    private void DetailStar_Click(object sender, RoutedEventArgs e) => _ = ViewModel.ToggleDetailImportantAsync();

    // Remind row: a plain property row (real To Do has NO toggle switch) — the
    // click opens the due-edit picker via the row's Button.Flyout (a reminder
    // needs a moment in time; setting one there turns the reminder on by
    // Decision 4). Turning it off is the trailing ✕ (value state only).
    private void DetailReminderOff_Click(object sender, RoutedEventArgs e) =>
        _ = ViewModel.SetDetailReminderAsync(false);

    // ---------- due-date / reminder popovers (stage-1 presets + stage-2 calendar) ----------

    // CalendarView exposes a SelectedDates COLLECTION (no single-date property).

    private static void SelectDate(CalendarView calendar, DateTimeOffset date)
    {
        calendar.SelectedDates.Clear();
        calendar.SelectedDates.Add(date);
    }

    private static DateTimeOffset? SelectedDate(CalendarView calendar) =>
        calendar.SelectedDates.Count > 0 ? calendar.SelectedDates[0] : null;

    private static DateTime NextMonday(DateTime date)
    {
        var days = ((int)DayOfWeek.Monday - (int)date.DayOfWeek + 7) % 7;
        return date.AddDays(days == 0 ? 7 : days);
    }

    // Stage-1 popovers: width synced to the row + right-aligned weekday
    // subtitles, computed at open (real-To-Do popover form).

    private void DueMenuFlyout_Opening(object sender, object e)
    {
        DueMenuPanel.MinWidth = Math.Max(240, DueRowButton.ActualWidth - 12);
        var today = DateTime.Now;
        DueTodaySub.Text = today.ToString("dddd");
        DueTomorrowSub.Text = today.AddDays(1).ToString("dddd");
        DueNextWeekSub.Text = NextMonday(today).ToString("dddd");
    }

    private void RemindMenuFlyout_Opening(object sender, object e)
    {
        RemindMenuPanel.MinWidth = Math.Max(240, RemindRowButton.ActualWidth - 12);
        var today = DateTime.Now;
        RemindTodaySub.Text = "18:00";
        RemindTomorrowSub.Text = today.AddDays(1).ToString("dddd") + " 9:00";
        RemindNextWeekSub.Text = NextMonday(today).ToString("dddd") + " 9:00";
    }

    private void DueToday_Click(object sender, RoutedEventArgs e)
    {
        DueMenuFlyout.Hide(); // buttons inside a Flyout do NOT auto-close it
        _ = ViewModel.ApplyDuePresetAsync(0);
    }

    private void DueTomorrow_Click(object sender, RoutedEventArgs e)
    {
        DueMenuFlyout.Hide();
        _ = ViewModel.ApplyDuePresetAsync(1);
    }

    private void DueNextWeek_Click(object sender, RoutedEventArgs e)
    {
        DueMenuFlyout.Hide();
        _ = ViewModel.ApplyDuePresetAsync(7);
    }

    /// <summary>选择日期 (due stage-2): seed the calendar from the task, then show.</summary>
    private void DuePickDate_Click(object sender, RoutedEventArgs e)
    {
        DueMenuFlyout.Hide();
        SelectDate(DueCalendar, ViewModel.DetailDueDate);
        DueEditFlyout.ShowAt(DueRowButton);
    }

    /// <summary>Due stage-2 保存: all-day due on the picked calendar date.</summary>
    private void DueEditSave_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDate(DueCalendar) is { } date)
            ViewModel.DetailDueDate = date;
        _ = ViewModel.ApplyDueDateAsync();
        DueEditFlyout.Hide(); // buttons inside a Flyout do NOT auto-close it
    }

    /// <summary>Stage-2 取消: discard unapplied picker edits, keep the row value.</summary>
    private void DueEditCancel_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetDueEditState();
        DueEditFlyout.Hide();
    }

    // Reminder stage-1 presets (real To Do): later today / tomorrow / next week / pick.

    private void RemindLaterToday_Click(object sender, RoutedEventArgs e)
    {
        RemindMenuFlyout.Hide(); // buttons inside a Flyout do NOT auto-close it
        _ = ViewModel.ApplyReminderPresetAsync(0);
    }

    private void RemindTomorrow_Click(object sender, RoutedEventArgs e)
    {
        RemindMenuFlyout.Hide();
        _ = ViewModel.ApplyReminderPresetAsync(1);
    }

    private void RemindNextWeek_Click(object sender, RoutedEventArgs e)
    {
        RemindMenuFlyout.Hide();
        _ = ViewModel.ApplyReminderPresetAsync(2);
    }

    /// <summary>选择日期和时间 (reminder stage-2): seed the calendar + time, then show.</summary>
    private void RemindPickDateTime_Click(object sender, RoutedEventArgs e)
    {
        RemindMenuFlyout.Hide();
        SelectDate(RemindCalendar, ViewModel.DetailDueDate);
        RemindTimePicker.Time = ViewModel.DetailDueTime;
        RemindEditFlyout.ShowAt(RemindRowButton);
    }

    /// <summary>Reminder stage-2 保存: timed due ⇒ reminder on (Decision 4).</summary>
    private void RemindEditSave_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDate(RemindCalendar) is { } date)
            ViewModel.DetailDueDate = date;
        ViewModel.DetailDueTime = RemindTimePicker.Time;
        ViewModel.DetailIsAllDay = false;
        _ = ViewModel.ApplyDueEditAsync();
        RemindEditFlyout.Hide(); // buttons inside a Flyout do NOT auto-close it
    }

    private void RemindEditCancel_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetDueEditState();
        RemindEditFlyout.Hide();
    }

    private void DueClear_Click(object sender, RoutedEventArgs e) => _ = ViewModel.ClearDueAsync();

    // 重复 / 添加文件 / 添加步骤 (stage ⑤): real features, no more placeholders.

    private void RepeatDaily_Click(object sender, RoutedEventArgs e)
    {
        RepeatMenuFlyout.Hide(); // buttons inside a Flyout do NOT auto-close it
        _ = ViewModel.SetRecurrenceAsync(TodoRecurrence.Daily);
    }

    private void RepeatWeekdays_Click(object sender, RoutedEventArgs e)
    {
        RepeatMenuFlyout.Hide();
        _ = ViewModel.SetRecurrenceAsync(TodoRecurrence.Weekdays);
    }

    private void RepeatWeekly_Click(object sender, RoutedEventArgs e)
    {
        RepeatMenuFlyout.Hide();
        _ = ViewModel.SetRecurrenceAsync(TodoRecurrence.Weekly);
    }

    private void RepeatMonthly_Click(object sender, RoutedEventArgs e)
    {
        RepeatMenuFlyout.Hide();
        _ = ViewModel.SetRecurrenceAsync(TodoRecurrence.Monthly);
    }

    private void RepeatYearly_Click(object sender, RoutedEventArgs e)
    {
        RepeatMenuFlyout.Hide();
        _ = ViewModel.SetRecurrenceAsync(TodoRecurrence.Yearly);
    }

    /// <summary>行尾 ✕（To Do ClearButton「从不重复」）：清除规则。</summary>
    private void RepeatClear_Click(object sender, RoutedEventArgs e) =>
        _ = ViewModel.SetRecurrenceAsync(null);

    /// <summary>
    /// 自定义 (real-To-Do parity, user ruling 2026-10-15): 重复周期 dialog —
    /// 每 [N] [天/周/月/年], 取消/保存. Seeds from the current rule (custom
    /// parsed; presets map to their 1-unit equivalent).
    /// </summary>
    private async void RepeatCustom_Click(object sender, RoutedEventArgs e)
    {
        RepeatMenuFlyout.Hide();
        if (_dialogOpen)
            return; // only one ContentDialog at a time (re-entrant ShowAsync throws)

        var interval = 1;
        var unitIndex = 0;
        if (TodoRecurrence.TryParseCustom(ViewModel.DetailRecurrenceRule, out var n, out var unit))
        {
            interval = n;
            unitIndex = Math.Max(0, Array.IndexOf(TodoRecurrence.CustomUnits, unit));
        }
        else if (ViewModel.DetailRecurrenceRule is { } preset)
        {
            unitIndex = preset switch
            {
                TodoRecurrence.Weekly => 1,
                TodoRecurrence.Monthly => 2,
                TodoRecurrence.Yearly => 3,
                _ => 0,
            };
        }

        var number = new NumberBox
        {
            Minimum = 1,
            Maximum = 999,
            SmallChange = 1,
            LargeChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = interval,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var units = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            SelectedIndex = unitIndex,
        };
        foreach (var key in new[] { "TodoRecurrenceUnitDay", "TodoRecurrenceUnitWeek", "TodoRecurrenceUnitMonth", "TodoRecurrenceUnitYear" })
            units.Items.Add(AppStrings.GetString(key));

        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        Grid.SetColumn(units, 1);
        content.Children.Add(number);
        content.Children.Add(units);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = AppStrings.GetString("TodoCustomRecurrenceTitle"),
            Content = content,
            PrimaryButtonText = AppStrings.GetString("TodoCustomRecurrenceSave"),
            CloseButtonText = AppStrings.GetString("TodoCustomRecurrenceCancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        _dialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
            var value = double.IsNaN(number.Value) ? 1 : (int)Math.Round(number.Value);
            var chosen = TodoRecurrence.CustomUnits[Math.Clamp(units.SelectedIndex, 0, TodoRecurrence.CustomUnits.Length - 1)];
            _ = ViewModel.SetRecurrenceAsync(TodoRecurrence.MakeCustom(Math.Max(1, value), chosen));
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void StepInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        _ = ViewModel.AddStepFromInputAsync(); // box keeps focus for continuous entry
    }

    private void StepToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TodoStepItemViewModel step)
            _ = ViewModel.ToggleStepAsync(step);
    }

    private void StepDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TodoStepItemViewModel step)
            _ = ViewModel.DeleteStepAsync(step);
    }

    private async void AttachRow_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        var files = await picker.PickMultipleFilesAsync();

        // Fail fast on over-cap files with a visible toast — the service-side
        // cap check would otherwise only log (silent "nothing happened" UX).
        // Recomputed per file: the per-TASK total moves as each attach lands.
        var oversized = new List<string>();
        foreach (var file in files)
        {
            var props = await file.GetBasicPropertiesAsync();
            var used = ViewModel.DetailAttachments.Sum(a => a.ByteSize);
            if (used + (long)props.Size > TodoService.MaxTaskAttachmentBytes)
            {
                oversized.Add(file.Name);
                continue;
            }
            using var stream = await file.OpenStreamForReadAsync();
            await ViewModel.AttachFileAsync(file.Name, stream);
        }

        if (oversized.Count > 0)
            ResultTip.ShowError(
                AppStrings.GetString("TodoAttachTooLargeTitle"),
                AppStrings.GetFormat("TodoAttachTooLargeMessage", string.Join("、", oversized)));
    }

    private async void AttachmentOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TodoAttachment attachment)
            return;
        try
        {
            var store = App.Services.GetRequiredService<ITodoAttachmentStore>();
            var path = await store.MaterializeForOpenAsync(attachment.Sha, attachment.FileName);
            await Launcher.LaunchUriAsync(new Uri(path)); // temp copy → default app
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<TodoPage>>()
                .LogWarning(ex, "Failed to open attachment {Sha}.", attachment.Sha);
        }
    }

    private void AttachmentDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TodoAttachment attachment)
            _ = ViewModel.DeleteAttachmentAsync(attachment);
    }

    // ---------- pane splitters (mstodo-ui-extraction §1: 8px Thumb, fixed
    // side panes; clamps are DYNAMIC — the middle list's MinWidth must always
    // fit, otherwise the grid overflows and clips the detail pane's right
    // edge children out of the window). ----------

    private void NavSplitter_DragDelta(object sender, double delta)
    {
        var detailWidth = DetailSplitter.Visibility == Visibility.Visible ? DetailPane.ActualWidth : 0;
        var maxByLayout = RootGrid.ActualWidth - 16 - 320 - detailWidth;
        var width = NavColumn.ActualWidth + delta;
        NavColumn.Width = new GridLength(Math.Clamp(width, 240, Math.Max(240, Math.Min(420, maxByLayout))));
    }

    private double _detailWidth = 360; // remembered across show/hide; splitter-dragged

    private void DetailSplitter_DragDelta(object sender, double delta)
    {
        var maxByLayout = RootGrid.ActualWidth - NavColumn.ActualWidth - 16 - 320;
        _detailWidth = Math.Clamp(_detailWidth - delta, 300, Math.Max(300, Math.Min(520, maxByLayout)));
        DetailColumn.Width = new GridLength(_detailWidth);
    }

    // ---------- settings (same surface as NotesPage; duplicated on purpose —
    // the notes page stays byte-stable this stage, see the class remarks) ----------

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
            void OnThemeChanged(FrameworkElement sender2, object args) => dialog.RequestedTheme = ActualTheme;
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

    /// <summary>About dialog (settings panel → 关于 → 关于…). Read-only.</summary>
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
            // Flush BEFORE the restore: overwrite swaps the DB file, so pending
            // todo edits would otherwise be flushed into the restored DB afterwards.
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
    /// friendly message (the technical reason stays in the log).
    /// </summary>
    private async Task ShowUpdateResultDialogAsync(UpdateCheckResult result)
    {
        // Only one ContentDialog may be open at a time (a re-entrant ShowAsync
        // throws); the flag chain keeps a late check result from stacking on an
        // open data/settings dialog (the result is dropped in those corners —
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
            else if (result.Status == UpdateCheckStatus.UpToDate)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = ActualTheme,
                    Title = AppStrings.GetString("CheckUpdateTitle"),
                    Content = AppStrings.GetString("UpToDateContent"),
                    CloseButtonText = AppStrings.GetString("GotItButton"),
                    DefaultButton = ContentDialogButton.Close,
                };
                await dialog.ShowAsync();
            }
            else
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = ActualTheme,
                    Title = AppStrings.GetString("CheckUpdateTitle"),
                    Content = AppStrings.GetString("CheckUpdateFailedContent"),
                    CloseButtonText = AppStrings.GetString("GotItButton"),
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

    /// <summary>
    /// Startup update dialog (1.5.1): fed by MainWindow ~5 s after the first
    /// activation when <see cref="IStartupUpdateCheck"/> found a newer version.
    /// Same one-dialog guards as every other page dialog.
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
                RequestedTheme = ActualTheme,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await Launcher.LaunchUriAsync(info.Target);

            // 「不再提示」 works regardless of which button closed the dialog.
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
    /// the same stored flag). Loaded-modified-saved via the service.
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
            App.Services.GetRequiredService<ILogger<TodoPage>>()
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
                RequestedTheme = ActualTheme,
                Title = title,
                Content = message,
                CloseButtonText = AppStrings.GetString("GotItButton"),
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }
}
