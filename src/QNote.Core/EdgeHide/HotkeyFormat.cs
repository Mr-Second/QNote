namespace QNote.EdgeHide;

/// <summary>
/// Edge-hide global hotkey helpers (ADR D6): a Win32 modifier mask + virtual-key
/// code pair, persisted as plain invariant ints in settings. Display formatting is
/// pure and headless-testable here; the capture control and RegisterHotKey call
/// live in the view layer.
/// </summary>
public static class HotkeyFormat
{
    // Win32 MOD_* mask bits (RegisterHotKey).
    public const int ModAlt = 0x0001;
    public const int ModControl = 0x0002;
    public const int ModShift = 0x0004;
    public const int ModWin = 0x0008;

    /// <summary>Default main key: VK_OEM_3 (` / ~, the key left of 1 on a US layout).</summary>
    public const int VkOem3 = 0xC0;

    /// <summary>First / last function-key virtual-key codes (F1–F24).</summary>
    public const int VkF1 = 0x70;

    public const int VkF24 = 0x87;

    /// <summary>
    /// "Win + Ctrl + `" style display string. Returns an empty string when the
    /// hotkey is disabled (<paramref name="virtualKey"/> 0) — the view supplies
    /// its own "未设置" placeholder.
    /// </summary>
    public static string ToDisplay(int modifiers, int virtualKey)
    {
        if (virtualKey == 0)
            return string.Empty;

        var parts = new List<string>(5);
        if ((modifiers & ModWin) != 0)
            parts.Add("Win");
        if ((modifiers & ModControl) != 0)
            parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0)
            parts.Add("Alt");
        if ((modifiers & ModShift) != 0)
            parts.Add("Shift");
        parts.Add(KeyName(virtualKey));
        return string.Join(" + ", parts);
    }

    /// <summary>Virtual-key code → stable display name (unknown codes fall back to hex).</summary>
    public static string KeyName(int vk) => vk switch
    {
        >= 0x30 and <= 0x39 => ((char)vk).ToString(), // 0-9
        >= 0x41 and <= 0x5A => ((char)vk).ToString(), // A-Z
        >= VkF1 and <= VkF24 => $"F{vk - VkF1 + 1}",
        0xC0 => "`",
        0x20 => "Space",
        0x09 => "Tab",
        0x0D => "Enter",
        0x1B => "Esc",
        0x08 => "Backspace",
        0x2E => "Delete",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0xBD => "-",
        0xBB => "=",
        0xDB => "[",
        0xDD => "]",
        0xBA => ";",
        0xDE => "'",
        0xBC => ",",
        0xBE => ".",
        0xBF => "/",
        0xDC => "\\",
        _ => $"0x{vk:X2}",
    };
}
