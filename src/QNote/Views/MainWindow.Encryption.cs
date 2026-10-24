using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QNote.Services;
using QNote.Sync;

namespace QNote.Views;

public sealed partial class MainWindow
{
    private bool _encryptionUiBusy;
    private string? _dismissedEncryptionTarget;
    private SyncCycleResult? _pendingEncryption;
    private DispatcherQueueTimer? _encryptionPromptTimer;

    private void OnEncryptionCycleCompleted(SyncCycleResult? result)
    {
        if (result?.AbortKind is not (SyncCycleAbortKind.E2eeKeyMismatch or SyncCycleAbortKind.EncryptionConversionRequired)) return;
        DispatcherQueue.TryEnqueue(() => RequestEncryptionResolution(result));
    }

    public void RequestEncryptionResolution(SyncCycleResult result, bool force = false)
    {
        if (result.EncryptionTarget is null) return;
        if (force) _dismissedEncryptionTarget = null;
        if (_dismissedEncryptionTarget == result.EncryptionTarget) return;
        _pendingEncryption = result;
        QueueEncryptionPrompt();
    }

    private void QueueEncryptionPrompt()
    {
        if (_pendingEncryption?.EncryptionTarget == _dismissedEncryptionTarget)
        { _pendingEncryption = null; return; }
        if (_pendingEncryption is null || _encryptionUiBusy || !AppWindow.IsVisible) return;
        _encryptionPromptTimer ??= DispatcherQueue.CreateTimer();
        _encryptionPromptTimer.IsRepeating = false;
        _encryptionPromptTimer.Interval = TimeSpan.FromMilliseconds(500);
        _encryptionPromptTimer.Tick -= OnEncryptionPromptTick;
        _encryptionPromptTimer.Tick += OnEncryptionPromptTick;
        _encryptionPromptTimer.Start();
    }

    private async void OnEncryptionPromptTick(DispatcherQueueTimer sender, object args)
    {
        if (_pendingEncryption is not { } pending || _encryptionUiBusy || !AppWindow.IsVisible) return;
        var root = (FrameworkElement)Content;
        if (VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Count > 0)
        { QueueEncryptionPrompt(); return; }
        _pendingEncryption = null;
        _encryptionUiBusy = true;
        try
        {
            var service = App.Services.GetRequiredService<SyncService>();
            var status = await service.RefreshEncryptionStatusAsync();
            if (status.Target != pending.EncryptionTarget) return;
            if (!status.NeedsPassword && status.ConversionTo is null) return;
            var done = status.NeedsPassword
                ? await ShowEncryptionPasswordAsync(status)
                : status.ConversionTo is not null && await ShowEncryptionChangeAsync(status);
            if (done) _ = _syncPolling.TriggerNow(bypassThrottle: true);
            else { _dismissedEncryptionTarget = status.Target; _pendingEncryption = null; }
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<MainWindow>>().LogWarning(ex, "The synchronization encryption dialog could not complete.");
            ShowEncryptionResult(false, "SyncEncryptionOperationFailed");
        }
        finally { _encryptionUiBusy = false; }
    }

    public async Task ShowEncryptionSettingsAsync()
    {
        if (_encryptionUiBusy) return;
        _encryptionUiBusy = true;
        try
        {
            await FlushActivePageAsync();
            var service = App.Services.GetRequiredService<SyncService>();
            var status = await service.RefreshEncryptionStatusAsync();
            if (status.NeedsPassword)
            {
                if (!await ShowEncryptionPasswordAsync(status))
                { _dismissedEncryptionTarget = status.Target; _pendingEncryption = null; return; }
                status = await service.RefreshEncryptionStatusAsync();
            }
            if (!status.Enabled && status.ConversionTo is null && !await ShowEncryptionWarningAsync()) return;
            if (await ShowEncryptionChangeAsync(status)) _ = _syncPolling.TriggerNow(bypassThrottle: true);
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<MainWindow>>().LogWarning(ex, "Changing synchronization encryption failed.");
            ShowEncryptionResult(false, "SyncEncryptionOperationFailed");
        }
        finally { _encryptionUiBusy = false; }
    }

    private async Task<bool> ShowEncryptionWarningAsync()
    {
        var icon = new FontIconSource
        {
            Glyph = "\uE7BA",
            FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
            Foreground = Converters.HexToBrushConverter.ResolveThemeBrush("SystemFillColorCriticalBrush"),
        };
        var warning = new InfoBar
        {
            IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning,
            Message = AppStrings.GetString("SyncEncryptionPasswordWarning"), Width = 360, IconSource = icon,
        };
        warning.ActualThemeChanged += (_, _) =>
            icon.Foreground = Converters.HexToBrushConverter.ResolveThemeBrush("SystemFillColorCriticalBrush");
        AutomationProperties.SetAutomationId(warning, "SyncEncryptionPasswordWarning");
        var dialog = CreateEncryptionDialog("SyncEncryptionPasswordWarningTitle", warning, "SyncEncryptionPasswordWarningContinue");
        dialog.DefaultButton = ContentDialogButton.Close;
        using var theme = new DialogThemeScope(dialog, (FrameworkElement)Content);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<bool> ShowEncryptionPasswordAsync(SyncEncryptionStatus status)
    {
        var password = PasswordField("SyncEncryptionPassword", "SyncEncryptionPasswordLabel");
        var error = ErrorText();
        var panel = new StackPanel { Spacing = 12, Width = 360 };
        panel.Children.Add(new TextBlock { Text = AppStrings.GetString("SyncEncryptionUnlockHint"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(DestinationText(status)); panel.Children.Add(password); panel.Children.Add(error);
        var dialog = CreateEncryptionDialog("SyncEncryptionUnlockTitle", panel, "SyncEncryptionUnlockAction");
        var unlocked = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            var submittedPassword = password.Password;
            try
            {
                await Task.Run(() => App.Services.GetRequiredService<SyncService>().UnlockEncryptionAsync(submittedPassword, status.Target));
                unlocked = true; _dismissedEncryptionTarget = null; dialog.Hide();
            }
            catch (SyncEncryptionPasswordException) { error.Text = AppStrings.GetString("SyncEncryptionWrongPassword"); }
            catch (Exception ex)
            {
                App.Services.GetRequiredService<ILogger<MainWindow>>().LogWarning(ex, "Unlocking sync encryption failed.");
                error.Text = AppStrings.GetString("SyncEncryptionOperationFailed");
            }
            finally { submittedPassword = ""; password.Password = ""; dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        using var theme = new DialogThemeScope(dialog, (FrameworkElement)Content);
        await dialog.ShowAsync();
        password.Password = "";
        return unlocked;
    }

    private async Task<bool> ShowEncryptionChangeAsync(SyncEncryptionStatus status)
    {
        var enable = status.ConversionTo ?? !status.Enabled;
        var setup = enable && status.ConversionTo is null;
        var password = PasswordField("SyncEncryptionNewPassword", "SyncEncryptionPasswordLabel");
        var confirm = PasswordField("SyncEncryptionConfirmPassword", "SyncEncryptionConfirmPasswordLabel");
        var error = ErrorText();
        var panel = new StackPanel { Spacing = 12, Width = 360 };
        panel.Children.Add(DestinationText(status));
        panel.Children.Add(new TextBlock
        {
            Text = AppStrings.GetString(setup ? "SyncEncryptionSetupHint" : status.ConversionTo is not null
                ? "SyncEncryptionResumeHint" : "SyncEncryptionDisableHint"), TextWrapping = TextWrapping.Wrap,
        });
        if (setup) { panel.Children.Add(password); panel.Children.Add(confirm); }
        panel.Children.Add(error);
        var dialog = CreateEncryptionDialog(setup ? "SyncEncryptionSetupTitle" : enable ? "SyncEncryptionResumeTitle" : "SyncEncryptionDisableTitle",
            panel, status.ConversionTo is not null ? "SyncEncryptionResumeAction" : enable ? "SyncEncryptionEnableAction" : "SyncEncryptionDisableAction");
        var done = false;
        var converting = false;
        dialog.Closing += (_, args) => { if (converting && !done) args.Cancel = true; };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (converting) return;
            if (setup && (password.Password.Length < 8 || password.Password != confirm.Password))
            { error.Text = AppStrings.GetString("SyncEncryptionPasswordInvalid"); return; }
            var submittedPassword = setup ? password.Password : null;
            password.Password = confirm.Password = "";
            var deferral = args.GetDeferral();
            converting = true;
            dialog.IsPrimaryButtonEnabled = false;
            dialog.PrimaryButtonText = ""; dialog.CloseButtonText = "";
            var operation = new TextBlock { Text = AppStrings.GetString(enable ? "SyncEncryptionEncrypting" : "SyncEncryptionDecrypting"), TextWrapping = TextWrapping.Wrap };
            var bar = new ProgressBar { IsIndeterminate = true, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(bar, "SyncEncryptionProgressBar");
            AutomationProperties.SetAutomationId(operation, "SyncEncryptionProgressText");
            var counts = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center };
            var progressPanel = new StackPanel { Spacing = 20, Width = 360, Padding = new Thickness(0, 16, 0, 16) };
            progressPanel.Children.Add(operation); progressPanel.Children.Add(bar); progressPanel.Children.Add(counts);
            AutomationProperties.SetAutomationId(progressPanel, "SyncEncryptionProgress");
            dialog.Content = progressPanel;
            var progress = new Progress<SyncEncryptionProgress>(value =>
            {
                bar.IsIndeterminate = value.Total == 0;
                if (value.Total > 0) { bar.Maximum = value.Total; bar.Value = value.Completed; }
                counts.Text = AppStrings.GetFormat("SyncEncryptionProgressCount", value.Completed, value.Total);
            });
            try
            {
                await FlushActivePageAsync();
                await Task.Run(() => App.Services.GetRequiredService<SyncService>().ChangeEncryptionAsync(enable, submittedPassword, status.Target, progress));
                done = true; _dismissedEncryptionTarget = null; dialog.Hide();
            }
            catch (Exception ex)
            {
                App.Services.GetRequiredService<ILogger<MainWindow>>().LogWarning(ex, "The cloud encryption conversion did not finish.");
                bar.IsIndeterminate = false;
                operation.Text = AppStrings.GetString("SyncEncryptionOperationFailed");
                dialog.CloseButtonText = AppStrings.GetString("CloseButton");
            }
            finally { submittedPassword = null; converting = false; deferral.Complete(); }
        };
        using var theme = new DialogThemeScope(dialog, (FrameworkElement)Content);
        await dialog.ShowAsync();
        password.Password = confirm.Password = "";
        if (done) ShowEncryptionResult(true, enable ? "SyncEncryptionEnabled" : "SyncEncryptionDisabled");
        return done;
    }

    private ContentDialog CreateEncryptionDialog(string title, object content, string action)
    {
        var root = (FrameworkElement)Content;
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, Title = AppStrings.GetString(title), Content = content,
            PrimaryButtonText = AppStrings.GetString(action), CloseButtonText = AppStrings.GetString("CancelButton"),
            DefaultButton = ContentDialogButton.Primary,
        };
        AutomationProperties.SetAutomationId(dialog, "SyncEncryptionDialog");
        return dialog;
    }

    private static PasswordBox PasswordField(string id, string header)
    {
        var field = new PasswordBox { Header = AppStrings.GetString(header) };
        AutomationProperties.SetAutomationId(field, id);
        AutomationProperties.SetName(field, AppStrings.GetString(header));
        return field;
    }

    private static TextBlock ErrorText() => new() { TextWrapping = TextWrapping.Wrap,
        Foreground = Converters.HexToBrushConverter.ResolveThemeBrush("SystemFillColorCriticalBrush") };

    private static TextBlock DestinationText(SyncEncryptionStatus status) => new()
    { Text = SyncOutcomeText.DestinationDetail(status.Destination), TextWrapping = TextWrapping.Wrap };

    private void ShowEncryptionResult(bool success, string messageKey)
    {
        var title = AppStrings.GetString(success ? "SyncEncryptionCompletedTitle" : "SettingsOperationFailedTitle");
        var message = AppStrings.GetString(messageKey);
        if (RootFrame.Content is NotesPage notes) notes.ShowEncryptionResult(success, title, message);
        else if (RootFrame.Content is TodoPage todo) todo.ShowEncryptionResult(success, title, message);
    }
}
