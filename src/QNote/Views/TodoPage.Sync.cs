using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using QNote.Services;
using QNote.Sync;

namespace QNote.Views;

public sealed partial class TodoPage
{
    private readonly SyncPollingScheduler _syncPolling = App.Services.GetRequiredService<SyncPollingScheduler>();
    private readonly SyncService _syncService = App.Services.GetRequiredService<SyncService>();
    private bool _syncSpinning;
    private bool _syncSubscribed;
    private bool _pairingPromptOpen;
    private bool _pairingPromptDismissed;
    private int _syncRefreshQueued;
    private Storyboard? _syncSpin;

    private async Task RefreshSyncButtonStateAsync()
    {
        try
        {
            var destination = await _syncService.GetDestinationAsync();
            var text = AppStrings.GetFormat("SyncNowTarget", SyncOutcomeText.DestinationDetail(destination));
            ToolTipService.SetToolTip(TodoSyncNowButton, text);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TodoSyncNowButton, text);
            TodoSyncNowButton.IsEnabled = destination.IsReady && !_syncSpinning;
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<TodoPage>>().LogWarning(ex, "Could not read the TODO sync destination.");
            TodoSyncNowButton.IsEnabled = false;
            ToolTipService.SetToolTip(TodoSyncNowButton, AppStrings.GetString("SyncConfigurationUnreadable"));
        }
    }

    private async void TodoSyncNow_Click(object sender, RoutedEventArgs e)
    {
        if (_syncSpinning) return;
        _syncSpinning = true;
        TodoSyncNowButton.IsEnabled = false;
        var animation = new DoubleAnimation { From = 0, To = 360, Duration = new Duration(TimeSpan.FromSeconds(1)) };
        Storyboard.SetTarget(animation, TodoSyncNowRotate);
        Storyboard.SetTargetProperty(animation, "Angle");
        _syncSpin = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        _syncSpin.Children.Add(animation);
        _syncSpin.Begin();
        try
        {
            await ViewModel.FlushAsync();
            if (ViewModel.HasPendingDetailEdits) return;
            var cycle = _syncPolling.TriggerNow(bypassThrottle: true);
            if (cycle is null) { ResultTip.ShowError(AppStrings.GetString("SyncNotStarted"), ""); return; }
            var result = await cycle;
            if (result?.AbortKind is SyncCycleAbortKind.E2eeKeyMismatch or SyncCycleAbortKind.EncryptionConversionRequired)
            { ((MainWindow)App.Window!).RequestEncryptionResolution(result, force: true); return; }
            if (!IsLoaded) return;
            if (result?.AbortKind == SyncCycleAbortKind.PairingRequired)
            {
                result = await ShowSyncPairingAsync(result);
                if (result is null) return;
            }
            await ViewModel.RefreshAfterSyncAsync();
            ShowSyncOutcome(result);
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<TodoPage>>().LogError(ex, "Manual TODO sync failed.");
            if (IsLoaded) ResultTip.ShowError(AppStrings.GetString("SyncToastFailed"), AppStrings.GetString("SyncAbortGeneric"));
        }
        finally
        {
            _syncSpin?.Stop(); _syncSpin = null; TodoSyncNowRotate.Angle = 0;
            _syncSpinning = false;
            if (IsLoaded) await RefreshSyncButtonStateAsync();
        }
    }

    private void OnSyncCycleCompleted(SyncCycleResult? result)
    {
        if (result is null || Interlocked.Exchange(ref _syncRefreshQueued, 1) != 0) return;
        if (DispatcherQueue.TryEnqueue(async () =>
        {
            Interlocked.Exchange(ref _syncRefreshQueued, 0);
            if (!IsLoaded || _syncSpinning) return;
            try
            {
                if (result.AbortKind == SyncCycleAbortKind.PairingRequired)
                {
                    if (_pairingPromptOpen || _pairingPromptDismissed || _settingsOpen || _dialogOpen || _dataDialogOpen) return;
                    var paired = await ShowSyncPairingAsync(result);
                    if (paired is not null) ShowSyncOutcome(paired);
                }
                else if (result.Downloaded + result.TombstonesApplied + result.ConflictCopies > 0)
                    await ViewModel.RefreshAfterSyncAsync();
            }
            catch (Exception ex)
            {
                App.Services.GetRequiredService<ILogger<TodoPage>>().LogError(ex, "Refreshing TODO data after sync failed.");
                if (IsLoaded) ResultTip.ShowError(AppStrings.GetString("SyncToastFailed"), AppStrings.GetString("SyncAbortGeneric"));
            }
        }) != true) Interlocked.Exchange(ref _syncRefreshQueued, 0);
    }

    private async Task<SyncCycleResult?> ShowSyncPairingAsync(SyncCycleResult pending)
    {
        if (_pairingPromptOpen) return null;
        _pairingPromptOpen = true;
        try
        {
            var (dialog, choices) = SyncPairingDialog.Create(this, pending.Destination);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            { _pairingPromptDismissed = true; return null; }
            // Prevent a new draft while an explicit replacement changes local ids.
            IsEnabled = false;
            try
            {
                await ViewModel.FlushAsync();
                if (ViewModel.HasPendingDetailEdits) return null;
                var choice = (SyncPairingChoice)choices.SelectedIndex;
                var result = await Task.Run(() => _syncService.RunAsync(choice,
                    pairingTarget: pending.PairingTarget));
                await ViewModel.RefreshAfterSyncAsync();
                return result;
            }
            finally { IsEnabled = true; }
        }
        finally { _pairingPromptOpen = false; }
    }

    private void ShowSyncOutcome(SyncCycleResult? result)
    {
        if (!IsLoaded) return;
        var outcome = SyncOutcomeText.Describe(result);
        if (outcome.Success) ResultTip.ShowSuccess(outcome.Title, outcome.Message);
        else ResultTip.ShowError(outcome.Title, outcome.Message);
    }
    internal void ShowEncryptionResult(bool success, string title, string message)
    {
        if (!IsLoaded) return;
        if (success) ResultTip.ShowSuccess(title, message);
        else ResultTip.ShowError(title, message);
    }

}
