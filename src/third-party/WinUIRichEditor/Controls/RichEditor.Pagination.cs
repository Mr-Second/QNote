using System;
using System.Collections.Generic;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.Graphics.Canvas.Text;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Pagination — increment 1: page-size/orientation properties, paper dimensions, and a paged wrap width
// (text reflows to the paper's content width). The visual page stack + line-aware breaks (the doc→view
// coordinate transform), print, and PDF build on this in later increments. Default PageSize is
// Continuous, so the existing continuous rendering is unchanged unless a host opts into a paper size.
public partial class RichEditor
{
    internal const double A4PageWidth = 794;
    internal const double A4PageHeight = 1123;
    // The page margins in DIPs, from PageMargin (millimetres). Every reader of the margins goes through these
    // four, so the conversion lives in one place. They were two constants (48 x 40 DIP) until the margins
    // became a document setting (upstream PR #50/#52, ported 2026-09-24).
    //
    // Rounded to whole DIPs (at most 0.13 mm off; the document keeps the exact millimetres): 15 mm is 56.69 DIP,
    // which puts the content box — and the page clip, which Win2D antialiases — on a fractional pixel. A table's
    // outer border there came out at 60% of an interior line even drawn inside its box (measured). Every walker,
    // the clip, the transforms and the hit-tests read these four, so the rounding cannot make them disagree.
    internal double PagePadLeft => Math.Round(PageMargin.LeftDips);
    internal double PagePadRight => Math.Round(PageMargin.RightDips);
    internal double PagePadTop => Math.Round(PageMargin.TopDips);
    internal double PagePadBottom => Math.Round(PageMargin.BottomDips);
    internal const double PageGap = 14;    // grey desk gap between stacked pages (page view)

    // ---- visual zoom -------------------------------------------------------
    // Engine-level zoom: the document lays out in LOGICAL coordinates exactly as at 1.0, then the render
    // session is scaled and the canvas sized by this factor, so glyphs re-rasterize crisply at any zoom
    // (no bitmap scaling). All view↔doc coordinate conversion folds the factor in at one place
    // (ViewToDoc / DocToView), so input, caret, and hit-testing stay correct under zoom.
    internal const double MinZoom = 0.25, MaxZoom = 5.0;

    /// <summary>Visual zoom for the document (1.0 = 100%). Text stays crisp at any factor; clamped to
    /// [0.25, 5.0]. The host chrome (toolbar/status bar) is unaffected.</summary>
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(RichEditor), new PropertyMetadata(1.0, OnZoomChanged));

    /// <summary>Visual zoom for the document (1.0 = 100%); clamped to [0.25, 5.0].</summary>
    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    private static void OnZoomChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ed = (RichEditor)d;
        ed.ClearLayoutCache();      // wrap width changes in continuous mode → rebuild layouts
        ed.RelayoutToViewport();
        ed.RaiseStatusChanged();
    }

    // The clamped, effective zoom used by all engine math.
    internal double EffectiveZoom => System.Math.Clamp(Zoom, MinZoom, MaxZoom);

    // The canvas size in LOGICAL (pre-zoom) units. The physical canvas is this × EffectiveZoom; draw and
    // desk-geometry code works in logical units under the zoom transform.
    private double CanvasLogicalWidth => _canvas.Width / EffectiveZoom;

    // ---- fit-to-width ------------------------------------------------------
    // When true, the zoom auto-recomputes on every viewport resize so the page/content width keeps filling
    // the view. Cleared when a host sets an explicit zoom via SetZoom.
    private bool _fitWidth;

    /// <summary>True while fit-to-width is active (zoom tracks the viewport width).</summary>
    public bool IsFitWidth => _fitWidth;

    /// <summary>Sets an explicit zoom factor and leaves fit-to-width mode. Clamped to [0.25, 5.0].</summary>
    public void SetZoom(double factor)
    {
        _fitWidth = false;
        Zoom = System.Math.Clamp(factor, MinZoom, MaxZoom);
    }

    /// <summary>Zooms so the page (paged) or content (continuous) width fills the viewport, and keeps it
    /// fitted as the view resizes. The chrome's "Fit width" item and Ctrl+0 call this.</summary>
    public void FitToWidth()
    {
        _fitWidth = true;
        double z = ComputeFitWidthZoom();
        if (z > 0)
        {
            double clamped = System.Math.Clamp(z, MinZoom, MaxZoom);
            if (System.Math.Abs(clamped - EffectiveZoom) > 0.001) { Zoom = clamped; return; } // relayouts via OnZoomChanged
        }
        RelayoutToViewport(); // viewport not laid out yet, or zoom unchanged — still refresh
        RaiseStatusChanged();
    }

    // The zoom that makes the page/content width fill the viewport (0 if the viewport isn't laid out yet).
    private double ComputeFitWidthZoom()
    {
        double vw = _scroll.ViewportWidth;
        if (vw <= 1) return 0;
        if (!IsPaged) return 1.0; // continuous already reflows to the viewport width
        const double inset = 24;   // small desk margin so the page isn't edge-to-edge
        double target = PagedChrome ? PaperWidth : PaperContentWidth;
        return (vw - inset) / target;
    }

    // Viewport resized: in fit mode, re-fit the zoom; otherwise just relayout to the new width.
    private void OnViewportResized()
    {
        if (_fitWidth)
        {
            double z = ComputeFitWidthZoom();
            if (z > 0)
            {
                double clamped = System.Math.Clamp(z, MinZoom, MaxZoom);
                if (System.Math.Abs(clamped - EffectiveZoom) > 0.001) { Zoom = clamped; return; } // relayouts via OnZoomChanged
            }
        }
        RelayoutToViewport();
    }

    // ---- page setup <-> document model ------------------------------------
    // Page setup (paper/orientation/header/footer/page numbers) is a DOCUMENT property persisted in
    // JSON/.flow, but the live source of truth is the control's DPs. These keep the two in sync, guarded
    // against the apply->DP-change->capture feedback loop.
    private bool _syncingPageSetup;

    // What the HOST asked for, as opposed to what the open document carries. A document with no page setup
    // of its own — and a new one (Clear) — starts from this. It used to adopt whatever the DPs held, which
    // after opening an A5-landscape file was THAT file's setup: the next plain document opened as A5
    // landscape with its header, and saving it wrote them in (decided 2026-09-12; upstream has the same
    // adopt-current design). A page DP set by code counts as the host's; values applied from a document do
    // not (_syncingPageSetup), and neither do the toolbar's paper/orientation pickers, which edit the open
    // document (EditDocumentPageSetup). Recorded PER PROPERTY: the DP callback is shared with unrelated DPs
    // (DefaultFontSize…), and snapshotting every page DP there would promote the open document's A5 to a
    // host default.
    private readonly Documents.PageSetup _hostPageSetup = new(); // starts equal to the DP defaults
    private int _documentPageSetupEdit;

    // Runs a page-setup change that edits the OPEN DOCUMENT (the toolbar's pickers), not the host defaults.
    internal void EditDocumentPageSetup(System.Action change)
    {
        _documentPageSetupEdit++;
        try { change(); }
        finally { _documentPageSetupEdit--; }
    }

    private void RecordHostPageSetup(DependencyProperty changed)
    {
        if (_syncingPageSetup || _documentPageSetupEdit > 0) return;
        if (changed == PageSizeProperty) _hostPageSetup.PageSize = PageSize;
        else if (changed == PageOrientationProperty) _hostPageSetup.Orientation = PageOrientation;
        else if (changed == ShowPageBoundariesProperty) _hostPageSetup.ShowPageBoundaries = ShowPageBoundaries;
        else if (changed == PageHeaderProperty) _hostPageSetup.Header = PageHeader;
        else if (changed == PageFooterProperty) _hostPageSetup.Footer = PageFooter;
        else if (changed == ShowPageNumbersProperty) _hostPageSetup.ShowPageNumbers = ShowPageNumbers;
        else if (changed == PageMarginProperty) _hostPageSetup.Margin = PageMargin;
    }

    // On Document change: a document that specifies a PageSetup drives the control's page DPs (model ->
    // control); a document that doesn't starts from the HOST's setup, which is then captured into it
    // (control -> model) so a later save persists what's shown. Called from OnDocumentChanged before the
    // status flush updates the chrome.
    private void SyncPageSetupOnDocumentChanged()
    {
        var doc = Document;
        if (doc == null) return;
        if (doc.PageSetup is { } ps) ApplyPageSetup(ps);
        else
        {
            ApplyPageSetup(_hostPageSetup);
            CapturePageSetupToDocument();
        }
    }

    private void ApplyPageSetup(Documents.PageSetup ps)
    {
        _syncingPageSetup = true;
        try
        {
            PageSize = ps.PageSize;
            PageOrientation = ps.Orientation;
            ShowPageBoundaries = ps.ShowPageBoundaries;
            PageHeader = ps.Header;
            PageFooter = ps.Footer;
            ShowPageNumbers = ps.ShowPageNumbers;
            PageMargin = ps.Margin;
        }
        finally { _syncingPageSetup = false; }
    }

    // Writes the control's current page DPs into Document.PageSetup (control -> model) so serialization
    // captures them. Stores null when everything is default, keeping plain documents' format unchanged.
    private void CapturePageSetupToDocument()
    {
        if (_syncingPageSetup) return;
        var doc = Document;
        if (doc == null) return;
        var ps = new Documents.PageSetup
        {
            PageSize = PageSize,
            Orientation = PageOrientation,
            ShowPageBoundaries = ShowPageBoundaries,
            Header = PageHeader,
            Footer = PageFooter,
            ShowPageNumbers = ShowPageNumbers,
            Margin = PageMargin,
        };
        // Null — "no setup", read back as the HOST's — only when the host's defaults are plain too. Under a host
        // that defaults to A4, a document switched to Continuous stored null, saved without a setup and reopened as
        // A4 (measured 2026-09-14). A default host keeps plain documents byte-identical, as before.
        doc.PageSetup = ps.IsDefault && _hostPageSetup.IsDefault ? null : ps;
    }

    /// <summary>Paper size for the document. <see cref="RichEditorPageSize.Continuous"/> (the default
    /// here) reflows to the control width; a concrete size wraps text to that paper's content width.</summary>
    public static readonly DependencyProperty PageSizeProperty = DependencyProperty.Register(
        nameof(PageSize), typeof(RichEditorPageSize), typeof(RichEditor),
        new PropertyMetadata(RichEditorPageSize.Continuous, OnLayoutAffectingChanged));

    /// <summary>Gets or sets the paper size. Default <see cref="RichEditorPageSize.Continuous"/>.</summary>
    public RichEditorPageSize PageSize
    {
        get => (RichEditorPageSize)GetValue(PageSizeProperty);
        set => SetValue(PageSizeProperty, value);
    }

    /// <summary>For a concrete <see cref="PageSize"/>, whether to draw page boundaries. Ignored for
    /// Continuous. Default true.</summary>
    public static readonly DependencyProperty ShowPageBoundariesProperty = DependencyProperty.Register(
        nameof(ShowPageBoundaries), typeof(bool), typeof(RichEditor),
        new PropertyMetadata(true, OnLayoutAffectingChanged));

    /// <summary>Gets or sets whether page boundaries are drawn for a concrete <see cref="PageSize"/>.</summary>
    public bool ShowPageBoundaries
    {
        get => (bool)GetValue(ShowPageBoundariesProperty);
        set => SetValue(ShowPageBoundariesProperty, value);
    }

    /// <summary>Page orientation. Landscape swaps the paper's width and height. No effect for Continuous.</summary>
    public static readonly DependencyProperty PageOrientationProperty = DependencyProperty.Register(
        nameof(PageOrientation), typeof(RichEditorPageOrientation), typeof(RichEditor),
        new PropertyMetadata(RichEditorPageOrientation.Portrait, OnLayoutAffectingChanged));

    /// <summary>Gets or sets the page orientation. Default Portrait.</summary>
    public RichEditorPageOrientation PageOrientation
    {
        get => (RichEditorPageOrientation)GetValue(PageOrientationProperty);
        set => SetValue(PageOrientationProperty, value);
    }

    /// <summary>Header text drawn in each page's top margin (page view and print). Null/empty = none.</summary>
    public static readonly DependencyProperty PageHeaderProperty = DependencyProperty.Register(
        nameof(PageHeader), typeof(string), typeof(RichEditor), new PropertyMetadata(null, OnLayoutAffectingChanged));

    /// <summary>Gets or sets the page header text (top margin).</summary>
    public string? PageHeader
    {
        get => (string?)GetValue(PageHeaderProperty);
        set => SetValue(PageHeaderProperty, value);
    }

    /// <summary>Footer text drawn in each page's bottom margin (page view and print). Null/empty = none.</summary>
    public static readonly DependencyProperty PageFooterProperty = DependencyProperty.Register(
        nameof(PageFooter), typeof(string), typeof(RichEditor), new PropertyMetadata(null, OnLayoutAffectingChanged));

    /// <summary>Gets or sets the page footer text (bottom margin).</summary>
    public string? PageFooter
    {
        get => (string?)GetValue(PageFooterProperty);
        set => SetValue(PageFooterProperty, value);
    }

    /// <summary>Draws "page / total" in each page's bottom-right margin. Default false.</summary>
    public static readonly DependencyProperty ShowPageNumbersProperty = DependencyProperty.Register(
        nameof(ShowPageNumbers), typeof(bool), typeof(RichEditor), new PropertyMetadata(false, OnLayoutAffectingChanged));

    /// <summary>Gets or sets whether page numbers are drawn (bottom margin).</summary>
    public bool ShowPageNumbers
    {
        get => (bool)GetValue(ShowPageNumbersProperty);
        set => SetValue(ShowPageNumbersProperty, value);
    }

    /// <summary>The page margins in MILLIMETRES — the band between the paper's edge and the text, where the
    /// header, footer and page number are drawn. Four sides, as Word, HWP and RTF have them; part of the
    /// document's <see cref="Documents.PageSetup"/>, so it is saved with the document and applied on load. Only
    /// meaningful for a concrete paper size. A margin that leaves no page to write on (negative, NaN, or two
    /// sides swallowing the paper) is refused: the editor keeps the last usable margins.</summary>
    public static readonly DependencyProperty PageMarginProperty = DependencyProperty.Register(
        nameof(PageMargin), typeof(Documents.PageMargins), typeof(RichEditor),
        new PropertyMetadata(Documents.PageSetup.DefaultMargin, OnPageMarginChanged));

    /// <summary>Gets or sets the page margins (millimetres, four sides). Defaults to
    /// <see cref="Documents.PageSetup.DefaultMargin"/>.</summary>
    public Documents.PageMargins PageMargin
    {
        get => (Documents.PageMargins)GetValue(PageMarginProperty);
        set => SetValue(PageMarginProperty, value);
    }

    // WinUI has no coerce callback, so an unusable value is put back here — the layout width would be zero or
    // negative, and every page-view measurement divides by it. A DP is reachable from XAML and bindings, so the
    // refusal lives here rather than at the dozen places that read it.
    private static void OnPageMarginChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ed = (RichEditor)d;
        var (w, h) = Documents.PageSetup.PaperMillimetres(ed.PageSize, ed.PageOrientation);
        if (!Documents.PageSetup.IsUsableMargin((Documents.PageMargins)e.NewValue, w, h))
        {
            ed.SetValue(PageMarginProperty, e.OldValue); // re-enters with the old (usable) value
            return;
        }
        OnLayoutAffectingChanged(d, e);
    }

    /// <summary>True when a concrete (non-Continuous) paper size is set.</summary>
    internal bool IsPaged => PageSize != RichEditorPageSize.Continuous;

    // The paper's pixel size at 96 DPI, accounting for orientation. Continuous reports its A4 fallback.
    // The table itself lives in Documents.PageSetup so the RTF writer uses the very same numbers.
    private (double w, double h) PaperDims => Documents.PageSetup.PaperDips(PageSize, PageOrientation);

    internal double PaperWidth => PaperDims.w;
    internal double PaperHeight => PaperDims.h;
    internal double PaperContentWidth => PaperWidth - PagePadLeft - PagePadRight;
    internal double PaperContentHeight => PaperHeight - PagePadTop - PagePadBottom;

    /// <summary>The current paper's pixel size at 96 DPI (accounts for <see cref="PageOrientation"/>).
    /// Continuous reports its A4 fallback. Useful for host fit-to-width and print math.</summary>
    public Size GetPaperPixelSize() => new(PaperWidth, PaperHeight);

    // ---- page-view geometry (increment 2) ---------------------------------
    // Paged AND boundaries enabled = stack white pages on a grey desk (the doc->view transform path).
    private bool PagedChrome => IsPaged && ShowPageBoundaries;

    internal const double DocContentLeft = 10; // listIndent: the doc-space left origin of content

    // Page-start positions in continuous document space, recomputed on relayout. Line-aware: a page
    // breaks between paragraph lines / table rows so glyphs and rows are never sliced.
    private List<double>? _pageBreaks;

    private List<double> EnsurePageBreaks()
        => _pageBreaks ??= ComputePageBreaks(PaperContentWidth, PaperContentHeight);

    private int PageOfDocY(double docY)
    {
        var br = EnsurePageBreaks();
        int i = br.Count - 1;
        while (i > 0 && docY < br[i]) i--;
        return i;
    }

    // Per-paragraph (height, visual-line bottoms) for pagination. Cached like the cheap height cache
    // (a double[] per paragraph, not a native layout) and measured with a TRANSIENT layout, so
    // recomputing page breaks — which happens on every relayout — neither inflates the heavy layout
    // cache with the whole document nor rebuilds DirectWrite layouts for unchanged paragraphs.
    // Weak-keyed for the reason spelled out on _heightCache: these entries must not outlive the
    // paragraph they describe.
    private sealed class LineEntry(long sig, double width, double height, double[] bottoms)
    {
        public readonly long Sig = sig; public readonly double Width = width;
        public readonly double Height = height; public readonly double[] Bottoms = bottoms;
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Paragraph, LineEntry> _lineCache = new();

    private (double height, double[] bottoms) ParagraphLines(Paragraph p, double width)
    {
        long sig = ParagraphSig(p);
        if (_lineCache.TryGetValue(p, out var hc) && hc.Sig == sig && hc.Width == width)
            return (hc.Height, hc.Bottoms);
        double h;
        double[] bottoms;
        // Measurement only: no colour or decoration is applied (see CreateLayout's forMeasure).
        using (var layout = CreateLayout(p, width, forMeasure: true))
        {
            h = System.Math.Max(EmptyLineHeight(p), layout.LayoutBounds.Height);
            bottoms = ParagraphLineBottoms(layout, h, GetParagraphLength(p)).ToArray();
        }
        _lineCache.AddOrUpdate(p, new LineEntry(sig, width, h, bottoms));
        // The height this just measured is exactly what ParagraphHeight would build a SECOND transient
        // layout for. In paged mode both run over the whole document on every relayout, so publish it.
        _heightCache.AddOrUpdate(p, new HeightEntry(sig, width, h));
        return (h, bottoms);
    }

    // Walks the document (mirroring the render advance) and returns the doc-space Y where each page
    // begins. Atoms are paragraph lines and table rows; an atom that would overflow the current page
    // starts the next one. Mirrors the Avalonia original's ComputePageBreaks.
    internal List<double> ComputePageBreaks(double contentWidth, double pageContentHeight)
    {
        var breaks = new List<double> { 0 };
        if (Document == null || pageContentHeight <= 0) return breaks;

        const double listIndent = 10;
        double y = 0, pageStart = 0;
        const double eps = 0.01;

        void PlaceAtom(double height)
        {
            if (y + height > pageStart + pageContentHeight + eps && y > pageStart)
            { breaks.Add(y); pageStart = y; }
            y += height;
        }

        foreach (var block in Document.Blocks)
        {
            y += TopGapOf(block);
            if (block is Paragraph p)
            {
                double px = ParaLeft(p);
                double pWidth = Math.Max(10, contentWidth - 20 - px - p.MarginRight);
                var (paraH, bottoms) = ParagraphLines(p, pWidth);
                double paraTop = y;
                double atomTop = 0;
                foreach (double atomBottom in bottoms)
                {
                    if (paraTop + atomBottom > pageStart + pageContentHeight + eps && paraTop + atomTop > pageStart)
                    { breaks.Add(paraTop + atomTop); pageStart = paraTop + atomTop; }
                    atomTop = atomBottom;
                }
                y = paraTop + paraH;
            }
            else if (block is TableBlock tb)
            {
                var tl = LayoutTable(tb, listIndent + tb.Indent, y);
                for (int r = 0; r < tb.Rows && r + 1 < tl.RowY.Length; r++)
                    PlaceAtom(tl.RowY[r + 1] - tl.RowY[r]);
            }
            else
            {
                PlaceAtom(BlockHeight(block, contentWidth));
            }
            y += block.MarginBottom;
        }
        return breaks;
    }

    // The doc-relative bottom Y of each visual line in a paragraph layout (the page-break atom edges).
    private List<double> ParagraphLineBottoms(CanvasTextLayout layout, double paraH, int textLength)
    {
        var result = new List<double>();
        var lm = LineMetricsOf(layout, textLength);
        if (lm.Length <= 1) return new List<double> { paraH };
        double acc = 0;
        for (int i = 0; i < lm.Length; i++)
        {
            acc += lm[i].Height;
            result.Add(i == lm.Length - 1 ? paraH : acc);
        }
        return result;
    }

    private double PageDeskX => System.Math.Max(0, (CanvasLogicalWidth - PaperWidth) / 2); // centered on the desk

    // Converts a canvas/view point (physical, zoom-scaled) to document space. Zoom is divided out first so
    // the rest works in logical units; the paged page-stack transform is then applied (identity if not paged).
    private Point ViewToDoc(Point v)
    {
        double z = EffectiveZoom;
        v = new Point(v.X / z, v.Y / z);
        if (!PagedChrome) return v;
        var br = EnsurePageBreaks();
        double stride = PaperHeight + PageGap;
        int i = System.Math.Clamp((int)((v.Y - PageGap) / System.Math.Max(1, stride)), 0, br.Count - 1);
        double viewContentLeft = PageDeskX + PagePadLeft;
        double viewContentTop = PageGap + i * stride + PagePadTop;
        return new Point(v.X - (viewContentLeft - DocContentLeft), br[i] + (v.Y - viewContentTop));
    }

    // Converts a document point to canvas/view space (physical, zoom-scaled). The paged transform maps to
    // logical view units; the zoom factor then scales to physical canvas coordinates.
    private Point DocToView(Point d)
    {
        double z = EffectiveZoom;
        Point lv;
        if (!PagedChrome) lv = d;
        else
        {
            var br = EnsurePageBreaks();
            double stride = PaperHeight + PageGap;
            int i = PageOfDocY(d.Y);
            double viewContentLeft = PageDeskX + PagePadLeft;
            double viewContentTop = PageGap + i * stride + PagePadTop;
            lv = new Point(d.X + (viewContentLeft - DocContentLeft), viewContentTop + (d.Y - br[i]));
        }
        return new Point(lv.X * z, lv.Y * z);
    }
}
