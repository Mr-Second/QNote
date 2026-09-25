using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using QNote.EdgeHide;
using QNote.ViewModels;
using Windows.System;
using Windows.UI.Core;

namespace QNote.Controls;

/// <summary>
/// The settings surface hosted inside the settings <see cref="ContentDialog"/>.
/// Grouped layout (显示 / 常规) — new groups append as siblings; state lives in
/// <see cref="SettingsViewModel"/> (changes save + apply live). The only local
/// mechanics here is the hotkey CAPTURE mode (ADR D6): click the button, press a
/// combo; Esc cancels, Backspace/Delete clears (disables) the hotkey.
/// </summary>
public sealed partial class SettingsPanel : UserControl
{
    private bool _capturingHotkey;

    public SettingsPanel(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    /// <summary>
    /// Raised when the user picks 备份/恢复. The hosting dialog must close BEFORE the
    /// follow-up dialog opens (only one ContentDialog may be open at a time) — the
    /// host (NotesPage) owns that choreography.
    /// </summary>
    public event Action? BackupRequested;

    public event Action? RestoreRequested;

    private void Backup_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => BackupRequested?.Invoke();

    private void Restore_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => RestoreRequested?.Invoke();

    // ---------- hotkey capture ----------

    private void Hotkey_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _capturingHotkey = true;
        ViewModel.BeginHotkeyCapture();
        // Focus the button so the follow-up key press lands in PreviewKeyDown.
        HotkeyButton.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    private void Hotkey_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturingHotkey)
            return;
        e.Handled = true; // swallow everything while capturing (Space/Enter/Tab included)

        var key = e.Key;
        if (key == VirtualKey.Escape)
        {
            _capturingHotkey = false;
            ViewModel.CancelHotkeyCapture();
            return;
        }

        if (key is VirtualKey.Back or VirtualKey.Delete)
        {
            _capturingHotkey = false;
            ViewModel.ClearHotkey();
            return;
        }

        // Modifier-only press: keep waiting for the main key.
        if (key is VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
            or VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.LeftWindows or VirtualKey.RightWindows)
            return;

        var modifiers = CurrentModifiers();
        var vk = (int)key;

        // A bare letter/digit hotkey would hijack everyday typing system-wide;
        // allow it only with a modifier — function keys (F1–F24) are safe alone.
        if (modifiers == 0 && vk is < HotkeyFormat.VkF1 or > HotkeyFormat.VkF24)
        {
            ViewModel.HotkeyError = "请同时按住 Win / Ctrl / Alt / Shift，或使用 F1–F12 功能键";
            return; // stay in capture mode
        }

        _capturingHotkey = false;
        ViewModel.CommitHotkey(modifiers, vk);
    }

    private static int CurrentModifiers()
    {
        var mods = 0;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
            mods |= HotkeyFormat.ModWin;
        if (IsDown(VirtualKey.LeftControl) || IsDown(VirtualKey.RightControl) || IsDown(VirtualKey.Control))
            mods |= HotkeyFormat.ModControl;
        if (IsDown(VirtualKey.LeftMenu) || IsDown(VirtualKey.RightMenu) || IsDown(VirtualKey.Menu))
            mods |= HotkeyFormat.ModAlt;
        if (IsDown(VirtualKey.LeftShift) || IsDown(VirtualKey.RightShift) || IsDown(VirtualKey.Shift))
            mods |= HotkeyFormat.ModShift;
        return mods;

        static bool IsDown(VirtualKey k) =>
            InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);
    }
}
