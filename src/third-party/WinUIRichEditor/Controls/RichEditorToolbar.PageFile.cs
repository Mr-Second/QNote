using System;
using WinUIRichEditor.Documents;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinUIRichEditor.Formatters;

namespace WinUIRichEditor.Controls;

/// <summary>How much of the toolbar is shown. A coarse density knob layered over the individual controls;
/// capability (read-only, Allow*) still vetoes what a level would otherwise show.</summary>
public enum ToolbarLevel
{
    /// <summary>Derive from the target: editable → <see cref="Normal"/>. (Read-only always shows the view
    /// toolbar regardless of level.)</summary>
    Auto,
    /// <summary>Undo/redo, B/I/U/S, font size only.</summary>
    Minimal,
    /// <summary>Full text formatting (font, color, heading, align, lists, indent, spacing) + inserts.</summary>
    Normal,
    /// <summary>Everything, plus the page/zoom controls and file actions (Export/Import/Print).</summary>
    Maximum,
}

// Built-in page/zoom controls (zoom · paper · orientation) and file actions (Export / Import / Print),
// moved here from RichEditorView so a standalone toolbar carries them too. RichEditorView now delegates to
// these (WindowHandle / PrintRequested / ShowFileActions forward straight through). Both sections default on
// and sit at the end of the wrapping strip, before any host TrailingItems.
public partial class RichEditorToolbar
{
    // ---- toolbar density level --------------------------------------------
    private ToolbarLevel _level = ToolbarLevel.Auto;

    /// <summary>Toolbar density. <see cref="ToolbarLevel.Auto"/> (default) resolves to
    /// <see cref="ToolbarLevel.Normal"/> for an editable target; a read-only target always shows the view
    /// toolbar (page/zoom + Export/Print) regardless of this. Capability (Allow*) still vetoes buttons.</summary>
    public ToolbarLevel ToolbarLevel
    {
        get => (ToolbarLevel)GetValue(ToolbarLevelProperty);
        set => SetValue(ToolbarLevelProperty, value);
    }

    /// <summary>Identifies the <see cref="ToolbarLevel"/> dependency property.</summary>
    public static readonly DependencyProperty ToolbarLevelProperty = DependencyProperty.Register(
        nameof(ToolbarLevel), typeof(ToolbarLevel), typeof(RichEditorToolbar),
        new PropertyMetadata(ToolbarLevel.Auto, (d, e) => ((RichEditorToolbar)d).OnLayoutFlagChanged(() => ((RichEditorToolbar)d)._level = (ToolbarLevel)e.NewValue)));

    // The toolbar's own layout settings are dependency properties so XAML can bind them; each one rebuilds the
    // strip, as its setter did. The fields stay what Build() reads.
    private void OnLayoutFlagChanged(Action store) { store(); Content = Build(); Sync(); }

    // The concrete level to build (Auto → Normal). Read-only is handled separately in Build().
    private ToolbarLevel EffectiveLevel() => _level == ToolbarLevel.Auto ? ToolbarLevel.Normal : _level;

    // ---- page / zoom ------------------------------------------------------
    private ComboBox? _zoom, _paper, _orient;
    private Border? _marginBox;
    private TextBlock? _marginLabel;
    private ComboBoxItem? _zoomFit;    // the "Fit width" entry (disabled in Continuous mode)

    private static readonly int[] ZoomLevels = { 50, 75, 100, 125, 150, 200 };
    private const string FitWidthTag = "fit";
    private static readonly (string label, RichEditorPageSize size)[] PaperSizes =
    {
        ("PaperContinuous", RichEditorPageSize.Continuous), ("A4", RichEditorPageSize.A4),
        ("A3", RichEditorPageSize.A3), ("A5", RichEditorPageSize.A5), ("B4", RichEditorPageSize.B4),
        ("B5", RichEditorPageSize.B5), ("Letter", RichEditorPageSize.Letter),
        ("Legal", RichEditorPageSize.Legal), ("Tabloid", RichEditorPageSize.Tabloid),
    };

    private bool _showPageControls = true;
    /// <summary>Whether the built-in zoom / paper-size / orientation controls are shown at the end of the
    /// strip. Default true.</summary>
    public bool ShowPageControls
    {
        get => (bool)GetValue(ShowPageControlsProperty);
        set => SetValue(ShowPageControlsProperty, value);
    }

    /// <summary>Identifies the <see cref="ShowPageControls"/> dependency property.</summary>
    public static readonly DependencyProperty ShowPageControlsProperty = DependencyProperty.Register(
        nameof(ShowPageControls), typeof(bool), typeof(RichEditorToolbar),
        new PropertyMetadata(true, (d, e) => ((RichEditorToolbar)d).OnLayoutFlagChanged(() => ((RichEditorToolbar)d)._showPageControls = (bool)e.NewValue)));

    private void BuildPageControls(ToolbarWrapPanel strip)
    {
        _zoom = MakeCombo(104, Loc("ZoomTip"));
        _zoomFit = new ComboBoxItem { Content = Loc("FitWidth"), Tag = FitWidthTag };
        _zoom.Items.Add(_zoomFit); // fit width at the top
        foreach (var pct in ZoomLevels) _zoom.Items.Add(new ComboBoxItem { Content = pct + "%", Tag = pct });
        _zoom.SelectionChanged += (_, _) =>
        {
            if (_suppress || Target == null || _zoom!.SelectedItem is not ComboBoxItem ci) return;
            if (Equals(ci.Tag, FitWidthTag)) Target.FitToWidth();
            else if (ci.Tag is int pct) Target.SetZoom(pct / 100.0);
        };
        // No icon: the Segoe Fluent "Zoom" glyph is a magnifier, and so is the Find button's "Search" one
        // — two unlabelled magnifiers side by side in the same strip read as the same control. The combo
        // identifies itself without help: its first item is "Fit width" and the rest are percentages, and
        // its tooltip says zoom. (The original toolbar has no icon here either.)
        strip.Children.Add(_zoom);

        _paper = MakeCombo(120, Loc("PaperTip"));
        foreach (var (label, _) in PaperSizes) _paper.Items.Add(new ComboBoxItem { Content = label == "PaperContinuous" ? Loc(label) : label });
        _paper.SelectionChanged += (_, _) => OnPaperChanged();
        strip.Children.Add(_paper);

        _orient = MakeCombo(86, Loc("OrientationTip"));
        _orient.Items.Add(new ComboBoxItem { Content = Loc("OrientPortrait"), Tag = RichEditorPageOrientation.Portrait });
        _orient.Items.Add(new ComboBoxItem { Content = Loc("OrientLandscape"), Tag = RichEditorPageOrientation.Landscape });
        // The pickers edit the OPEN DOCUMENT's page setup, not the host's defaults (see EditDocumentPageSetup).
        _orient.SelectionChanged += (_, _) =>
        {
            if (!_suppress && Target is { } t && _orient!.SelectedItem is ComboBoxItem ci)
                t.EditDocumentPageSetup(() => t.PageOrientation = (RichEditorPageOrientation)ci.Tag);
        };
        strip.Children.Add(_orient);

        // Page margins. Presets only, as Word and HWP lead with (upstream PR #52, ported 2026-09-24): a margin set
        // by a host or carried by a document need not be one of them — then the box states the millimetres.
        strip.Children.Add(BuildMarginControl());
    }

    // Five steps, narrowest first, in millimetres. The middle one is the editor's own default, so an untouched
    // document shows "Normal".
    private static readonly (string Label, PageMargins Margin)[] MarginPresets =
    {
        ("MarginNarrowest", new PageMargins(5)),
        ("MarginNarrow", new PageMargins(10)),
        ("MarginNormal", PageSetup.DefaultMargin),   // 15 mm
        ("MarginWide", new PageMargins(20)),
        ("MarginWidest", new PageMargins(30)),
    };

    // Margins that match no step, written as short as they are regular.
    private static string MarginText(PageMargins m)
    {
        if (m.Left == m.Right && m.Top == m.Bottom)
            return m.Left == m.Top ? $"{m.Left:0.#}mm" : $"{m.Left:0.#} / {m.Top:0.#}mm";
        return $"{m.Left:0.#} {m.Top:0.#} {m.Right:0.#} {m.Bottom:0.#}mm";
    }

    // Built like the line-spacing box: the icon once, the current step, and a chevron dropping plain entries.
    private Border BuildMarginControl()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(AsElement(IconOrText(RichEditorIcon.PageMargin, "▭")));
        _marginLabel = new TextBlock { FontSize = 12, MinWidth = 88, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_marginLabel);

        var menu = new MenuFlyout();
        ReturnsFocus(menu);
        foreach (var (label, margin) in MarginPresets)
        {
            var m = margin;
            var item = new MenuFlyoutItem { Text = Loc(label) };
            // The picker edits the OPEN DOCUMENT's setup, not the host's defaults (see EditDocumentPageSetup).
            item.Click += (_, _) => { if (Target is { } t) t.EditDocumentPageSetup(() => t.PageMargin = m); Sync(); };
            menu.Items.Add(item);
        }
        var presets = new Button
        {
            Content = SmallChevron(),
            Background = ClearBrush, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(2, 0, 2, 0), MinWidth = 16, VerticalAlignment = VerticalAlignment.Center,
            Flyout = menu,
        };
        row.Children.Add(presets);
        _marginBox = new Border
        {
            Child = row,
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0xDC, 0xDC, 0xDC)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0, 4, 0),
            Height = CtlHeight, VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(_marginBox, Loc("MarginTip"));
        return _marginBox;
    }

    private void OnPaperChanged()
    {
        if (_suppress || Target == null || _paper!.SelectedIndex < 0) return;
        var size = PaperSizes[_paper.SelectedIndex].size;
        var t = Target;
        t.EditDocumentPageSetup(() => // an edit of the open document, not of the host's defaults
        {
            t.PageSize = size;
            // A concrete paper size shows the page outline (page view); Continuous reflows with no chrome.
            t.ShowPageBoundaries = size != RichEditorPageSize.Continuous;
        });
        // Fit-width has no meaning in Continuous; drop back to 100% when leaving a fitted paged view.
        if (size == RichEditorPageSize.Continuous && Target.IsFitWidth) Target.SetZoom(1.0);
        // Suppressed: SyncPage writes SelectedItem/SelectedIndex on the zoom and orientation combos, and
        // this runs from a SelectionChanged handler (so _suppress is NOT already set). Without the guard
        // those writes re-enter their own handlers and push values back into the editor — harmless today
        // only because they happen to be the values just set. Sync() wraps SyncPage the same way.
        _suppress = true;
        try { SyncPage(); } finally { _suppress = false; }
    }

    // Reflect the editor's page/zoom state onto the built-in controls (called from Sync()).
    private void SyncPage()
    {
        if (_zoom == null || Target == null) return;
        bool paged = Target.PageSize != RichEditorPageSize.Continuous;
        if (_zoomFit != null) _zoomFit.IsEnabled = paged;
        if (paged && Target.IsFitWidth) SelectByTag(_zoom, FitWidthTag);
        else
        {
            // Non-preset factors (Ctrl+wheel produces 110%, 121%, …) show via PlaceholderText with
            // nothing selected, instead of leaving the combo blank. Deliberately NOT a dynamic
            // ComboBoxItem: SyncPage runs once per wheel notch during a Ctrl+wheel storm, and mutating
            // Items + selection there crashed WinUI's ComboBox (stowed E_INVALIDARG, app-fatal).
            // PlaceholderText only renders while SelectedIndex is -1, so stale text is harmless.
            int pct = (int)Math.Round(Target.Zoom * 100);
            if (Array.IndexOf(ZoomLevels, pct) >= 0) SelectByTag(_zoom, pct);
            else
            {
                _zoom.SelectedIndex = -1;
                _zoom.PlaceholderText = pct + "%";
            }
        }
        if (_paper != null)
            for (int i = 0; i < PaperSizes.Length; i++)
                if (PaperSizes[i].size == Target.PageSize) { _paper.SelectedIndex = i; break; }
        if (_orient != null)
        {
            SelectByTag(_orient, Target.PageOrientation);
            _orient.IsEnabled = paged; // orientation is meaningless in Continuous
        }
        if (_marginLabel != null && _marginBox != null)
        {
            string? step = null;
            foreach (var (label, m) in MarginPresets)
                if (m.Equals(Target.PageMargin)) { step = Loc(label); break; }
            _marginLabel.Text = step ?? MarginText(Target.PageMargin);
            // Margins are meaningless in Continuous, as orientation is. A Border has no IsEnabled: dim it and
            // disable the chevron, which is the only thing in it that acts.
            _marginBox.Opacity = paged ? 1 : 0.5;
            if (((StackPanel)_marginBox.Child).Children[^1] is Button chevron) chevron.IsEnabled = paged;
        }
    }

    // ---- file actions -----------------------------------------------------
    private Button? _exportBtn, _importBtn, _printBtn;

    /// <summary>HWND of the owning window, required to show the file picker in an unpackaged app. Set this
    /// from the host (<c>WinRT.Interop.WindowNative.GetWindowHandle(window)</c>); Export/Import are inert
    /// until it is set. It also lets the image button use the built-in picker when no <see cref="ImagePicker"/> is set.</summary>
    public nint WindowHandle
    {
        get => _windowHandle;
        set { _windowHandle = value; Sync(); }
    }
    private nint _windowHandle;

    private bool _showFileActions = true;
    /// <summary>Whether the built-in Export / Import (and Print, once <see cref="PrintRequested"/> is
    /// handled) buttons are shown at the end of the strip. Default true.</summary>
    public bool ShowFileActions
    {
        get => (bool)GetValue(ShowFileActionsProperty);
        set => SetValue(ShowFileActionsProperty, value);
    }

    /// <summary>Identifies the <see cref="ShowFileActions"/> dependency property.</summary>
    public static readonly DependencyProperty ShowFileActionsProperty = DependencyProperty.Register(
        nameof(ShowFileActions), typeof(bool), typeof(RichEditorToolbar),
        new PropertyMetadata(true, (d, e) => ((RichEditorToolbar)d).OnLayoutFlagChanged(() => ((RichEditorToolbar)d)._showFileActions = (bool)e.NewValue)));

    private EventHandler? _printRequested;
    /// <summary>Raised when the user clicks the built-in Print button. Printing is host-specific, so a host
    /// handles this to drive its own print/preview. The Print button stays hidden until a handler is attached.</summary>
    public event EventHandler? PrintRequested
    {
        add { _printRequested += value; Sync(); }
        remove { _printRequested -= value; Sync(); }
    }

    private void BuildFileActions(ToolbarWrapPanel strip)
    {
        _exportBtn = IconButton("⤓", Loc("Export"), () => _ = ExportAsync(), RichEditorIcon.Export);
        _importBtn = IconButton("⤒", Loc("Import"), () => _ = ImportAsync(), RichEditorIcon.Import);
        _printBtn = IconButton("⎙", Loc("Print"), () => _printRequested?.Invoke(this, EventArgs.Empty), RichEditorIcon.Print);
        _printBtn.Visibility = Visibility.Collapsed; // shown by SyncFileActions once a Print handler exists
        strip.Children.Add(_exportBtn);
        strip.Children.Add(_importBtn);
        strip.Children.Add(_printBtn);
    }

    // Import edits, so it's hidden in the read-only view toolbar; Print stays hidden until a host handles
    // PrintRequested. (Called from Sync().)
    private void SyncFileActions()
    {
        bool ro = Target?.IsReadOnly == true;
        if (_importBtn != null) _importBtn.Visibility = ro ? Visibility.Collapsed : Visibility.Visible;
        if (_printBtn != null)
            _printBtn.Visibility = _printRequested != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InitWithWindow(object target) => WinRT.Interop.InitializeWithWindow.Initialize(target, WindowHandle);

    // Both file actions are invoked fire-and-forget from their button (`() => _ = ExportAsync()`), so an
    // exception that escapes becomes an UNOBSERVED task exception and disappears without a trace — no
    // dialog, no error, nothing on screen. The picker calls themselves are the likeliest throwers (COM
    // failure, a broker that refuses, an unreadable file), which is exactly why the guard has to wrap the
    // WHOLE body including the picker, not just the parsing that follows it.
    private async Task ExportAsync()
    {
        try { await ExportCoreAsync(); }
        catch (Exception ex)
        {
            RichEditorDiagnostics.Report(ex);
            System.Diagnostics.Debug.WriteLine($"Export failed: {ex.Message}");
        }
    }

    private async Task ExportCoreAsync()
    {
        if (WindowHandle == 0 || Target?.Document == null) return;
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "document",
        };
        picker.FileTypeChoices.Add("JSON document", new List<string> { ".json" });
        picker.FileTypeChoices.Add("Flow package", new List<string> { ".flow" });
        picker.FileTypeChoices.Add("HTML document", new List<string> { ".html", ".htm" });
        picker.FileTypeChoices.Add("RTF document", new List<string> { ".rtf" });
        picker.FileTypeChoices.Add("PDF document", new List<string> { ".pdf" });
        InitWithWindow(picker);
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        // Format follows the chosen extension: .flow = ZIP package, .html/.htm = HTML, .rtf = RTF,
        // .pdf = rasterized pages (the control's SavePdf engine; output-only), else JSON.
        var ext = file.FileType.ToLowerInvariant();
        if (ext == ".flow")
        {
            using var ms = new MemoryStream();
            await Target.SavePackageAsync(ms);
            await FileIO.WriteBytesAsync(file, ms.ToArray());
        }
        else if (ext is ".html" or ".htm")
            await FileIO.WriteTextAsync(file, Target.ToHtml());
        else if (ext == ".rtf")
            await FileIO.WriteTextAsync(file, Target.ToRtf()); // RTF is ASCII (non-ASCII is \u-escaped)
        else if (ext == ".pdf")
        {
            using var ms = new MemoryStream();
            Target.SavePdf(ms); // Win2D render — UI thread (we're on it)
            await FileIO.WriteBytesAsync(file, ms.ToArray());
        }
        else
            await FileIO.WriteTextAsync(file, await Target.ToJsonAsync());
        // PDF is an output-only rendering (not reloadable), so it doesn't count as "saving" the
        // document — the dirty flag stays. The document formats clear it.
        if (ext != ".pdf") Target.MarkSaved();
    }

    private async Task ImportAsync()
    {
        try { await ImportCoreAsync(); }
        catch (Exception ex)
        {
            RichEditorDiagnostics.Report(ex);
            System.Diagnostics.Debug.WriteLine($"Import failed: {ex.Message}");
        }
    }

    private async Task ImportCoreAsync()
    {
        if (WindowHandle == 0 || Target == null) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var ext in new[] { ".json", ".flow", ".html", ".htm", ".rtf" }) picker.FileTypeFilter.Add(ext);
        InitWithWindow(picker);
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        var buffer = await FileIO.ReadBufferAsync(file);
        byte[] bytes = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
        await ImportBytesAsync(bytes);
    }

    /// <summary>Loads an imported file's bytes into the target, whatever format they are — Import minus its
    /// picker, so a test can hand it the bytes of any file.</summary>
    internal async Task ImportBytesAsync(byte[] bytes)
    {
        if (Target == null || bytes.Length == 0) return;

        // Sniff the content: ZIP magic ("PK") = .flow package, "{\rtf" = RTF, "<" = HTML, else JSON.
        // Faults land in ImportAsync's guard; the RTF branch reports through TryParse before that.
        if (bytes.Length >= 2 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K')
        {
            using var ms = new MemoryStream(bytes);
            await Target.LoadPackageAsync(ms);
            return;
        }

        // A byte-order mark goes before the sniff. Windows tools write one (Notepad before 1903, Visual Studio,
        // PowerShell 5's -Encoding utf8, "Unicode" = UTF-16), and it is not whitespace to TrimStart: the sniff
        // saw neither "<" nor "{\rtf" and every such file went to the JSON reader, which failed on it
        // (measured 2026-09-19: HTML, JSON and RTF with a UTF-8 BOM, HTML and JSON in UTF-16 — all five).
        // The failure lands in the diagnostics channel only, so on screen the import did nothing.
        var (encoding, bom) = TextEncodingOf(bytes);
        string text = encoding.GetString(bytes, bom, bytes.Length - bom);

        // RTF is parsed here rather than through LoadRtf so a damaged file reports on the same channel
        // as every other import fault: LoadRtf deliberately keeps the open document and stays silent,
        // which on a file-open reads as "nothing happened". RTF is 7-bit ASCII with its own escapes, so
        // its bytes are read as Latin1 (one char per byte), whatever mark precedes them.
        if (RtfDocumentFormatter.LooksLikeRtf(text))
        {
            string rtf = encoding is UTF8Encoding ? System.Text.Encoding.Latin1.GetString(bytes, bom, bytes.Length - bom) : text;
            if (RtfDocumentFormatter.TryParse(rtf, out var rtfDoc, out var rtfError))
                Target.LoadDocument(rtfDoc);
            else
                System.Diagnostics.Debug.WriteLine($"Import failed: {rtfError}");
        }
        else if (text.TrimStart().StartsWith("<", StringComparison.Ordinal)) Target.LoadHtml(text);
        else await Target.LoadJsonAsync(text);
    }

    // The encoding a byte-order mark names, and the mark's length; UTF-8 when there is none (what every
    // exporter here writes).
    private static (System.Text.Encoding encoding, int bom) TextEncodingOf(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (new UTF8Encoding(false), 3);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (System.Text.Encoding.Unicode, 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (System.Text.Encoding.BigEndianUnicode, 2);
        return (new UTF8Encoding(false), 0);
    }
}
