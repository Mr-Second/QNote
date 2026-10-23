using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using QNote.EdgeHide;
using QNote.Services;
using QNote.ViewModels;
using Windows.System;
using Windows.UI.Core;

namespace QNote.Controls;

/// <summary>
/// The settings surface hosted inside the settings <see cref="ContentDialog"/>.
/// Grouped layout (显示 / 常规 / 数据 / 关于) — new groups append as siblings;
/// state lives in <see cref="SettingsViewModel"/> (changes save + apply live).
/// Local mechanics: the hotkey CAPTURE mode (ADR D6) and the sync section's
/// sign-in/out feedback (ResultTip toast + the destructive-red 注销 state).
/// </summary>
public sealed partial class SettingsPanel : UserControl
{
    private bool _capturingHotkey;

    public SettingsPanel(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        // Subscribe ONCE here and never unsubscribe. The VM is transient (one per
        // settings-dialog open, App.Services AddTransient) and outlives only this
        // panel, so there is nothing to leak. ⚠️ Do NOT unhook in Unloaded: the
        // hosting ContentDialog REPARENTS its content, so Unloaded fires while the
        // dialog is still open — an unhook there killed the toast subscription for
        // good (live-caught 2026-10-23: sign-in and sign-out both succeeded, no
        // toast ever appeared, and no later Loaded re-hooked it).
        ViewModel.SyncAuthCompleted += OnSyncAuthCompleted;
        Unloaded += (_, _) => ResultTip.Dismiss();
    }

    public SettingsViewModel ViewModel { get; }

    private void OnSyncAuthCompleted(SyncAuthOutcome outcome)
    {
        // The button state is already flipped by the VM from the operation
        // outcome (two static buttons, so no runtime restyling is needed).
        if (outcome.Success)
            ResultTip.ShowSuccess(outcome.Title, outcome.Message, TimeSpan.FromSeconds(6));
        else
            ResultTip.ShowError(outcome.Title, outcome.Message);
    }

    /// <summary>
    /// Raised when the user picks 备份/恢复/关于. The hosting dialog must close
    /// BEFORE the follow-up dialog opens (only one ContentDialog may be open at a
    /// time) — the host (NotesPage) owns that choreography.
    /// </summary>
    public event Action? BackupRequested;

    public event Action? RestoreRequested;

    public event Action? AboutRequested;

    private void Backup_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => BackupRequested?.Invoke();

    private void Restore_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => RestoreRequested?.Invoke();

    private void About_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => AboutRequested?.Invoke();

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
            ViewModel.HotkeyError = AppStrings.GetString("HotkeyNeedsModifier");
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
