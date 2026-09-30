using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Windows.UI;
using Windows.UI.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

/// <summary>Optional formatting toolbar for <see cref="RichEditor"/>. Point <see cref="Target"/> at an
/// editor and the toolbar drives it through the editor's public commands and reflects the caret's
/// formatting on its buttons (via <see cref="RichEditor.StatusChanged"/> + <see cref="RichEditor.GetCaretFormat"/>).
/// Labels come from <see cref="RichEditorLocalization"/>. Built in code (no XAML), AOT-friendly.</summary>
public partial class RichEditorToolbar : UserControl
{
    private RichEditor? _target;

    /// <summary>Identifies the <see cref="Target"/> dependency property.</summary>
    // A dependency property so XAML can bind it (Target="{x:Bind Editor}"). It was a plain CLR property, which
    // left a toolbar declared in XAML unbindable to its editor (the 1.1 candidate from the 2026-08-07 surface
    // diff; upstream's is a StyledProperty). _target stays the field every internal path reads.
    // Registered as object, not RichEditor: a {Binding} checks the value against the DP's type through XAML type
    // metadata, which an app only generates for types its own markup names — typed RichEditor, a binding in an
    // app without that metadata delivered null (measured 2026-09-19). The CLR property is still RichEditor-typed;
    // any other value is taken as no target.
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(object), typeof(RichEditorToolbar),
        new PropertyMetadata(null, (d, e) => ((RichEditorToolbar)d).OnTargetChanged(e.NewValue as RichEditor)));

    /// <summary>The editor this toolbar drives.</summary>
    public RichEditor? Target
    {
        get => GetValue(TargetProperty) as RichEditor;
        set => SetValue(TargetProperty, value);
    }

    private void OnTargetChanged(RichEditor? value)
    {
        if (ReferenceEquals(_target, value)) return;
        UnhookTarget();
        _target = value;
        HookTarget();
        Content = Build(); // rebuild so the strip reflects the new target's read-only state
        Sync();
    }

    // Fills the font picker from the editor's FontFamilyChoices (installed system fonts, localized names).
    // Each row renders in its own typeface. Built lazily because Target is usually assigned after Build().
    // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): fills the Flyout's ListView instead of a
    // ComboBox's Items; the refresh of the visible list goes through the search filter.
    private void PopulateFontList()
    {
        if (_font == null) return;
        _font.All.Clear();
        var choices = Target?.FontFamilyChoices;
        if (choices != null) foreach (var f in choices) _font.All.Add(f);
        ApplyFontFilter(_font.Search?.Text);
        // items changed; the next Sync must re-resolve the selection
        _font.Reflected = null;
        if (_font.Selected != null) SetFontSelection(_font.Selected, apply: false);
    }

    // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): the searchable font picker. A Button face in the
    // strip opens a Flyout whose top is a filter TextBox and whose body is the font ListView. Replaces the
    // stock ComboBox because its dropdown cannot host the search box (PRD D9) and re-templating the whole
    // ComboBox would be far more invasive than this self-contained control.
    private sealed class FontPicker
    {
        public required Button Face;   // the closed control shown in the strip
        public ListView? List;         // the dropdown rows
        public TextBox? Search;        // the dropdown filter box
        public string? Selected;       // current family name (the applied selection)
        public string? Reflected;      // family last pushed by Sync — skips the per-keystroke rescan
        public readonly List<string> All = new();
    }

    private Button BuildFontPicker()
    {
        var picker = new FontPicker
        {
            Face = new Button
            {
                Width = 160, Height = CtlHeight, Padding = new Thickness(10, 0, 6, 0),
                Background = ClearBrush, BorderThickness = new Thickness(1), BorderBrush = ComboBorderBrush,
                CornerRadius = new CornerRadius(Corner),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = new TextBlock { FontSize = ComboFontSize, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        _font = picker;
        ToolTipService.SetToolTip(picker.Face, Loc("FontFamily"));
        ApplyStripButtonChrome(picker.Face);
        NoFocus(picker.Face);

        var search = new TextBox
        {
            PlaceholderText = Loc("FontFamily"),
            FontSize = ComboFontSize, MinHeight = 0,
            Margin = new Thickness(6, 6, 6, 4),
        };
        picker.Search = search;

        var list = new ListView
        {
            MaxHeight = 260, MinWidth = 200,
            SelectionMode = ListViewSelectionMode.Single,
        };
        picker.List = list;
        list.SelectionChanged += OnFontRowSelected;
        // Filter as the user types (rebuilds the row list from the cached family names).
        search.TextChanged += (_, _) => ApplyFontFilter(search.Text);

        var panel = new StackPanel { Width = 240, Padding = new Thickness(0, 0, 0, 6) };
        panel.Children.Add(search);
        panel.Children.Add(list);

        var flyout = new Flyout { FlyoutPresenterStyle = TightFlyoutPresenter(), Content = panel };
        ReturnsFocus(flyout);
        picker.Face.Flyout = flyout;
        // Clear the search each time it opens so the full list is visible, and put the caret in the box.
        flyout.Opening += (_, _) => { search.Text = string.Empty; };
        return picker.Face;
    }

    // Rebuilds the font ListView's rows from the cached family names, filtered by `query` (case-insensitive
    // substring). Each row is a TextBlock rendering in its own typeface (the ListView hosts UIElement items
    // directly, so no DataTemplate — and no trim/AOT-hostile XamlReader — is needed). The selection is
    // preserved when the family survives the filter.
    private void ApplyFontFilter(string? query)
    {
        if (_font?.List == null) return;
        string? selected = _font.Selected;
        _font.List.SelectionChanged -= OnFontRowSelected; // suppress while we rebuild the rows
        _font.List.Items.Clear();
        foreach (var name in _font.All)
        {
            if (!string.IsNullOrWhiteSpace(query)
                && name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
            var row = new TextBlock
            {
                Text = name, FontFamily = SafeFontFamily(name), FontSize = ComboFontSize,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _font.List.Items.Add(row);
            if (name == selected) _font.List.SelectedItem = row;
        }
        _font.List.SelectionChanged += OnFontRowSelected;
    }

    private void OnFontRowSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress || _font == null) return;
        if (_font.List?.SelectedItem is TextBlock { Text: { } name } && name != _font.Selected)
            SetFontSelection(name, apply: true);
        _font.Face.Flyout?.Hide();
    }

    // Reflects / applies a font family. `apply` pushes it to the editor (a user pick); otherwise this is
    // just the caret reflecting onto the face + list.
    private void SetFontSelection(string name, bool apply)
    {
        if (_font == null) return;
        _font.Selected = name;
        _font.Reflected = name;
        if (_font.Face.Content is TextBlock face) { face.Text = name; face.FontFamily = SafeFontFamily(name); }
        _suppress = true;
        try
        {
            if (_font.List is { } list)
                foreach (var it in list.Items)
                    if (it is TextBlock { Text: { } t } && t == name) { list.SelectedItem = it; break; }
        }
        finally { _suppress = false; }
        if (apply) Target?.SetRunFontFamily(name);
    }


    private static FontFamily SafeFontFamily(string name)
    {
        try { return new FontFamily(name); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return FontFamily.XamlAutoFontFamily; }
    }

    private Func<Task<byte[]?>>? _imagePicker;
    /// <summary>Optional async image-bytes provider (e.g. a file picker). When set, the image button
    /// awaits it and inserts the returned bytes. Hosts supply this because picking a file needs the
    /// owning window handle, which a control in the tree can't reach on its own. Without it the button uses
    /// the built-in file picker once <see cref="WindowHandle"/> is set, and is disabled until one of the two is.</summary>
    public Func<Task<byte[]?>>? ImagePicker
    {
        get => _imagePicker;
        set { _imagePicker = value; Sync(); }
    }

    /// <summary>Host controls shown at the start of the strip, before the formatting buttons (e.g.
    /// app-shell actions like save/open). Add/remove controls and the toolbar rebuilds; they share the
    /// strip's wrapping, so the whole toolbar stays one row that wraps together when narrow.</summary>
    public ObservableCollection<UIElement> LeadingItems { get; } = new();

    /// <summary>Host controls shown at the end of the strip, after the formatting buttons (e.g. zoom).</summary>
    public ObservableCollection<UIElement> TrailingItems { get; } = new();

    // "Active" (toggled-on) face: a soft tint, not the system accent. WinUI's ToggleButton Checked
    // state paints the accent colour with a white glyph, which shouts next to the flat icon strip —
    // ApplyToggleCheckedStyle overrides the template's checked brushes with these.
    //
    // Created LAZILY, not in field initializers. A SolidColorBrush is a XAML object whose construction
    // needs the WinUI runtime, and a static field initializer runs on FIRST TOUCH OF ANY STATIC MEMBER —
    // so `RichEditorToolbar.FontSizes = ...` from a plain unit test (or from Main before
    // Application.Start) used to die with a COMException from the class initializer, nowhere near the
    // line that caused it. Deferring to first USE moves the runtime requirement to where a brush is
    // actually painted, which is always inside a built toolbar.
    //
    // `??=` is not synchronized: these are only ever touched while building/syncing a toolbar, which is
    // UI-thread work. A torn race would cost an extra brush, not correctness.
    // [ThreadStatic]: one set PER UI THREAD. A brush belongs to the thread that made it, and a process-wide set made
    // the first toolbar's thread the owner of every toolbar's brushes — a toolbar in a window on its own thread got
    // RPC_E_WRONG_THREAD (the editor's brush defaults did, measured 2026-09-14; upstream a66b472 is the same shape).
    [ThreadStatic] private static SolidColorBrush? _activeBrush, _activeHoverBrush, _clearBrush, _blackInk;
    // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): pointer-over / pressed faces for the flat
    // icon strip. WinUI's stock Button/ToggleButton hovers are a very faint wash that vanished next
    // to the canvas; these are the PRD's confirmed values (light: black 10% / 13%; dark: white 10% / 14%).
    [ThreadStatic] private static SolidColorBrush? _hoverBrush, _pressedBrush, _comboBorderBrush, _sepBrush;
    // Theme-aware cache: the "active" faces and ink are rebuilt when the toolbar's
    // effective theme flips (see IsDarkTheme / RebuildForTheme). Stored per thread for
    // the same reason as the brushes themselves (a brush is thread-affine).
    [ThreadStatic] private static bool? _brushThemeIsDark;
    private static bool IsDarkTheme => CurrentThemeImpl?.Invoke() ?? false;
    // Hook installed by the first toolbar instance so the static brush caches can read
    // the live theme without a WinUI dependency in the model-only paths (tests).
    private static Func<bool>? CurrentThemeImpl;

    // QNOTE VENDORED PATCH (dark-mode toolbar): the active/hover faces and the ink were
    // hardcoded light-theme colors, which read as glaringly bright on a dark chrome. They
    // now resolve from the effective theme; EnsureThemeBrushes drops the cached set the
    // first time a brush is read under a different theme so a live switch recolors.
    private static void EnsureThemeBrushes(bool isDark)
    {
        if (_brushThemeIsDark == isDark) return;
        _brushThemeIsDark = isDark;
        _activeBrush = null;
        _activeHoverBrush = null;
        _hoverBrush = null;
        _pressedBrush = null;
        _comboBorderBrush = null;
        _sepBrush = null;
        _blackInk = null;
        _dimInk = null;
        _noColorBrush = null;
    }

    // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): pointer-over / pressed / active faces follow
    // the PRD Visual Spec (user-confirmed). The block above (P1) still governs WHICH theme is live;
    // these only pick the color. Active is the theme accent at 10% (light) / 18% (dark), not the old
    // grey tint — it reads as "on" without the stock accent fill + white glyph shout.
    private static SolidColorBrush ActiveBrush
    {
        get { EnsureThemeBrushes(IsDarkTheme); return _activeBrush ??= new(IsDarkTheme ? Color.FromArgb(0x2E, 0x4C, 0xC2, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x67, 0xC0)); }
    }
    private static SolidColorBrush ActiveHoverBrush
    {
        get { EnsureThemeBrushes(IsDarkTheme); return _activeHoverBrush ??= new(IsDarkTheme ? Color.FromArgb(0x38, 0x4C, 0xC2, 0xFF) : Color.FromArgb(0x26, 0x00, 0x67, 0xC0)); }
    }
    // Pointer-over: light black 10% (#1A000000) / dark white 10% (#1AFFFFFF).
    private static SolidColorBrush HoverBrush
    {
        get { EnsureThemeBrushes(IsDarkTheme); return _hoverBrush ??= new(IsDarkTheme ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00)); }
    }
    // Pressed: light black 13% (#20000000) / dark white 14% (#24FFFFFF).
    private static SolidColorBrush PressedBrush
    {
        get { EnsureThemeBrushes(IsDarkTheme); return _pressedBrush ??= new(IsDarkTheme ? Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x20, 0x00, 0x00, 0x00)); }
    }

    // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): the group separator rule — light 20% black
    // (#33000000) / dark 22% white (#38FFFFFF). Strong enough to read as a group break, light enough to
    // stay a hairline (the old fixed 24%-black wash was nearly invisible on the light strip).
    private static SolidColorBrush SeparatorBrush
    {
        get { EnsureThemeBrushes(IsDarkTheme); return _sepBrush ??= new(IsDarkTheme ? Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x33, 0x00, 0x00, 0x00)); }
    }

    // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): 1px combo border — light black 12% (#1F000000)
    // / dark white 12% (#1FFFFFFF), per the PRD's dropdown spec (theme-aware; P1 never covered combos).
    private static SolidColorBrush ComboBorderBrush
    {
        get { EnsureThemeBrushes(IsDarkTheme); return _comboBorderBrush ??= new(IsDarkTheme ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00)); }
    }
    private static SolidColorBrush ClearBrush => _clearBrush ??= new(Colors.Transparent);
    private static SolidColorBrush BlackInk // shared: Sync runs per keystroke
    {
        get { EnsureThemeBrushes(IsDarkTheme); return _blackInk ??= new(IsDarkTheme ? Colors.White : Colors.Black); }
    }

    // Variation Selector-15: forces text (monochrome) presentation of an emoji that has no symbol-font
    // glyph, so the leftover emoji fallbacks don't render as colour and clash with the FontIcon set.
    private static readonly string Mono = ((char)0xFE0E).ToString();

    // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): heading glyphs for the paragraph-style combo's
    // "left icon + right text" item layout (PRD D9). These are Lucide `heading-1..4` (ISC) / `type` for
    // 正文 — kept LOCAL to the toolbar (they are ComboBoxItem faces, not RichEditorIcon slots) and rendered
    // through the vendored RichEditorIconRenderer so they inherit the theme-aware ink for free.
    private static readonly IconLayer[] Heading1Icon =
    {
        new("M4 12h8", false), new("M4 18V6", false), new("M12 18V6", false), new("m17 12 3-2v8", false),
    };
    private static readonly IconLayer[] Heading2Icon =
    {
        new("M4 12h8", false), new("M4 18V6", false), new("M12 18V6", false),
        new("M21 18h-4c0-4 4-3 4-6 0-1.5-2-2.5-4-1", false),
    };
    private static readonly IconLayer[] Heading3Icon =
    {
        new("M4 12h8", false), new("M4 18V6", false), new("M12 18V6", false),
        new("M17.5 10.5c1.7-1 3.5 0 3.5 1.5a2 2 0 0 1-2 2", false),
        new("M17 17.5c2 1.5 4 .3 4-1.5a2 2 0 0 0-2-2", false),
    };
    private static readonly IconLayer[] Heading4Icon =
    {
        new("M12 18V6", false), new("M17 10v3a1 1 0 0 0 1 1h3", false),
        new("M21 10v8", false), new("M4 12h8", false), new("M4 18V6", false),
    };
    private static readonly IconLayer[] BodyTextIcon = // Lucide `type`
    {
        new("M12 4v16", false), new("M4 7V5a1 1 0 0 1 1-1h14a1 1 0 0 1 1 1v2", false), new("M9 20h6", false),
    };

    // A "left icon + right text" ComboBoxItem face: a fixed-width icon column (so the text columns line
    // up across items) then the label. Used by the paragraph-style and alignment combos (PRD D9); the
    // font/size/zoom/paper combos keep plain text.
    private static UIElement ComboIconText(UIElement icon, string text)
        => new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border { Width = IconBox, Child = icon, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
            },
        };

    // Heading item icon for level 0 (body) / 1..4; higher levels reuse the heading-4 glyph.
    private static IconLayer[] HeadingLayers(int level) => level switch
    {
        1 => Heading1Icon,
        2 => Heading2Icon,
        3 => Heading3Icon,
        >= 4 => Heading4Icon,
        _ => BodyTextIcon,
    };

    // Reflected controls are nullable: a Minimal / read-only toolbar builds only a subset, so Sync() must
    // null-guard every access. Null = "not built at the current ToolbarLevel".
    private ToggleButton? _bold, _italic, _underline, _strike, _painter;
    private Button? _bullet, _number;                 // list-box icon buttons (toggle the list)
    private Button? _quote;                           // quote toggle
    private TextBlock? _bulletPreview, _numberPreview; // current list marker shown in the list boxes
    [ThreadStatic] private static SolidColorBrush? _dimInk; // per UI thread, as _activeBrush
    private static SolidColorBrush DimInk // inactive marker; QNOTE dark-mode patch (theme-aware)
    {
        get { EnsureThemeBrushes(IsDarkTheme); return _dimInk ??= new(IsDarkTheme ? Color.FromArgb(255, 0x6A, 0x6A, 0x6A) : Color.FromArgb(255, 0xBF, 0xC3, 0xC7)); }
    }
    private ComboBox? _size, _heading, _align;
    // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): the font picker is no longer a bare ComboBox —
    // PRD D9 wants a search box at the top of its dropdown, which a stock ComboBox cannot host. It is a
    // Button face + a Flyout holding the search TextBox over the font ListView. All the selection
    // behaviour (reflect the caret font, apply on pick, each row in its own typeface) is preserved.
    private FontPicker? _font;
    private TextBox? _spacingBox; // editable line-spacing %, reflects/sets the caret paragraph
    private Button? _undo, _redo;
    private Button? _tableBtn, _imageBtn, _dividerBtn;
    private bool _suppress; // guards combo SelectionChanged while syncing toolbar <- caret state
    private bool _builtReadOnly; // read-only state captured at the last Build (to rebuild the view toolbar on toggle)

    private static double[] _fontSizes = { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 40, 48, 60, 72 };

    /// <summary>The available font sizes in the toolbar combo box. Hosts can replace this array to customize the options.
    /// <para>Read while the strip is being built, so assign it BEFORE creating the toolbar. Any point
    /// works, including <c>Main</c> before <c>Application.Start</c> — the toolbar's static brushes are
    /// created lazily precisely so that touching this property does not drag in the WinUI runtime. An
    /// existing toolbar keeps the sizes it was built with until something rebuilds it — assigning
    /// <see cref="Target"/> or <see cref="ToolbarLevel"/> does.</para></summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">The array is empty, or holds a size that is not a positive
    /// finite number.</exception>
    public static double[] FontSizes
    {
        get => _fontSizes;
        // Validated here rather than at the point of use: the array is consumed while the toolbar builds
        // itself, so a bad value would otherwise surface as a crash inside Build() with nothing pointing
        // back at the assignment that caused it. A non-positive or NaN size reaches CanvasTextFormat.
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length == 0)
                throw new ArgumentException("At least one font size is required.", nameof(value));
            foreach (double pt in value)
                if (!double.IsFinite(pt) || pt <= 0)
                    throw new ArgumentException($"Font size must be a positive finite number, was {pt}.", nameof(value));
            _fontSizes = value;
        }
    }
    private const double BodySizePt = 10; // the model's default run size, shown when a run has none

    // A point-size label ("10 pt", "10.5 pt") — the unit the model, API and serialization all speak.
    private static string PtText(double pt)
        => pt.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " pt";
    private static readonly int[] SpacingPercents = { 100, 110, 120, 130, 150, 160, 180, 200, 250, 300 };

    // The 40-swatch palette (greys + hues in a few shades) shared by the text-color/highlight pickers,
    // matching the original AvaloniaRichEditor toolbar. Internal: the editor's cell-background
    // context-menu palette reuses it so all color pickers offer the same swatches.
    /// <summary>The color palette (hex strings) shared by the toolbar's text/highlight pickers and the editor's cell background context menu. Hosts can replace this array.
    /// <para>Read when a color flyout is built, so assign it before the toolbar is created (see
    /// <see cref="FontSizes"/> — any point works). Entries are parsed as <c>#RRGGBB</c> or
    /// <c>#AARRGGBB</c>; an entry that does not parse renders as BLACK rather than throwing, so a typo
    /// shows up as an unexpected swatch, not a crash.</para></summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">The array is empty.</exception>
    public static string[] Palette
    {
        get => _palette;
        // Entry FORMAT is deliberately not validated — ParseHex already falls back to black, and the
        // swatch grid is cosmetic. Null/empty is different: it produces an empty color picker, which
        // reads as a broken toolbar rather than a wrong colour.
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length == 0)
                throw new ArgumentException("At least one palette entry is required.", nameof(value));
            _palette = value;
        }
    }

    private static string[] _palette =
    {
        "#000000","#444444","#666666","#999999","#BBBBBB","#DDDDDD","#EEEEEE","#FFFFFF",
        "#FF0000","#E67E22","#F1C40F","#2ECC71","#1ABC9C","#3498DB","#9B59B6","#E91E63",
        "#C0392B","#D35400","#F39C12","#27AE60","#16A085","#2980B9","#8E44AD","#AD1457",
        "#7B241C","#935116","#9A7D0A","#196F3D","#0E6251","#1A5276","#5B2C6F","#78281F",
        "#FFCDD2","#FFE0B2","#FFF9C4","#C8E6C9","#B2DFDB","#BBDEFB","#E1BEE7","#F8BBD0",
    };

    [ThreadStatic] private static SolidColorBrush? _noColorBrush; // per UI thread, as _activeBrush
    private static SolidColorBrush NoColorBrush // "no highlight" face; QNOTE dark-mode patch (theme-aware)
    {
        get { EnsureThemeBrushes(IsDarkTheme); return _noColorBrush ??= new(IsDarkTheme ? Color.FromArgb(255, 0x55, 0x55, 0x55) : Color.FromArgb(255, 0xDD, 0xDD, 0xDD)); }
    }
    private Border? _colorSwatch, _highlightSwatch; // current-colour bars under the picker glyphs

    // Uniform strip metrics: every control renders in the same-height box so rows read as one even
    // line, and icon buttons are a FIXED width so the gaps are identical. Gaps come from the wrap
    // panel's spacing (in-group) and Sep()'s margins (inter-group), NOT per-control margins.
    // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): the PRD Visual Spec's confirmed metrics —
    // 30×30 buttons around a 16px icon with a 4px corner radius, group gap 2px / separator gap 2×4px.
    private const double CtlHeight = 30;
    private const double BtnWidth = 30;
    // Icon box: a 16px glyph centered in the 30px button (the old ~18px glyphs sat heavy in a 26px box).
    private const double IconBox = 16;
    // Corner radius shared by buttons, combo borders and the list/line-spacing boxes.
    private const double Corner = 4;
    // Uniform combo content point-size. Without it each combo inherited the default and the font-name
    // combo (whose items carry their own FontFamily) rendered its selected value in that face at an
    // apparently different size than the plain-text combos (size/heading/align/zoom/paper/orient).
    private const double ComboFontSize = 12;

    private static string Loc(string key) => RichEditorLocalization.GetString(key);

    // Tooltip with the command's shortcut appended, e.g. "굵게 (Ctrl+B)". Single-sourced from the table.
    private static string TipSc(string key, RichEditorShortcutId id) => Loc(key) + " (" + RichEditorShortcuts.Display(id) + ")";

    public RichEditorToolbar()
    {
        // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): a UserControl's default HorizontalContentAlignment
        // let the strip float toward the centre instead of hugging the left edge. Pin it so the toolbar's
        // content fills the width and the single-row strip stays left-aligned (matches the reference).
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;
        // Changing the host item slots rebuilds the strip so they sit inline with the formatting buttons.
        void Rebuild(object? s, NotifyCollectionChangedEventArgs e) { Content = Build(); Sync(); }
        LeadingItems.CollectionChanged += Rebuild;
        TrailingItems.CollectionChanged += Rebuild;
        Content = Build();
        // Both subscriptions are dropped on Unloaded and restored on Loaded. The target's StatusChanged
        // matters as much as the static LanguageChanged: the EDITOR holds that handler, so a host that
        // detaches the toolbar while keeping the editor alive would otherwise keep the whole toolbar
        // (and its visual subtree) reachable forever.
        Loaded += (_, _) => { RichEditorLocalization.LanguageChanged += OnLanguageChanged; HookTarget(); Sync(); };
        Unloaded += (_, _) => { RichEditorLocalization.LanguageChanged -= OnLanguageChanged; UnhookTarget(); };

        // QNOTE VENDORED PATCH (dark-mode toolbar): the static brush caches read the live
        // theme through this hook, and a theme flip rebuilds the strip so the already-built
        // controls pick up the recolored brushes.
        CurrentThemeImpl ??= () => ActualTheme == ElementTheme.Dark;
        // QNOTE VENDORED PATCH (P2): share the live-theme hook with the icon renderer so host-provided
        // vector icons (RichEditorIcons.Provider, e.g. QNoteIcons) pick the right ink.
        RichEditorIconRenderer.IsDarkTheme ??= () => ActualTheme == ElementTheme.Dark;
        ActualThemeChanged += (_, _) =>
        {
            EnsureThemeBrushes(ActualTheme == ElementTheme.Dark);
            Content = Build();
            Sync();
        };
    }

    // Idempotent: the `-=` before the `+=` means a Loaded that arrives while already hooked (reparenting,
    // or Loaded firing after the Target setter already hooked) can't double-subscribe.
    private void HookTarget()
    {
        if (_target == null) return;
        _target.StatusChanged -= OnTargetStatusChanged;
        _target.StatusChanged += OnTargetStatusChanged;
        _target.FontFamilyChoicesChanged -= OnTargetFontChoicesChanged;
        _target.FontFamilyChoicesChanged += OnTargetFontChoicesChanged;
        _target.FindUiChanged -= OnTargetStatusChanged;
        _target.FindUiChanged += OnTargetStatusChanged;
    }

    private void UnhookTarget()
    {
        if (_target == null) return;
        _target.StatusChanged -= OnTargetStatusChanged;
        _target.FontFamilyChoicesChanged -= OnTargetFontChoicesChanged;
        _target.FindUiChanged -= OnTargetStatusChanged;
    }

    // A rebuild, not an in-place refill: mutating a ComboBox's Items from a sync path is what crashed the toolbar
    // once already (the storm crash), and a curated font list changes rarely.
    private void OnTargetFontChoicesChanged(object? sender, EventArgs e) { Content = Build(); Sync(); }

    // LanguageChanged is raised synchronously on whatever thread set RichEditorLocalization.Language, and
    // rebuilding the strip touches XAML — so a host that switches language from a background thread (a
    // settings load, a locale watcher) would take the toolbar down with RPC_E_WRONG_THREAD. Marshal.
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue is { } dq && !dq.HasThreadAccess) { dq.TryEnqueue(RebuildForLanguage); return; }
        RebuildForLanguage();
    }

    private void RebuildForLanguage() { Content = Build(); Sync(); }

    private void OnTargetStatusChanged(object? sender, EventArgs e) => Sync();

    // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): the toolbar is a FIXED TWO-ROW bar.
    // `_strip` = row 1 (icon buttons), `_dropdownRow` = row 2 (the wide dropdowns). Each row is an
    // OverflowRowPanel with its own trailing More button, so a narrow window folds each row independently.
    private Panel? _strip; // row 1 (icon buttons), kept so a rebuild can detach reused host items
    private Panel? _dropdownRow;               // row 2 (paragraph style / font / size dropdowns)
    private OverflowRowPanel? _row1Panel, _row2Panel;
    private Panel? _row1OverflowHost, _row2OverflowHost;
    private Button? _row1More, _row2More;

    // ---- focus discipline --------------------------------------------------------------------------
    // The caret is painted only while the editor's canvas has focus, so anything in the strip that takes
    // focus hides the caret AND sends the next keystroke somewhere else. The commands still ran (they act
    // on the remembered caret position), which is exactly why this looked like it worked while the
    // keyboard had gone dead. Two rules:
    //  (a) nothing in the strip accepts focus on interaction — including host-supplied Leading/Trailing
    //      items, which sit in the same strip and grab focus just the same;
    //  (b) what legitimately needs focus while open (a combo's dropdown, a picker popup) hands it back on
    //      close — on CLOSE, not on selection change, which would yank focus mid-arrowing through a list.

    /// <summary>Marks an element so clicking it does not move keyboard focus. Applied to every button the
    /// toolbar builds; hosts adding their own to <see cref="LeadingItems"/>/<see cref="TrailingItems"/>
    /// get it applied automatically when the strip is built.</summary>
    private static T NoFocus<T>(T element) where T : FrameworkElement
    {
        element.AllowFocusOnInteraction = false;
        return element;
    }

    // Gives focus back to the editing surface. Used by every popup/dropdown close handler.
    private void ReturnFocusToEditor() => Target?.FocusEditor();

    // Wires a picker popup to hand focus back when it closes.
    private FlyoutBase ReturnsFocus(FlyoutBase flyout)
    {
        flyout.Closed += (_, _) => ReturnFocusToEditor();
        return flyout;
    }

    // Clears focus-on-interaction across a built subtree. The factories already do it for what this class
    // creates; this catches host items (whose content we don't control) and anything added later. Walks
    // the LOGICAL structure — at build time nothing is in the visual tree yet, so VisualTreeHelper is
    // empty. Depth is bounded by the strip's own nesting, and host items are shallow wrappers in practice.
    private static void ClearFocusOnInteraction(object? node, int depth = 0)
    {
        if (depth > 12) return; // a host could hand us anything; don't walk an arbitrary graph forever
        switch (node)
        {
            case ButtonBase b: b.AllowFocusOnInteraction = false; break;
            case Panel panel:
                foreach (var child in panel.Children) ClearFocusOnInteraction(child, depth + 1);
                return;
            case Border border: ClearFocusOnInteraction(border.Child, depth + 1); return;
            case ContentControl cc: ClearFocusOnInteraction(cc.Content, depth + 1); return;
        }
    }

    private UIElement Build()
    {
        // Detach reused host (leading/trailing) items from the previous strip. FrameworkElement.Parent is
        // null before the toolbar is loaded, so clearing the old collection is the reliable way to reparent
        // them — adding an element that still has a parent throws (0x800F1000).
        _strip?.Children.Clear();
        // The two per-row overflow hosts are rebuilt from scratch each Build() (a theme flip / language /
        // target change must not leave stale demoted items). Clear them so no element ends up in two parents.
        _row1OverflowHost?.Children.Clear();
        _row2OverflowHost?.Children.Clear();
        _row1Panel = _row2Panel = null; _row1More = _row2More = null;
        _row1OverflowHost = _row2OverflowHost = null;
        // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): FIXED TWO-ROW toolbar with PER-ROW overflow.
        // Row 1 = icon buttons, row 2 = the wide dropdowns. Each row is an OverflowRowPanel that justifies
        // its visible children (first hugs the left edge, last hugs the right edge) and moves the tail into
        // its OWN trailing More flyout when the row runs out of width — so a narrow window (the app is a
        // small utility and may be only ~1000px wide) folds each row independently instead of clipping.
        var strip = new OverflowRowPanel { MinSpacing = 2, MaxSpacing = 20, VerticalAlignment = VerticalAlignment.Center };
        _strip = strip; _row1Panel = strip;
        var dropdowns = new OverflowRowPanel { MinSpacing = 2, MaxSpacing = 20, VerticalAlignment = VerticalAlignment.Center };
        _dropdownRow = dropdowns; _row2Panel = dropdowns;

        // Reset reflected controls; only the ones the current level/state re-builds are re-assigned. Sync()
        // null-guards each, so a Minimal or read-only toolbar (a subset) reflects safely.
        _bold = _italic = _underline = _strike = _painter = null;
        _bullet = _number = _quote = _undo = _redo = _tableBtn = _imageBtn = _dividerBtn = null;
        _bulletPreview = _numberPreview = null;
        _font = null; _size = _heading = _align = null;
        _spacingBox = null; _colorSwatch = _highlightSwatch = null;
        _zoom = _paper = _orient = null; _zoomFit = null;
        _exportBtn = _importBtn = _printBtn = null;

        bool ro = Target?.IsReadOnly == true;
        _builtReadOnly = ro;
        var lvl = EffectiveLevel();
        bool normal = lvl >= ToolbarLevel.Normal;
        bool maximum = lvl >= ToolbarLevel.Maximum;

        void Add(UIElement c) => strip.Children.Add(c);
        void AddSep() => strip.Children.Add(Sep());
        // Row 2 (the wide dropdowns).
        void AddD(UIElement c) => dropdowns.Children.Add(c);

        // Leading host items (always).
        foreach (var c in LeadingItems) Add(c);
        if (LeadingItems.Count > 0) AddSep();

        if (ro)
        {
            // Read-only = view toolbar: page/zoom + Export/Print (no editing controls, Import hidden).
            // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): the Find button was removed from the strip
            // (QNote has no find UI wired; Ctrl+F stays inert). The read-only branch no longer builds it.
            bool page = ShowPageControls, file = ShowFileActions;
            if (page) BuildPageControls(strip);
            if (file) { if (page) AddSep(); BuildFileActions(strip); }
        }
        else
        {
            // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): the strip order follows the reference
            // (Office/WPS) toolbar, NOT the upstream AvaloniaRichEditor order:
            //   history | format-painter·clear | paragraph·font·size·A± | colour·B I U S |
            //   align | lists | indent | (More① = line-spacing·quote)  |  PIN(inserts) | More②(divider)
            _undo = IconButton("↶", TipSc("Undo", RichEditorShortcutId.Undo), () => Target?.Undo(), RichEditorIcon.Undo);
            _redo = IconButton("↷", TipSc("Redo", RichEditorShortcutId.Redo), () => Target?.Redo(), RichEditorIcon.Redo);
            Add(_undo); Add(_redo); AddSep();

            if (normal)
            {
                _painter = ToggleBtn("🖌" + Mono, Loc("FormatPainter"), () => Target?.StartFormatPainter(), icon: RichEditorIcon.FormatPainter);
                Add(_painter);
                Add(IconButton("✕", Loc("ClearFormatting"), () => Target?.ClearFormatting(), RichEditorIcon.ClearFormatting));
                AddSep();
            }

            // ---- ROW 2: the wide dropdowns (paragraph style / font / size) ----------------------------
            if (normal)
            {
                var heading = MakeCombo(108, Loc("ParagraphStyle")); _heading = heading;
                // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): "left icon + right text" items (PRD D9).
                // Tag still carries the level, so SelectionChanged / reapply / SelectByTag are untouched.
                heading.Items.Add(new ComboBoxItem { Content = ComboIconText(RichEditorIconRenderer.Create(IconBox, BodyTextIcon), Loc("BodyText")), Tag = 0 });
                for (int i = 1; i <= 6; i++)
                    heading.Items.Add(new ComboBoxItem { Content = ComboIconText(RichEditorIconRenderer.Create(IconBox, HeadingLayers(i)), Loc("Heading" + i)), Tag = i });
                heading.SelectionChanged += (_, _) => { if (!_suppress && heading.SelectedItem is ComboBoxItem ci) Target?.SetHeading((int)ci.Tag); };
                // Re-picking the level already shown re-applies it — that restores a heading's bold and size
                // after the user changed them (HeadingStyle.Retype). SelectionChanged cannot see that pick: the
                // selection does not change. The press records whether the item was ALREADY the selected one and
                // the release applies only then, so picking a different level is applied once, by SelectionChanged.
                bool reapply = false;
                foreach (var item in heading.Items.OfType<ComboBoxItem>())
                {
                    item.AddHandler(UIElement.PointerPressedEvent,
                        new Microsoft.UI.Xaml.Input.PointerEventHandler((s, _) => reapply = ReferenceEquals(heading.SelectedItem, s)), true);
                    item.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((s, _) =>
                    {
                        if (!reapply || s is not ComboBoxItem ci) return;
                        reapply = false;
                        Target?.SetHeading((int)ci.Tag);
                    }), true);
                }
                AddD(heading);

                // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): searchable font picker (PRD D9).
                AddD(BuildFontPicker());
                PopulateFontList();
            }

            // Sizes read as points ("10 pt"), so the unit is explicit — the model/API speak pt. The
            // numeric value lives in Tag, keeping display text and the sync/apply value separate.
            var size = MakeCombo(86, Loc("FontSize")); _size = size;
            foreach (var s in FontSizes) size.Items.Add(new ComboBoxItem { Content = PtText(s), Tag = s });
            size.SelectionChanged += (_, _) => { if (!_suppress && size.SelectedItem is ComboBoxItem ci && ci.Tag is double v) Target?.SetFontSize(v); };
            AddD(size);

            if (normal)
            {
                // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): A⁺ / A⁻ (grow / shrink the font by one
                // step on the editor's ladder) — the reference toolbar's A± pair, after the size combo.
                Add(IconButton("A+", TipSc("FontSizeIncrease", RichEditorShortcutId.FontLarger), () => Target?.IncreaseFontSize(), RichEditorIcon.FontSizeIncrease));
                Add(IconButton("A-", TipSc("FontSizeDecrease", RichEditorShortcutId.FontSmaller), () => Target?.DecreaseFontSize(), RichEditorIcon.FontSizeDecrease));
                AddSep();
            }

            _bold = ToggleBtn("B", TipSc("Bold", RichEditorShortcutId.Bold), () => Target?.ToggleBold(), bold: true, icon: RichEditorIcon.Bold);
            _italic = ToggleBtn("I", TipSc("Italic", RichEditorShortcutId.Italic), () => Target?.ToggleItalic(), italic: true, icon: RichEditorIcon.Italic);
            _underline = ToggleBtn("U", TipSc("Underline", RichEditorShortcutId.Underline), () => Target?.ToggleUnderline(), icon: RichEditorIcon.Underline,
                decorations: Windows.UI.Text.TextDecorations.Underline);
            _strike = ToggleBtn("S", TipSc("Strikethrough", RichEditorShortcutId.Strikethrough), () => Target?.ToggleStrikethrough(), icon: RichEditorIcon.Strikethrough,
                decorations: Windows.UI.Text.TextDecorations.Strikethrough);

            if (normal)
            {
                Add(ColorButton("A", Loc("TextColor"), false));
                Add(ColorButton("✎", Loc("Highlight"), true));
            }
            Add(_bold); Add(_italic); Add(_underline); Add(_strike);
            AddSep();

            if (normal)
            {
                // Indent (both steps — plain buttons, so row 1).
                Add(IconButton("⇥", TipSc("IndentIncrease", RichEditorShortcutId.IndentIncrease), () => Target?.Indent(20), RichEditorIcon.IndentIncrease));
                Add(IconButton("⇤", TipSc("IndentDecrease", RichEditorShortcutId.IndentDecrease), () => Target?.Indent(-20), RichEditorIcon.IndentDecrease));
                AddSep();

                // Quote (plain button, row 1).
                _quote = IconButton("❝", Loc("Quote"), () => Target?.ToggleQuote(), RichEditorIcon.Quote);
                Add(_quote);
                AddSep();

                // Inserts: table + image + divider (plain buttons, row 1).
                _tableBtn = BaseButton("▦", Loc("InsertTable"), RichEditorIcon.InsertTable);
                _tableBtn.Flyout = BuildTableGridPicker();
                Add(_tableBtn);
                _imageBtn = IconButton("🖼", Loc("InsertImage"), async () => await PickAndInsertImageAsync(), RichEditorIcon.InsertImage);
                Add(_imageBtn);
                _dividerBtn = IconButton("―", Loc("InsertDivider"), () => Target?.InsertDivider(), RichEditorIcon.InsertDivider);
                Add(_dividerBtn);
            }

            // ---- ROW 2 continued: the remaining dropdowns (align / lists / line-spacing) --------------
            if (normal)
            {
                var align = MakeCombo(96, Loc("Alignment")); _align = align;
                // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): icon+text items (PRD D9); the Tag still
                // carries the TextAlignment, so SelectionChanged / SelectByTag are untouched.
                void AddAlign(string text, TextAlignment a, RichEditorIcon icon)
                    => align.Items.Add(new ComboBoxItem { Content = ComboIconText(ResolvedIcon(icon), text), Tag = a });
                AddAlign(Loc("AlignLeft"), TextAlignment.Left, RichEditorIcon.AlignLeft);
                AddAlign(Loc("AlignCenter"), TextAlignment.Center, RichEditorIcon.AlignCenter);
                AddAlign(Loc("AlignRight"), TextAlignment.Right, RichEditorIcon.AlignRight);
                AddAlign(Loc("AlignJustify"), TextAlignment.Justify, RichEditorIcon.AlignJustify);
                align.SelectionChanged += (_, _) => { if (!_suppress && align.SelectedItem is ComboBoxItem ci) Target?.SetTextAlignment((TextAlignment)ci.Tag); };
                AddD(align);

                // Lists: each is a combo-style box [icon (toggles the list) | current marker | ▾ (glyph/format)].
                var bullet = BuildListBox(RichEditorIcon.BulletList, Loc("BulletList"), () => Target?.ToggleBullet(),
                    (ListMarkerStyle.Disc, "•"), (ListMarkerStyle.Circle, "◦"), (ListMarkerStyle.Square, "▪"), (ListMarkerStyle.Dash, "–"));
                _bullet = bullet.Icon; _bulletPreview = bullet.Preview;
                AddD(bullet.Box);
                var number = BuildListBox(RichEditorIcon.NumberedList, Loc("NumberedList"), () => Target?.ToggleNumbering(),
                    (ListMarkerStyle.Decimal, "1."), (ListMarkerStyle.DecimalParen, "1)"),
                    (ListMarkerStyle.LowerAlpha, "a)"), (ListMarkerStyle.UpperAlpha, "A)"), (ListMarkerStyle.LowerRoman, "i)"));
                _number = number.Icon; _numberPreview = number.Preview;
                AddD(number.Box);

                // Line spacing (a dropdown control, so row 2).
                AddD(BuildLineSpacingControl());
            }

            // Maximum adds the page/zoom controls and file actions.
            if (maximum)
            {
                if (ShowPageControls) { AddSep(); BuildPageControls(strip); }
                if (ShowFileActions) { AddSep(); BuildFileActions(strip); }
            }
        }

        // Host trailing items, after a separator.
        if (TrailingItems.Count > 0) AddSep();
        foreach (var c in TrailingItems) Add(c);

        // Host-supplied Leading/TrailingItems come through here too — see ClearFocusOnInteraction.
        foreach (var child in strip.Children) ClearFocusOnInteraction(child);
        foreach (var child in dropdowns.Children) ClearFocusOnInteraction(child);

        // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): FIXED TWO-ROW root with three hairline rules
        // (above row 1, between the rows, below row 2). Each row is a Grid [OverflowRowPanel (*) | More (Auto)]
        // so the row's trailing More button is pinned to the right edge and the panel gets the rest.
        var row1 = BuildRowWithOverflow(strip, out _row1More, out _row1OverflowHost);
        var row2 = BuildRowWithOverflow(dropdowns, out _row2More, out _row2OverflowHost);
        strip.MoreButton = _row1More; strip.OverflowHost = _row1OverflowHost;
        dropdowns.MoreButton = _row2More; dropdowns.OverflowHost = _row2OverflowHost;
        strip.IsOverflowOpen = () => _row1More?.Flyout is Flyout f && f.IsOpen;
        dropdowns.IsOverflowOpen = () => _row2More?.Flyout is Flyout f && f.IsOpen;

        var root = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        for (int i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(row1, 1);
        Grid.SetRow(row2, 3);
        root.Children.Add(RowRule(0));      // above row 1
        root.Children.Add(row1);
        root.Children.Add(RowRule(2));      // between the rows
        root.Children.Add(row2);
        root.Children.Add(RowRule(4));      // below row 2
        return new Border { Padding = new Thickness(4, 2, 4, 2), Child = root, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    // Wraps one row's panel with its trailing More button + flyout. The Grid's second column is Auto so the
    // More button reserves exactly its width (or collapses to 0 when nothing overflows).
    private Grid BuildRowWithOverflow(OverflowRowPanel panel, out Button more, out Panel overflowHost)
    {
        // Single horizontal row that sizes to its content (no fixed width → no trailing blank; no wrap →
        // the folded items read left-to-right, matching the row they came from).
        var host = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        overflowHost = host;
        var flyout = new Flyout
        {
            FlyoutPresenterStyle = TightFlyoutPresenter(),
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = true,
        };
        ReturnsFocus(flyout);
        flyout.Content = new Border { Padding = new Thickness(4), Child = host };
        more = BaseButton("⋮", Loc("More"), RichEditorIcon.MoreVertical);
        more.Flyout = flyout;
        more.Visibility = Visibility.Collapsed;   // shown by the panel only when something overflows
        Grid.SetColumn(panel, 0);
        Grid.SetColumn(more, 1);
        var g = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(panel);
        g.Children.Add(more);
        return g;
    }

    // A hairline horizontal rule spanning the toolbar width, used to separate the two rows.
    private static UIElement RowRule(int row)
    {
        var line = new Border
        {
            Height = 1,
            Background = SeparatorBrush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 3, 0, 3),
        };
        Grid.SetRow(line, row);
        return line;
    }


    // A flyout presenter with no default padding / min-size, so a flyout hugs its content (the default
    // presenter adds ~12px padding and a min-size, which dwarfs the small grid picker).
    internal static Style TightFlyoutPresenter()
    {
        var s = new Style(typeof(FlyoutPresenter));
        s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        s.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
        s.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
        return s;
    }

    // Draw-to-size table picker: hover the grid to choose rows×columns, click to arm the size drag.
    private const int GridRows = 8, GridCols = 10;
    private Flyout BuildTableGridPicker()
    {
        var flyout = new Flyout { FlyoutPresenterStyle = TightFlyoutPresenter() };
        ReturnsFocus(flyout); // the popup takes focus while open; give it back on close
        var root = new StackPanel { Spacing = 4, Padding = new Thickness(2) };
        var label = new TextBlock { Text = "1 × 1", HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 };
        var grid = new Grid();
        for (int c = 0; c < GridCols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < GridRows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var idle = new SolidColorBrush(Color.FromArgb(255, 0xE4, 0xE4, 0xE4));
        var hot = new SolidColorBrush(Color.FromArgb(255, 0x60, 0xA0, 0xE0));
        var border = new SolidColorBrush(Color.FromArgb(255, 0xAA, 0xAA, 0xAA));
        var cells = new Border[GridRows, GridCols];

        void Highlight(int rr, int cc)
        {
            for (int r = 0; r < GridRows; r++)
                for (int c = 0; c < GridCols; c++)
                    cells[r, c].Background = (r <= rr && c <= cc) ? hot : idle;
            label.Text = $"{rr + 1} × {cc + 1}";
        }

        for (int r = 0; r < GridRows; r++)
            for (int c = 0; c < GridCols; c++)
            {
                var cell = new Border
                {
                    Width = 16, Height = 16, Margin = new Thickness(1),
                    Background = idle, BorderBrush = border, BorderThickness = new Thickness(0.5),
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                int rr = r, cc = c;
                cell.PointerEntered += (_, _) => Highlight(rr, cc);
                // Pick rows×cols here; then drag from the caret on the document to set the table's size.
                cell.Tapped += (_, _) => { Target?.BeginTableDraw(rr + 1, cc + 1); flyout.Hide(); };
                cells[r, c] = cell;
                grid.Children.Add(cell);
            }
        Highlight(0, 0);
        root.Children.Add(grid);
        root.Children.Add(label);
        flyout.Content = root;
        return flyout;
    }

    private async Task PickAndInsertImageAsync()
    {
        if (Target == null) return;
        try
        {
            if (_imagePicker == null)
            {
                // No host picker: the editor's own, which only needs the window handle the file actions use.
                if (WindowHandle != 0) await Target.InsertImageFromFileAsync(WindowHandle);
                return;
            }
            var bytes = await _imagePicker();
            if (bytes is { Length: > 0 }) Target.InsertImageBlock(bytes);
        }
        // host picker failed/cancelled
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
    }

    // A combo-style list control: a bordered box of [icon (toggles the list) | current marker | ▾ (style
    // menu)]. Returns the box plus the icon button and preview label so Sync can highlight the active state
    // and show the caret paragraph's current marker.
    private (Border Box, Button Icon, TextBlock Preview) BuildListBox(
        RichEditorIcon iconKind, string tip, Action toggle, params (ListMarkerStyle Style, string Glyph)[] options)
    {
        var icon = new Button
        {
            Content = IconOrText(iconKind, options[0].Glyph),
            Background = ClearBrush, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 2, 2, 2), MinWidth = 0, VerticalAlignment = VerticalAlignment.Center,
        };
        icon.Click += (_, _) => toggle();
        ToolTipService.SetToolTip(icon, tip);

        var preview = new TextBlock
        {
            Text = options[0].Glyph, FontSize = 12, MinWidth = 16,
            TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };

        // Styles only — no "없음" entry. Removal is the icon button immediately to the left of this
        // dropdown (its toggle now clears the whole list state, nesting level included), so a "none"
        // item here was a second door to the same command and mixed a non-style into a style picker.
        // Word/HWP pickers do carry one; Google Docs doesn't, and with a complete toggle the simpler
        // shape wins. The explicit, labelled "목록 제거" survives in the right-click menu.
        var menu = new MenuFlyout();
        ReturnsFocus(menu);
        foreach (var (style, g) in options)
        {
            var s = style;
            var item = new MenuFlyoutItem { Text = g };
            item.Click += (_, _) => Target?.SetListStyle(s);
            menu.Items.Add(item);
        }
        var caret = new Button
        {
            Content = SmallChevron(),
            Background = ClearBrush, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(2, 0, 2, 0), MinWidth = 16, VerticalAlignment = VerticalAlignment.Center,
            Flyout = menu,
        };
        ToolTipService.SetToolTip(caret, tip);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(icon);
        row.Children.Add(preview);
        row.Children.Add(caret);
        var box = new Border
        {
            Child = row,
            BorderBrush = ComboBorderBrush,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 0, 4, 0),
            Height = CtlHeight, VerticalAlignment = VerticalAlignment.Center,
        };
        return (box, icon, preview);
    }

    // Line-spacing control: a bordered box (matching the list boxes) holding the glyph, an editable % box,
    // tight ▲▼ steppers (±10%) and a ▾ presets dropdown. Each maps to Paragraph.LineSpacing = %/100.
    private Border BuildLineSpacingControl()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(AsElement(IconOrText(RichEditorIcon.LineSpacing, "↕")));

        _spacingBox = new TextBox
        {
            Text = "100%", MinWidth = 36, FontSize = 12, MinHeight = 0,
            Padding = new Thickness(2, 1, 2, 1), BorderThickness = new Thickness(0),
            Background = ClearBrush, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        // Each commit applies a line spacing, and that pushes an undo checkpoint — so one Enter must
        // produce exactly one. Returning focus below raises LostFocus, whose own Commit is suppressed.
        bool committing = false;
        void Commit()
        {
            if (committing) return;
            committing = true;
            try { ApplySpacingPercent(CurrentSpacingPercent()); }
            finally { committing = false; }
        }
        // The box is a real edit field, so it takes focus on purpose (unlike the buttons). Enter means
        // "done" — commit AND hand focus back, or the caret stays hidden and typing goes on landing here.
        _spacingBox.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            committing = true;
            try { ApplySpacingPercent(CurrentSpacingPercent()); ReturnFocusToEditor(); }
            finally { committing = false; }
        };
        _spacingBox.LostFocus += (_, _) => Commit();
        ToolTipService.SetToolTip(_spacingBox, Loc("LineSpacing"));
        row.Children.Add(_spacingBox);

        var menu = new MenuFlyout();
        ReturnsFocus(menu);
        foreach (var pct in SpacingPercents)
        {
            int p = pct;
            var item = new MenuFlyoutItem { Text = p + "%" };
            item.Click += (_, _) => ApplySpacingPercent(p);
            menu.Items.Add(item);
        }
        var presets = new Button
        {
            Content = SmallChevron(),
            Background = ClearBrush, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(2, 0, 2, 0), MinWidth = 16, VerticalAlignment = VerticalAlignment.Center,
            Flyout = menu,
        };
        ToolTipService.SetToolTip(presets, Loc("LineSpacing"));

        var steppers = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        steppers.Children.Add(StepButton(up: true, +10));
        steppers.Children.Add(StepButton(up: false, -10));

        row.Children.Add(presets);
        row.Children.Add(steppers);
        return new Border
        {
            Child = row,
            BorderBrush = ComboBorderBrush,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0, 4, 0),
            Height = CtlHeight, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private Button StepButton(bool up, int delta)
    {
        var b = new Button
        {
            Content = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = ((char)(up ? 0xE70E : 0xE70D)).ToString(), FontSize = 8 },
            Width = 16, Height = 11, Padding = new Thickness(0),
            Background = ClearBrush, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        b.Click += (_, _) => ApplySpacingPercent(CurrentSpacingPercent() + delta);
        return b;
    }

    // The percentage currently shown in the spacing box (digits only); 100 when empty/unparsable.
    private int CurrentSpacingPercent()
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in _spacingBox?.Text ?? "") if (char.IsDigit(c)) sb.Append(c);
        return int.TryParse(sb.ToString(), out int p) && p > 0 ? p : 100;
    }

    // Clamps a line-spacing %, reflects it in the box, and applies it to the caret paragraph.
    private void ApplySpacingPercent(int pct)
    {
        pct = Math.Clamp(pct, 100, 1000);
        if (_spacingBox != null) _spacingBox.Text = pct + "%";
        Target?.SetLineSpacing(pct / 100.0);
    }

    // ---- caret-state reflection -------------------------------------------
    private void Sync()
    {
        var rt = Target;
        // Read-only toggled since the last Build → rebuild (edit toolbar ⇄ view toolbar).
        if (rt != null && rt.IsReadOnly != _builtReadOnly) Content = Build();
        if (rt == null) return;

        _suppress = true;
        try
        {
            var f = rt.GetCaretFormat();
            if (_bold != null) SetActive(_bold, f.Bold);
            if (_italic != null) SetActive(_italic, f.Italic);
            if (_underline != null) SetActive(_underline, f.Underline);
            if (_strike != null) SetActive(_strike, f.Strike);
            if (_bullet != null) SetActive(_bullet, f.List == ListKind.Bullet);
            if (_number != null) SetActive(_number, f.List == ListKind.Ordered);
            if (_quote != null) SetActive(_quote, f.Quote);
            if (_painter != null) SetActive(_painter, rt.IsFormatPainterActive);

            // List previews show the caret paragraph's current marker, full-ink when that list kind is
            // active and dimmed otherwise.
            if (_bulletPreview != null)
            {
                bool bulletOn = f.List == ListKind.Bullet;
                _bulletPreview.Text = RichEditor.ListMarkerText(ListKind.Bullet, bulletOn ? f.ListMarker : ListMarkerStyle.Default, 1);
                _bulletPreview.Foreground = bulletOn ? BlackInk : DimInk;
            }
            if (_numberPreview != null)
            {
                bool numberOn = f.List == ListKind.Ordered;
                _numberPreview.Text = RichEditor.ListMarkerText(ListKind.Ordered, numberOn ? f.ListMarker : ListMarkerStyle.Default, 1);
                _numberPreview.Foreground = numberOn ? BlackInk : DimInk;
            }

            // Picker bars follow the caret's run: explicit colours show as-is, defaults fall back to
            // black text / "no highlight" grey. (ReflectPickerColor null-guards the swatches.)
            ReflectPickerColor(false, f.Foreground is { } fg ? new SolidColorBrush(fg) : BlackInk);
            ReflectPickerColor(true, f.Background is { } bg ? new SolidColorBrush(bg) : NoColorBrush);

            if (_font != null)
            {
                string fam = f.FontFamily ?? rt.DefaultFontFamily;
                if (fam != _font.Reflected) SetFontSelection(fam, apply: false);
            }
            if (_size != null)
            {
                // Off-ladder sizes (e.g. 10.5 pt from a pasted document) have no item — show them via
                // PlaceholderText rather than leaving the combo blank (same trick as the zoom combo).
                double fs = f.FontSize > 0 ? f.FontSize : BodySizePt;
                ComboBoxItem? hit = null;
                foreach (var it in _size.Items)
                    if (it is ComboBoxItem ci && ci.Tag is double d && Math.Abs(d - fs) < 0.01) { hit = ci; break; }
                if (hit != null) _size.SelectedItem = hit;
                else { _size.SelectedIndex = -1; _size.PlaceholderText = PtText(fs); }
            }
            if (_heading != null) SelectByTag(_heading, f.Heading);
            if (_align != null) SelectByTag(_align, f.Align);

            // Spacing box shows the caret paragraph's current % (unset = the HWP default 160%). Don't
            // overwrite while the user is editing the field.
            if (_spacingBox != null && _spacingBox.FocusState == FocusState.Unfocused)
            {
                double ls = f.LineSpacing;
                int pct = (int)Math.Round((double.IsNaN(ls) || ls <= 0 ? RichEditor.DefaultLineSpacing : ls) * 100);
                _spacingBox.Text = pct + "%";
            }

            if (_undo != null) _undo.IsEnabled = rt.CanUndo;
            if (_redo != null) _redo.IsEnabled = rt.CanRedo;
            if (_tableBtn != null) _tableBtn.Visibility = rt.AllowTables ? Visibility.Visible : Visibility.Collapsed;
            if (_imageBtn != null)
            {
                _imageBtn.Visibility = rt.AllowImages ? Visibility.Visible : Visibility.Collapsed;
                // Enabled, it did nothing at all with neither a host picker nor a window handle (measured
                // 2026-09-14): PickAndInsertImageAsync returned silently. Upstream falls back to its own picker.
                _imageBtn.IsEnabled = _imagePicker != null || WindowHandle != 0;
            }
            // The divider belongs to the insert group: shown while tables OR images are allowed, like the
            // context menu's divider item (and upstream's toolbar). It used to stay visible regardless.
            if (_dividerBtn != null) _dividerBtn.Visibility = rt.AllowTables || rt.AllowImages ? Visibility.Visible : Visibility.Collapsed;
            SyncPage();        // reflect zoom/paper/orientation state
            SyncFileActions(); // Print/Import button visibility
        }
        finally { _suppress = false; }
    }

    private static void SetActive(ToggleButton b, bool active)
    {
        b.IsChecked = active;
        b.Background = active ? ActiveBrush : ClearBrush;
    }

    // List-box icon buttons aren't ToggleButtons; reflect active state via background only.
    private static void SetActive(Button b, bool active) => b.Background = active ? ActiveBrush : ClearBrush;

    private static void SelectByTag(ComboBox combo, object tag)
    {
        foreach (var item in combo.Items)
            if (item is ComboBoxItem ci && Equals(ci.Tag, tag)) { combo.SelectedItem = ci; return; }
        combo.SelectedIndex = -1;
    }

    // ---- small widget builders --------------------------------------------
    // Fixed-height group divider (a full-line bar looked heavy; wrap-panel centering aligns it).
    // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): the 4px horizontal margin makes the inter-group
    // gap (2×4 = 8px between neighbours) read wider than the 2px in-group gap without a second panel
    // constant. Tagged with SepTag so the Batch-3 overflow panel can identify separators as group breaks.
    /// <summary>Tag applied to the toolbar's group separators (see <see cref="Sep"/>).</summary>
    internal const string SepTag = "QNote.Toolbar.Sep";
    private static UIElement Sep()
    {
        var bar = new Border
        {
            Width = 1, Height = 20, Margin = new Thickness(4, 0, 4, 0),
            // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): theme-aware group rule. The old fixed
            // 24%-black wash was nearly invisible on the light strip; these read as a clear (not heavy)
            // group break in both themes — light 20% black, dark 22% white.
            Background = SeparatorBrush,
        };
        bar.Tag = SepTag;
        return bar;
    }

    // Icon precedence: host override (RichEditorIcons.Provider) > built-in vector icon
    // (ToolbarIcons.CreateVector, the upstream peer's pictures) > styled-text fallback (`text` — the letters
    // B/I/U/S by design, as upstream). The Segoe glyphs (ToolbarIcons.Create) are the context menu's.
    // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): the provider signature carries no box, so a
    // returned Viewbox is re-sized to `box` here (the host's icons ship at their own default 18); the
    // built-in vector is asked for `box` directly.
    private static object IconOrText(RichEditorIcon? icon, string text, double box = IconBox)
    {
        UIElement? el = icon is { } k ? RichEditorIcons.TryCreate(k) ?? ToolbarIcons.CreateVector(k, box) : null;
        if (el is Viewbox vb) { vb.Width = box; vb.Height = box; }
        return el ?? (object)text;
    }

    // Resolves a Host-override-or-built-in icon into an element (never the text fallback), re-sized to
    // `box`. Used by the combo item templates, which need a real icon, not a letter.
    private static UIElement ResolvedIcon(RichEditorIcon icon, double box = IconBox)
    {
        var el = RichEditorIcons.TryCreate(icon) ?? ToolbarIcons.CreateVector(icon, box);
        if (el is Viewbox vb) { vb.Width = box; vb.Height = box; }
        return el ?? new Border { Width = box, Height = box };
    }

    // A vector icon is drawn in fixed ink, so unlike a FontIcon it does not follow the button's disabled
    // foreground: dim it with the button (undo/redo with no history).
    // A property callback, not IsEnabledChanged: that event is not raised synchronously for a local set (and
    // not at all off the live tree), so the first Sync after a build left undo looking enabled.
    private static void DimVectorIconWhenDisabled(ContentControl button)
    {
        if (button.Content is not Viewbox icon) return;
        void Sync() => icon.Opacity = button.IsEnabled ? 1 : 0.4;
        button.RegisterPropertyChangedCallback(Control.IsEnabledProperty, (_, _) => Sync());
        Sync();
    }

    private Button IconButton(string glyph, string tip, Action act, RichEditorIcon? icon = null)
    {
        var b = BaseButton(glyph, tip, icon);
        b.Click += (_, _) => act();
        return b;
    }

    private Button IconButton(string glyph, string tip, Func<Task> act, RichEditorIcon? icon = null)
    {
        var b = BaseButton(glyph, tip, icon);
        b.Click += async (_, _) => await act();
        return b;
    }

    private static Button BaseButton(string glyph, string tip, RichEditorIcon? icon = null)
    {
        var b = new Button
        {
            // AsElement centres + tightens text fallbacks (▦ table, ✕ clear, ⇥/⇤ indent) so they
            // line up with the FontIcon buttons instead of sitting low as raw strings.
            Content = AsElement(IconOrText(icon, glyph)),
            Width = BtnWidth,
            Height = CtlHeight,
            Padding = new Thickness(0),
            Background = ClearBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(Corner),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTipService.SetToolTip(b, tip);
        ApplyStripButtonChrome(b);
        DimVectorIconWhenDisabled(b);
        return NoFocus(b); // the caret must survive a button click — see the focus discipline section
    }

    // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): the shared flat-strip button chrome.
    //  - hover / pressed faces: override the template's PointerOver/Pressed resources (winning over the
    //    stock faint wash) with HoverBrush / PressedBrush.
    //  - 150ms fade: the stock template animates the ContentPresenter's Background over its own default
    //    (83ms, `0:0:0.083`); the presenter is created lazily by the template, so retime it on Loaded.
    //    (Control exposes no BackgroundTransition — only Border/Panel/ContentPresenter do — so the
    //    presenter is reached through the visual tree rather than set on the Button itself.)
    //  - 1.06 icon scale: the icon element (button.Content) is scaled via UIElement.Scale with a
    //    Vector3Transition, kept subtle enough not to disturb the layout (a render transform is not
    //    measured) and to leave `DimVectorIconWhenDisabled`'s `Content is Viewbox` check intact.
    private static void ApplyStripButtonChrome(ContentControl button)
    {
        button.Resources["ButtonBackgroundPointerOver"] = HoverBrush;
        button.Resources["ButtonBackgroundPressed"] = PressedBrush;
        button.Resources["ButtonBackgroundDisabled"] = ClearBrush;
        button.Resources["ButtonBorderBrushPointerOver"] = ClearBrush;
        button.Resources["ButtonBorderBrushPressed"] = ClearBrush;
        button.Resources["ButtonBorderBrushDisabled"] = ClearBrush;
        ApplyHoverMotion(button);
    }

    // Installs the 150ms hover fade + 1.06 icon scale. Split out so ToggleBtn (which needs the checked
    // faces on top) can reuse it.
    private static void ApplyHoverMotion(ContentControl button)
    {
        button.Loaded += (_, _) =>
        {
            if (FindContentPresenter(button) is { } presenter)
                presenter.BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(150) };
        };
        button.PointerEntered += (_, _) => ScaleIcon(button, 1.06f);
        button.PointerExited += (_, _) => ScaleIcon(button, 1.0f);
    }

    // Sets the button's icon element scale, installing the transition once so the change eases. The icon
    // element is button.Content (a Viewbox for vector icons, a TextBlock for letter fallbacks, a
    // StackPanel for the colour faces) — all are UIElements and all scale the same way.
    private static void ScaleIcon(ContentControl button, float scale)
    {
        if (button.Content is not UIElement icon) return;
        icon.ScaleTransition ??= new Vector3Transition { Duration = TimeSpan.FromMilliseconds(150) };
        icon.Scale = new System.Numerics.Vector3(scale, scale, 1f);
    }

    // The stock Button/ToggleButton template's ContentPresenter (the element that actually paints the
    // hover background). Found depth-first; the presenter is the first Border-less content host.
    private static ContentPresenter? FindContentPresenter(DependencyObject root)
    {
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ContentPresenter cp) return cp;
            if (FindContentPresenter(child) is { } deep) return deep;
        }
        return null;
    }

    private ToggleButton ToggleBtn(string glyph, string tip, Action act, bool bold = false, bool italic = false, RichEditorIcon? icon = null,
        Windows.UI.Text.TextDecorations decorations = Windows.UI.Text.TextDecorations.None)
    {
        var content = AsElement(IconOrText(icon, glyph)); // centred + tight, like the icon buttons
        // The styled letters (B I U S) show what they do — U underlined, S struck through, as upstream.
        if (content is TextBlock letter) letter.TextDecorations = decorations;
        var b = new ToggleButton
        {
            Content = content,
            Width = BtnWidth,
            Height = CtlHeight,
            Padding = new Thickness(0),
            Background = ClearBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(Corner),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            // Letter styling only matters for the text fallback; a FontIcon ignores it.
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
            FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
        };
        ToolTipService.SetToolTip(b, tip);
        ApplyStripButtonChrome(b); // P2: hover/pressed/150ms/1.06 scale (shared with BaseButton)
        ApplyToggleCheckedStyle(b);
        // Drive on click; Sync() owns IsChecked, so don't react to Checked/Unchecked (would double-toggle).
        b.Click += (_, _) => { if (!_suppress) act(); };
        return NoFocus(b);
    }

    // Retints the ToggleButton template's Checked visual states. Without this the checked background
    // comes from the theme's accent brushes (solid blue + white glyph) regardless of what we assign to
    // Background, because the Checked visual state overwrites it.
    private static void ApplyToggleCheckedStyle(ToggleButton b)
    {
        // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): the UNCHECKED pointer-over/pressed faces
        // use the ToggleButton* resource keys (the Button* keys ApplyStripButtonChrome sets don't apply
        // to a ToggleButton's template), so the strip's hover reads the same on toggles and buttons.
        b.Resources["ToggleButtonBackgroundPointerOver"] = HoverBrush;
        b.Resources["ToggleButtonBackgroundPressed"] = PressedBrush;
        b.Resources["ToggleButtonBackgroundDisabled"] = ClearBrush;
        b.Resources["ToggleButtonBorderBrushPointerOver"] = ClearBrush;
        b.Resources["ToggleButtonBorderBrushPressed"] = ClearBrush;
        b.Resources["ToggleButtonBorderBrushDisabled"] = ClearBrush;

        b.Resources["ToggleButtonBackgroundChecked"] = ActiveBrush;
        b.Resources["ToggleButtonBackgroundCheckedPointerOver"] = ActiveHoverBrush;
        b.Resources["ToggleButtonBackgroundCheckedPressed"] = ActiveHoverBrush;
        b.Resources["ToggleButtonForegroundChecked"] = BlackInk;
        b.Resources["ToggleButtonForegroundCheckedPointerOver"] = BlackInk;
        b.Resources["ToggleButtonForegroundCheckedPressed"] = BlackInk;
        b.Resources["ToggleButtonBorderBrushChecked"] = ClearBrush;
        b.Resources["ToggleButtonBorderBrushCheckedPointerOver"] = ClearBrush;
        b.Resources["ToggleButtonBorderBrushCheckedPressed"] = ClearBrush;
    }

    private ComboBox MakeCombo(double width, string tip)
    {
        // No margin: the wrap panel's HorizontalSpacing is the single source of gaps, so the space
        // between any two strip controls is identical (per-control margins made combo gaps wider).
        // FontSize pinned so every combo's selected value renders at the same point size (the font-name
        // items still show in their own typeface — a feature — just at the uniform size).
        // MinHeight too: the default ComboBox style pins TextControlThemeMinHeight (32), which would
        // clamp a smaller Height right back up and leave the combos taller than the buttons.
        // QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): border + corner radius per the PRD dropdown
        // spec (1px #1F000000/#1FFFFFFF, radius 4, height 30). Height/MinHeight track CtlHeight so the
        // combos stay level with the 30px buttons; the border is theme-aware and recolors on a theme flip
        // (the toolbar rebuilds the strip, re-reading ComboBorderBrush).
        var c = new ComboBox
        {
            Width = width,
            FontSize = ComboFontSize,
            Height = CtlHeight,
            MinHeight = CtlHeight,
            Padding = new Thickness(10, 0, 6, 0),
            CornerRadius = new CornerRadius(Corner),
            BorderThickness = new Thickness(1),
            BorderBrush = ComboBorderBrush,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(c, tip);
        // A combo legitimately needs focus while its list is open, so it can't simply refuse it like the
        // buttons do. Hand focus back once the dropdown CLOSES — doing it on SelectionChanged instead
        // would yank focus away while arrowing through an open list. Every combo in the strip (including
        // the page/zoom ones in RichEditorToolbar.PageFile) is built here, so this is the single point.
        c.DropDownClosed += (_, _) => ReturnFocusToEditor();
        return c;
    }

    // A palette + hex-input flyout button. The face is the picker glyph over a thin bar that shows the
    // caret's current colour (updated in Sync). `highlight` selects foreground vs highlight (background).
    private Button ColorButton(string glyph, string tip, bool highlight)
    {
        var initial = new SolidColorBrush(highlight ? Color.FromArgb(255, 0xFF, 0xF1, 0x76) : Colors.Black);

        // Text colour keeps the plain "A" letter (a FontColor FontIcon carries its own colour element,
        // which doubles up awkwardly with the bar); the highlight face uses the real Highlight pen icon
        // so its visual weight matches the other 16px FontIcons (the ✎ text glyph looked undersized).
        var glyphEl = highlight
            ? RichEditorIcons.TryCreate(RichEditorIcon.Highlight) ?? ToolbarIcons.CreateVector(RichEditorIcon.Highlight, 16) ?? AsElement(glyph)
            : AsElement(glyph);
        var swatch = new Border
        {
            Height = 6, MinWidth = 24, CornerRadius = new CornerRadius(1),
            Background = initial, Margin = new Thickness(0, 2, 0, 0),
        };
        if (highlight) _highlightSwatch = swatch; else _colorSwatch = swatch;
        var face = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        face.Children.Add(glyphEl);
        face.Children.Add(swatch);

        var btn = new Button
        {
            Content = face,
            Width = BtnWidth,
            Height = CtlHeight,
            Padding = new Thickness(0),
            Background = ClearBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(Corner), // P2: match the strip's rounded hover face
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTipService.SetToolTip(btn, tip);
        ApplyStripButtonChrome(btn); // P2: hover/pressed faces + 150ms fade + 1.06 scale
        NoFocus(btn);

        var flyout = new Flyout();
        ReturnsFocus(flyout); // ditto — a swatch/hex click must leave the caret where it was
        void Apply(Color? c)
        {
            if (Target == null) return;
            if (highlight) Target.SetHighlight(c);
            else Target.SetForeground(c); // null = back to the automatic default (theme-aware)
            ReflectPickerColor(highlight, c is { } cc ? new SolidColorBrush(cc) : (highlight ? NoColorBrush : BlackInk));
            flyout.Hide();
        }

        // 8-column palette: fixed-size swatches in a width-capped wrap panel wrap to 5 rows of 8.
        var grid = new ToolbarWrapPanel { Width = 8 * 24, HorizontalSpacing = 0, VerticalSpacing = 0 };
        foreach (var hex in Palette)
        {
            var color = ParseHex(hex);
            var sw = new Button
            {
                Background = new SolidColorBrush(color),
                Width = 22, Height = 22, Margin = new Thickness(1), Padding = new Thickness(0),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0x80, 0x80, 0x80)),
            };
            sw.Click += (_, _) => Apply(color);
            grid.Children.Add(sw);
        }

        var panel = new StackPanel { Spacing = 6, Width = 200, Padding = new Thickness(4) };
        panel.Children.Add(grid);

        // "No highlight" / "Automatic (default)": both clear the explicit color back to null. The
        // foreground previously had no way back to the automatic default short of ClearFormatting.
        var none = new Button { Content = Loc(highlight ? "NoHighlight" : "AutoColor"), HorizontalAlignment = HorizontalAlignment.Stretch };
        none.Click += (_, _) => Apply(null);
        panel.Children.Add(none);

        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var hexBox = new TextBox { PlaceholderText = "#RRGGBB", Width = 110 };
        var applyBtn = new Button { Content = Loc("Apply") };
        applyBtn.Click += (_, _) => { if (TryParseHex(hexBox.Text, out var c)) Apply(c); };
        hexRow.Children.Add(hexBox);
        hexRow.Children.Add(applyBtn);
        panel.Children.Add(hexRow);

        flyout.Content = panel;
        btn.Flyout = flyout;
        return btn;
    }

    // Pushes `brush` onto the relevant picker's current-colour bar.
    private void ReflectPickerColor(bool highlight, Brush brush)
    {
        if (highlight) { if (_highlightSwatch != null) _highlightSwatch.Background = brush; }
        else { if (_colorSwatch != null) _colorSwatch.Background = brush; }
    }

    // Small dropdown chevron (Segoe Fluent ChevronDown) — the "▾" text glyph rendered at wildly
    // different sizes depending on the fallback font; a 10px FontIcon is identical everywhere.
    private static UIElement SmallChevron() => new FontIcon
    {
        FontFamily = new FontFamily("Segoe Fluent Icons"),
        Glyph = ((char)0xE70D).ToString(), // ChevronDown
        FontSize = 10,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // Coerces an IconOrText result (UIElement or string) into a centred element for a button face.
    // Text fallbacks render at the object-icon size with TIGHT line bounds — otherwise the glyph's
    // ascent/descent padding pushes it off the vertical centre that FontIcons sit on (this is why a
    // raw-string "▦" table glyph looked low and wide next to the image FontIcon).
    private static UIElement AsElement(object content)
        => content as UIElement ?? new TextBlock
        {
            Text = content.ToString(), FontSize = 15,
            TextLineBounds = Microsoft.UI.Xaml.TextLineBounds.Tight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

    private static Color ParseHex(string hex) => TryParseHex(hex, out var c) ? c : Colors.Black;

    private static bool TryParseHex(string? hex, out Color color)
    {
        color = Colors.Black;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6 && s.Length != 8) return false;
        if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var v)) return false;
        byte a = s.Length == 8 ? (byte)(v >> 24) : (byte)255;
        byte r = (byte)((v >> (s.Length == 8 ? 16 : 16)) & 0xFF);
        byte g = (byte)((v >> 8) & 0xFF);
        byte b = (byte)(v & 0xFF);
        color = Color.FromArgb(a, r, g, b);
        return true;
    }
}
