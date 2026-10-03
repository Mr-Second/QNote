using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QNote.Services;
using Windows.Storage.Pickers;

namespace QNote.Views;

/// <summary>
/// Backup dialog (PRD D5): destination path + optional password, then a guarded
/// async run. Pre-run validation failures show a floating toast inside the
/// dialog; a final outcome (success or failure) closes the dialog and is handed
/// to the caller via <see cref="FinalResult"/> for a page-level toast.
/// </summary>
public sealed partial class BackupDialog : ContentDialog
{
    private readonly IBackupService _backup;
    private bool _running;

    public BackupDialog(IBackupService backup)
    {
        _backup = backup;
        InitializeComponent();
        PathBox.Text = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            _backup.SuggestedBackupFileName());
        // Closing mid-run would free the UI while the backup continues in the
        // background — block dismissal until it finishes.
        Closing += (_, args) => { if (_running) args.Cancel = true; };
        Closed += (_, _) => ResultTip.Dismiss();
    }

    /// <summary>Set when the backup run produced a final outcome; the caller shows it as a toast.</summary>
    public OperationResult? FinalResult { get; private set; }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = _backup.SuggestedBackupFileName(),
        };
        picker.FileTypeChoices.Add(AppStrings.GetString("BackupFileTypeLabel"), new[] { IBackupService.ArchiveExtension });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        if (await picker.PickSaveFileAsync() is { } file)
            PathBox.Text = file.Path;
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Run the backup instead of closing; Hide() happens only on success+close.
        args.Cancel = true;
        if (_running)
            return;

        var path = PathBox.Text.Trim();
        if (path.Length == 0)
        {
            ResultTip.ShowError(AppStrings.GetString("BackupCannotStart"), AppStrings.GetString("BackupNoPath"));
            return;
        }
        if (PasswordBox.Password != ConfirmBox.Password)
        {
            ResultTip.ShowError(AppStrings.GetString("BackupCannotStart"), AppStrings.GetString("BackupPasswordMismatch"));
            return;
        }

        var deferral = args.GetDeferral();
        _running = true;
        SetBusy(true);
        try
        {
            await _backup.BackupAsync(path,
                password: PasswordBox.Password.Length > 0 ? PasswordBox.Password : null);

            FinalResult = new OperationResult(true,
                AppStrings.GetString("BackupSuccessTitle"),
                AppStrings.GetFormat("BackupSuccessPathFormat", path));
        }
        catch (Exception ex)
        {
            FinalResult = new OperationResult(false,
                AppStrings.GetString("BackupFailedTitle"), BackupErrorText.Describe(ex));
        }
        finally
        {
            _running = false;
            SetBusy(false);
            deferral.Complete();
        }

        // Final outcome reached: close and let the caller surface the toast on the page.
        Hide();
    }

    private void SetBusy(bool busy)
    {
        IsPrimaryButtonEnabled = !busy;
        PathBox.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        ConfirmBox.IsEnabled = !busy;
        WorkingBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
            ResultTip.Dismiss();
    }
}
