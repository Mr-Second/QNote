using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using QNote.Sync;

namespace QNote.Services;

public sealed record SyncEncryptionStatus(string Target, SyncDestination Destination, bool Enabled,
    bool NeedsPassword, bool? ConversionTo, bool Checked = true);

public sealed class SyncEncryptionPasswordException() : Exception("The encryption password did not unlock this target.");

public sealed partial class SyncService
{
    public async Task<SyncEncryptionStatus> GetCachedEncryptionStatusAsync(CancellationToken ct = default)
    {
        var configuration = await _configuration.LoadAsync(ct);
        var account = configuration.Provider == SyncProvider.OneDrive ? await _tokens.GetSignedInAccountAsync(ct) : null;
        var target = TargetIdentity(configuration, account);
        var destination = DescribeDestination(configuration, account);
        return _encryptionStatus?.Target == target ? _encryptionStatus
            : new SyncEncryptionStatus(target, destination, false, false, null, Checked: false);
    }

    public async Task<SyncEncryptionStatus> RefreshEncryptionStatusAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        byte[]? key = null;
        try
        {
            var context = await PrepareEncryptionContextAsync(null, ct);
            var cloud = await SyncEncryptionManager.ReadManifestAsync(_rawDriver!, ct);
            key = await LoadVerifiedKeyAsync(context.Target, cloud.Manifest, ct);
            return UpdateEncryptionStatus(context.Target, context.Destination, cloud.Manifest, key is not null,
                !cloud.Manifest.E2eeEnabled && await _encryptionKeys.IsPreparedAsync(context.Target, ct));
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); _gate.Release(); }
    }

    public async Task UnlockEncryptionAsync(string password, string expectedTarget, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        byte[]? key = null;
        try
        {
            var context = await PrepareEncryptionContextAsync(expectedTarget, ct);
            var cloud = await SyncEncryptionManager.ReadManifestAsync(_rawDriver!, ct);
            if (!cloud.Manifest.E2eeEnabled)
            { UpdateEncryptionStatus(context.Target, context.Destination, cloud.Manifest, false); return; }
            key = DeriveVerifiedKey(password, cloud.Manifest);
            await _encryptionKeys.SaveAsync(context.Target, cloud.Manifest.E2eeSalt!, key, ct);
            UpdateEncryptionStatus(context.Target, context.Destination, cloud.Manifest, true);
            _logs.CreateLogger<SyncService>().LogInformation("Synchronization encryption unlocked for the current target.");
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); _gate.Release(); }
    }

    public async Task<SyncEncryptionStatus> ChangeEncryptionAsync(bool enable, string? password, string expectedTarget,
        IProgress<SyncEncryptionProgress>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        byte[]? key = null;
        try
        {
            var context = await PrepareEncryptionContextAsync(expectedTarget, ct);
            var cloud = await SyncEncryptionManager.ReadManifestAsync(_rawDriver!, ct);
            var encryption = cloud.Manifest;
            var locallyPrepared = !cloud.Manifest.E2eeEnabled && await _encryptionKeys.IsPreparedAsync(context.Target, ct);
            if (locallyPrepared && !enable) throw new InvalidOperationException("Resume the pending encryption setup first.");
            if (cloud.Manifest.EncryptionConversionTo is { } pending && pending != enable)
                throw new InvalidOperationException("Resume the pending conversion before changing direction.");
            if (!cloud.Manifest.E2eeEnabled && enable)
            {
                var salt = locallyPrepared
                    ? Convert.FromBase64String((await _encryptionKeys.GetSaltAsync(context.Target, ct))!)
                    : RandomNumberGenerator.GetBytes(SyncKeyDeriver.SaltByteCount);
                if (locallyPrepared) key = await _encryptionKeys.LoadAsync(context.Target, null, ct);
                else
                {
                    if (password is null || password.Length < 8) throw new ArgumentException("The encryption password must contain at least eight characters.");
                    key = SyncKeyDeriver.Derive(password, salt);
                }
                if (key is null) throw new SyncEncryptionPasswordException();
                encryption = cloud.Manifest with
                {
                    E2eeSalt = Convert.ToBase64String(salt), E2eeKeyCheck = SyncEncryptedFileCodec.CreateKeyCheck(key),
                    EncryptionVersion = 1, E2eeKdfIterations = SyncKeyDeriver.Iterations,
                    EncryptionDisabledProof = null,
                };
                // Recovery material must be durable before publishing the barrier.
                await _encryptionKeys.SaveAsync(context.Target, encryption.E2eeSalt, key, ct, prepared: true);
            }
            else if (cloud.Manifest.E2eeEnabled)
            {
                key = await LoadVerifiedKeyAsync(context.Target, cloud.Manifest, ct);
                if (key is null && password is not null) key = DeriveVerifiedKey(password, cloud.Manifest);
                if (key is null) throw new SyncEncryptionPasswordException();
                await _encryptionKeys.SaveAsync(context.Target, encryption.E2eeSalt!, key, ct);
            }
            else return UpdateEncryptionStatus(context.Target, context.Destination, cloud.Manifest, false);

            await _backups.CreateAutoBackupAsync(ct);
            _logs.CreateLogger<SyncService>().LogInformation("Sync encryption conversion started (encrypted destination: {Enabled}, resume: {Resume}).",
                enable, cloud.Manifest.EncryptionConversionTo is not null);
            try { await SyncEncryptionManager.ConvertAsync(_rawDriver!, cloud, encryption, key, enable, progress, ct); }
            finally
            {
                // Failure may have left a recoverable barrier; the settings row
                // continues to represent the unfinished operation, not success.
                try
                {
                    var latest = await SyncEncryptionManager.ReadManifestAsync(_rawDriver!, ct);
                    if (latest.Manifest.E2eeEnabled)
                        await _encryptionKeys.SaveAsync(context.Target, latest.Manifest.E2eeSalt!, key, ct);
                    UpdateEncryptionStatus(context.Target, context.Destination, latest.Manifest, true,
                        !latest.Manifest.E2eeEnabled && await _encryptionKeys.IsPreparedAsync(context.Target, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { _logs.CreateLogger<SyncService>().LogWarning(ex, "Could not refresh encryption state after conversion."); }
            }
            if (!enable)
            {
                try { await _encryptionKeys.ForgetAsync(context.Target, ct); }
                catch (IOException ex) { _logs.CreateLogger<SyncService>().LogWarning(ex, "Could not remove an obsolete protected sync key."); }
            }
            _logs.CreateLogger<SyncService>().LogInformation("Sync encryption conversion completed (encrypted destination: {Enabled}).", enable);
            return _encryptionStatus!;
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); _gate.Release(); }
    }

    private async Task<(string Target, SyncDestination Destination)> PrepareEncryptionContextAsync(string? expected, CancellationToken ct)
    {
        var configuration = await _configuration.LoadAsync(ct);
        var account = configuration.Provider == SyncProvider.OneDrive ? await _tokens.GetSignedInAccountAsync(ct) : null;
        var destination = DescribeDestination(configuration, account);
        if (!destination.IsReady) throw new InvalidOperationException("Configure a sync destination first.");
        var target = TargetIdentity(configuration, account);
        if (expected is not null && target != expected) throw new InvalidOperationException("The sync destination changed; reopen encryption settings.");
        EnsureDriver(configuration, target);
        return (target, destination);
    }

    private void EnsureDriver(SyncConfiguration configuration, string target)
    {
        if (_target == target && _activeConfiguration == configuration && _rawDriver is not null) return;
        (_rawDriver as IDisposable)?.Dispose();
        _rawDriver = _driverFactory?.Invoke(configuration) ?? (configuration.Provider == SyncProvider.OneDrive
            ? new OneDriveDriver(_tokens, _logs.CreateLogger<OneDriveDriver>(), _throttle)
            : new WebDavDriver(configuration.FolderUrl, configuration.Username, configuration.Password, _throttle));
        _driver = _rawDriver;
        _target = target;
        _activeConfiguration = configuration;
    }

    private static string TargetIdentity(SyncConfiguration configuration, AccountInfo? account)
    {
        var identity = configuration.Provider == SyncProvider.OneDrive ? "onedrive\n" + account?.HomeAccountId
            : "webdav\n" + configuration.FolderUrl + "\n" + configuration.Username;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private async Task<byte[]?> LoadVerifiedKeyAsync(string target, SyncManifest manifest, CancellationToken ct)
    {
        if (!manifest.E2eeEnabled)
        {
            var remembered = await _encryptionKeys.LoadAsync(target, null, ct);
            if (remembered is null) return null;
            try
            {
                if (await _encryptionKeys.IsPreparedAsync(target, ct)) return null;
                if (manifest.EncryptionVersion != 1 || manifest.E2eeKdfIterations != SyncKeyDeriver.Iterations
                    || !SyncEncryptedFileCodec.VerifyDisabledProof(manifest.EncryptionDisabledProof, remembered, manifest.E2eeSalt))
                    throw new InvalidDataException("An unauthenticated encryption downgrade was blocked.");
                await _encryptionKeys.ForgetAsync(target, ct);
                return null;
            }
            finally { CryptographicOperations.ZeroMemory(remembered); }
        }
        var key = await _encryptionKeys.LoadAsync(target, manifest.E2eeSalt, ct);
        if (key is not null && SyncEncryptedFileCodec.VerifyKey(manifest.E2eeKeyCheck, key))
        {
            if (await _encryptionKeys.IsPreparedAsync(target, ct))
                await _encryptionKeys.SaveAsync(target, manifest.E2eeSalt!, key, ct);
            return key;
        }
        if (key is not null) CryptographicOperations.ZeroMemory(key);
        return null;
    }

    private static byte[] DeriveVerifiedKey(string password, SyncManifest manifest)
    {
        if (string.IsNullOrEmpty(password)) throw new SyncEncryptionPasswordException();
        var key = SyncKeyDeriver.Derive(password, Convert.FromBase64String(manifest.E2eeSalt!));
        if (SyncEncryptedFileCodec.VerifyKey(manifest.E2eeKeyCheck, key)) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new SyncEncryptionPasswordException();
    }

    private SyncEncryptionStatus UpdateEncryptionStatus(string target, SyncDestination destination, SyncManifest manifest, bool hasKey, bool locallyPrepared = false) =>
        _encryptionStatus = new SyncEncryptionStatus(target, destination,
            manifest.EncryptionConversionTo == true ? false : manifest.E2eeEnabled,
            manifest.E2eeEnabled && !hasKey, locallyPrepared ? true : manifest.EncryptionConversionTo);

    private static SyncCycleResult EncryptionBlocked(SyncCycleAbortKind kind, string target) =>
        SyncCycleResult.Aborted(kind, kind == SyncCycleAbortKind.E2eeKeyMismatch
            ? "Unlock the encrypted sync destination first." : "Complete the pending encryption conversion first.")
        with { EncryptionTarget = target };
}
