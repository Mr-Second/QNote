namespace QNote.Services;

/// <summary>
/// Global (window-level Win32 <c>RegisterHotKey</c>) hotkey that toggles
/// edge-hide (ADR D6). Abstraction so view-models can re-register without
/// touching WinUI/Win32 — the Presentation implementation subclasses the main
/// window's WndProc for <c>WM_HOTKEY</c>.
/// </summary>
public interface IGlobalHotkey
{
    /// <summary>Raised on the UI thread when the registered hotkey is pressed.</summary>
    event Action? Pressed;

    /// <summary>True while a hotkey registration is active.</summary>
    bool IsRegistered { get; }

    /// <summary>Bind to the main window handle (subclasses its WndProc). Call once, UI thread.</summary>
    void Attach(nint hwnd);

    /// <summary>
    /// Register (or atomically replace) the hotkey; <paramref name="virtualKey"/> 0
    /// unregisters and disables it. Registration failure (the combo is owned by
    /// another app) is an expected outcome: reported as false, never thrown.
    /// </summary>
    bool TryRegister(int modifiers, int virtualKey);
}
