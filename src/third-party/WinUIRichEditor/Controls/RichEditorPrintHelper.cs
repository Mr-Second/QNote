using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Printing;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using Windows.Graphics.Printing;

namespace WinUIRichEditor.Controls;

/// <summary>Connects a <see cref="RichEditor"/> to the Windows print dialog. Pages are drawn by the editor's
/// own renderer straight into the printer's drawing session (Win2D <c>CanvasPrintDocument</c>), so text
/// reaches the printer as text — selectable in a PDF printer's output, at the printer's resolution. The
/// dialog is shown through <c>IPrintManagerInterop</c>, which works for unpackaged apps given the host
/// window's HWND, Native AOT included.</summary>
public static class RichEditorPrintHelper
{
    // The print flow outlives ShowPrintUIAsync: the dialog calls back into the print document for page
    // counts, preview, and the pages themselves, and the spooler runs later still. Nothing in the print
    // system holds a managed reference back, so locals here are collectable the moment the method awaits —
    // with the document gone its callbacks never run and the dialog sits on "loading preview" forever.
    // These fields are the strong reference for the whole job; PrintTask.Completed releases them.
    private static CanvasPrintDocument? _document;
    private static PrintManager? _manager;
    private static TypedEventHandler<PrintManager, PrintTaskRequestedEventArgs>? _onTaskRequested;

    /// <summary>Shows the system print UI for the editor's current document.
    /// <paramref name="windowHandle"/> is the host window's HWND
    /// (<c>WinRT.Interop.WindowNative.GetWindowHandle(window)</c>). Returns <see langword="false"/>
    /// when printing is unavailable on this system or the dialog could not be shown; the print job
    /// itself (after the dialog) is asynchronous and owned by the spooler. UI thread only.
    /// <para>Each page is drawn when the dialog asks for it — preview pages as they are viewed, the print
    /// output page by page — scaled uniformly to fit the paper the user picks. <paramref name="dpi"/> is
    /// no longer used (pages print as vector drawing, not a bitmap); it stays for source compatibility.</para></summary>
    public static async Task<bool> ShowPrintUIAsync(RichEditor editor, nint windowHandle, string jobTitle = "Document", double dpi = 150)
    {
        if (editor.Document == null || windowHandle == 0) return false;
        try { if (!PrintManager.IsSupported()) return false; }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return false; }

        Release();   // drop any previous job's references

        var queue = editor.DispatcherQueue;
        var paper = editor.GetPaperPixelSize();
        var printDoc = new CanvasPrintDocument();
        _document = printDoc;

        // The page count does not depend on the options (the page is scaled to fit any paper), but the
        // dialog asks for it here, and the preview has to be redrawn for the new paper.
        printDoc.PrintTaskOptionsChanged += (sender, args) => OnUiThread(queue, args.GetDeferral, () =>
        {
            int count = 1;
            editor.WithPrintPages((n, _) => count = Math.Max(1, n));
            sender.SetPageCount((uint)count);
            sender.InvalidatePreview();
        });
        printDoc.Preview += (_, args) => OnUiThread(queue, args.GetDeferral, () =>
            editor.WithPrintPages((_, draw) => DrawFitted(args.DrawingSession, args.PrintTaskOptions, (int)args.PageNumber, paper, draw)));
        printDoc.Print += (_, args) => OnUiThread(queue, args.GetDeferral, () =>
            editor.WithPrintPages((count, draw) =>
            {
                for (int page = 1; page <= count; page++)
                {
                    using var ds = args.CreateDrawingSession();
                    DrawFitted(ds, args.PrintTaskOptions, page, paper, draw);
                }
            }));

        var manager = PrintManagerInterop.GetForWindow(windowHandle);
        _manager = manager;

        // Set once the dialog asks for the document: proof it engaged, even if the operation reports false
        // afterwards. Callers use the return value to decide on a fallback, and showing one on top of a live
        // print dialog is worse than trusting the dialog.
        bool taskRequested = false;
        _onTaskRequested = (_, args) =>
        {
            taskRequested = true;
            var task = args.Request.CreatePrintTask(jobTitle, req => req.SetSource(printDoc));
            task.Completed += (_, _) => queue.TryEnqueue(Release);
        };
        manager.PrintTaskRequested += _onTaskRequested;

        nint op = 0;
        try
        {
            op = StartShowPrintUI(windowHandle);
            // The operation completes when the dialog is done with; poll it on the UI thread (the await
            // resumes here, in the apartment that owns the pointer).
            int status;
            while ((status = AsyncStatusOf(op)) == AsyncStarted) await Task.Delay(50);
            bool shown = status == AsyncCompleted && AsyncBoolResult(op);
            if (!shown && !taskRequested) Release();
            return shown || taskRequested;
        }
        catch (Exception ex)
        {
            RichEditorDiagnostics.Report(ex);
            if (!taskRequested) Release();
            return taskRequested;
        }
        finally
        {
            if (op != 0) ComRelease(op);
        }
    }

    // One page, scaled uniformly onto the printer's page and centred: the editor's paper (its own page size,
    // in DIPs) fits whole on whatever paper and orientation the user picks. pageNumber is 1-based.
    private static void DrawFitted(CanvasDrawingSession ds, PrintTaskOptions options, int pageNumber, Size paper,
        Action<CanvasDrawingSession, int> draw)
    {
        var target = options.GetPageDescription((uint)pageNumber).PageSize;
        double scale = Math.Min(target.Width / paper.Width, target.Height / paper.Height);
        float ox = (float)((target.Width - paper.Width * scale) / 2), oy = (float)((target.Height - paper.Height * scale) / 2);
        ds.Transform = Matrix3x2.CreateScale((float)scale) * Matrix3x2.CreateTranslation(ox, oy);
        draw(ds, pageNumber - 1);
    }

    // The editor — model and layout caches — is UI-thread only; the print system may call from elsewhere.
    // Runs `work` there, holding the event's deferral until it is done. A fault is reported, not thrown
    // into the print system.
    private static void OnUiThread(DispatcherQueue queue, Func<CanvasPrintDeferral> getDeferral, Action work)
    {
        if (queue.HasThreadAccess) { Guarded(work); return; }
        var deferral = getDeferral();
        if (!queue.TryEnqueue(() => { try { Guarded(work); } finally { deferral.Complete(); } }))
            deferral.Complete();
    }

    private static void Guarded(Action work)
    {
        try { work(); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
    }

    // Ends the job's lifetime: unhooks the manager and drops the document. Not disposed — a new job must
    // not cut off one the spooler may still be reading.
    private static void Release()
    {
        if (_manager != null && _onTaskRequested != null)
            _manager.PrintTaskRequested -= _onTaskRequested;

        _onTaskRequested = null;
        _manager = null;
        _document = null;
    }

    // ---- the dialog, called through its COM interface ---------------------------------------------
    // PrintManagerInterop.ShowPrintUIForWindowAsync returns IAsyncOperation<bool>, and CsWinRT (2.2)
    // resolves that generic instantiation's ABI helper by reflection — which Native AOT refuses (dynamic
    // code is off in any PublishAot build). The throw landed AFTER the dialog was on screen, so until
    // 2026-09-12 this helper declined outright under AOT and hosts fell back to PDF. There is no public way
    // to pre-register the instantiation, so the projection is bypassed instead: the call goes straight to
    // IPrintManagerInterop's vtable, and the returned operation is read through its own vtable
    // (IAsyncInfo.Status, then GetResults). No generic projection, nothing to reflect — one path for JIT
    // and AOT alike.

    private static readonly Guid IID_IPrintManagerInterop = new("C5435A42-8D43-4E7B-A68A-EF311E392087");
    private static readonly Guid IID_IAsyncOperationOfBoolean = new("CDB5EFB3-5788-509D-9BE1-71CCB8A3362A");
    private static readonly Guid IID_IAsyncInfo = new("00000036-0000-0000-C000-000000000046");
    private const int AsyncStarted = 0, AsyncCompleted = 1; // Windows.Foundation.AsyncStatus

    // Starts ShowPrintUIForWindowAsync and returns the IAsyncOperation<bool> (the caller releases it).
    private static unsafe nint StartShowPrintUI(nint hwnd)
    {
        const string cls = "Windows.Graphics.Printing.PrintManager";
        nint hstr = 0, factory = 0, op = 0;
        try
        {
            fixed (char* p = cls) Marshal.ThrowExceptionForHR(WindowsCreateString(p, (uint)cls.Length, &hstr));
            Guid factoryIid = IID_IPrintManagerInterop;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstr, &factoryIid, &factory));
            // IInspectable's six slots, then GetForWindow (6) and ShowPrintUIForWindowAsync (7).
            var show = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)factory)[7];
            Guid opIid = IID_IAsyncOperationOfBoolean;
            Marshal.ThrowExceptionForHR(show(factory, hwnd, &opIid, &op));
            return op;
        }
        finally
        {
            if (factory != 0) ComRelease(factory);
            if (hstr != 0) WindowsDeleteString(hstr);
        }
    }

    // The operation's AsyncStatus (Started/Completed/Canceled/Error); a failed read counts as Error.
    private static unsafe int AsyncStatusOf(nint op)
    {
        Guid iid = IID_IAsyncInfo;
        nint info = 0;
        var qi = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)op)[0];
        if (qi(op, &iid, &info) < 0 || info == 0) return 3;
        try
        {
            int status = 3;
            var getStatus = (delegate* unmanaged[Stdcall]<nint, int*, int>)(*(nint**)info)[7]; // IInspectable + Id, Status
            return getStatus(info, &status) < 0 ? 3 : status;
        }
        finally { ComRelease(info); }
    }

    // IAsyncOperation<bool>.GetResults: IInspectable, then Completed put/get (6, 7), GetResults (8).
    private static unsafe bool AsyncBoolResult(nint op)
    {
        byte result = 0;
        var getResults = (delegate* unmanaged[Stdcall]<nint, byte*, int>)(*(nint**)op)[8];
        return getResults(op, &result) >= 0 && result != 0;
    }

    private static unsafe void ComRelease(nint unknown)
        => ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)unknown)[2])(unknown);

    [DllImport("combase.dll")]
    private static extern unsafe int WindowsCreateString(char* sourceString, uint length, nint* hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll")]
    private static extern unsafe int RoGetActivationFactory(nint activatableClassId, Guid* iid, nint* factory);
}
