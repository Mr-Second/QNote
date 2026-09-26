using System.Runtime.InteropServices;
using System.Text;

namespace QNote.Controls;

/// <summary>
/// Raw Win32 clipboard reads used as the fallback when the WinRT
/// <c>DataPackageView</c> async APIs fail (they are apartment-sensitive and can
/// throw RPC_E_WRONG_THREAD for clipboards produced by some apps, and WinRT
/// rejects some plain CF_DIB shapes). <c>OpenClipboard</c>/<c>GetClipboardData</c>
/// have no apartment requirements. All handles stay borrowed from the clipboard —
/// nothing here owns or frees the global memory.
/// </summary>
internal static class Win32Clipboard
{
    private const uint CfHdrop = 15;
    private const uint CfDib = 8;
    private const uint CfDibv5 = 17;
    private const uint CfUnicodeText = 13;
    private const uint DragQueryFileCount = 0xFFFFFFFF;

    /// <summary>Returns the CF_HDROP file list, or an empty list when absent/unreadable.</summary>
    public static IReadOnlyList<string> ReadFileDrop()
    {
        var paths = new List<string>();
        if (!OpenClipboard(IntPtr.Zero))
            return paths;
        try
        {
            var hDrop = GetClipboardData(CfHdrop);
            if (hDrop == IntPtr.Zero)
                return paths;

            var count = DragQueryFile(hDrop, DragQueryFileCount, null, 0);
            for (uint i = 0; i < count; i++)
            {
                var length = DragQueryFile(hDrop, i, null, 0);
                if (length == 0)
                    continue;
                var buffer = new StringBuilder((int)length + 1);
                if (DragQueryFile(hDrop, i, buffer, (uint)buffer.Capacity) > 0)
                    paths.Add(buffer.ToString());
            }
        }
        finally
        {
            CloseClipboard();
        }
        return paths;
    }

    /// <summary>
    /// Reads CF_DIBV5 (preferred) or CF_DIB and returns a complete BMP file
    /// (14-byte <c>BITMAPFILEHEADER</c> prepended) so WIC's BMP decoder can take
    /// it. Returns <c>null</c> when no DIB is on the clipboard or it is malformed.
    /// </summary>
    public static byte[]? ReadDibAsBmp()
    {
        if (!OpenClipboard(IntPtr.Zero))
            return null;
        try
        {
            var format = IsClipboardFormatAvailable(CfDibv5) ? CfDibv5
                       : IsClipboardFormatAvailable(CfDib) ? CfDib
                       : 0u;
            if (format == 0)
                return null;

            var handle = GetClipboardData(format);
            if (handle == IntPtr.Zero)
                return null;
            var size = (long)GlobalSize(handle);
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero || size < 40)
                return null;

            try
            {
                var dib = new byte[size];
                Marshal.Copy(ptr, dib, 0, (int)size);

                // bfOffBits = file header + DIB header + bit-field masks (when the
                // header does not already embed them) + palette entries.
                var headerSize = BitConverter.ToInt32(dib, 0);
                var compression = BitConverter.ToInt32(dib, 16);
                var colorsUsed = BitConverter.ToInt32(dib, 32);
                if (headerSize < 40 || headerSize > dib.Length)
                    return null;

                var masksBytes = compression switch
                {
                    3 => headerSize >= 56 ? 0 : 12, // BI_BITFIELDS, RGB masks
                    6 => headerSize >= 64 ? 0 : 16, // BI_ALPHABITFIELDS, RGBA masks
                    _ => 0,
                };
                var paletteBytes = colorsUsed > 0 ? colorsUsed * 4 : 0;
                var pixelOffset = 14 + headerSize + masksBytes + paletteBytes;
                if (pixelOffset > 14 + dib.Length)
                    return null;

                using var bmp = new MemoryStream(pixelOffset + dib.Length);
                var writer = new BinaryWriter(bmp);
                writer.Write((byte)'B');
                writer.Write((byte)'M');
                writer.Write(14 + dib.Length);   // bfSize
                writer.Write(0);                 // bfReserved
                writer.Write(pixelOffset);       // bfOffBits
                writer.Write(dib);
                writer.Flush();
                return bmp.ToArray();
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Reads the "Rich Text Format" clipboard format (a registered format holding
    /// ASCII RTF markup) as a string, or <c>null</c> when absent/unreadable.
    /// Fallback for WinRT <c>GetRtfAsync</c>, which is apartment-sensitive.
    /// </summary>
    public static string? ReadRtfText()
    {
        var format = RegisterClipboardFormat("Rich Text Format");
        if (format == 0)
            return null;
        var bytes = ReadClipboardBytes(format);
        return bytes is null ? null : Encoding.ASCII.GetString(bytes).TrimEnd('\0');
    }

    /// <summary>Reads CF_UNICODETEXT as a string, or <c>null</c> when absent/unreadable.</summary>
    public static string? ReadUnicodeText()
    {
        var bytes = ReadClipboardBytes(CfUnicodeText);
        return bytes is null ? null : Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    /// <summary>Copies a clipboard format's global memory into a managed byte array.</summary>
    private static byte[]? ReadClipboardBytes(uint format)
    {
        if (!OpenClipboard(IntPtr.Zero))
            return null;
        try
        {
            var handle = GetClipboardData(format);
            if (handle == IntPtr.Zero)
                return null;
            var size = (long)GlobalSize(handle);
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero || size <= 0)
                return null;
            try
            {
                var bytes = new byte[size];
                Marshal.Copy(ptr, bytes, 0, (int)size);
                return bytes;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr hMem);
}
