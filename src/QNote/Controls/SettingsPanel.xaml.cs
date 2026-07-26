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
}
