using System;
using System.IO;
using System.Numerics;
using Windows.Foundation;
using Microsoft.UI;
using Microsoft.Graphics.Canvas;
using WinUIRichEditor.Formatters;

namespace WinUIRichEditor.Controls;

// Print / PDF export. Pages are rendered off-screen to a Win2D CanvasRenderTarget at the requested DPI
// (content only — no caret/selection/handles, gated by _printMode), then handed to the ported PdfWriter
// as raw RGB. Mirrors the Avalonia original's RenderPrintPage / SavePdf.
public partial class RichEditor
{
    // True while rendering for print/PDF: suppresses caret, selection highlight, and resize handles.
    private bool _printMode;

    /// <summary>Number of pages the document occupies when paginated at the current <see cref="PageSize"/>
    /// (Continuous falls back to A4). Printing always paginates, even from the continuous layout.</summary>
    // Paged mode reuses the cached breaks (same width/height inputs) — the status bar calls this on every
    // StatusChanged, and recomputing walked the whole document per keystroke.
    public int GetPrintPageCount()
        => IsPaged ? EnsurePageBreaks().Count : ComputePageBreaks(PaperContentWidth, PaperContentHeight).Count;

    /// <summary>Renders one page to a Win2D bitmap at the given DPI (96 = screen, 300 = print quality).
    /// Content only — no caret, selection, or handles. The caller owns/disposes the result. UI thread.</summary>
    public CanvasRenderTarget RenderPrintPage(int pageIndex, double dpi = 96)
    {
        CanvasRenderTarget result = null!;
        WithPrintLayout(() => result = RenderPageToTarget(pageIndex, dpi, EnsurePageBreaks().Count));
        return result;
    }

    /// <summary>Writes the document to <paramref name="stream"/> as a PDF, one page per document page.
    /// Pages are VECTOR where Windows' "Microsoft Print to PDF" printer is available (it is by default): the
    /// editor draws them for the printer with no dialog, so text stays text — selectable, searchable, sharp
    /// at any zoom — with subset fonts embedded. Where that printer or the print spooler is missing, each
    /// page is instead rasterized at <paramref name="dpi"/> (default 150), as before 2026-09. Must run on the
    /// UI thread (Win2D rendering).</summary>
    public void SavePdf(Stream stream, double dpi = 150)
    {
        if (Document == null) return;
        if (WindowsPdfPrinter.TryWrite(this) is { } vector)
        {
            stream.Write(vector, 0, vector.Length);
            return;
        }
        WithPrintLayout(() =>
        {
            int count = EnsurePageBreaks().Count;
            double ptW = PaperWidth * 72.0 / 96.0, ptH = PaperHeight * 72.0 / 96.0;
            PdfWriter.Write(stream, ptW, ptH, count, i =>
            {
                using var rt = RenderPageToTarget(i, dpi, count);
                return ReadRgb(rt);
            });
        });
    }

    // Forces the paged layout (paper content width + line-aware breaks) and print mode for the duration
    // of `body`, then restores the on-screen layout. Printing always paginates, even from Continuous.
    private void WithPrintLayout(Action body)
    {
        double savedWidth = _layoutWidth;
        var savedBreaks = _pageBreaks;
        bool savedPrint = _printMode;
        _layoutWidth = PaperContentWidth;
        _pageBreaks = ComputePageBreaks(PaperContentWidth, PaperContentHeight);
        _printMode = true;
        try { body(); }
        finally
        {
            _layoutWidth = savedWidth; _pageBreaks = savedBreaks; _printMode = savedPrint;
            // Print-resolution bitmaps live for the print only. Everything that draws them — a render target,
            // a command list added to the print control, the printer's session — has used them by now.
            if (!savedPrint) _images.EndPrint();
        }
    }

    private CanvasRenderTarget RenderPageToTarget(int pageIndex, double dpi, int pageCount)
    {
        var device = CanvasDevice.GetSharedDevice();
        var rt = new CanvasRenderTarget(device, (float)PaperWidth, (float)PaperHeight, (float)dpi);
        using (var ds = rt.CreateDrawingSession())
        {
            ds.Clear(Colors.White);
            DrawPrintPage(ds, pageIndex, pageCount);
        }
        return rt;
    }

    // One page of the print layout, in paper DIPs (PaperWidth × PaperHeight), drawn UNDER whatever transform
    // the caller has set: identity into a bitmap for PDF, a fit-to-paper scale into the printer's own session
    // for the print dialog — where text reaches the printer as text, not pixels. Inside WithPrintLayout only.
    private void DrawPrintPage(CanvasDrawingSession ds, int pageIndex, int pageCount)
    {
        var paperToDevice = ds.Transform;
        var breaks = EnsurePageBreaks();
        double docTop = breaks[Math.Clamp(pageIndex, 0, breaks.Count - 1)];
        using (ds.CreateLayer(1f, new Rect(PagePadLeft, PagePadTop, PaperContentWidth, PaperContentHeight)))
        {
            // Document space → paper, then the caller's paper → device (row vectors: left applies first).
            ds.Transform = Matrix3x2.CreateTranslation((float)(PagePadLeft - DocContentLeft), (float)(PagePadTop - docTop)) * paperToDevice;
            // _printMode gates all geometry recording, so this walk can't pollute hit-test caches.
            // Clip to this page's doc range so printing/PDF renders O(page), not O(document) per page.
            DrawContentWalk(ds, docTop - 1, docTop + PaperContentHeight + 1);
            ds.Transform = paperToDevice;
        }
        DrawPageMarginChrome(ds, new Rect(0, 0, PaperWidth, PaperHeight), pageIndex, pageCount);
    }

    // The vector print path (RichEditorPrintHelper): `body` runs with the print layout in force and gets the
    // page count and a page drawer (session, 0-based page) that draws in paper DIPs under the session's
    // current transform. UI thread.
    internal void WithPrintPages(Action<int, Action<CanvasDrawingSession, int>> body)
    {
        if (Document == null) return;
        WithPrintLayout(() =>
        {
            int count = EnsurePageBreaks().Count;
            body(count, (ds, i) => DrawPrintPage(ds, i, count));
        });
    }

    // Reads a render target's pixels as top-down 24-bit RGB (PdfWriter's expected format).
    private static (int width, int height, byte[] rgb) ReadRgb(CanvasRenderTarget rt)
    {
        var size = rt.SizeInPixels;
        int w = (int)size.Width, h = (int)size.Height;
        byte[] bgra = rt.GetPixelBytes();
        var rgb = new byte[w * h * 3];
        for (int s = 0, d = 0; d < rgb.Length; s += 4, d += 3)
        {
            rgb[d] = bgra[s + 2];     // R
            rgb[d + 1] = bgra[s + 1]; // G
            rgb[d + 2] = bgra[s];     // B
        }
        return (w, h, rgb);
    }
}
