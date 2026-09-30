using Microsoft.UI.Xaml;

namespace WinUIRichEditor.Controls;

// Feature flags gating optional capabilities (images, tables, rich paste). These are the capability layer:
// set them directly (there is no EditorMode preset). They only restrict an editable editor — a read-only
// viewer (IsReadOnly=true) blocks editing regardless of these.
public partial class RichEditor
{
    /// <summary>Identifies the <see cref="AllowImages"/> dependency property.</summary>
    public static readonly DependencyProperty AllowImagesProperty = DependencyProperty.Register(
        nameof(AllowImages), typeof(bool), typeof(RichEditor), new PropertyMetadata(true, OnToolbarFlagChanged));

    // The toolbar mirrors AllowImages / AllowTables / AllowFindReplace (its insert and find buttons) and
    // re-reads them on StatusChanged — which a flag change did not raise, so a host that switched a
    // capability off at run time kept showing its button until the next keystroke. Upstream's toolbar
    // subscribes to the property change itself; here the flag announces it through the same channel the
    // toolbar already listens on.
    private static void OnToolbarFlagChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RichEditor)d).RaiseStatusChanged();

    /// <summary>When true, image insertion and image paste are allowed. Default true.</summary>
    public bool AllowImages
    {
        get => (bool)GetValue(AllowImagesProperty);
        set => SetValue(AllowImagesProperty, value);
    }

    /// <summary>Identifies the <see cref="AllowTables"/> dependency property.</summary>
    public static readonly DependencyProperty AllowTablesProperty = DependencyProperty.Register(
        nameof(AllowTables), typeof(bool), typeof(RichEditor), new PropertyMetadata(true, OnToolbarFlagChanged));

    /// <summary>When true, table insertion and TSV-to-table paste are allowed. Default true.</summary>
    public bool AllowTables
    {
        get => (bool)GetValue(AllowTablesProperty);
        set => SetValue(AllowTablesProperty, value);
    }

    /// <summary>Identifies the <see cref="AllowRichPaste"/> dependency property.</summary>
    public static readonly DependencyProperty AllowRichPasteProperty = DependencyProperty.Register(
        nameof(AllowRichPaste), typeof(bool), typeof(RichEditor), new PropertyMetadata(true));

    /// <summary>When true, paste honors rich formats (internal snapshot, RTF, HTML, image, TSV).
    /// When false, paste falls back to plain text only. Default true.</summary>
    public bool AllowRichPaste
    {
        get => (bool)GetValue(AllowRichPasteProperty);
        set => SetValue(AllowRichPasteProperty, value);
    }

    /// <summary>Identifies the <see cref="ShowFormattingMenu"/> dependency property.</summary>
    public static readonly DependencyProperty ShowFormattingMenuProperty = DependencyProperty.Register(
        nameof(ShowFormattingMenu), typeof(bool), typeof(RichEditor), new PropertyMetadata(false));

    /// <summary>When true, the right-click menu includes the full formatting groups (글자 모양 / 문단 모양 /
    /// 목록 / 제목). When false (default), the control shows a slim menu — clipboard, quick B/I/U toggles,
    /// select-all/undo/redo — leaving rich formatting to the toolbar. Object menus (image / table / link) and
    /// inserts are unaffected. Hosts that use <see cref="RichEditor"/> standalone (no toolbar) can set this
    /// true to expose formatting from the context menu; the toolbar/View path leaves it off. Default false.</summary>
    public bool ShowFormattingMenu
    {
        get => (bool)GetValue(ShowFormattingMenuProperty);
        set => SetValue(ShowFormattingMenuProperty, value);
    }
}
