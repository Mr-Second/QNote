using System;
using System.Runtime.InteropServices;
using Windows.System;

namespace WinUIRichEditor.Controls;

// AltGr and Ctrl+Alt are the same keystroke to Windows: AltGr arrives as Ctrl+Alt. On a layout that has AltGr
// characters (German ² ³ { [ ] }, Polish ą ę ś ć, French €, ...) a Ctrl+Alt shortcut therefore swallows a character
// the user meant to TYPE — the heading shortcuts Ctrl+Alt+1..6 took German AltGr+2 (²) and AltGr+3 (³). Word has
// the same conflict. The rule here: when the current layout turns this key with Ctrl+Alt into a character, it is
// that character, not a shortcut.
//
// Asked of the OS layout itself (ToUnicodeEx), so every layout answers for itself — no table of AltGr layouts.
// Flag 0x4 keeps the query from disturbing a pending dead key (Windows 10 1607+). Blittable pointers, so the
// P/Invoke compiles under Native AOT without runtime marshalling.
internal static unsafe class AltGrKeys
{
    /// <summary>The decision, separated from the OS query so it can be tested without a German keyboard:
    /// a Ctrl+Alt chord that produces a character is typing, not a shortcut.</summary>
    internal static bool IsTyping(bool ctrl, bool alt, Func<string?> producedText)
        => ctrl && alt && producedText() is { Length: > 0 } s && !char.IsControl(s[0]);

    /// <summary>The text the current keyboard layout produces for <paramref name="key"/> with Ctrl+Alt held (and
    /// Shift when <paramref name="shift"/>), or null when it produces none.</summary>
    internal static string? TextForCtrlAlt(VirtualKey key, bool shift) => TextFor(key, shift, ctrlAlt: true, GetKeyboardLayout(0));

    internal static string? TextFor(VirtualKey key, bool shift, bool ctrlAlt, IntPtr layout)
    {
        byte* state = stackalloc byte[256];
        for (int i = 0; i < 256; i++) state[i] = 0;
        const byte Down = 0x80;
        if (ctrlAlt) { state[0x11] = Down; state[0xA2] = Down; state[0x12] = Down; state[0xA5] = Down; } // Ctrl, LCtrl, Alt, RAlt (= AltGr)
        if (shift) { state[0x10] = Down; state[0xA0] = Down; }
        char* buf = stackalloc char[8];
        uint scan = MapVirtualKeyEx((uint)key, 0 /* MAPVK_VK_TO_VSC */, layout);
        int n = ToUnicodeEx((uint)key, scan, state, buf, 8, 0x4, layout);
        return n > 0 ? new string(buf, 0, n) : null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr layout);

    [DllImport("user32.dll")]
    private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte* keyState, char* buffer, int bufferSize, uint flags, IntPtr layout);
}
