using System;
using System.Runtime.InteropServices;

namespace WinUIRichEditor.Controls;

// Input timings the OS owns, not the control. Hard-coding these is not a style question — both are
// ACCESSIBILITY settings:
//   * Caret blink can be slowed or switched OFF entirely (Settings > Accessibility > Text cursor), for
//     users who find the motion distracting or who lose a flashing caret. A control with its own
//     530 ms constant overrides that silently, and the "off" case has no constant that can express it.
//   * Double-click speed is what a user with limited dexterity slows down so double-clicks register
//     at all. At the default 500 ms their word-select never fires.
// Sibling of SystemFontInfo (same Windows-only guard + fallback shape).
internal static class SystemInputSettings
{
    /// <summary>Windows default caret blink half-period, used when the OS value is unavailable.</summary>
    public const double FallbackBlinkMs = 530;

    /// <summary>Windows default double-click interval, used when the OS value is unavailable.</summary>
    public const double FallbackDoubleClickMs = 500;

    // GetCaretBlinkTime returns this when the user has turned blinking off.
    private const uint Infinite = 0xFFFFFFFF;

    /// <summary>Caret blink half-period in milliseconds, or <see langword="null"/> when the user has
    /// turned blinking OFF. Null means "draw a steady caret", NOT "hide it" — the setting is about
    /// motion, and a reader who disabled the flashing still needs to see where they are.</summary>
    public static double? CaretBlinkMs()
    {
        if (!OperatingSystem.IsWindows()) return FallbackBlinkMs;
        try
        {
            uint ms = GetCaretBlinkTime();
            if (ms == Infinite) return null;
            if (ms == 0) return FallbackBlinkMs; // documented failure return
            // Floor only against a pathological registry value: a 10 ms timer would repaint the whole
            // canvas 100x a second. The Settings slider's own minimum is well above this.
            return Math.Max(100, ms);
        }
        catch (Exception ex)
        {
            RichEditorDiagnostics.Report(ex);
            return FallbackBlinkMs;
        }
    }

    /// <summary>Double-click interval in milliseconds.</summary>
    public static double DoubleClickMs()
    {
        if (!OperatingSystem.IsWindows()) return FallbackDoubleClickMs;
        try
        {
            uint ms = GetDoubleClickTime();
            return ms == 0 ? FallbackDoubleClickMs : ms;
        }
        catch (Exception ex)
        {
            RichEditorDiagnostics.Report(ex);
            return FallbackDoubleClickMs;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetCaretBlinkTime();

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
}
