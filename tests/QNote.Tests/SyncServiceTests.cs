using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using QNote.Data;
using QNote.Models;
using QNote.Services;
using QNote.Sync;
using QNote.ViewModels;

namespace QNote.Tests;

public sealed class SyncServiceTests
{
    [Fact]
    public async Task SettingsProviderCheck_FollowsSuccessfulSaveAndClear_NotDraftSelection()
    {
        using var db = new TestDatabase();
        var store = new SyncConfigurationStore(db.Paths);
        await store.SaveAsync(WebDav() with { Provider = SyncProvider.OneDrive });
        var tokens = new TestTokens();
        using var sync = Service(db, store, tokens, _ => new FakeSyncDriver());
        var ports = new SettingsPorts();
        var vm = new SettingsViewModel(db.NewSettingsService(), ports, ports, ports, tokens, sync,
            isPackaged: true, NullLogger<SettingsViewModel>.Instance);
        await vm.InitializeAsync();
        Assert.Equal("SyncActiveProviderOption", vm.SyncOneDriveOptionText);
        Assert.Equal("WebDAV", vm.SyncWebDavOptionText);
        var changed = new List<string?>();
        var outcomes = new List<SettingsOperationOutcome>();
        vm.OperationCompleted += outcomes.Add;
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SyncProviderIndex = 1;
        Assert.Equal("SyncActiveProviderOption", vm.SyncOneDriveOptionText);
        Assert.Equal("WebDAV", vm.SyncWebDavOptionText);
        vm.WebDavFolderUrl = "ftp://invalid.example.com/";
        await vm.SaveSyncConfigurationCommand.ExecuteAsync(null);
        Assert.Equal(SyncProvider.OneDrive, (await store.LoadAsync()).Provider);
        Assert.Equal("SyncActiveProviderOption", vm.SyncOneDriveOptionText);
        vm.WebDavFolderUrl = WebDav().FolderUrl;
        await vm.SaveSyncConfigurationCommand.ExecuteAsync(null);

        Assert.Equal(SyncProvider.WebDav, (await store.LoadAsync()).Provider);
        Assert.Equal("OneDrive", vm.SyncOneDriveOptionText);
        Assert.Equal("SyncActiveProviderOption", vm.SyncWebDavOptionText);
        Assert.Contains(nameof(vm.SyncOneDriveOptionText), changed);
        Assert.Contains(nameof(vm.SyncWebDavOptionText), changed);

        vm.SyncProviderIndex = 0;
        Assert.Equal("SyncActiveProviderOption", vm.SyncWebDavOptionText);
        await vm.SaveSyncConfigurationCommand.ExecuteAsync(null);
        Assert.Equal("SyncActiveProviderOption", vm.SyncOneDriveOptionText);
        Assert.Equal(WebDav(), (await store.LoadAsync()) with { Provider = SyncProvider.WebDav });

        vm.SyncProviderIndex = 1;
        await vm.ForgetWebDavCommand.ExecuteAsync(null);
        Assert.Equal(new SyncConfiguration(), await store.LoadAsync());
        Assert.Equal("SyncActiveProviderOption", vm.SyncOneDriveOptionText);
        Assert.Equal("WebDAV", vm.SyncWebDavOptionText);
        Assert.Equal(0, vm.SyncProviderIndex);
        Assert.Equal(new[] { false, true, true, true }, outcomes.Select(o => o.Success));
        Assert.Equal("SettingsOperationFailedTitle", outcomes[0].Title);
        Assert.Equal("SettingsSavedTitle", outcomes[1].Title);
        Assert.Equal("SettingsClearedTitle", outcomes[^1].Title);
    }

    [Fact]
    public async Task CompletedDestination_DoesNotFollowAQueuedProviderSwitch()
    {
        using var db = new TestDatabase();
        var store = new SyncConfigurationStore(db.Paths);
        var saved = WebDav();
        await store.SaveAsync(saved);
        var cloud = new PausedDriver();
        var tokens = new TestTokens();
        using var sync = Service(db, store, tokens, _ => cloud);

        var cycle = sync.RunAsync();
        await cloud.Started.Task;
        var change = sync.SaveConfigurationAsync(saved with { Provider = SyncProvider.OneDrive });
        Assert.False(change.IsCompleted);
        cloud.Release.SetResult();
        var completed = await cycle;
        await change;

        Assert.Equal(new SyncDestination("WebDAV", saved.Username, saved.FolderUrl), completed!.Destination);
        Assert.DoesNotContain(saved.Password, completed.Destination!.ToString());
        var current = await sync.GetDestinationAsync();
        Assert.Equal("OneDrive", current.Provider);
        Assert.Equal(tokens.Account!.Username, current.Account);
        Assert.Null(current.FolderUrl);
        Assert.Equal(saved, (await store.LoadAsync()) with { Provider = SyncProvider.WebDav });
        Assert.Equal("WebDAV", completed.Destination.Provider);
    }

    [Fact]
    public async Task PairingAndAbortedResults_KeepDestinationDetails()
    {
        using var db = new TestDatabase();
        await db.NewNoteService().CreateAsync();
        var store = new SyncConfigurationStore(db.Paths);
        await store.SaveAsync(WebDav());
        var cloud = new FakeSyncDriver();
        SeedCloud(cloud);
        using var sync = Service(db, store, new TestTokens(), _ => cloud);

        var pairing = await sync.RunAsync();
        Assert.Equal(SyncCycleAbortKind.PairingRequired, pairing!.AbortKind);
        Assert.Equal(WebDav().FolderUrl, pairing.Destination!.FolderUrl);

        cloud.Truncated = true;
        var aborted = await sync.RunAsync();
        Assert.Equal(SyncCycleAbortKind.ListingIncomplete, aborted!.AbortKind);
        Assert.Equal(pairing.Destination, aborted.Destination);
    }

    [Fact]
    public async Task DestinationReadiness_UsesAccountPresence_NotDisplayUsername()
    {
        using var db = new TestDatabase();
        var store = new SyncConfigurationStore(db.Paths);
        var tokens = new TestTokens { Account = new AccountInfo("", "account-id") };
        using var sync = Service(db, store, tokens, _ => new FakeSyncDriver());

        Assert.True((await sync.GetDestinationAsync()).IsReady);
        tokens.Account = null;
        var signedOut = await sync.GetDestinationAsync();
        Assert.False(signedOut.IsReady);
        Assert.Equal("OneDrive", signedOut.Provider);
    }

    [Theory]
    [InlineData("https://dav.example.com/sync/")]
    [InlineData("http://nas.local:5005/sync/")]
    public async Task Configuration_PersistsAcrossInstances_AsDpapiCiphertext(string folderUrl)
    {
        using var db = new TestDatabase();
        var configuration = WebDav() with { FolderUrl = folderUrl };
        await new SyncConfigurationStore(db.Paths).SaveAsync(configuration);

        var stored = await new SyncConfigurationStore(db.Paths).LoadAsync();
        var ciphertext = await File.ReadAllBytesAsync(Path.Combine(db.Paths.Root, "sync-configuration.bin"));

        Assert.Equal(configuration, stored);
        Assert.DoesNotContain(configuration.Password, Encoding.UTF8.GetString(ciphertext));
        Assert.DoesNotContain(configuration.Username, Encoding.UTF8.GetString(ciphertext));
        Assert.DoesNotContain(configuration.Password, configuration.ToString());
        Assert.Null(await db.NewSettingsService().GetAsync("password"));
        await using var conn = db.Factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM settings WHERE Value LIKE $secret;";
        cmd.Parameters.AddWithValue("$secret", "%" + configuration.Password + "%");
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());

        var ports = new SettingsPorts();
        using var sync = Service(db, new SyncConfigurationStore(db.Paths), new TestTokens(), _ => new FakeSyncDriver());
        var vm = new SettingsViewModel(db.NewSettingsService(), ports, ports, ports, new TestTokens(), sync,
            isPackaged: true, NullLogger<SettingsViewModel>.Instance);
        await vm.InitializeAsync();
        Assert.Equal(folderUrl.StartsWith("http:", StringComparison.Ordinal), vm.ShowWebDavHttpWarning);
    }

    [Theory]
    [InlineData("http://nas.local:5005/sync/", true)]
    [InlineData(" HTTP://NAS.local:5005/sync/ ", true)]
    [InlineData("https://nas.local:5006/sync/", false)]
    [InlineData("ftp://nas.local/sync/", false)]
    [InlineData("not an address", false)]
    public void HttpNotice_FollowsDraftUrlAndProvider(string address, bool expected)
    {
        using var db = new TestDatabase();
        var ports = new SettingsPorts();
        var tokens = new TestTokens();
        using var sync = Service(db, new SyncConfigurationStore(db.Paths), tokens, _ => new FakeSyncDriver());
        var vm = new SettingsViewModel(db.NewSettingsService(), ports, ports, ports, tokens, sync,
            isPackaged: true, NullLogger<SettingsViewModel>.Instance) { SyncProviderIndex = 1 };
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        vm.WebDavFolderUrl = address;

        Assert.Equal(expected, vm.ShowWebDavHttpWarning);
        Assert.Contains(nameof(vm.ShowWebDavHttpWarning), notifications);
        vm.SyncProviderIndex = 0;
        Assert.False(vm.ShowWebDavHttpWarning);
        vm.SyncProviderIndex = 1;
        vm.WebDavFolderUrl = "https://nas.local/sync/";
        Assert.False(vm.ShowWebDavHttpWarning);
    }

    [Theory]
    [InlineData("ftp://invalid.example.com/QNote/", "synthetic-app-secret")]
    [InlineData("http://nas.local:5005/QNote/", "")]
    public async Task ConnectionTest_InvalidDraft_UsesOneToastWithoutSavingOrInlineFeedback(string address, string password)
    {
        using var db = new TestDatabase();
        var store = new SyncConfigurationStore(db.Paths);
        var saved = WebDav() with { Provider = SyncProvider.OneDrive };
        await store.SaveAsync(saved);
        var tokens = new TestTokens();
        var ports = new SettingsPorts();
        using var sync = Service(db, store, tokens, _ => new FakeSyncDriver());
        var vm = new SettingsViewModel(db.NewSettingsService(), ports, ports, ports, tokens, sync,
            isPackaged: true, NullLogger<SettingsViewModel>.Instance);
        await vm.InitializeAsync();
        var signedIn = vm.SyncSignedIn;
        vm.SyncProviderIndex = 1;
        vm.WebDavFolderUrl = address;
        vm.WebDavUsername = "test-user";
        vm.WebDavPassword = password;
        var toasts = new List<SettingsOperationOutcome>();
        vm.OperationCompleted += outcome =>
        {
            Assert.False(vm.SyncConfigurationBusy);
            toasts.Add(outcome);
        };

        await vm.TestWebDavCommand.ExecuteAsync(null);

        var toast = Assert.Single(toasts);
        Assert.False(toast.Success);
        Assert.Equal("SyncWebDavTestFailedTitle", toast.Title);
        Assert.Equal("SyncWebDavInvalid", toast.Message);
        Assert.False(vm.SyncConfigurationBusy);
        Assert.Equal(saved, await store.LoadAsync());
        Assert.Equal(signedIn, vm.SyncSignedIn);
        Assert.Equal(address.StartsWith("http:", StringComparison.Ordinal), vm.ShowWebDavHttpWarning);
    }

    [Fact]
    public async Task ConnectionTest_WhileConfigurationBusy_DoesNotEmitOrAlterOtherFeedback()
    {
        using var db = new TestDatabase();
        var ports = new SettingsPorts();
        var tokens = new TestTokens();
        using var sync = Service(db, new SyncConfigurationStore(db.Paths), tokens, _ => new FakeSyncDriver());
        var vm = new SettingsViewModel(db.NewSettingsService(), ports, ports, ports, tokens, sync,
            isPackaged: true, NullLogger<SettingsViewModel>.Instance)
        {
            SyncConfigurationBusy = true,
        };
        var count = 0;
        vm.OperationCompleted += _ => count++;

        await vm.TestWebDavCommand.ExecuteAsync(null);

        Assert.Equal(0, count);
        Assert.True(vm.SyncConfigurationBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAndClear_StorageFailure_ToastAndKeepSavedProvider(bool clear)
    {
        using var db = new TestDatabase();
        var store = new SyncConfigurationStore(db.Paths);
        var original = WebDav() with { Provider = clear ? SyncProvider.WebDav : SyncProvider.OneDrive };
        await store.SaveAsync(original);
        var tokens = new TestTokens();
        var ports = new SettingsPorts();
        using var sync = Service(db, store, tokens, _ => new FakeSyncDriver());
        var vm = new SettingsViewModel(db.NewSettingsService(), ports, ports, ports, tokens, sync,
            isPackaged: true, NullLogger<SettingsViewModel>.Instance);
        await vm.InitializeAsync();
        vm.SyncProviderIndex = 1;
        var outcomes = new List<SettingsOperationOutcome>();
        vm.OperationCompleted += outcomes.Add;

        using (File.Open(Path.Combine(db.Paths.Root, "sync-configuration.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            if (clear) await vm.ForgetWebDavCommand.ExecuteAsync(null);
            else await vm.SaveSyncConfigurationCommand.ExecuteAsync(null);
        }

        var outcome = Assert.Single(outcomes);
        Assert.False(outcome.Success);
        Assert.Equal("SyncConfigurationSaveFailed", outcome.Message);
        Assert.Equal(original, await store.LoadAsync());
        Assert.Equal(clear ? "SyncActiveProviderOption" : "WebDAV", vm.SyncWebDavOptionText);
        Assert.False(vm.SyncConfigurationBusy);
    }

    [Fact]
    public async Task UnreadableConfiguration_ReportsOpeningFailureThroughTheSameEvent()
    {
        using var db = new TestDatabase();
        Directory.CreateDirectory(db.Paths.Root);
        await File.WriteAllBytesAsync(Path.Combine(db.Paths.Root, "sync-configuration.bin"), [1, 2, 3]);
        var ports = new SettingsPorts();
        var tokens = new TestTokens();
        using var sync = Service(db, new SyncConfigurationStore(db.Paths), tokens, _ => new FakeSyncDriver());
        var vm = new SettingsViewModel(db.NewSettingsService(), ports, ports, ports, tokens, sync,
            isPackaged: true, NullLogger<SettingsViewModel>.Instance);
        var outcomes = new List<SettingsOperationOutcome>();
        vm.OperationCompleted += outcomes.Add;

        await vm.InitializeAsync();

        Assert.Equal("SyncConfigurationUnreadable", Assert.Single(outcomes).Message);
        Assert.Equal("OneDrive", vm.SyncOneDriveOptionText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthFailure_UsesToastAndPreservesAccountState(bool signedIn)
    {
        using var db = new TestDatabase();
        var ports = new SettingsPorts();
        var tokens = new TestTokens { Account = signedIn ? new("user", "id") : null };
        using var sync = Service(db, new SyncConfigurationStore(db.Paths), tokens, _ => new FakeSyncDriver());
        var vm = new SettingsViewModel(db.NewSettingsService(), ports, ports, ports, tokens, sync,
            isPackaged: true, NullLogger<SettingsViewModel>.Instance);
        await vm.InitializeAsync();
        tokens.AuthFailure = new TokenProviderException(TokenProviderErrorKind.AuthRequired, "synthetic-secret");
        var outcomes = new List<SettingsOperationOutcome>();
        vm.OperationCompleted += outcomes.Add;

        await vm.SyncAuthCommand.ExecuteAsync(null);

        var outcome = Assert.Single(outcomes);
        Assert.False(outcome.Success);
        Assert.Equal(signedIn ? "SyncSignOutError" : "SyncSignInError", outcome.Message);
        Assert.DoesNotContain("synthetic-secret", outcome.Message);
        Assert.Equal(signedIn, vm.SyncSignedIn);
    }

    [Fact]
    public async Task AuthCancellation_IsSilent_AndSuccessReportsOnce()
    {
        using var db = new TestDatabase();
        var ports = new SettingsPorts();
        var tokens = new TestTokens { Account = null, AuthFailure = new OperationCanceledException() };
        using var sync = Service(db, new SyncConfigurationStore(db.Paths), tokens, _ => new FakeSyncDriver());
        var vm = new SettingsViewModel(db.NewSettingsService(), ports, ports, ports, tokens, sync,
            isPackaged: true, NullLogger<SettingsViewModel>.Instance);
        await vm.InitializeAsync();
        var outcomes = new List<SettingsOperationOutcome>();
        vm.OperationCompleted += outcomes.Add;

        await vm.SyncAuthCommand.ExecuteAsync(null);
        Assert.Empty(outcomes);
        Assert.False(vm.SyncSignedIn);
        tokens.AuthFailure = null;
        await vm.SyncAuthCommand.ExecuteAsync(null);
        Assert.Equal("SyncSignInSuccess", Assert.Single(outcomes).Title);
        Assert.True(vm.SyncSignedIn);
        outcomes.Clear();
        await vm.SyncAuthCommand.ExecuteAsync(null);
        Assert.Equal("SyncSignOutSuccess", Assert.Single(outcomes).Title);
        Assert.False(vm.SyncSignedIn);
    }

    [Fact]
    public async Task AccountReadFailure_UsesToastWithoutErasingKnownSignedInState()
    {
        using var db = new TestDatabase();
        var ports = new SettingsPorts();
        var tokens = new TestTokens();
        using var sync = Service(db, new SyncConfigurationStore(db.Paths), tokens, _ => new FakeSyncDriver());
        var vm = new SettingsViewModel(db.NewSettingsService(), ports, ports, ports, tokens, sync,
            isPackaged: true, NullLogger<SettingsViewModel>.Instance);
        await vm.InitializeAsync();
        var outcomes = new List<SettingsOperationOutcome>();
        vm.OperationCompleted += outcomes.Add;
        tokens.ProbeFailure = new IOException("synthetic-private-detail");

        await vm.RefreshSyncAccountAsync();

        Assert.Equal("SyncAccountReadFailed", Assert.Single(outcomes).Message);
        Assert.True(vm.SyncSignedIn);
    }

    [Fact]
    public async Task ImmediatePreferenceSaveFailure_ReportsThroughSettingsToast()
    {
        using var db = new TestDatabase();
        var ports = new SettingsPorts();
        var tokens = new TestTokens();
        using var sync = Service(db, new SyncConfigurationStore(db.Paths), tokens, _ => new FakeSyncDriver());
        var vm = new SettingsViewModel(db.NewSettingsService(), ports, ports, ports, tokens, sync,
            isPackaged: true, NullLogger<SettingsViewModel>.Instance);
        await vm.InitializeAsync();
        var outcomes = new List<SettingsOperationOutcome>();
        vm.OperationCompleted += outcomes.Add;
        string path;
        using (var connection = db.Factory.OpenRead()) path = connection.DataSource;

        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            vm.AlwaysOnTop = !vm.AlwaysOnTop;

        Assert.Equal("SettingsSaveFailed", Assert.Single(outcomes).Message);
    }

    [Fact]
    public async Task StartupDenied_ReportsActionFailureAndRetainsPersistentOsExplanation()
    {
        using var db = new TestDatabase();
        var ports = new SettingsPorts { EnableResult = StartupTaskStatus.DisabledByUser };
        var tokens = new TestTokens();
        using var sync = Service(db, new SyncConfigurationStore(db.Paths), tokens, _ => new FakeSyncDriver());
        var vm = new SettingsViewModel(db.NewSettingsService(), ports, ports, ports, tokens, sync,
            isPackaged: true, NullLogger<SettingsViewModel>.Instance);
        await vm.InitializeAsync();
        var outcome = new TaskCompletionSource<SettingsOperationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.OperationCompleted += result => outcome.TrySetResult(result);

        vm.LaunchAtStartup = true;

        var failed = await outcome.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(failed.Success);
        Assert.Equal("StartupDisabledByUser", failed.Message);
        Assert.Equal(failed.Message, vm.LaunchAtStartupHint);
        Assert.False(vm.LaunchAtStartup);
    }

    [Fact]
    public async Task StartupOperationException_UsesToastAndReconcilesTheToggle()
    {
        using var db = new TestDatabase();
        var ports = new SettingsPorts { StartupFailure = new IOException("synthetic-private-detail") };
        var tokens = new TestTokens();
        using var sync = Service(db, new SyncConfigurationStore(db.Paths), tokens, _ => new FakeSyncDriver());
        var vm = new SettingsViewModel(db.NewSettingsService(), ports, ports, ports, tokens, sync,
            isPackaged: true, NullLogger<SettingsViewModel>.Instance);
        await vm.InitializeAsync();
        var completed = new TaskCompletionSource<SettingsOperationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.OperationCompleted += outcome => completed.TrySetResult(outcome);

        vm.LaunchAtStartup = true;

        Assert.Equal("StartupOperationFailed", (await completed.Task.WaitAsync(TimeSpan.FromSeconds(10))).Message);
        Assert.False(vm.LaunchAtStartup);
        Assert.Equal("", vm.LaunchAtStartupHint);
    }

    [Fact]
    public async Task HotkeyValidationRegistrationAndClear_UseTheSameOutcomeEvent()
    {
        using var db = new TestDatabase();
        var ports = new SettingsPorts();
        var tokens = new TestTokens();
        using var sync = Service(db, new SyncConfigurationStore(db.Paths), tokens, _ => new FakeSyncDriver());
        var vm = new SettingsViewModel(db.NewSettingsService(), ports, ports, ports, tokens, sync,
            isPackaged: true, NullLogger<SettingsViewModel>.Instance);
        await vm.InitializeAsync();
        var outcomes = new List<SettingsOperationOutcome>();
        vm.OperationCompleted += outcomes.Add;

        vm.BeginHotkeyCapture();
        vm.RejectHotkeyCapture();
        vm.CancelHotkeyCapture();
        Assert.Equal("HotkeyNeedsModifier", Assert.Single(outcomes).Message);
        ports.RegisterSucceeds = false;
        await vm.CommitHotkeyAsync(2, 65);
        Assert.Equal("HotkeyRegisterFailed", outcomes[^1].Message);
        ports.RegisterSucceeds = true;
        await vm.ClearHotkeyAsync();
        Assert.True(outcomes[^1].Success);
        Assert.Equal("HotkeyNotSet", vm.HotkeyDisplay);
        Assert.Equal(0, (await db.NewSettingsService().LoadAsync()).EdgeHideHotkeyKey);
        Assert.Equal(3, outcomes.Count);
    }

    [Theory]
    [InlineData(UpdateCheckStatus.UpToDate, true)]
    [InlineData(UpdateCheckStatus.Failed, false)]
    [InlineData(UpdateCheckStatus.UpdateAvailable, true)]
    public async Task UpdateCheck_OnlyDownloadDecisionUsesTheModalEvent(UpdateCheckStatus status, bool success)
    {
        using var db = new TestDatabase();
        var ports = new SettingsPorts { UpdateResult = new(status, "https://example.com/release", "2.0", "synthetic-secret") };
        var tokens = new TestTokens();
        using var sync = Service(db, new SyncConfigurationStore(db.Paths), tokens, _ => new FakeSyncDriver());
        var vm = new SettingsViewModel(db.NewSettingsService(), ports, ports, ports, tokens, sync,
            isPackaged: false, NullLogger<SettingsViewModel>.Instance);
        var outcomes = new List<SettingsOperationOutcome>();
        var decisions = new List<UpdateCheckResult>();
        vm.OperationCompleted += outcomes.Add;
        vm.UpdateCheckCompleted += decisions.Add;

        await vm.CheckUpdateCommand.ExecuteAsync(null);

        if (status == UpdateCheckStatus.UpdateAvailable)
        {
            Assert.Empty(outcomes);
            Assert.Single(decisions);
        }
        else
        {
            Assert.Empty(decisions);
            Assert.Equal(success, Assert.Single(outcomes).Success);
            Assert.DoesNotContain("synthetic-secret", outcomes[0].Message);
        }
        Assert.Equal("CheckUpdateButton", vm.UpdateCheckButtonText);
    }

    [Fact]
    public async Task CorruptConfiguration_DoesNotSilentlyFallBackToAnotherProvider()
    {
        using var db = new TestDatabase();
        Directory.CreateDirectory(db.Paths.Root);
        await File.WriteAllBytesAsync(Path.Combine(db.Paths.Root, "sync-configuration.bin"), [1, 2, 3]);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => new SyncConfigurationStore(db.Paths).LoadAsync());
    }

    [Fact]
    public async Task InvalidConfiguration_DoesNotReplacePreviouslySavedCredentials()
    {
        using var db = new TestDatabase();
        var store = new SyncConfigurationStore(db.Paths);
        await store.SaveAsync(WebDav());
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(WebDav() with { FolderUrl = "ftp://invalid.example.com/" }));
        Assert.Equal(WebDav(), await store.LoadAsync());
    }

    [Fact]
    public async Task WebDavReadiness_DoesNotRequireOrProbeMicrosoftAccount()
    {
        using var db = new TestDatabase();
        var store = new SyncConfigurationStore(db.Paths);
        await store.SaveAsync(WebDav());
        var tokens = new TestTokens { Account = null };
        using var sync = Service(db, store, tokens, _ => new FakeSyncDriver());

        Assert.True(await sync.IsReadyAsync());
        Assert.Equal(0, tokens.Probes);
        Assert.True((await sync.RunAsync())!.Ok);
        Assert.Equal(0, tokens.Probes);
    }

    [Fact]
    public async Task OneDriveSignedOut_SkipsWithoutCloudTraffic()
    {
        using var db = new TestDatabase();
        var store = new SyncConfigurationStore(db.Paths);
        var tokens = new TestTokens { Account = null };
        var creations = 0;
        using var sync = Service(db, store, tokens, _ => { creations++; return new FakeSyncDriver(); });

        Assert.False(await sync.IsReadyAsync());
        Assert.Null(await sync.RunAsync());
        Assert.Equal(0, creations);
    }

    [Theory]
    [InlineData(SyncPairingChoice.Merge, 2)]
    [InlineData(SyncPairingChoice.CloudReplacesLocal, 1)]
    [InlineData(SyncPairingChoice.LocalReplacesCloud, 1)]
    public async Task FirstPairing_RequiresExplicitChoice_ThenConverges(SyncPairingChoice choice, int expectedCount)
    {
        using var db = new TestDatabase();
        var local = await db.NewNoteService().CreateAsync();
        var cloud = new FakeSyncDriver();
        var remote = SeedCloud(cloud);
        cloud.SeedText("notes/unrelated.txt", "keep me");
        var store = new SyncConfigurationStore(db.Paths);
        await store.SaveAsync(WebDav());
        using var sync = Service(db, store, new TestTokens(), _ => cloud);

        var blocked = await sync.RunAsync();

        Assert.Equal(SyncCycleAbortKind.PairingRequired, blocked!.AbortKind);
        Assert.Empty(cloud.Writes);
        Assert.Empty(cloud.Deletes);
        Assert.Single(await db.NewNoteService().GetAllAsync());

        var completed = await sync.RunAsync(choice, pairingTarget: blocked.PairingTarget);

        Assert.True(completed!.Ok);
        var notes = await db.NewNoteService().GetAllAsync();
        Assert.Equal(expectedCount, notes.Count);
        if (choice == SyncPairingChoice.CloudReplacesLocal)
            Assert.Single(Directory.EnumerateFiles(db.Paths.BackupsDir, "auto-backup-*.qns"));
        if (choice != SyncPairingChoice.CloudReplacesLocal) Assert.Contains(notes, n => n.Uuid == local.Uuid);
        if (choice != SyncPairingChoice.LocalReplacesCloud) Assert.Contains(notes, n => n.Uuid == remote);
        Assert.Contains("notes/unrelated.txt", cloud.Files.Keys);
        cloud.Writes.Clear(); cloud.Deletes.Clear();
        Assert.True((await sync.RunAsync())!.Ok);
        Assert.Empty(cloud.Writes);
        Assert.Empty(cloud.Deletes);
    }

    [Fact]
    public async Task ChangingTarget_InvalidatesEtagAgreement_ButPasswordChangeDoesNot()
    {
        using var db = new TestDatabase();
        await db.NewNoteService().CreateAsync();
        var firstCloud = new FakeSyncDriver();
        var secondCloud = new FakeSyncDriver();
        SeedCloud(secondCloud);
        var store = new SyncConfigurationStore(db.Paths);
        await store.SaveAsync(WebDav());
        using var sync = Service(db, store, new TestTokens(), config =>
            config.FolderUrl.Contains("second", StringComparison.Ordinal) ? secondCloud : firstCloud);
        Assert.True((await sync.RunAsync())!.Ok);
        var state = new SyncStateStore(db.Factory);
        var agreed = await state.GetByKindAsync(SyncEntryKind.Note);
        Assert.Single(agreed);

        await sync.SaveConfigurationAsync(WebDav() with { Password = "new-password" });
        firstCloud.Writes.Clear();
        Assert.True((await sync.RunAsync())!.Ok);
        Assert.Equal(agreed, await state.GetByKindAsync(SyncEntryKind.Note));
        Assert.Empty(firstCloud.Writes);

        await sync.SaveConfigurationAsync(WebDav() with { FolderUrl = "https://dav.example.com/second/" });
        Assert.Equal(SyncCycleAbortKind.PairingRequired, (await sync.RunAsync())!.AbortKind);
        Assert.Empty(await state.GetByKindAsync(SyncEntryKind.Note));
        Assert.Empty(secondCloud.Writes);
        Assert.Single(await db.NewNoteService().GetAllAsync());
    }

    [Fact]
    public async Task RestartWithSameTarget_KeepsAgreement_AndDoesNotAskForPairingAgain()
    {
        using var db = new TestDatabase();
        await db.NewNoteService().CreateAsync();
        var cloud = new FakeSyncDriver();
        var store = new SyncConfigurationStore(db.Paths);
        await store.SaveAsync(WebDav());
        using (var sync = Service(db, store, new TestTokens(), _ => cloud))
            Assert.True((await sync.RunAsync())!.Ok);
        cloud.Writes.Clear();
        using var restarted = Service(db, new SyncConfigurationStore(db.Paths), new TestTokens(), _ => cloud);

        Assert.True((await restarted.RunAsync())!.Ok);
        Assert.Empty(cloud.Writes);
    }

    [Fact]
    public async Task ConfigurationChanges_WaitUntilInFlightCycleReleasesTarget()
    {
        using var db = new TestDatabase();
        var store = new SyncConfigurationStore(db.Paths);
        await store.SaveAsync(WebDav());
        var cloud = new PausedDriver();
        using var sync = Service(db, store, new TestTokens(), _ => cloud);
        var cycle = sync.RunAsync();
        await cloud.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var change = sync.SaveConfigurationAsync(WebDav() with { FolderUrl = "https://dav.example.com/second/" });
        Assert.False(change.IsCompleted);
        Assert.Equal(WebDav(), await store.LoadAsync());
        cloud.Release.SetResult();
        Assert.True((await cycle)!.Ok);
        await change;
        Assert.EndsWith("/second/", (await store.LoadAsync()).FolderUrl);
    }

    [Fact]
    public async Task UnsupportedManifest_NeverAppliesAReplacement()
    {
        using var db = new TestDatabase();
        var local = await db.NewNoteService().CreateAsync();
        var cloud = new FakeSyncDriver();
        SeedCloud(cloud);
        cloud.SeedText("manifest.json", "{\"formatVersion\":99}");
        var store = new SyncConfigurationStore(db.Paths);
        using var sync = Service(db, store, new TestTokens(), _ => cloud);

        var request = await sync.RunAsync();
        Assert.Equal(SyncCycleAbortKind.UnsupportedManifest,
            (await sync.RunAsync(SyncPairingChoice.CloudReplacesLocal, pairingTarget: request!.PairingTarget))!.AbortKind);
        Assert.Equal(local.Uuid, Assert.Single(await db.NewNoteService().GetAllAsync()).Uuid);
        Assert.Empty(cloud.Deletes);
    }

    [Fact]
    public async Task PairingChoice_ForPreviousTarget_CannotReplaceNewTarget()
    {
        using var db = new TestDatabase();
        var local = await db.NewNoteService().CreateAsync();
        var firstCloud = new FakeSyncDriver();
        var secondCloud = new FakeSyncDriver();
        SeedCloud(firstCloud);
        var remote = SeedCloud(secondCloud);
        var store = new SyncConfigurationStore(db.Paths);
        await store.SaveAsync(WebDav());
        using var sync = Service(db, store, new TestTokens(), config =>
            config.FolderUrl.Contains("second", StringComparison.Ordinal) ? secondCloud : firstCloud);
        var request = await sync.RunAsync();

        await sync.SaveConfigurationAsync(WebDav() with { FolderUrl = "https://dav.example.com/second/" });
        var stale = await sync.RunAsync(SyncPairingChoice.LocalReplacesCloud, pairingTarget: request!.PairingTarget);

        Assert.Equal(SyncCycleAbortKind.PairingRequired, stale!.AbortKind);
        Assert.NotEqual(request.PairingTarget, stale.PairingTarget);
        Assert.Contains($"notes/{remote}.qnote", secondCloud.Files.Keys);
        Assert.Empty(secondCloud.Writes);
        Assert.Empty(secondCloud.Deletes);
        Assert.Equal(local.Uuid, Assert.Single(await db.NewNoteService().GetAllAsync()).Uuid);
    }

    [Fact]
    public async Task LocalReplacement_PropagatesDeletionToExistingPeer()
    {
        using var db = new TestDatabase();
        using var peer = new TestDatabase();
        await db.NewNoteService().CreateAsync();
        var cloud = new FakeSyncDriver();
        var remote = SeedCloud(cloud);
        var peerEngine = new SyncEngine(peer.NewRepository(), new SyncStateStore(peer.Factory),
            cloud, peer.Paths, NullLogger<SyncEngine>.Instance);
        Assert.True((await peerEngine.RunAsync()).Ok);
        var store = new SyncConfigurationStore(db.Paths);
        await store.SaveAsync(WebDav());
        using var sync = Service(db, store, new TestTokens(), _ => cloud);
        var request = await sync.RunAsync();

        Assert.True((await sync.RunAsync(SyncPairingChoice.LocalReplacesCloud,
            pairingTarget: request!.PairingTarget))!.Ok);
        Assert.True((await peerEngine.RunAsync()).Ok);

        Assert.DoesNotContain($"notes/{remote}.qnote", cloud.Files.Keys);
        Assert.Contains($"tombstones/{remote}", cloud.Files.Keys);
        Assert.DoesNotContain(await peer.NewNoteService().GetAllAsync(), n => n.Uuid == remote);
    }

    private static SyncConfiguration WebDav() => new()
    {
        Provider = SyncProvider.WebDav, FolderUrl = "https://dav.example.com/sync/",
        Username = "test-user", Password = "synthetic-app-secret",
    };

    private static SyncService Service(TestDatabase db, SyncConfigurationStore store,
        TestTokens tokens, Func<SyncConfiguration, ISyncDriver> factory) => new(
            store, tokens, db.NewRepository(), new SyncStateStore(db.Factory), db.Paths,
            NullLoggerFactory.Instance, new ThrottleGate(600, TimeSpan.FromMinutes(30)),
            new BackupService(db.Paths, db.Factory, db.NewRepository(), db.NewTodoRepository(),
                db.NewSearchService(), db.NewSettingsService(), db.NewImageService(),
                new QNote.Data.Schema.SchemaInitializer(db.Factory), NullLogger<BackupService>.Instance),
            driverFactory: factory);

    private static string SeedCloud(FakeSyncDriver cloud)
    {
        var uuid = Guid.NewGuid().ToString("N");
        cloud.Seed($"notes/{uuid}.qnote", QnoteContainerCodec.Encode(
            new QnoteHeader { Uuid = Guid.Parse(uuid), CreatedAtMs = 1, UpdatedAtMs = 1000, DeletedAtMs = 0 },
            SyncNoteBody.Encode("cloud", "", "remote content"), []));
        return uuid;
    }

    private sealed class SettingsPorts : IGlobalHotkey, IStartupTaskService, IGitHubUpdateService
    {
        public event Action? Pressed { add { } remove { } }
        public bool IsRegistered { get; private set; } = true;
        public bool RegisterSucceeds { get; set; } = true;
        public StartupTaskStatus StartupState { get; set; } = StartupTaskStatus.Disabled;
        public StartupTaskStatus EnableResult { get; set; } = StartupTaskStatus.Enabled;
        public Exception? StartupFailure { get; set; }
        public UpdateCheckResult UpdateResult { get; set; } = new(UpdateCheckStatus.UpToDate, null, null, null);
        public void Attach(nint hwnd) { }
        public bool TryRegister(int modifiers, int virtualKey)
        { IsRegistered = RegisterSucceeds && virtualKey != 0; return RegisterSucceeds; }
        public Task<StartupTaskStatus> GetStateAsync(CancellationToken ct = default) =>
            Task.FromResult(StartupState);
        public Task<StartupTaskStatus> RequestEnableAsync(CancellationToken ct = default)
        {
            if (StartupFailure is { } error) return Task.FromException<StartupTaskStatus>(error);
            StartupState = EnableResult; return Task.FromResult(StartupState);
        }
        public Task<StartupTaskStatus> DisableAsync(CancellationToken ct = default)
        { StartupState = StartupTaskStatus.Disabled; return Task.FromResult(StartupState); }
        public Task<UpdateCheckResult> CheckLatestAsync(CancellationToken ct = default) =>
            Task.FromResult(UpdateResult);
    }

    private sealed class TestTokens : ITokenProvider
    {
        public AccountInfo? Account { get; set; } = new("test@example.com", "synthetic-account-id");
        public int Probes { get; private set; }
        public Exception? AuthFailure { get; set; }
        public Exception? ProbeFailure { get; set; }
        public Task<AccountInfo?> GetSignedInAccountAsync(CancellationToken ct = default)
        { Probes++; return ProbeFailure is { } error ? Task.FromException<AccountInfo?>(error) : Task.FromResult(Account); }
        public Task<AuthResult> AcquireSilentAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AuthResult> AcquireInteractiveAsync(CancellationToken ct = default)
        {
            if (AuthFailure is { } error) return Task.FromException<AuthResult>(error);
            Account ??= new AccountInfo("test@example.com", "synthetic-account-id");
            return Task.FromResult(new AuthResult("synthetic-token", DateTimeOffset.UtcNow.AddHours(1), Account));
        }
        public Task SignOutAsync(CancellationToken ct = default)
        {
            if (AuthFailure is { } error) return Task.FromException(error);
            Account = null;
            return Task.CompletedTask;
        }
    }

    private sealed class PausedDriver : ISyncDriver
    {
        private readonly FakeSyncDriver _inner = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SupportsRangeRead => false;
        public async Task<DriverListing> ListAsync(string folderPath, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return await _inner.ListAsync(folderPath, ct);
        }
        public Task<byte[]?> ReadFileAsync(string path, CancellationToken ct = default) => _inner.ReadFileAsync(path, ct);
        public Task<byte[]?> ReadRangeAsync(string path, long offset, int count, CancellationToken ct = default) => _inner.ReadRangeAsync(path, offset, count, ct);
        public Task<DriverWriteResult> WriteFileAsync(string path, byte[] content, string? ifMatchEtag = null, CancellationToken ct = default) => _inner.WriteFileAsync(path, content, ifMatchEtag, ct);
        public Task DeleteFileAsync(string path, CancellationToken ct = default) => _inner.DeleteFileAsync(path, ct);
    }
}
