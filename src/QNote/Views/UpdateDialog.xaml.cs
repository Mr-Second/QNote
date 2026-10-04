using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QNote.Services;

namespace QNote.Views;

/// <summary>
/// The startup update dialog (1.5.1): newer version + changelog (when the
/// orchestrator could source one) + 「不再提示」 checkbox. The confirm action
/// and its target live in <see cref="StartupUpdateInfo"/> (channel-agnostic
/// UI); after ShowAsync returns, the caller reads <see cref="MuteRequested"/>
/// — checked means "turn the startup check off", regardless of which button
/// closed the dialog.
/// </summary>
public sealed partial class UpdateDialog : ContentDialog
{
    /// <summary>True when the user ticked 「不再提示」 before the dialog closed.</summary>
    public bool MuteRequested => MuteCheckBox.IsChecked == true;

    public UpdateDialog(StartupUpdateInfo info)
    {
        InitializeComponent();

        VersionText.Text = AppStrings.GetFormat("UpdateDialogVersionFormat", info.Version);

        if (string.IsNullOrWhiteSpace(info.Notes))
        {
            NotesHost.Visibility = Visibility.Collapsed;
        }
        else
        {
            NotesText.Text = info.Notes;
        }
    }
}
