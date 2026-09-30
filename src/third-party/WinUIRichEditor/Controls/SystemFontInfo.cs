using System;
using System.Runtime.InteropServices;

namespace WinUIRichEditor.Controls;

// Queries the OS default UI font so unstyled documents render with the system look
// (e.g. "맑은 고딕" on Korean Windows — the face name comes back localized, matching the
// localized family names DirectWrite/CanvasFontSet reports for FontFamilyChoices).
// A direct port of AvaloniaRichEditor's SystemFontInfo (Windows-only).
internal static class SystemFontInfo
{
    /// <summary>The system message-box font face name, or null when unavailable.</summary>
    public static string? MessageFontFaceName()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var ncm = new NONCLIENTMETRICSW { cbSize = (uint)Marshal.SizeOf<NONCLIENTMETRICSW>() };
            if (!SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, ncm.cbSize, ref ncm, 0)) return null;
            string name = ncm.lfMessageFont.lfFaceName;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception ex)
        {
            RichEditorDiagnostics.Report(ex);
            return null;
        }
    }

    private const uint SPI_GETNONCLIENTMETRICS = 0x0029;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONTW
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string lfFaceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NONCLIENTMETRICSW
    {
        public uint cbSize;
        public int iBorderWidth;
        public int iScrollWidth;
        public int iScrollHeight;
        public int iCaptionWidth;
        public int iCaptionHeight;
        public LOGFONTW lfCaptionFont;
        public int iSmCaptionWidth;
        public int iSmCaptionHeight;
        public LOGFONTW lfSmCaptionFont;
        public int iMenuWidth;
        public int iMenuHeight;
        public LOGFONTW lfMenuFont;
        public LOGFONTW lfStatusFont;
        public LOGFONTW lfMessageFont;
        public int iPaddedBorderWidth; // Vista+
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref NONCLIENTMETRICSW pvParam, uint fWinIni);
}
