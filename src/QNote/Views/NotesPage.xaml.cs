using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using QNote.Controls;
using QNote.ViewModels;

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

    public NotesPageViewModel ViewModel { get; }

    public NotesPage()
    {
        ViewModel = App.Services.GetRequiredService<NotesPageViewModel>();
        InitializeComponent();

        _editor = new RichTextEditorController(
            ContentEditor,
            App.Services.GetService<Microsoft.Extensions.Logging.ILogger<RichTextEditorController>>());
        ViewModel.EditorContentProvider = () => (_editor.GetRtf(), _editor.GetPlainText());

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

    // VM raised a debounced search off-thread → marshal back to the UI thread and
    // swap the note list there (ObservableCollection is UI-thread-affine).
    private void OnSearchCompleted(IReadOnlyList<QNote.Models.NoteSummary> results, string keyword)
    {
        App.DispatcherQueue.TryEnqueue(() =>
        {
            var wasSelected = ViewModel.SelectedNote;
            ViewModel.Notes.Clear();
            foreach (var s in results)
                ViewModel.Notes.Add(new NoteItemViewModel(s) { Keyword = keyword });

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
                ViewModel.Notes.Add(new NoteItemViewModel(s));

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

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
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
                ColorSwatch.Background = new SolidColorBrush(color);
        }
        finally
        {
            _syncingToolbar = false; // a throw must not permanently freeze toolbar sync
        }
    }
}
