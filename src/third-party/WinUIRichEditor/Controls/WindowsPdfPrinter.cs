using System;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;

namespace WinUIRichEditor.Controls;

// SavePdf's vector path: the pages the print dialog prints — RichEditor.DrawPrintPage, into Win2D command
// lists — handed to Direct2D's print control, aimed at Windows' "Microsoft Print to PDF" printer with the job
// output captured in memory. No dialog, no "save as" prompt, and the text stays text: the printer embeds
// subset fonts and a ToUnicode map (probe: one page with a line of Latin and Hangul, 98.7 KB, no images,
// MediaBox = the page size passed in). The PDF is complete when the print control closes — every byte was in
// the stream at Close, so nothing waits on the spooler.
//
// Windows-only by nature, and that printer can be removed or the spooler stopped: then this returns null and
// SavePdf writes its raster pages. (The upstream peer, cross-platform, writes its vector PDF with Skia.)
//
// Called through COM vtables with function pointers, as RichEditorPrintHelper does: the projection-free
// route that Native AOT can compile.
internal static class WindowsPdfPrinter
{
    public const string PrinterName = "Microsoft Print to PDF";

    public static byte[]? TryWrite(RichEditor editor, string printerName = PrinterName)
    {
        try { return Write(editor, printerName); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return null; }
    }

    private static readonly Guid CLSID_PrintDocumentPackageTargetFactory = new("348EF17D-6C81-4982-92B4-EE188A43867A");
    private static readonly Guid IID_IPrintDocumentPackageTargetFactory = new("D2959BF7-B31B-4A3D-9600-712EB1335BA4");
    private static readonly Guid CLSID_WICImagingFactory = new("CACAF262-9370-4615-A13B-9F5539DA4C0A");
    private static readonly Guid IID_IWICImagingFactory = new("EC5EC8A9-C395-4314-9C77-54D7A935FF70");
    private static readonly Guid IID_ICanvasResourceWrapperNative = new("5F10688D-EA55-4D55-A3B0-4DDB55C0C20A");
    private static readonly Guid IID_ID2D1Device = new("47DD575D-AC05-4CDD-8049-9B02CD16F44C");
    private static readonly Guid IID_ID2D1CommandList = new("B4F34A19-2383-4D76-94F6-EC343657C3DC");

    [StructLayout(LayoutKind.Sequential)]
    private struct PrintControlProperties { public int FontSubset; public float RasterDpi; public int ColorSpace; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeF { public float Width, Height; }

    private static unsafe byte[]? Write(RichEditor editor, string printerName)
    {
        nint factory = 0, output = 0, target = 0, wic = 0, d2dDevice = 0, printControl = 0;
        try
        {
            Guid clsid = CLSID_PrintDocumentPackageTargetFactory, iid = IID_IPrintDocumentPackageTargetFactory;
            if (CoCreateInstance(&clsid, 0, 1 /* CLSCTX_INPROC_SERVER */, &iid, &factory) < 0) return null;
            Check(CreateStreamOnHGlobal(0, 1, &output));
            // The printer or the spooler missing fails here, before anything is drawn.
            if (CreateTarget(factory, printerName, output, &target) < 0) return null;

            Guid wicClsid = CLSID_WICImagingFactory, wicIid = IID_IWICImagingFactory;
            Check(CoCreateInstance(&wicClsid, 0, 1, &wicIid, &wic));
            var device = CanvasDevice.GetSharedDevice();
            d2dDevice = NativeResource(device, IID_ID2D1Device);
            // Font subsetting left to Direct2D's default; images inside the page print at 150 DPI where the
            // printer path has to rasterize them (the SavePdf default).
            var props = new PrintControlProperties { FontSubset = 0, RasterDpi = 150, ColorSpace = 1 /* sRGB */ };
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, PrintControlProperties*, nint*, int>)Vtbl(d2dDevice)[5])(
                d2dDevice, wic, target, &props, &printControl)); // ID2D1Device::CreatePrintControl

            var paper = editor.GetPaperPixelSize(); // DIPs — Direct2D's page unit
            nint pc = printControl;
            editor.WithPrintPages((count, draw) =>
            {
                for (int i = 0; i < count; i++)
                {
                    using var list = new CanvasCommandList(device);
                    using (var ds = list.CreateDrawingSession()) draw(ds, i);
                    AddPage(pc, NativeResource(list, IID_ID2D1CommandList), (float)paper.Width, (float)paper.Height);
                }
            });
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Vtbl(printControl)[4])(printControl)); // ID2D1PrintControl::Close
            return ReadAll(output);
        }
        finally
        {
            Release(printControl);
            Release(d2dDevice);
            Release(wic);
            Release(target);
            Release(output);
            Release(factory);
        }
    }

    // IPrintDocumentPackageTargetFactory::CreateDocumentPackageTargetForPrintJob — no job print ticket (the
    // printer's defaults; the page size comes from each AddPage).
    private static unsafe int CreateTarget(nint factory, string printerName, nint output, nint* target)
    {
        fixed (char* printer = printerName)
        fixed (char* job = "WinUIRichEditor")
            return ((delegate* unmanaged[Stdcall]<nint, char*, char*, nint, nint, nint*, int>)Vtbl(factory)[3])(
                factory, printer, job, output, 0, target);
    }

    // Win2D closes its Direct2D command list lazily — on first use as an image — and the print control
    // refuses an open one (D2DERR_WRONG_STATE, measured), so close it first. Takes ownership of `commandList`.
    private static unsafe void AddPage(nint printControl, nint commandList, float width, float height)
    {
        try
        {
            ((delegate* unmanaged[Stdcall]<nint, int>)Vtbl(commandList)[5])(commandList); // ID2D1CommandList::Close
            var size = new SizeF { Width = width, Height = height };
            Check(((delegate* unmanaged[Stdcall]<nint, nint, SizeF, nint, nint, nint, int>)Vtbl(printControl)[3])(
                printControl, commandList, size, 0, 0, 0)); // ID2D1PrintControl::AddPage
        }
        finally { Release(commandList); }
    }

    // The Direct2D object behind a Win2D one, through ICanvasResourceWrapperNative::GetNativeResource. The
    // result is owned by the caller.
    private static unsafe nint NativeResource(object win2dObject, Guid iid)
    {
        nint unknown = ((WinRT.IWinRTObject)win2dObject).NativeObject.ThisPtr; // borrowed
        Guid wrapperIid = IID_ICanvasResourceWrapperNative;
        nint wrapper = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtbl(unknown)[0])(unknown, &wrapperIid, &wrapper));
        try
        {
            nint resource = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, float, Guid*, nint*, int>)Vtbl(wrapper)[3])(
                wrapper, 0, 0, &iid, &resource));
            return resource;
        }
        finally { Release(wrapper); }
    }

    // The whole HGLOBAL-backed output stream.
    private static unsafe byte[]? ReadAll(nint stream)
    {
        ulong size = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)Vtbl(stream)[5])(stream, 0, 2 /* STREAM_SEEK_END */, &size));
        if (size == 0) return null;
        nint hGlobal = 0;
        Check(GetHGlobalFromStream(stream, &hGlobal));
        nint data = GlobalLock(hGlobal);
        if (data == 0) return null;
        try
        {
            var bytes = new byte[checked((int)size)];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return bytes.Length > 4 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F' ? bytes : null;
        }
        finally { GlobalUnlock(hGlobal); }
    }

    private static unsafe nint* Vtbl(nint unknown) => *(nint**)unknown;

    private static unsafe void Release(nint unknown)
    {
        if (unknown != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Vtbl(unknown)[2])(unknown);
    }

    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

    [DllImport("ole32.dll")]
    private static extern unsafe int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* obj);

    [DllImport("ole32.dll")]
    private static extern unsafe int CreateStreamOnHGlobal(nint hGlobal, int deleteOnRelease, nint* stream);

    [DllImport("ole32.dll")]
    private static extern unsafe int GetHGlobalFromStream(nint stream, nint* hGlobal);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll")]
    private static extern int GlobalUnlock(nint hMem);
}
