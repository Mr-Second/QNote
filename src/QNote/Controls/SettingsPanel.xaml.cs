using Microsoft.UI.Xaml.Controls;
using QNote.ViewModels;

namespace QNote.Controls;

/// <summary>
/// The settings surface hosted inside the settings <see cref="ContentDialog"/>.
/// Grouped layout (显示 / 常规) — new groups append as siblings; no logic here,
/// all state lives in <see cref="SettingsViewModel"/> (changes save + apply live).
/// </summary>
public sealed partial class SettingsPanel : UserControl
{
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
}
