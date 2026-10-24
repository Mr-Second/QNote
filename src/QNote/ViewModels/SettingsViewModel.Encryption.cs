using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace QNote.ViewModels;

public partial class SettingsViewModel
{
    private bool _refreshingEncryption;
    public event Action? EncryptionRequested;

    [ObservableProperty]
    public partial bool EncryptionEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EncryptionConfigurationEnabled))]
    public partial bool EncryptionAvailable { get; set; }

    public bool EncryptionConfigurationEnabled => SyncConfigurationEnabled && EncryptionAvailable
        && (SyncWebDavSelected || SyncSignedIn)
        && _syncConfigurationLoaded && SyncProviderIndex == (int)_savedSyncConfiguration.Provider
        && (!SyncWebDavSelected || WebDavFolderUrl.Trim().TrimEnd('/') + "/" == _savedSyncConfiguration.FolderUrl
            && WebDavUsername.Trim() == _savedSyncConfiguration.Username && WebDavPassword.Length == 0);

    public async Task RefreshEncryptionAsync()
    {
        if (_refreshingEncryption) return;
        _refreshingEncryption = true;
        EncryptionAvailable = false;
        try
        {
            var cached = await _syncService.GetCachedEncryptionStatusAsync();
            EncryptionEnabled = cached.Enabled;
            if (!cached.Destination.IsReady) return;
            var status = await _syncService.RefreshEncryptionStatusAsync();
            EncryptionEnabled = status.Enabled;
            EncryptionAvailable = status.Destination.IsReady;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not refresh the synchronization encryption state."); }
        finally { _refreshingEncryption = false; }
    }

    [RelayCommand]
    private void ConfigureEncryption()
    {
        if (EncryptionConfigurationEnabled) EncryptionRequested?.Invoke();
    }
}
