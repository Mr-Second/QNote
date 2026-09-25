using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace QNote.Services;

/// <summary>
/// Window-level global hotkey (Win32 <c>RegisterHotKey</c>) toggling edge-hide
/// (ADR D6). <c>WM_HOTKEY</c> reaches the app through a comctl32 window subclass
/// on the main HWND (<c>SetWindowSubclass</c> — the standard WinUI 3 message-hook
/// path, the same technique H.NotifyIcon uses). The hotkey works while the window
/// is off-screen / unfocused, which a local KeyboardAccelerator cannot do.
///
/// Registration failure (the combo is owned by another app or reserved by the
/// shell) is an expected outcome: logged + reported as false, never thrown.
/// All methods run on the UI thread (RegisterHotKey is bound to the HWND's thread).
/// </summary>
public sealed class GlobalHotkeyService : IGlobalHotkey
{
    private const int HotkeyId = 0x51E1; // arbitrary, unique within this HWND
    private const uint WmHotkey = 0x0312;

    /// <summary>MOD_NOREPEAT: holding the combo must not strobe-toggle the window.</summary>
    private const uint ModNoRepeat = 0x4000;

    private readonly ILogger<GlobalHotkeyService> _log;

    // Rooted against GC: comctl32 calls back into this delegate from the WndProc chain.
    private readonly SubclassProc _subclass;

    private nint _hwnd;

    public GlobalHotkeyService(ILogger<GlobalHotkeyService> log)
    {
        _log = log;
        _subclass = SubclassProcImpl;
    }

    public event Action? Pressed;

    public bool IsRegistered { get; private set; }

    public void Attach(nint hwnd)
    {
        _hwnd = hwnd;
        if (!SetWindowSubclass(hwnd, _subclass, HotkeyId, 0))
        {
            _log.LogError("SetWindowSubclass failed (error {Error}) — the global hotkey is unavailable.",
                Marshal.GetLastWin32Error());
        }
    }

    public bool TryRegister(int modifiers, int virtualKey)
    {
        // Replace = unregister the old combo first, then register the new one.
        UnregisterCurrent();

        if (virtualKey == 0 || _hwnd == 0)
            return true; // disabled, or not attached yet (startup ordering)

        if (RegisterHotKey(_hwnd, HotkeyId, (uint)modifiers | ModNoRepeat, (uint)virtualKey))
        {
            IsRegistered = true;
            return true;
        }

        _log.LogWarning(
            "Global hotkey registration failed (modifiers 0x{Modifiers:X}, vk 0x{Vk:X2}, error {Error}) — likely owned by another app.",
            modifiers, virtualKey, Marshal.GetLastWin32Error());
        return false;
    }

    private void UnregisterCurrent()
    {
        if (IsRegistered && _hwnd != 0)
            UnregisterHotKey(_hwnd, HotkeyId);
        IsRegistered = false;
    }

    private nint SubclassProcImpl(nint hWnd, uint msg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData)
    {
        if (msg == WmHotkey && wParam == HotkeyId)
        {
            try
            {
                Pressed?.Invoke();
            }
            catch (Exception ex)
            {
                // A WndProc callback must never let an exception escape into Win32.
                _log.LogError(ex, "Global hotkey handler failed.");
            }
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    // ---------- P/Invoke (DllImport, not LibraryImport — no unsafe blocks) ----------

    private delegate nint SubclassProc(
        nint hWnd, uint uMsg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(
        nint hWnd, SubclassProc pfnSubclass, nint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);
}
