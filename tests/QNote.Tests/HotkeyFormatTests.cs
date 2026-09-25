using QNote.EdgeHide;

namespace QNote.Tests;

/// <summary>
/// Hotkey display formatting (ADR D6): modifier order, key names, disabled state.
/// Pure helpers — no Win32/WinRT involved.
/// </summary>
public class HotkeyFormatTests
{
    [Fact]
    public void Default_WinPlusBacktick()
    {
        Assert.Equal("Win + `", HotkeyFormat.ToDisplay(HotkeyFormat.ModWin, HotkeyFormat.VkOem3));
    }

    [Fact]
    public void ModifierOrder_IsWinCtrlAltShift_RegardlessOfMaskComposition()
    {
        const int all = HotkeyFormat.ModShift | HotkeyFormat.ModAlt | HotkeyFormat.ModControl | HotkeyFormat.ModWin;
        Assert.Equal("Win + Ctrl + Alt + Shift + A", HotkeyFormat.ToDisplay(all, 0x41));
    }

    [Fact]
    public void LettersAndDigits_RenderAsCharacters()
    {
        Assert.Equal("Ctrl + Shift + A",
            HotkeyFormat.ToDisplay(HotkeyFormat.ModControl | HotkeyFormat.ModShift, 0x41));
        Assert.Equal("Alt + 5", HotkeyFormat.ToDisplay(HotkeyFormat.ModAlt, 0x35));
    }

    [Fact]
    public void FunctionKeys_RenderByNumber()
    {
        Assert.Equal("F5", HotkeyFormat.ToDisplay(0, 0x74));
        Assert.Equal("Win + F12", HotkeyFormat.ToDisplay(HotkeyFormat.ModWin, 0x7B));
    }

    [Fact]
    public void DisabledHotkey_RendersEmpty()
    {
        Assert.Equal("", HotkeyFormat.ToDisplay(0, 0));
        Assert.Equal("", HotkeyFormat.ToDisplay(HotkeyFormat.ModWin, 0)); // key 0 wins
    }

    [Fact]
    public void UnknownVirtualKey_FallsBackToHex()
    {
        Assert.Equal("Ctrl + 0xA5", HotkeyFormat.ToDisplay(HotkeyFormat.ModControl, 0xA5));
    }
}
