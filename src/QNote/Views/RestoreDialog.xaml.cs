using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QNote.Models;
using QNote.Services;
using Windows.Storage.Pickers;

namespace QNote.Views;

/// <summary>An auto-backup entry for the restore dialog's one-click list (file + display name).</summary>
public sealed record AutoBackupEntry(string Path, string Display);

/// <summary>
/// Restore dialog (PRD D5): pick file → (password if encrypted) → conflict analysis
/// (冲突 / 仅备份 / 仅当前 three counts) → mode (覆盖 / 合并 / 仅导入新增) → run.
/// <see cref="RestoreCompleted"/> tells the caller to reload the whole notes UI —
/// an overwrite restore swaps the database file beneath every cached list.
/// Mid-flow failures show a floating toast inside the dialog; a final outcome
/// (success or failure) closes the dialog and is handed to the caller via
/// <see cref="FinalResult"/> for a page-level toast.
/// </summary>
public sealed partial class RestoreDialog : ContentDialog
{
    private readonly IBackupService _backup;
    private string? _archivePath;
    private bool _encrypted;
    private bool _running;

    public RestoreDialog(IBackupService backup)
    {
        _backup = backup;
        InitializeComponent();
        // Closing mid-run is dangerous here: an overwrite restore swaps the DB file
        // in the background while the freed UI would keep editing the OLD file.
        Closing += (_, args) => { if (_running) args.Cancel = true; };
        Closed += (_, _) => ResultTip.Dismiss();
        LoadAutoBackups();
    }

    /// <summary>True when a restore ran to completion; the caller must reload its data.</summary>
    public bool RestoreCompleted { get; private set; }

    /// <summary>Set when the restore run produced a final outcome; the caller shows it as a toast.</summary>
    public OperationResult? FinalResult { get; private set; }

    private async void Pick_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(IBackupService.ArchiveExtension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        if (await picker.PickSingleFileAsync() is not { } file)
            return;

        await SelectArchiveAsync(file.Path);
    }

    // Shared by the file picker and the auto-backup list: sets the archive, checks
    // encryption, and reveals the analysis step. Failures surface as in-dialog toasts.
    private async Task SelectArchiveAsync(string path)
    {
        _archivePath = path;
        FilePathText.Text = path;
        ResetAnalysis();

        try
        {
            _encrypted = await _backup.IsEncryptedAsync(path);
            PasswordBox.Visibility = _encrypted ? Visibility.Visible : Visibility.Collapsed;
            AnalyzeButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ResultTip.ShowError("无法读取备份", BackupErrorText.Describe(ex));
        }
    }

    // Auto-backup entry (D4 safety net): lists backups\auto-backup-*.qns newest
    // first; the display name parses the timestamp out of the file name.
    private void LoadAutoBackups()
    {
        var backups = _backup.ListAutoBackups();
        if (backups.Count == 0)
            return;

        AutoBackupsList.ItemsSource = backups.Select(p => new AutoBackupEntry(p, FormatAutoBackupName(p))).ToList();
        AutoBackupsPanel.Visibility = Visibility.Visible;
    }

    private static string FormatAutoBackupName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        const string prefix = "auto-backup-";
        if (name.StartsWith(prefix, StringComparison.Ordinal)
            && DateTime.TryParseExact(
                name[(prefix.Length)..], "yyyyMMdd-HHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var created))
        {
            return created.ToString("yyyy-MM-dd HH:mm:ss");
        }
        return name;
    }

    private async void AutoBackup_Click(object sender, ItemClickEventArgs e)
    {
        if (_running || e.ClickedItem is not AutoBackupEntry entry)
            return;

        await SelectArchiveAsync(entry.Path);
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (_archivePath is null || _running)
            return;

        _running = true;
        SetBusy(true);
        try
        {
            var analysis = await _backup.AnalyzeAsync(_archivePath, CurrentPassword());
            CountsText.Text =
                $"备份包含 {analysis.BackupNoteCount} 条便签" +
                (analysis.BackupCreatedAt is { } created ? $"（创建于 {created.ToLocalTime():yyyy-MM-dd HH:mm}）" : "") +
                $"：与当前冲突 {analysis.ConflictCount} 条，仅备份中存在 {analysis.NewCount} 条，仅当前存在 {analysis.CurrentOnlyCount} 条。";
            AnalysisPanel.Visibility = Visibility.Visible;
            IsPrimaryButtonEnabled = true;
            DefaultButton = ContentDialogButton.Primary;
        }
        catch (Exception ex)
        {
            ResultTip.ShowError("分析失败", BackupErrorText.Describe(ex));
        }
        finally
        {
            _running = false;
            SetBusy(false);
        }
    }

    private void ModeRadios_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        OverwriteWarning.Visibility = ModeRadios.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Run the restore instead of closing (same deferral pattern as BackupDialog).
        args.Cancel = true;
        if (_running || _archivePath is null)
            return;

        var mode = ModeRadios.SelectedIndex switch
        {
            0 => RestoreMode.Overwrite,
            2 => RestoreMode.ImportNewOnly,
            _ => RestoreMode.Merge,
        };

        var deferral = args.GetDeferral();
        _running = true;
        SetBusy(true);
        var progress = new Progress<double>(p => WorkingBar.Value = p);
        try
        {
            await _backup.RestoreAsync(_archivePath, mode, CurrentPassword(), progress);
            RestoreCompleted = true;
            FinalResult = new OperationResult(true, "恢复成功", "备份数据已恢复。");
        }
        catch (Exception ex)
        {
            FinalResult = new OperationResult(false, "恢复失败", BackupErrorText.Describe(ex));
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

    private string? CurrentPassword() =>
        _encrypted && PasswordBox.Password.Length > 0 ? PasswordBox.Password : null;

    private void ResetAnalysis()
    {
        AnalysisPanel.Visibility = Visibility.Collapsed;
        IsPrimaryButtonEnabled = false;
        DefaultButton = ContentDialogButton.None;
        ResultTip.Dismiss();
    }

    private void SetBusy(bool busy)
    {
        IsPrimaryButtonEnabled = !busy && AnalysisPanel.Visibility == Visibility.Visible;
        PickButton.IsEnabled = !busy;
        AnalyzeButton.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        ModeRadios.IsEnabled = !busy;
        WorkingBar.Value = 0;
        WorkingBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
            ResultTip.Dismiss();
    }
}
