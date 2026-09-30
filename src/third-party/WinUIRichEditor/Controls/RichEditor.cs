using System;
using System.Collections.Generic;
using Windows.UI;
using Windows.UI.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

/// <summary>A from-scratch rich text editor built on Win2D's <c>CanvasTextLayout</c> engine (a port of
/// AvaloniaRichEditor). Phase 2a: read-only rendering of paragraphs (runs, lists, headings, quotes,
/// alignment, inline images), dividers and block images. Caret/selection/input (Phase 3), IME
/// (Phase 4), tables and the rest (Phase 5) build on this rendering core.</summary>
public partial class RichEditor : ContentControl
{
    // ---- layout constants (mirror the Avalonia original) -------------------
    internal const double BodyFontSizePt = 10;
    internal const double NaturalLineFactor = 1.2;
    // HWP's default line spacing (160%), applied when a paragraph sets neither LineSpacing nor LineHeight.
    // Paragraph.LineSpacing is HWP's "글자에 따라" ratio: line box = largest font size × ratio.
    internal const double DefaultLineSpacing = 1.6;
    // Where the text baseline sits inside a line box, as a fraction of its height. Used BOTH to tell
    // DirectWrite where to put the baseline under custom (Uniform) line spacing and to work back from a
    // baseline to the glyph top when placing the caret. One constant, because if the two disagree the
    // caret drifts away from the text — which is exactly what a hard-coded pair did at 200% spacing.
    internal const double BaselineFraction = 0.8;
    private const double ListMarkerWidth = 22;
    private const double DividerHeight = 18;
    internal const double A4ContentWidth = 698;

    // Font sizes in the model/API/serialization are points (pt). Win2D's CanvasTextLayout takes
    // device-independent pixels at a 96-DPI baseline (1pt = 4/3 px); convert only at this boundary.
    internal static double PtToPx(double pt) => pt * (4.0 / 3.0);

    // The gap above a block, which every walker advances by before the block's own box starts: its MarginTop,
    // or — when that is Block.AutoTopMargin (NaN) — ONE LINE GAP of body text: the white space a line break
    // leaves between two lines. Without it a table or picture butts straight against the paragraph above,
    // because paragraphs carry no bottom margin (HWP-style) and those blocks carried no top one. Sized from the
    // DOCUMENT's default typography rather than from the block above (upstream PR #52, ported 2026-09-24).
    internal double TopGapOf(Block block)
        => double.IsNaN(block.MarginTop) ? AutoBlockTopGap : block.MarginTop;

    // One line gap of body text: the line box (font size x spacing) minus the text it holds.
    internal double AutoBlockTopGap => Math.Max(0, PtToPx(DefaultFontSize) * (DefaultLineSpacing - 1));

    internal static double HeadingFontSize(int level) => HeadingStyle.Size(level);

    // The size (pt) a run is DRAWN at — the one rule, used by CreateLayout to draw it and by GetCaretFormat
    // to report it, so the toolbar cannot show a size the text is not shown at (and IncreaseFontSize, which
    // steps from the reported size, cannot shrink text it means to grow). An unset size (≤ 0) falls back to
    // DefaultFontSize. A heading no longer overrides it: its size is on its runs (HeadingStyle).
    private static double DrawnRunSize(Run r, double defaultSize) => r.FontSize <= 0 ? defaultSize : r.FontSize;

    // The attributes the renderer FORCES, whatever the run says — same pairing as DrawnRunSize: CreateLayout
    // draws with them and GetCaretFormat reports them, so the toolbar shows what is on screen. A hyperlink is
    // drawn underlined, and link-blue unless it has its own colour (so in-app SetHyperlink and pasted links
    // look identical). A heading's bold used to be forced too; it is a run attribute now (HeadingStyle).
    internal static readonly Windows.UI.Color LinkColor = Windows.UI.Color.FromArgb(255, 0, 0, 255);
    private static bool DrawnBold(Run r) => r.FontWeight.IsBold();
    private static bool DrawnUnderline(Run r)
        => r.TextDecorations.HasFlag(TextDecorationFlags.Underline) || !string.IsNullOrEmpty(r.NavigateUri);
    private static Windows.UI.Color? DrawnForeground(Run r)
        => r.Foreground ?? (string.IsNullOrEmpty(r.NavigateUri) ? null : LinkColor);

    // Left x where a paragraph's text starts: base indent + manual indent + nesting + (list marker gap).
    private static double ParaLeft(Paragraph p)
        => 10 + p.Indent + p.ListLevel * 20 + (p.ListType != ListKind.None ? ListMarkerWidth : 0);

    // The same per-paragraph left inset INSIDE a table cell: indent + list nesting + the marker gutter,
    // but without the document content-left origin (a cell's origin is its own content box). Every cell
    // walk — draw, hit-test, caret geometry, height measurement — must apply this identically, or the
    // rendered text and the caret/hit-test geometry drift apart (rule #1: one layout, one source).
    private static double CellParaLeft(Paragraph p) => ParaLeft(p) - DocContentLeft;

    /// <summary>The list-item marker text (forwarded to the shared helper, kept for API parity).</summary>
    internal static string ListMarkerText(ListKind kind, ListMarkerStyle style, int num)
        => ListMarkers.Text(kind, style, num);

    // ---- visual tree -------------------------------------------------------
    private readonly CanvasVirtualControl _canvas;
    private readonly ScrollViewer _scroll;
    private readonly ImageCache _images = new();

    // Per-paragraph CanvasTextLayout cache, keyed by paragraph identity. Built with the shared device
    // (CanvasTextLayout is device-independent for layout, and drawing it on any session is valid).
    // CanvasTextLayout is a heavy native DirectWrite object. Only the render/hit-test paths cache here,
    // so in continuous mode the cache naturally stays viewport-sized; measurement uses transient layouts
    // (see CreateLayout / ParagraphHeight) and the cheap per-paragraph height cache below, so total
    // memory no longer scales with document length. LayoutCacheCap is a safety net (clear-all when
    // exceeded) far larger than any single draw's working set, so it never disposes an in-use layout.
    private const int LayoutCacheCap = 2048;
    private sealed class LayoutEntry(long sig, double width, CanvasTextLayout layout)
    {
        public readonly long Sig = sig; public readonly double Width = width; public readonly CanvasTextLayout Layout = layout;
        public long Stamp; // last use, for TrimLayoutCache
    }
    private readonly Dictionary<Paragraph, LayoutEntry> _layoutCache = new();
    private long _layoutStamp;

    // "Viewport-sized" held only while nothing scrolled: the render path caches every paragraph it DRAWS, and
    // paging once to the end of a document draws all of them — measured 2026-09-27 (MemBaseline scroll mode),
    // 2,000 paragraphs left 2,000 native layouts resident until the 2048 clear-all. After each screen draw pass
    // the cache is trimmed back to the LayoutKeep most recently used once it passes LayoutKeep + LayoutTrimSlack
    // (the slack keeps it from sorting on every frame). A trimmed paragraph that comes back into view rebuilds
    // its layout — CPU only, nothing visible. Safe where the cap is not: at the end of a draw pass no walk is
    // holding a layout, so no in-use layout can be disposed, whatever the number.
    private const int LayoutKeep = 256, LayoutTrimSlack = 128;

    private void TrimLayoutCache()
    {
        if (_layoutPinDepth != 0 || _layoutCache.Count <= LayoutKeep + LayoutTrimSlack) return;
        var byAge = new List<KeyValuePair<Paragraph, LayoutEntry>>(_layoutCache);
        byAge.Sort((a, b) => a.Value.Stamp.CompareTo(b.Value.Stamp));
        for (int i = 0; i < byAge.Count - LayoutKeep; i++)
        {
            byAge[i].Value.Layout.Dispose();
            _layoutCache.Remove(byAge[i].Key);
        }
    }

    // ---- the per-paragraph side caches ------------------------------------
    // Height, pagination lines and text statistics are cached per paragraph, keyed by identity and
    // validated by ParagraphSig — so a stale entry is impossible. What a Dictionary<Paragraph, …> could
    // not do is FORGET: it is a strong reference, and paragraphs leave the document constantly
    // (Backspace merging two, a paste replacing a selection, a list toggle rebuilding them). Nothing
    // removed them from these caches, so a long editing session retained the whole graveyard — measured:
    // a paragraph dropped from Document.Blocks was still reachable after a full GC. Only the arbitrary
    // 100,000-entry "guard against pathological growth" bounded it, and 100,000 dead paragraphs with
    // their runs and text is not a bound worth having.
    //
    // ConditionalWeakTable ties each entry's lifetime to its key's, so a paragraph the document drops is
    // collected with its cache entries and the caps are no longer needed. (The HEAVY layout cache stays a
    // Dictionary on purpose: CanvasTextLayout is a native DirectWrite object that must be Disposed, and a
    // weak table would leave that to the finalizer. It is bounded by LayoutCacheCap + EvictLayouts, which
    // dispose properly, and it only ever holds a viewport's worth — measured at 22 entries for a
    // 300-paragraph document.)
    private sealed class HeightEntry(long sig, double width, double height)
    {
        public readonly long Sig = sig; public readonly double Width = width; public readonly double Height = height;
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Paragraph, HeightEntry> _heightCache = new();

    private double _measuredHeight;

    public RichEditor()
    {
        _canvas = new CanvasVirtualControl
        {
            // 캔버스 Height/Width는 문서 크기에 맞춰 명시적으로 설정된다(RelayoutToViewport).
            // 기본 정렬(Stretch)인 채로 두면 뷰포트보다 작을 때 Stretch+고정크기 조합 때문에
            // 캔버스가 스크롤뷰어 안에서 세로로 가운데 배치되어 캐럿이 문서 상단이 아닌
            // 뷰포트 중앙에 나타난다. 항상 좌상단에 고정해 문서가 짧아도 위에서부터 시작하게 한다.
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            // 상단 여백. 문서 좌표계(y=0부터 렌더링)를 건드리면 측정·히트테스트·페이지분할 등
            // 여러 곳을 함께 고쳐야 하므로, 호스트 레벨(캔버스 위치)에서만 밀어내 시각적 여백만 준다.
            // 좌측 여백(ParaLeft의 DocContentLeft=10)과 동일한 값으로 맞춤 — 포인터 이벤트는
            // 캔버스 자신에 붙어 캔버스 로컬 좌표를 쓰므로 Margin이 캐럿/클릭 좌표에 영향 없음.
            Margin = new Thickness(0, DocContentLeft, 0, 0),
        };
        _canvas.RegionsInvalidated += OnRegionsInvalidated;

        _scroll = new ScrollViewer
        {
            Content = _canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            ZoomMode = ZoomMode.Disabled,
        };
        Content = _scroll;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        _images.OnReady += () => _canvas.Invalidate();
        _scroll.SizeChanged += (_, _) => OnViewportResized();
        _canvas.CreateResources += (_, args) =>
        {
            // A NEW device (lost device after a sleep/resume, a driver reset, a display change)
            // invalidates every device-bound resource we hold. ClearLayoutCache drops the layouts and
            // marker layouts for exactly that reason — and the decoded CanvasBitmaps in _images are
            // just as device-bound: drawing one created on the lost device into the new device's
            // session raises E_INVALIDARG ("objects used together must be created by the same
            // CanvasDevice"). Two halves of one invariant; only one of them was being honoured.
            // Prune with an empty live set is ImageCache's documented "clear" (the in-flight decodes,
            // which were also issued against the old device, self-dispose on completion).
            // FirstTime has nothing cached yet and DpiChanged keeps the device, so neither needs it.
            if (args.Reason == Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesReason.NewDevice)
                _images.Prune(new HashSet<object>());
            ClearLayoutCache();
            RelayoutToViewport();
        };

        Unloaded += (_, _) => ReleaseWhileUnloaded();
        _images.OnReleased += ScheduleDeviceTrim;

        SetupInput();
    }

    // Disposing a CanvasBitmap releases the texture, but the driver keeps the memory it staged the upload
    // through (and its pools) until the device is trimmed. Measured 2026-09-27 (MemBaseline scroll mode,
    // Intel UHD): after scrolling back from the end of a 100-photo document, CanvasDevice.Trim() released a
    // further 38 MB of Private bytes and 70 MB of GPU memory. It is a hint that costs a stall, so it runs once,
    // two seconds after the last release (a scroll through photos releases every few frames), never per frame.
    // The device is shared by every Win2D control in the process; trimming only drops what nothing is using.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _deviceTrimTimer;

    private void ScheduleDeviceTrim()
    {
        if (_deviceTrimTimer == null)
        {
            if (DispatcherQueue is not { } queue) return;
            _deviceTrimTimer = queue.CreateTimer();
            _deviceTrimTimer.Interval = TimeSpan.FromSeconds(2);
            _deviceTrimTimer.IsRepeating = false;
            _deviceTrimTimer.Tick += (_, _) => TrimDevice();
        }
        _deviceTrimTimer.Stop();
        _deviceTrimTimer.Start();
    }

    internal int DeviceTrims; // test hook

    private void TrimDevice()
    {
        try
        {
            if (_canvas.Device is { } device) { device.Trim(); DeviceTrims++; }
        }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); } // a lost device: nothing to trim
    }

    // Out of the visual tree (a hidden tab, a page navigated away from) nothing is drawn, yet the editor kept
    // every native layout and decoded picture it had — measured 2026-09-27 (MemBaseline scroll mode), nothing
    // came back on unload. The layouts rebuild on the next draw (CPU only); the pictures near the viewport are
    // kept so coming back shows them at once, and everything else re-decodes if it is scrolled to again.
    // IsLoaded: a move within the tree can raise the old Unloaded after the new Loaded.
    private void ReleaseWhileUnloaded()
    {
        if (IsLoaded) return;
        EvictLayouts();
        ClearMarkerLayouts();
        TrimOffscreenImages(0);
        ScheduleDeviceTrim(); // even with no picture released: the text and surfaces it drew staged through it too
    }

    // ---- accessibility ----------------------------------------------------
    private RichEditorAutomationPeer? _automationPeer;

    /// <inheritdoc/>
    protected override Microsoft.UI.Xaml.Automation.Peers.AutomationPeer OnCreateAutomationPeer()
        => _automationPeer ??= new RichEditorAutomationPeer(this);

    // ---- dependency properties --------------------------------------------
    /// <summary>The document model being rendered.</summary>
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(FlowDocument), typeof(RichEditor), new PropertyMetadata(null, OnDocumentChanged));

    /// <summary>The document model being rendered.
    /// <para>Assigning it (in code or through a binding) counts as an edit: <see cref="IsModified"/> becomes true,
    /// since the editor cannot tell a document read from a file from one built in code. To open a file as
    /// unmodified, use <see cref="LoadJson"/>, <see cref="LoadJsonAsync"/>, <see cref="LoadPackageAsync"/>,
    /// <see cref="LoadHtml"/> or <see cref="LoadRtf"/> (which also clear the undo history), or call
    /// <see cref="MarkSaved"/> after assigning.</para></summary>
    public FlowDocument? Document
    {
        get => (FlowDocument?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    private static void OnDocumentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ed = (RichEditor)d;
        ed.ClearLayoutCache();
        // Prune (not Clear): dispose bitmaps the new document doesn't reference. Undo/redo swaps in a
        // snapshot whose images share RawBytes with the cached entries, so those stay warm — no
        // placeholder flash / full re-decode on every Ctrl+Z — and the pictures the step took out are
        // kept within the edit budget, so the matching redo is warm too. A genuine swap frees everything.
        ed._images.Prune(ed.CollectLiveImageKeys(), ed._applyingHistory ? ed.ImageRetainBytes : 0);
        ed.OnDocumentAssigned();
        ed.SyncPageSetupOnDocumentChanged(); // apply the loaded doc's page setup (or adopt current into it)
        ed.RelayoutToViewport();
        ed.MarkTextChanged();      // wholesale content swap flushes as TextChanged
        ed.RaiseDocumentChanged();
        ed.RaiseStatusChanged();
    }

    /// <summary>When true, edits/text input are blocked; selection and caret navigation still work,
    /// and the caret is hidden.</summary>
    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
        nameof(IsReadOnly), typeof(bool), typeof(RichEditor), new PropertyMetadata(false, OnIsReadOnlyChanged));

    private static void OnIsReadOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RichEditor)d).OnReadOnlyChanged((bool)e.NewValue);

    /// <summary>When true, edits/text input are blocked.</summary>
    public bool IsReadOnly
    {
        get => (bool)GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    /// <summary>Font family for runs that don't specify one. Defaults to the OS UI font
    /// (e.g. "맑은 고딕" on Korean Windows); assign to override.</summary>
    public static readonly DependencyProperty DefaultFontFamilyProperty = DependencyProperty.Register(
        nameof(DefaultFontFamily), typeof(string), typeof(RichEditor),
        new PropertyMetadata(SystemDefaultFontFamily(), OnLayoutAffectingChanged));

    /// <summary>Font family for runs that don't specify one.</summary>
    public string DefaultFontFamily
    {
        get => (string)GetValue(DefaultFontFamilyProperty);
        set => SetValue(DefaultFontFamilyProperty, value);
    }

    /// <summary>Font size in points for runs WITHOUT an explicit size (<c>Run.FontSize</c> ≤ 0).
    /// Default: 10.
    /// <para>Scope note: runs the editor itself creates (typing, paste, load) carry an explicit size
    /// (the model default 10pt — see <c>Run.FontSize</c>; the .flow format stores it, matching the
    /// original AvaloniaRichEditor wire format), so changing this property does NOT restyle typed
    /// text. It applies to host-constructed documents that deliberately leave <c>FontSize</c> unset
    /// (≤ 0), to empty-paragraph line heights, and to the toolbar's displayed fallback size. To change
    /// the size of actual content, use <see cref="SetFontSize"/> on a selection instead.</para></summary>
    public static readonly DependencyProperty DefaultFontSizeProperty = DependencyProperty.Register(
        nameof(DefaultFontSize), typeof(double), typeof(RichEditor),
        new PropertyMetadata(BodyFontSizePt, OnLayoutAffectingChanged));

    /// <summary>Font size in points for runs that don't specify one (see the scope note on
    /// <see cref="DefaultFontSizeProperty"/> — this does not restyle editor-created runs).</summary>
    public double DefaultFontSize
    {
        get => (double)GetValue(DefaultFontSizeProperty);
        set => SetValue(DefaultFontSizeProperty, value);
    }

    private static void OnLayoutAffectingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ed = (RichEditor)d;
        ed.ClearLayoutCache();
        ed.RelayoutToViewport();
        ed.RecordHostPageSetup(e.Property); // a page DP set by code is the host's default (see there)
        ed.CapturePageSetupToDocument(); // keep the doc's page setup in sync when a page DP changes
    }

    // ---- sizing / invalidation --------------------------------------------
    private double _layoutWidth = A4ContentWidth;

    // The content width = the ScrollViewer viewport (so text wraps to the visible area, no horizontal
    // scroll), floored so a tiny window still reads. Recomputes content height and resizes the canvas.
    private void RelayoutToViewport()
    {
        // Unconditional: drag-resize paths (image/table column/row) mutate the model per pointer move
        // with only ONE PushUndo at drag start, so MarkTextChanged alone would leave the map stale.
        InvalidateBlockLayout();
        double z = EffectiveZoom;
        // Paged: text wraps to the paper's content width (fixed; zoom magnifies it). Continuous: reflow to
        // the LOGICAL viewport width (viewport / zoom), so the zoom-scaled content fills the viewport.
        double width = IsPaged
            ? PaperContentWidth
            : (_scroll.ViewportWidth is var vw && vw > 1 ? vw / z : A4ContentWidth);
        _layoutWidth = Math.Max(50, width);

        _measuredHeight = MeasureContentHeight(_layoutWidth);
        _pageBreaks = null; // recompute lazily at the new width
        ClearRecordedGeometry(); // positions may have moved; the next draw re-records what's visible
        _paraIndexMap = null;    // belt-and-braces alongside MarkTextChanged (see ComparePositions)
        // The physical canvas is the logical content size × zoom (Win2D re-rasterizes under the scale, so
        // text stays crisp). Geometry/draw code works in logical units under the zoom transform.
        if (PagedChrome)
        {
            int pages = EnsurePageBreaks().Count;
            // Canvas at least as wide as the viewport so the page centers on a grey desk (with side margins).
            _canvas.Width = Math.Max(PaperWidth * z, _scroll.ViewportWidth);
            _canvas.Height = (pages * (PaperHeight + PageGap) + PageGap) * z;
        }
        else
        {
            _canvas.Width = _layoutWidth * z;
            _canvas.Height = _measuredHeight * z;
        }
        _canvas.Invalidate();
    }

    private void ClearLayoutCache()
    {
        foreach (var entry in _layoutCache.Values) entry.Layout.Dispose();
        _layoutCache.Clear();
        _heightCache.Clear();
        _lineCache.Clear();
        _statsCache.Clear();
        _tableRowHeights.Clear();
        InvalidateBlockLayout();
        ClearMarkerLayouts(); // device-bound like the layouts above (device recreate path)
    }

    // The heavy layout cache's safety net: dispose + drop EVERY entry (the per-draw-pass bound is
    // TrimLayoutCache). Safe because no caller holds a returned layout across building LayoutCacheCap other
    // layouts (each use is synchronous within one measure/draw step), and pinned walks defer it.
    private void EvictLayouts()
    {
        foreach (var entry in _layoutCache.Values) entry.Layout.Dispose();
        _layoutCache.Clear();
    }

    // ---- block layout map ---------------------------------------------------
    // Per-top-level-block layout: content top (after MarginTop), height, and the ordered-list index
    // consumed BEFORE the block. Built ONCE per (content change × width) and shared by every doc-space
    // walk — draw, hit-test, caret geometry, block-image rects, total height. Without it each of those
    // walked the whole document accumulating y (a paragraph-sig + cache lookup per block), so scrolling
    // and every caret move cost O(document) even after the caches. Invalidated by MarkTextChanged (all
    // edits funnel through PushUndo) and rebuilt lazily at whatever width is current (print's temporary
    // width swap just triggers a rebuild via the width key).
    private List<(Block block, double top, double height, int orderedStart)>? _blockLayout;
    private Dictionary<Block, int>? _blockLayoutIndex;
    private double _blockLayoutWidth = -1;

    internal void InvalidateBlockLayout() { _blockLayout = null; _blockLayoutIndex = null; }

    private List<(Block block, double top, double height, int orderedStart)> EnsureBlockLayout(double width)
    {
        if (_blockLayout != null && _blockLayoutWidth == width) return _blockLayout;
        var map = new List<(Block, double, double, int)>(Document?.Blocks.Count ?? 0);
        var index = new Dictionary<Block, int>();
        double y = 0;
        // Ordered-list numbering: see OrderedStartFor, which the cell walk shares. (The old single
        // counter made a nested ordered list continue its PARENT's numbers — 1, 2, then the sublist
        // showing 3 — which is why the level semantics exist at all.)
        List<int>? ordCounters = null; // ordCounters[level] = items consumed at that level
        if (Document != null)
        {
            foreach (var block in Document.Blocks)
            {
                y += TopGapOf(block);
                double h = BlockHeight(block, width);
                int orderedStart = OrderedStartFor(block, ref ordCounters);
                index[block] = map.Count;
                map.Add((block, y, h, orderedStart));
                y += h + block.MarginBottom;
            }
        }
        _blockLayout = map;
        _blockLayoutIndex = index;
        _blockLayoutWidth = width;
        return map;
    }

    // First map index whose block bottom reaches docY (map tops are ascending). map.Count when past the end.
    private static int BlockIndexAtY(List<(Block block, double top, double height, int orderedStart)> map, double docY)
    {
        int lo = 0, hi = map.Count - 1, ans = map.Count;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (map[mid].top + map[mid].height < docY) lo = mid + 1;
            else { ans = mid; hi = mid - 1; }
        }
        return ans;
    }

    // ---- content height ----------------------------------------------------
    // Total rendered height at the given content width (the block map's end plus breathing room).
    private double MeasureContentHeight(double width)
    {
        var map = EnsureBlockLayout(width);
        if (map.Count == 0) return 40;
        var last = map[^1];
        return last.top + last.height + last.block.MarginBottom + 40;
    }

    private double BlockHeight(Block block, double width)
    {
        switch (block)
        {
            case Paragraph p:
            {
                double px = ParaLeft(p);
                double pWidth = Math.Max(10, width - 20 - px - p.MarginRight);
                return ParagraphHeight(p, pWidth);
            }
            case ImageBlock img:
                return BlockImageDims(img).h;
            case DividerBlock:
                return DividerHeight;
            case TableBlock tb:
                return LayoutTable(tb, 10 + tb.Indent, 0).TotalHeight;
            default:
                return 0;
        }
    }

    // A natural single-line height for an empty paragraph (no glyphs to measure). Honors a custom
    // LineSpacing/LineHeight so an empty paragraph keeps the same height as its surrounding lines.
    private double EmptyLineHeight(Paragraph p)
    {
        double pt = p.HeadingLevel is >= 1 and <= 6 ? HeadingFontSize(p.HeadingLevel) : DefaultFontSize;
        double lh = ResolveLineHeight(p, pt, pt);
        return !double.IsNaN(lh) && lh > 0 ? lh : PtToPx(pt) * NaturalLineFactor;
    }

    // The glyph-sized (natural) line height for an empty paragraph, ignoring custom line spacing. The
    // caret on a blank line must be as tall as a character would be, sitting on the line's baseline —
    // not the full spaced line box, which at 200% draws a caret twice the height of the text.
    private double EmptyTextHeight(Paragraph p)
        => PtToPx(p.HeadingLevel is >= 1 and <= 6 ? HeadingFontSize(p.HeadingLevel) : DefaultFontSize) * NaturalLineFactor;

    // ---- text layout (the single source of truth) -------------------------
    // The paragraph's logical text, with each atomic object inline (image, table) collapsed to one
    // U+FFFC so character offsets line up with the CanvasTextLayout (rule #2).
    private const char ObjChar = '￼';

    private static int InlineLen(Inline inline) => inline is Run r ? (r.Text?.Length ?? 0) : 1;

    private static string BuildPlain(Paragraph p)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var inline in p.Inlines)
        {
            if (inline is Run r && r.Text != null) sb.Append(r.Text);
            else if (inline is not Run) sb.Append(ObjChar);
        }
        return sb.ToString();
    }

    private static CanvasHorizontalAlignment MapAlign(TextAlignment a) => a switch
    {
        TextAlignment.Center => CanvasHorizontalAlignment.Center,
        TextAlignment.Right => CanvasHorizontalAlignment.Right,
        TextAlignment.Justify => CanvasHorizontalAlignment.Justified,
        _ => CanvasHorizontalAlignment.Left,
    };

    // Builds (or returns a cached) CanvasTextLayout for a paragraph at the given wrap width. Per-run
    // formatting is applied over character ranges; inline images/tables reserve one U+FFFC each via a
    // SpacerInlineObject (the bitmap/grid is painted afterward by the render walk).
    internal CanvasTextLayout BuildTextLayout(Paragraph p, double maxWidth)
    {
        long sig = ParagraphSig(p);
        if (_layoutCache.TryGetValue(p, out var cached) && cached.Width == maxWidth && cached.Sig == sig)
        {
            cached.Stamp = ++_layoutStamp;
            return cached.Layout;
        }

        var layout = CreateLayout(p, maxWidth);
        if (_layoutCache.TryGetValue(p, out var prev)) { prev.Layout.Dispose(); _layoutCache.Remove(p); }
        // Don't evict while a walk holds a cached layout (see _layoutPinDepth): an inline-table walk keeps
        // the HOST paragraph's layout live across building every cell layout, so a clear-all here would
        // dispose the layout still in use and the next GetCharacterRegions on it would throw. Deferring
        // lets the cache overshoot the cap briefly; the next unpinned build evicts it.
        if (_layoutPinDepth == 0 && _layoutCache.Count >= LayoutCacheCap) EvictLayouts();
        _layoutCache[p] = new LayoutEntry(sig, maxWidth, layout) { Stamp = ++_layoutStamp };
        return layout;
    }

    // >0 while a layout walk holds a cached layout across further BuildTextLayout calls (inline-table
    // descent, DrawInlineObjects). EvictLayouts is suppressed for that span so the held layout survives.
    private int _layoutPinDepth;
    private readonly struct LayoutPin : IDisposable
    {
        private readonly RichEditor _r;
        public LayoutPin(RichEditor r) { _r = r; r._layoutPinDepth++; }
        public void Dispose() => _r._layoutPinDepth--;
    }

    // Constructs a fresh CanvasTextLayout (NOT cached). Measurement callers wrap it in `using` so the
    // heavy native object is released immediately instead of polluting the bounded layout cache, which
    // is what keeps total memory from scaling with document length.
    //
    // forMeasure skips the three range calls that only affect DRAWING — SetColor, SetUnderline,
    // SetStrikethrough. DirectWrite treats colour as a drawing effect and underline/strikethrough as
    // decorations painted from the line's own metrics; none of them moves a glyph or changes a line box,
    // so LayoutBounds is identical either way (pinned by MeasureLayoutMatchesDrawLayout). Every caller
    // that measures and throws the layout away — ParagraphHeight and ParagraphLines, i.e. the whole
    // document on load and on every relayout — was paying for a per-run colour object it never drew.
    private CanvasTextLayout CreateLayout(Paragraph p, double maxWidth, bool forMeasure = false)
    {
        var device = CanvasDevice.GetSharedDevice();
        string defaultFamily = DefaultFontFamily;
        double defaultSize = DefaultFontSize;

        string plain = BuildPlain(p);
        // `using`: CanvasTextLayout copies the format's state at construction, so the format is dead
        // weight afterwards. Without this the hottest path in the control (every measurement — see
        // ParagraphHeight / ParagraphLines, which deliberately dispose their transient LAYOUT) leaked a
        // native DirectWrite text format per call, defeating that bounded-memory design.
        using var fmt = new CanvasTextFormat
        {
            FontFamily = defaultFamily,
            FontSize = (float)PtToPx(defaultSize),
            HorizontalAlignment = MapAlign(p.TextAlignment),
            WordWrapping = CanvasWordWrapping.Wrap,
        };
        var layout = new CanvasTextLayout(device, plain, fmt, (float)Math.Max(1, maxWidth), 0f);

        int pos = 0;
        double maxRunPt = 0;
        double maxObjectH = 0; // tallest inline image/table — decides whether uniform spacing can apply
        foreach (var inline in p.Inlines)
        {
            int len = InlineLen(inline);
            if (len == 0) continue;
            if (inline is Run r && r.Text != null)
            {
                var family = string.IsNullOrEmpty(r.FontFamily) ? defaultFamily : r.FontFamily!;
                var weight = r.FontWeight;
                double size = DrawnRunSize(r, defaultSize);
                if (size > maxRunPt) maxRunPt = size;

                layout.SetFontFamily(pos, len, family);
                layout.SetFontSize(pos, len, (float)PtToPx(size));
                layout.SetFontWeight(pos, len, weight);
                layout.SetFontStyle(pos, len, r.FontStyle);
                if (!forMeasure)
                {
                    if (DrawnForeground(r) is { } fg) layout.SetColor(pos, len, fg);
                    if (DrawnUnderline(r)) layout.SetUnderline(pos, len, true);
                    if (r.TextDecorations.HasFlag(TextDecorationFlags.Strikethrough)) layout.SetStrikethrough(pos, len, true);
                }
            }
            else if (inline is InlineImage img)
            {
                double w = img.Width > 0 ? img.Width : 16;
                double h = img.Height > 0 ? img.Height : 16;
                maxObjectH = Math.Max(maxObjectH, h);
                layout.SetInlineObject(pos, 1, new SpacerInlineObject(new Windows.Foundation.Size(w, h)));
            }
            else if (inline is InlineTable itbl)
            {
                // Reserve a box exactly the size of the wrapped table so the grid, hit-test, and selection
                // highlight all share one rect (no padding gap that would make the highlight sag below).
                var box = LayoutTable(itbl.Table, 0, 0);
                maxObjectH = Math.Max(maxObjectH, box.TotalHeight);
                layout.SetInlineObject(pos, 1, new SpacerInlineObject(new Windows.Foundation.Size(box.TableWidth, box.TotalHeight)));
            }
            pos += len;
        }

        // Line spacing (HWP "글자에 따라"): line box = largest font size × ratio, else an absolute
        // LineHeight, else the HWP default 160%. With no text to measure, the size the first typed
        // character will get — a heading's preset (as EmptyLineHeight), else the default.
        // Uniform spacing ignores inline object sizes (DirectWrite), so a proportional paragraph holding
        // an image/table taller than the box keeps content-driven spacing — the line grows to the object.
        double lh = ResolveLineHeight(p, maxRunPt, p.HeadingLevel is >= 1 and <= 6 ? HeadingFontSize(p.HeadingLevel) : defaultSize);
        bool fixedHeight = double.IsNaN(p.LineSpacing) && !double.IsNaN(p.LineHeight);
        if (!double.IsNaN(lh) && lh > 0 && (fixedHeight || maxObjectH <= lh))
        {
            layout.LineSpacingMode = CanvasLineSpacingMode.Uniform;
            layout.LineSpacing = (float)lh;
            layout.LineSpacingBaseline = (float)(lh * BaselineFraction);
        }

        return layout;
    }

    // The resolved line height in DIPs. LineSpacing (HWP ratio of the largest font size) takes priority
    // over LineHeight (absolute DIPs); with neither set, the HWP default 160%.
    private double ResolveLineHeight(Paragraph p, double maxRunPt, double basePtFallback)
    {
        if (double.IsNaN(p.LineSpacing) && !double.IsNaN(p.LineHeight)) return p.LineHeight;
        double ratio = double.IsNaN(p.LineSpacing) ? DefaultLineSpacing : p.LineSpacing;
        double basePt = maxRunPt > 0 ? maxRunPt : basePtFallback;
        return ratio * PtToPx(basePt);
    }

    // A cheap content+formatting fingerprint of a paragraph; when it (and the wrap width) are unchanged
    // the cached layout is reused. Brushes/colors fold in via value (Color) hashing. Run TEXT folds in
    // via the per-run cached hash (Run.TextHash) — this sig is recomputed for EVERY paragraph on each
    // relayout (i.e. per keystroke), and hashing the characters here made typing O(document chars).
    // With the cache the sig is O(runs); formatting fields are still read live, so nothing can go stale.
    /// <summary>Test hook: how many native <c>CanvasTextLayout</c>s the heavy cache is holding. The whole
    /// bounded-memory design rests on this staying viewport-sized whatever the document's length.</summary>
    internal int LayoutCacheCount => _layoutCache.Count;

    /// <summary>Test hook for the one assumption <c>forMeasure</c> rests on: a layout built WITHOUT the
    /// colour/underline/strikethrough range calls must lay out exactly like the one built with them.
    /// Returns (measure-only bounds, drawing bounds) so a test can compare them directly — if DirectWrite
    /// ever let a decoration move a glyph or grow a line box, every height in the document would drift
    /// away from what is drawn, and nothing else in the suite would say so.</summary>
    internal (Windows.Foundation.Rect measure, Windows.Foundation.Rect draw) MeasureBothWays(Paragraph p, double width)
    {
        using var m = CreateLayout(p, width, forMeasure: true);
        using var d = CreateLayout(p, width, forMeasure: false);
        return (m.LayoutBounds, d.LayoutBounds);
    }

    private static long ParagraphSig(Paragraph p)
    {
        unchecked
        {
            long h = 1469598103934665603; // FNV-1a 64-bit offset basis
            void Mix(long v) { h = (h ^ v) * 1099511628211; }
            void MixStr(string? s) { if (s == null) { Mix(0); return; } foreach (char ch in s) Mix(ch); Mix(s.Length + 1); }
            // Full size-relevant fingerprint of a (possibly nested) table hosted by an inline table.
            // Runs only for paragraphs that actually host one, so the hot plain-paragraph path is untouched.
            void MixTable(TableBlock t)
            {
                Mix(t.Rows);
                Mix(t.Columns);
                foreach (var w in t.ColumnWidths) Mix(BitConverter.DoubleToInt64Bits(w));
                foreach (var rh in t.RowHeights) Mix(BitConverter.DoubleToInt64Bits(rh));
                foreach (var row in t.ColSpans) foreach (var v in row) Mix(v);
                foreach (var row in t.RowSpans) foreach (var v in row) Mix(v);
                foreach (var (_, _, cell) in t.LogicalCells())
                {
                    Mix((long)cell.VerticalAlignment);
                    foreach (var b in cell.Blocks)
                    {
                        switch (b)
                        {
                            case Paragraph cp: Mix(ParagraphSig(cp)); break;
                            case ImageBlock ib:
                                Mix(31);
                                Mix(BitConverter.DoubleToInt64Bits(ib.Width));
                                Mix(BitConverter.DoubleToInt64Bits(ib.Height));
                                Mix(ib.RawBytes?.GetHashCode() ?? ib.Image?.GetHashCode() ?? 0);
                                break;
                            case TableBlock nt: Mix(41); MixTable(nt); break;
                            case DividerBlock: Mix(37); break;
                        }
                    }
                }
            }

            Mix((long)p.TextAlignment);
            Mix(BitConverter.DoubleToInt64Bits(p.Indent));
            Mix((long)p.ListType);
            Mix((long)p.ListMarker);
            Mix(p.ListLevel);
            Mix(p.HeadingLevel);
            Mix(BitConverter.DoubleToInt64Bits(p.LineHeight));
            Mix(BitConverter.DoubleToInt64Bits(p.LineSpacing));
            foreach (var inl in p.Inlines)
            {
                if (inl is Run r)
                {
                    Mix(r.TextHash);
                    Mix(r.Text?.Length ?? -1);
                    MixStr(r.FontFamily);
                    MixStr(r.NavigateUri);
                    Mix(BitConverter.DoubleToInt64Bits(r.FontSize));
                    Mix(r.FontWeight.Weight);
                    Mix((long)r.FontStyle);
                    Mix(r.Foreground?.GetHashCode() ?? 0);
                    Mix(r.Background?.GetHashCode() ?? 0);
                    Mix((long)r.TextDecorations);
                }
                else if (inl is InlineImage img)
                {
                    Mix(7);
                    Mix(BitConverter.DoubleToInt64Bits(img.Width));
                    Mix(BitConverter.DoubleToInt64Bits(img.Height));
                    Mix(img.RawBytes?.GetHashCode() ?? img.Image?.GetHashCode() ?? 0);
                }
                else if (inl is InlineTable it)
                {
                    // The inline table reserves a run-sized box, so EVERYTHING that changes its rendered
                    // size must invalidate this paragraph's cached layout/height: row heights (row-boundary
                    // drags mutate only RowHeights, with one PushUndo at drag start), merge spans, cell
                    // vertical alignment, and non-paragraph cell blocks — not just column widths and cell
                    // text. Missing any of these left a stale spacer box (the table drew larger than the
                    // reserved slot and overlapped following lines until an unrelated edit).
                    Mix(13);
                    MixTable(it.Table);
                }
            }
            return h;
        }
    }
}
