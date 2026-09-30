using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace WinUIRichEditor.Formatters;

// Minimal raster PDF writer: each page is one full-bleed FlateDecode RGB image. No external
// dependencies — zlib framing comes from System.IO.Compression.ZLibStream. The control renders each
// page's pixels (Win2D) and hands them here; vector text output is out of scope.
internal static class PdfWriter
{
    // getPage(i) returns one rendered page as raw 24-bit RGB, top-down. Pages are pulled lazily so
    // only one page's pixels are in memory at a time.
    public static void Write(Stream output, double pageWidthPt, double pageHeightPt,
        int pageCount, Func<int, (int width, int height, byte[] rgb)> getPage)
    {
        long pos = 0;
        var offsets = new List<long>(); // offsets[k] = byte offset of object k+1

        void WriteRaw(byte[] bytes) { output.Write(bytes, 0, bytes.Length); pos += bytes.Length; }
        void WriteAscii(string s) => WriteRaw(Encoding.ASCII.GetBytes(s));
        void BeginObj(int num)
        {
            while (offsets.Count < num) offsets.Add(0);
            offsets[num - 1] = pos;
            WriteAscii($"{num} 0 obj\n");
        }
        string Num(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);

        WriteAscii("%PDF-1.4\n");
        WriteRaw(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

        BeginObj(1);
        WriteAscii("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        BeginObj(2);
        var kids = new StringBuilder();
        for (int i = 0; i < pageCount; i++) kids.Append($"{3 + 3 * i} 0 R ");
        WriteAscii($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>\nendobj\n");

        for (int i = 0; i < pageCount; i++)
        {
            var (w, h, rgb) = getPage(i);

            BeginObj(3 + 3 * i);
            WriteAscii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Num(pageWidthPt)} {Num(pageHeightPt)}] " +
                       $"/Resources << /XObject << /Im0 {5 + 3 * i} 0 R >> >> /Contents {4 + 3 * i} 0 R >>\nendobj\n");

            byte[] content = Encoding.ASCII.GetBytes($"q {Num(pageWidthPt)} 0 0 {Num(pageHeightPt)} 0 0 cm /Im0 Do Q");
            BeginObj(4 + 3 * i);
            WriteAscii($"<< /Length {content.Length} >>\nstream\n");
            WriteRaw(content);
            WriteAscii("\nendstream\nendobj\n");

            byte[] deflated;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                    z.Write(rgb, 0, rgb.Length);
                deflated = ms.ToArray();
            }
            BeginObj(5 + 3 * i);
            WriteAscii($"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} " +
                       $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {deflated.Length} >>\nstream\n");
            WriteRaw(deflated);
            WriteAscii("\nendstream\nendobj\n");
        }

        long xrefPos = pos;
        WriteAscii($"xref\n0 {offsets.Count + 1}\n");
        WriteAscii("0000000000 65535 f \n");
        foreach (long off in offsets)
            WriteAscii($"{off:0000000000} 00000 n \n");
        WriteAscii($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
        output.Flush();
    }
}
