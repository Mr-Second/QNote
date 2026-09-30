using System;
using System.Runtime.InteropServices;

namespace WinUIRichEditor.Controls;

// Decodes encoded image bytes straight to a target pixel size with WIC, synchronously and off any particular
// thread. Two things CanvasBitmap.LoadAsync cannot do, and both are why this exists:
//
//  · It decodes at SOURCE size. A 12 MP photo shown 240 px wide held a 46 MB bitmap (measured 2026-09-16,
//    MemBaseline images mode). WIC's scaler asks a JPEG decoder for a reduced-size DCT decode where it can,
//    so the full-size pixels are often never materialised at all.
//  · It is async only. Printing draws synchronously, so a picture not yet decoded for the screen printed as
//    the "loading" placeholder — any page never scrolled into view, or a file printed right after opening.
//
// Output is premultiplied BGRA (Win2D's own format for loaded bitmaps). The first frame only, no EXIF
// rotation — both as LoadAsync does, so the picture looks the same as before.
//
// Called through COM vtables with function pointers, like WindowsPdfPrinter: the projection-free route that
// Native AOT compiles. WIC's factory is free-threaded, so this runs on a pool thread or the UI thread.
internal static unsafe class ImageDecoder
{
    private static readonly Guid CLSID_WICImagingFactory = new("CACAF262-9370-4615-A13B-9F5539DA4C0A");
    private static readonly Guid IID_IWICImagingFactory = new("EC5EC8A9-C395-4314-9C77-54D7A935FF70");
    private static readonly Guid GUID_WICPixelFormat32bppPBGRA = new("6FDDC324-4E03-4BFE-B185-3D77768DC910");

    private const int WICDecodeMetadataCacheOnDemand = 0;
    private const int WICBitmapInterpolationModeHighQualityCubic = 4;
    private const int WICBitmapDitherTypeNone = 0, WICBitmapPaletteTypeCustom = 0;

    /// <summary>Decodes <paramref name="encoded"/> to the smallest size that covers <paramref name="boxWidth"/> ×
    /// <paramref name="boxHeight"/> pixels, keeping the aspect ratio and never enlarging (see <see cref="CoverBox"/>).
    /// Returns null for bytes WIC can't decode. <c>Natural</c> is the source size, so a caller knows whether a
    /// bigger request could ever get more detail.</summary>
    public static (byte[] Pixels, int Width, int Height, int NaturalWidth, int NaturalHeight)? Decode(
        byte[] encoded, int boxWidth, int boxHeight)
    {
        nint factory = 0, stream = 0, decoder = 0, frame = 0, scaler = 0, converter = 0;
        try
        {
            fixed (byte* data = encoded) // WIC's memory stream does not copy: keep the bytes pinned throughout
            {
                if (!OpenFrame(encoded, data, ref factory, ref stream, ref decoder, ref frame)) return null;
                uint natW, natH;
                if (GetSize(frame, &natW, &natH) < 0 || natW == 0 || natH == 0) return null;

                var (w, h) = CoverBox((int)natW, (int)natH, boxWidth, boxHeight);
                nint source = frame;
                bool scaleFirst = IsJpeg(encoded); // no alpha: scaling first keeps the decoder's reduced-size DCT path
                if (!scaleFirst) source = Convert(factory, source, ref converter);
                if (source == 0) return null;
                if (w != natW || h != natH)
                {
                    if (Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtbl(factory)[11])(factory, &scaler)) // CreateBitmapScaler
                        || Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, int, int>)Vtbl(scaler)[8])(
                            scaler, source, (uint)w, (uint)h, WICBitmapInterpolationModeHighQualityCubic))) // IWICBitmapScaler::Initialize
                        return null;
                    source = scaler;
                }
                // Straight alpha scaled as straight alpha would drag transparent black into the edges, which is
                // why non-JPEG sources are premultiplied BEFORE the scaler above.
                if (scaleFirst) source = Convert(factory, source, ref converter);
                if (source == 0) return null;

                var pixels = new byte[checked(w * h * 4)];
                fixed (byte* buf = pixels)
                {
                    if (Check(((delegate* unmanaged[Stdcall]<nint, void*, uint, uint, byte*, int>)Vtbl(source)[7])(
                        source, null, (uint)(w * 4), (uint)pixels.Length, buf))) // IWICBitmapSource::CopyPixels(whole)
                        return null;
                }
                return (pixels, w, h, (int)natW, (int)natH);
            }
        }
        finally
        {
            Release(converter); Release(scaler); Release(frame); Release(decoder); Release(stream); Release(factory);
        }
    }

    /// <summary>The smallest size with the source's aspect ratio that covers the box on BOTH axes, never above
    /// the source. A picture is drawn stretched into its rect, which need not have the source's proportions (a
    /// resize handle, an <c>&lt;img width height&gt;</c>); fitting INSIDE the box left one axis short, and the
    /// cache, seeing it short, re-decoded the same size on every draw. One uniform scale per request also keeps
    /// requests totally ordered, so pictures sharing a decode at different proportions can't ping-pong.</summary>
    internal static (int Width, int Height) CoverBox(int natW, int natH, int boxW, int boxH)
    {
        boxW = Math.Max(1, boxW);
        boxH = Math.Max(1, boxH);
        double s = Math.Max((double)boxW / natW, (double)boxH / natH);
        if (s >= 1) return (natW, natH);
        // Ceiling: rounding down would land a pixel short of the box and read as "needs more" again. The
        // epsilon keeps an exact fit (200/4000 × 4000) from rounding up past it on floating-point noise.
        static int Up(double v) => Math.Max(1, (int)Math.Ceiling(v - 1e-9));
        return (Math.Min(natW, Up(natW * s)), Math.Min(natH, Up(natH * s)));
    }

    private static bool IsJpeg(byte[] b) => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    private static bool OpenFrame(byte[] encoded, byte* data, ref nint factory, ref nint stream, ref nint decoder, ref nint frame)
    {
        if (encoded.Length == 0) return false;
        Guid clsid = CLSID_WICImagingFactory, iid = IID_IWICImagingFactory;
        nint f = 0, s = 0, d = 0, fr = 0;
        try
        {
            if (Check(CoCreateInstance(&clsid, 0, 1 /* CLSCTX_INPROC_SERVER */, &iid, &f))) return false;
            if (Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtbl(f)[14])(f, &s))) return false; // CreateStream
            if (Check(((delegate* unmanaged[Stdcall]<nint, byte*, uint, int>)Vtbl(s)[16])(s, data, (uint)encoded.Length))) return false; // InitializeFromMemory
            if (Check(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, int, nint*, int>)Vtbl(f)[4])(
                f, s, null, WICDecodeMetadataCacheOnDemand, &d))) return false; // CreateDecoderFromStream
            if (Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtbl(d)[13])(d, 0, &fr))) return false; // GetFrame(0)
            return true;
        }
        finally { factory = f; stream = s; decoder = d; frame = fr; }
    }

    // A premultiplied-BGRA view of `source` (the converter is owned by the caller), or 0 on failure.
    private static nint Convert(nint factory, nint source, ref nint converter)
    {
        nint c = 0;
        if (Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtbl(factory)[10])(factory, &c))) { converter = c; return 0; } // CreateFormatConverter
        converter = c;
        Guid format = GUID_WICPixelFormat32bppPBGRA;
        if (Check(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, int, nint, double, int, int>)Vtbl(c)[8])(
            c, source, &format, WICBitmapDitherTypeNone, 0, 0.0, WICBitmapPaletteTypeCustom))) return 0; // IWICFormatConverter::Initialize
        return c;
    }

    private static int GetSize(nint source, uint* w, uint* h)
        => ((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Vtbl(source)[3])(source, w, h); // IWICBitmapSource::GetSize

    private static bool Check(int hr) => hr < 0;

    private static nint* Vtbl(nint unknown) => *(nint**)unknown;

    private static void Release(nint unknown)
    {
        if (unknown != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Vtbl(unknown)[2])(unknown);
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* obj);
}
