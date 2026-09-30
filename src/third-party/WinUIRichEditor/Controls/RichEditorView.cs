using System;
using System.Threading.Tasks;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

/// <summary>Drop-in host that bundles a <see cref="RichEditorToolbar"/> (which now carries the zoom · paper
/// size · orientation controls and the Export/Import/Print file actions built in), a <see cref="RichEditor"/>,
/// and a status bar (character/word count, caret line/column, and page count when paged). Built in code
/// (no XAML), AOT-friendly. Set <see cref="Document"/> to load content and <see cref="ImagePicker"/> to
/// enable the toolbar's image button.</summary>
public partial class RichEditorView : UserControl
{
    /// <summary>The embedded editor.</summary>
    public RichEditor Editor { get; }

    /// <summary>The embedded toolbar (owns the page/zoom controls and file actions).</summary>
    public RichEditorToolbar Toolbar { get; }

    private readonly TextBlock _status;
    private Border _statusBar = null!; // the status-bar container, toggled by ShowStatusBar

    /// <summary>Whether the status bar (character/word count · caret line/column · page count) is shown at
    /// the bottom. Default true. Mirrors the original AvaloniaRichEditor's <c>RichEditorView.ShowStatusBar</c>.</summary>
    public bool ShowStatusBar
    {
        get => (bool)GetValue(ShowStatusBarProperty);
        set => SetValue(ShowStatusBarProperty, value);
    }

    /// <summary>Identifies the <see cref="ShowStatusBar"/> dependency property.</summary>
    // The view's own settings are dependency properties so XAML can bind them (upstream's are StyledProperties).
    // Properties that only forward the EDITOR's state (Document, IsReadOnly, ZoomFactor, ...) stay plain: the
    // editor's own are dependency properties already, so a binding goes to Editor.Xxx and nothing has to keep
    // two copies in step.
    public static readonly DependencyProperty ShowStatusBarProperty = DependencyProperty.Register(
        nameof(ShowStatusBar), typeof(bool), typeof(RichEditorView),
        new PropertyMetadata(true, (d, e) => ((RichEditorView)d)._statusBar.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

    /// <summary>The editor's zoom factor (1.0 = 100%). A thin proxy over <see cref="RichEditor.Zoom"/> /
    /// <see cref="RichEditor.SetZoom"/> — the port keeps zoom on the editor (engine-level crisp scaling),
    /// and this restores the original AvaloniaRichEditor's <c>RichEditorView.ZoomFactor</c> surface.</summary>
    public double ZoomFactor
    {
        get => Editor.Zoom;
        set => Editor.SetZoom(value);
    }

    /// <summary>The document shown in the editor.</summary>
    public FlowDocument? Document
    {
        get => Editor.Document;
        set => Editor.Document = value;
    }

    /// <summary>Async image-bytes provider used by the toolbar's image button (forwarded to
    /// <see cref="RichEditorToolbar.ImagePicker"/>).</summary>
    public Func<Task<byte[]?>>? ImagePicker
    {
        get => Toolbar.ImagePicker;
        set => Toolbar.ImagePicker = value;
    }

    /// <summary>When true, the editor is read-only (a viewer): rendering + selection/copy, no editing.
    /// Forwards to <see cref="RichEditor.IsReadOnly"/>.</summary>
    public bool IsReadOnly
    {
        get => Editor.IsReadOnly;
        set => Editor.IsReadOnly = value;
    }

    /// <summary>Whether the read-only viewer still shows a (non-blinking) caret, so keyboard navigation
    /// — arrows, Shift+arrows, Home/End, PageUp/Down, Ctrl+F — has a visible position. Default false.
    /// Forwards to <see cref="RichEditor.ShowCaretWhenReadOnly"/>.</summary>
    public bool ShowCaretWhenReadOnly
    {
        get => Editor.ShowCaretWhenReadOnly;
        set => Editor.ShowCaretWhenReadOnly = value;
    }

    public RichEditorView()
    {
        Editor = new RichEditor();
        // The View is the full host, so its toolbar carries everything: page/zoom + file actions.
        Toolbar = new RichEditorToolbar { ToolbarLevel = ToolbarLevel.Maximum, Target = Editor };

        _status = new TextBlock
        {
            Margin = new Thickness(8, 4, 8, 4),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 0x55, 0x55, 0x55)),
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0 toolbar (incl. page/zoom + file actions)
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1 find bar (collapsed until Ctrl+F/H)
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 2 editor
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 3 status

        var toolbarBar = new Border
        {
            Child = Toolbar,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
        };
        Grid.SetRow(toolbarBar, 0);

        _findBarHost = new Border
        {
            Visibility = Visibility.Collapsed,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
            Background = new SolidColorBrush(Color.FromArgb(10, 0, 0, 0)),
        };
        Grid.SetRow(_findBarHost, 1);

        Grid.SetRow(Editor, 2);

        _statusBar = new Border
        {
            Child = _status,
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
            Background = new SolidColorBrush(Color.FromArgb(12, 0, 0, 0)),
        };
        Grid.SetRow(_statusBar, 3);

        grid.Children.Add(toolbarBar);
        grid.Children.Add(_findBarHost);
        grid.Children.Add(Editor);
        grid.Children.Add(_statusBar);
        Content = grid;

        Editor.StatusChanged += (_, _) => UpdateStatus();
        Editor.FindRequested += (_, withReplace) => ShowFindBar(withReplace); // Ctrl+F / Ctrl+H
        Loaded += (_, _) => { RichEditorLocalization.LanguageChanged += OnLanguageChanged; UpdateStatus(); };
        Unloaded += (_, _) => RichEditorLocalization.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => UpdateStatus();

    private void UpdateStatus()
    {
        var (chars, words, line, col) = Editor.GetStatus();
        string text = string.Format(RichEditorLocalization.GetString("StatusFormat"), chars, words, line, col);
        if (Editor.PageSize != RichEditorPageSize.Continuous)
            text += "   " + string.Format(RichEditorLocalization.GetString("PageCountFormat"), Editor.GetPrintPageCount());
        _status.Text = text;
    }
}
