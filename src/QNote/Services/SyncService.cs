using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QNote.Data;
using QNote.Data.Schema;
using QNote.Infrastructure;
using QNote.Models;
using QNote.Sync;

namespace QNote.Services;

/// <summary>Serializes target changes and cycles; a cycle never changes account or driver halfway.</summary>
public sealed partial class SyncService : IDisposable
{
    private readonly SyncConfigurationStore _configuration;
    private readonly ITokenProvider _tokens;
    private readonly INoteRepository _notes;
    private readonly SyncStateStore _state;
    private readonly AppPaths _paths;
    private readonly ILoggerFactory _logs;
    private readonly SyncEngine.DisplayImageGenerator? _displayImages;
    private readonly string _conflictSuffix;
    private readonly ThrottleGate _throttle;
    private readonly IBackupService _backups;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<SyncConfiguration, ISyncDriver>? _driverFactory;
    private ISyncDriver? _driver;
    private ISyncDriver? _rawDriver;
    private readonly SyncEncryptionKeyStore _encryptionKeys;
    private SyncEncryptionStatus? _encryptionStatus;
    private SyncEngine? _engine;
    private string? _target;
    private SyncConfiguration? _activeConfiguration;
    private readonly ITodoSyncRepository? _todos;
    private readonly ITodoReminderCoordinator? _reminders;

    public SyncService(SyncConfigurationStore configuration, ITokenProvider tokens,
        INoteRepository notes, SyncStateStore state, AppPaths paths, ILoggerFactory logs,
        ThrottleGate throttle, IBackupService backups, SyncEngine.DisplayImageGenerator? displayImages = null,
        string conflictSuffix = " (conflict)", Func<SyncConfiguration, ISyncDriver>? driverFactory = null,
        ITodoSyncRepository? todos = null, ITodoReminderCoordinator? reminders = null)
    {
        _configuration = configuration; _tokens = tokens; _notes = notes; _state = state;
        _paths = paths; _logs = logs; _throttle = throttle;
        _backups = backups;
        _encryptionKeys = new SyncEncryptionKeyStore(paths, logs.CreateLogger<SyncEncryptionKeyStore>());
        _todos = todos; _reminders = reminders;
        _displayImages = displayImages; _conflictSuffix = conflictSuffix; _driverFactory = driverFactory;
    }

    public Task<SyncConfiguration> GetConfigurationAsync(CancellationToken ct = default) => _configuration.LoadAsync(ct);

    public async Task<SyncDestination> GetDestinationAsync(CancellationToken ct = default)
    {
        var configuration = await _configuration.LoadAsync(ct);
        var account = configuration.Provider == SyncProvider.OneDrive
            ? await _tokens.GetSignedInAccountAsync(ct) : null;
        return DescribeDestination(configuration, account);
    }

    public async Task<bool> IsReadyAsync(CancellationToken ct = default) =>
        (await GetDestinationAsync(ct)).IsReady;

    private static SyncDestination DescribeDestination(SyncConfiguration configuration, AccountInfo? account) =>
        configuration.Provider == SyncProvider.OneDrive
            ? new SyncDestination("OneDrive", account?.Username ?? "", IsReady: account is not null)
            : new SyncDestination("WebDAV", configuration.Username, configuration.FolderUrl);

    /// <summary>Tests drafts without saving or changing the active sync destination.</summary>
    public async Task TestWebDavAsync(SyncConfiguration configuration, CancellationToken ct = default)
    {
        configuration = configuration.Validate();
        using var driver = new WebDavDriver(configuration.FolderUrl, configuration.Username, configuration.Password, _throttle);
        await driver.CheckConnectionAsync(ct);
    }

    public async Task SaveConfigurationAsync(SyncConfiguration configuration, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await _configuration.SaveAsync(configuration, ct); }
        finally { _gate.Release(); }
    }

    public async Task ChangeAccountAsync(Func<Task> change, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await change(); }
        finally { _gate.Release(); }
    }

    public async Task<SyncCycleResult?> RunAsync(SyncPairingChoice? pairingChoice = null, CancellationToken ct = default,
        string? pairingTarget = null)
    {
        await _gate.WaitAsync(ct);
        SyncDestination? destination = null;
        byte[]? cycleKey = null;
        SyncCycleResult Complete(SyncCycleResult result) => result with { Destination = destination };
        try
        {
            var configuration = await _configuration.LoadAsync(ct);
            var account = configuration.Provider == SyncProvider.OneDrive
                ? await _tokens.GetSignedInAccountAsync(ct) : null;
            destination = DescribeDestination(configuration, account);
            if (configuration.Provider == SyncProvider.OneDrive && account is null)
                return null;
            var target = TargetIdentity(configuration, account);
            EnsureDriver(configuration, target);
            var encryption = await SyncEncryptionManager.ReadManifestAsync(_rawDriver!, ct);
            cycleKey = await LoadVerifiedKeyAsync(target, encryption.Manifest, ct);
            var locallyPrepared = !encryption.Manifest.E2eeEnabled && await _encryptionKeys.IsPreparedAsync(target, ct);
            UpdateEncryptionStatus(target, destination, encryption.Manifest, cycleKey is not null, locallyPrepared);
            if (locallyPrepared)
                return Complete(EncryptionBlocked(SyncCycleAbortKind.EncryptionConversionRequired, target));
            if (encryption.Manifest.E2eeEnabled && cycleKey is null)
                return Complete(EncryptionBlocked(SyncCycleAbortKind.E2eeKeyMismatch, target));
            if (encryption.Manifest.EncryptionConversionTo is not null)
                return Complete(EncryptionBlocked(SyncCycleAbortKind.EncryptionConversionRequired, target));
            if (pairingChoice is not null && !string.Equals(pairingTarget, target, StringComparison.Ordinal))
                return Complete(PairingRequired(target));
            _driver = new SyncStorageDriver(_rawDriver!, cycleKey, encryption.Bytes);
            _engine = new SyncEngine(_notes, _state, _driver, _paths,
                _logs.CreateLogger<SyncEngine>(), _displayImages, _conflictSuffix, _todos);

            var needsPairing = await _state.PrepareTargetAsync(target, ct);
            if (needsPairing)
            {
                var listings = new[]
                {
                    await _driver!.ListAsync(SyncEngine.NotesFolder, ct),
                    await _driver.ListAsync(SyncEngine.CategoriesFolder, ct),
                    await _driver.ListAsync(SyncEngine.TombstonesFolder, ct),
                    _todos is null ? new DriverListing([], false) : await _driver.ListAsync(SyncEngine.TodoListsFolder, ct),
                    _todos is null ? new DriverListing([], false) : await _driver.ListAsync(SyncEngine.TodoTasksFolder, ct),
                };
                if (listings.Any(l => l.IsTruncated))
                    return Complete(SyncCycleResult.Aborted(SyncCycleAbortKind.ListingIncomplete, "The first-pairing listing is incomplete."));
                var managedFiles = listings.SelectMany(l => l.Files).Where(IsManagedFile).ToList();
                var hasLocal = (await _notes.GetAllUuidTimestampsAsync(ct)).Count > 0
                    || (await _notes.GetCategoriesAsync(ct)).Any(c => !IsBuiltin(c.Uuid) || c.UpdatedAt > c.CreatedAt);
                if (_todos is not null)
                {
                    var todo = await _todos.ReadSyncSnapshotAsync(ct);
                    hasLocal |= todo.Tasks.Count > 0 || todo.Lists.Any(l =>
                        l.Uuid is not (SchemaInitializer.WorkListUuid or SchemaInitializer.LifeListUuid) || l.UpdatedAt > l.CreatedAt);
                }
                var hasCloud = managedFiles.Count > 0;
                if (hasLocal && hasCloud)
                {
                    if (pairingChoice is null)
                        return Complete(PairingRequired(target));
                    // Inspect the manifest before any user-authorized replacement.
                    var bytes = await _driver.ReadFileAsync(SyncEngine.ManifestPath, ct);
                    if (bytes is not null)
                    {
                        var manifest = JsonSerializer.Deserialize(bytes, SyncManifestJsonContext.Default.SyncManifest);
                        if (manifest?.FormatVersion != SyncManifest.CurrentFormatVersion)
                            return Complete(SyncCycleResult.Aborted(SyncCycleAbortKind.UnsupportedManifest, "The cloud manifest is unsupported."));

                    }
                    if (pairingChoice == SyncPairingChoice.CloudReplacesLocal)
                    {
                        // A corrupt/inaccessible cloud source must not make the
                        // user's previous local library unrecoverable.
                        await _backups.CreateAutoBackupAsync(ct);
                        await _state.ReplaceLocalNotesAsync(ct);
                    }
                    else if (pairingChoice == SyncPairingChoice.LocalReplacesCloud)
                    {
                        await ReplaceCloudAsync(managedFiles, ct);
                    }
                }
            }
            var result = await _engine!.RunAsync(cycleKey, ct);
            if (_reminders is not null && (result.Downloaded + result.TombstonesApplied + result.ConflictCopies > 0
                || pairingChoice == SyncPairingChoice.CloudReplacesLocal))
            {
                try { await _reminders.ReconcileAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { _logs.CreateLogger<SyncService>().LogWarning(ex, "Reminder reconciliation after sync failed."); }
            }
            if (result.Ok && needsPairing)
                await _state.MarkTargetPairedAsync(target, ct);
            return Complete(result);
        }
        catch (SyncDriverException ex)
        {
            _logs.CreateLogger<SyncService>().LogWarning(ex, "Sync target preparation failed.");
            return Complete(new SyncCycleResult
            {
                Uploaded = 0, Downloaded = 0, DeletedRemote = 0, TombstonesApplied = 0,
                ConflictCopies = 0, Errors = [ex.Message],
            });
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return Complete(SyncCycleResult.Aborted(SyncCycleAbortKind.UnsupportedManifest, "The cloud manifest is unreadable."));
        }
        finally
        {
            if (cycleKey is not null) CryptographicOperations.ZeroMemory(cycleKey);
            _gate.Release();
        }
    }

    private static bool IsBuiltin(string uuid) => uuid is SchemaInitializer.WorkCategoryUuid
        or SchemaInitializer.LifeCategoryUuid or SchemaInitializer.ImportantCategoryUuid;

    private async Task ReplaceCloudAsync(IReadOnlyList<DriverFile> files, CancellationToken ct)
    {
        var local = (await _notes.GetAllUuidTimestampsAsync(ct))
            .Concat(await _notes.GetAllCategoryUuidTimestampsAsync(ct)).ToList();
        var live = local.Where(s => s.DeletedAt is null).Select(s => s.Uuid).ToHashSet(StringComparer.Ordinal);
        var newest = local.Select(s => s.UpdatedAt.ToUnixTimeMilliseconds())
            .Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Max();
        if (_todos is not null)
        {
            var todo = await _todos.ReadSyncSnapshotAsync(ct);
            foreach (var list in todo.Lists)
            {
                newest = Math.Max(newest, list.UpdatedAt.ToUnixTimeMilliseconds());
                if (list.DeletedAt is null) live.Add(SyncTodoPaths.ListDeletionPrefix + list.Uuid);
            }
            foreach (var detail in todo.Tasks)
            {
                newest = Math.Max(newest, detail.Task.UpdatedAt.ToUnixTimeMilliseconds());
                if (detail.Task.DeletedAt is null) live.Add(SyncTodoPaths.TaskDeletionPrefix + detail.Task.Uuid);
            }
        }
        // Read every version before the first replacement. The explicit choice
        // supersedes these versions, including clocks ahead of this device.
        foreach (var file in files)
        {
            var bytes = await _driver!.ReadFileAsync(file.Path, ct)
                ?? throw new SyncDriverException(SyncDriverErrorKind.ServiceError, "A cloud entry disappeared before replacement.");
            long updated;
            if (file.Path.StartsWith(SyncEngine.TombstonesFolder + "/", StringComparison.Ordinal))
            {
                if (bytes.Length != sizeof(long))
                    throw new SyncDriverException(SyncDriverErrorKind.ServiceError, "A cloud deletion record is invalid.");
                updated = BinaryPrimitives.ReadInt64LittleEndian(bytes);
            }
            else if (file.Path.StartsWith(SyncEngine.NotesFolder + "/", StringComparison.Ordinal)
                || file.Path.StartsWith(SyncEngine.TodoTasksFolder + "/", StringComparison.Ordinal))
                updated = QnoteContainerCodec.ReadHeader(bytes).UpdatedAtMs;
            else if (file.Path.StartsWith(SyncEngine.TodoListsFolder + "/", StringComparison.Ordinal))
                updated = (JsonSerializer.Deserialize(bytes, SyncTodoJsonContext.Default.TodoList)
                    ?? throw new SyncDriverException(SyncDriverErrorKind.ServiceError, "A cloud TODO list is invalid.")).UpdatedAt.ToUnixTimeMilliseconds();
            else
                updated = JsonSerializer.Deserialize(bytes, SyncCategoryFileJsonContext.Default.SyncCategoryFile)?.UpdatedAtMs
                    ?? throw new SyncDriverException(SyncDriverErrorKind.ServiceError, "A cloud category is invalid.");
            newest = Math.Max(newest, updated);
        }
        var replacementMs = checked(newest + 1);
        await _state.RestampLocalNotesAsync(replacementMs, ct);
        var tombstone = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(tombstone, replacementMs);
        foreach (var file in files)
        {
            var name = file.Path[(file.Path.IndexOf('/') + 1)..];
            var isTombstone = file.Path.StartsWith(SyncEngine.TombstonesFolder + "/", StringComparison.Ordinal);
            var uuid = isTombstone ? name : name[..name.LastIndexOf('.')];
            var deletionPath = SyncEngine.TombstonesFolder + "/" + uuid;
            if (SyncTodoPaths.TryDescribe(file.Path, out var kind, out var todoUuid, out _))
            {
                uuid = SyncTodoPaths.DeletionPrefix(kind) + todoUuid;
                deletionPath = SyncTodoPaths.DeletionPath(kind, todoUuid);
            }
            if (isTombstone)
            {
                if (live.Contains(uuid))
                    await _driver!.DeleteFileAsync(file.Path, ct);
                continue;
            }
            if (!live.Contains(uuid))
                await _driver!.WriteFileAsync(deletionPath, tombstone, ct: ct);
            await _driver!.DeleteFileAsync(file.Path, ct);
        }
    }

    private static SyncCycleResult PairingRequired(string target) =>
        SyncCycleResult.Aborted(SyncCycleAbortKind.PairingRequired,
            "Local and cloud data require an explicit first-pairing choice for this target.") with { PairingTarget = target };

    private static bool IsManagedFile(DriverFile file)
    {
        if (SyncTodoPaths.TryDescribe(file.Path, out _, out _, out _)) return true;
        var parts = file.Path.Split('/');
        if (parts.Length != 2)
            return false;
        var name = parts[1];
        return parts[0] switch
        {
            SyncEngine.NotesFolder => name.EndsWith(SyncEngine.NoteContainerExtension, StringComparison.Ordinal)
                && Guid.TryParseExact(name[..^SyncEngine.NoteContainerExtension.Length], "N", out _),
            SyncEngine.CategoriesFolder => name.EndsWith(SyncEngine.CategoryFileExtension, StringComparison.Ordinal)
                && Guid.TryParseExact(name[..^SyncEngine.CategoryFileExtension.Length], "N", out _),
            SyncEngine.TombstonesFolder => Guid.TryParseExact(name, "N", out _),
            _ => false,
        };
    }

    public void Dispose()
    {
        (_rawDriver as IDisposable)?.Dispose();
        // A cancelled scheduler cycle may still be releasing the gate on shutdown.
    }
}
