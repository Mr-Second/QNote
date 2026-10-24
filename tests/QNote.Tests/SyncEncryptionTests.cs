using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using QNote.Data;
using QNote.Data.Schema;
using QNote.Models;
using QNote.Services;
using QNote.Sync;

namespace QNote.Tests;

public sealed class SyncEncryptionTests
{
    private const string Password = "synthetic encryption password 42";

    [Fact]
    public async Task FailedPreflight_PreservesEncryptionIntentAndCanResumeWithoutAnotherPassword()
    {
        using var source = await Device.CreateAsync(); await source.SeedAsync();
        var status = await source.Service.RefreshEncryptionStatusAsync();
        source.Cloud.TruncatedFolders.Add("notes");
        await Assert.ThrowsAsync<SyncDriverException>(() => source.Service.ChangeEncryptionAsync(true, Password, status.Target));
        source.Cloud.TruncatedFolders.Clear();
        var pending = await source.Service.RefreshEncryptionStatusAsync();
        Assert.Equal(true, pending.ConversionTo);
        Assert.False(pending.Enabled);
        Assert.Equal(SyncCycleAbortKind.EncryptionConversionRequired, (await source.Service.RunAsync())!.AbortKind);
        await source.Service.ChangeEncryptionAsync(true, null, status.Target);
        AssertEncrypted(source.Cloud);
    }

    [Fact]
    public async Task ForgedUnencryptedManifest_CannotMakeAnUnlockedDeviceUploadPlaintext()
    {
        using var source = await Device.CreateAsync(); await source.SeedAsync();
        var status = await source.Service.RefreshEncryptionStatusAsync();
        await source.Service.ChangeEncryptionAsync(true, Password, status.Target);
        source.Cloud.Seed("manifest.json", JsonSerializer.SerializeToUtf8Bytes(new SyncManifest(), SyncManifestJsonContext.Default.SyncManifest));
        source.Cloud.Writes.Clear();
        var note = await source.Db.NewNoteService().CreateAsync();
        await source.Db.NewNoteService().UpdateAsync(note with { Content = "must never leave as plaintext" });
        Assert.Equal(SyncCycleAbortKind.UnsupportedManifest, (await source.Service.RunAsync())!.AbortKind);
        Assert.Empty(source.Cloud.Writes);
        Assert.DoesNotContain("notes/" + note.Uuid + ".qnote", source.Cloud.Files.Keys);
    }

    [Fact]
    public async Task ConversionWaitsForRequestBudgetPauseAndContinues()
    {
        using var source = await Device.CreateAsync(); await source.SeedAsync();
        var status = await source.Service.RefreshEncryptionStatusAsync();
        var paused = false;
        source.Cloud.WriteFailure = path =>
        {
            if (paused || !SyncEncryptionManager.IsManagedPath(path)) return null;
            paused = true;
            return new SyncDriverException(SyncDriverErrorKind.ServiceError, "budget pause", retryAfter: TimeSpan.FromMilliseconds(1));
        };
        await source.Service.ChangeEncryptionAsync(true, Password, status.Target);
        Assert.True(paused); AssertEncrypted(source.Cloud);
    }

    [Fact]
    public async Task ChangedFileDuringConversion_IsNotOverwritten_AndResumePreservesItsNewVersion()
    {
        using var source = await Device.CreateAsync();
        var (note, _, _) = await source.SeedAsync();
        var status = await source.Service.RefreshEncryptionStatusAsync();
        var path = "notes/" + note.Uuid + ".qnote";
        source.Cloud.WriteFailure = writing =>
        {
            if (writing != path) return null;
            source.Cloud.WriteFailure = null;
            source.Cloud.Seed(path, QnoteContainerCodec.Encode(new QnoteHeader
            {
                Uuid = Guid.ParseExact(note.Uuid, "N"), CreatedAtMs = note.CreatedAt.ToUnixTimeMilliseconds(),
                UpdatedAtMs = note.UpdatedAt.AddMinutes(1).ToUnixTimeMilliseconds(), DeletedAtMs = 0,
            }, SyncNoteBody.Encode("concurrent edit", "", "new peer content"), []));
            return null;
        };
        var failure = await Assert.ThrowsAsync<SyncDriverException>(() => source.Service.ChangeEncryptionAsync(true, Password, status.Target));
        Assert.Equal(SyncDriverErrorKind.PreconditionFailed, failure.Kind);
        Assert.False(QnoteContainerCodec.IsEncrypted(source.Cloud.Files[path]));
        await source.Service.ChangeEncryptionAsync(true, null, status.Target);
        var manifest = await SyncEncryptionManager.ReadManifestAsync(source.Cloud);
        var store = new SyncEncryptionKeyStore(source.Db.Paths, NullLogger<SyncEncryptionKeyStore>.Instance);
        var key = (await store.LoadAsync(status.Target, manifest.Manifest.E2eeSalt))!;
        try
        {
            var payload = QnoteContainerCodec.Decode(source.Cloud.Files[path], key);
            Assert.Equal("concurrent edit", SyncNoteBody.Decode(payload.Body).Title);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    [Fact]
    public async Task ReenablingWithNewPassword_RequiresOtherDevicesToUnlockTheNewSalt()
    {
        using var source = await Device.CreateAsync(); using var peer = await Device.CreateAsync(source.Cloud);
        await source.SeedAsync();
        var status = await source.Service.RefreshEncryptionStatusAsync();
        await source.Service.ChangeEncryptionAsync(true, Password, status.Target);
        await peer.Service.UnlockEncryptionAsync(Password, status.Target);
        Assert.True((await peer.Service.RunAsync())!.Ok);
        await source.Service.ChangeEncryptionAsync(false, null, status.Target);
        await source.Service.ChangeEncryptionAsync(true, "new synthetic encryption password", status.Target);
        Assert.Equal(SyncCycleAbortKind.E2eeKeyMismatch, (await peer.Service.RunAsync())!.AbortKind);
        await Assert.ThrowsAsync<SyncEncryptionPasswordException>(() => peer.Service.UnlockEncryptionAsync(Password, status.Target));
        await peer.Service.UnlockEncryptionAsync("new synthetic encryption password", status.Target);
        Assert.True((await peer.Service.RunAsync())!.Ok);
    }

    [Theory]
    [InlineData(SyncPairingChoice.Merge)]
    [InlineData(SyncPairingChoice.CloudReplacesLocal)]
    [InlineData(SyncPairingChoice.LocalReplacesCloud)]
    public async Task EncryptedFirstPairing_HonorsAllChoicesWithoutWritingPlaintext(SyncPairingChoice choice)
    {
        using var source = await Device.CreateAsync(); using var peer = await Device.CreateAsync(source.Cloud);
        var (remote, remoteTask, _) = await source.SeedAsync();
        var status = await source.Service.RefreshEncryptionStatusAsync();
        await source.Service.ChangeEncryptionAsync(true, Password, status.Target);
        var local = await peer.Db.NewNoteService().CreateAsync();
        await peer.Service.UnlockEncryptionAsync(Password, status.Target);
        var pending = await peer.Service.RunAsync();
        Assert.Equal(SyncCycleAbortKind.PairingRequired, pending!.AbortKind);
        var completed = await peer.Service.RunAsync(choice, pairingTarget: pending.PairingTarget);
        Assert.True(completed!.Ok, string.Join("; ", completed.Errors));
        var notes = await peer.Db.NewNoteService().GetAllAsync();
        if (choice == SyncPairingChoice.Merge) Assert.Equal(2, notes.Count);
        else if (choice == SyncPairingChoice.CloudReplacesLocal)
        {
            Assert.Equal(remote.Uuid, Assert.Single(notes).Uuid);
            Assert.NotNull(await peer.Db.NewTodoRepository().GetTaskByUuidAsync(remoteTask.Uuid));
        }
        else
        {
            Assert.Equal(local.Uuid, Assert.Single(notes).Uuid);
            Assert.Contains(SyncTodoPaths.DeletionPath(SyncEntryKind.TodoTask, remoteTask.Uuid), source.Cloud.Files.Keys);
            Assert.True((await source.Service.RunAsync())!.Ok);
            Assert.Null(await source.Db.NewTodoRepository().GetTaskByUuidAsync(remoteTask.Uuid));
        }
        AssertEncrypted(source.Cloud);
    }

    [Fact]
    public async Task PlainTimestampBeginningWithEnvelopeMagic_IsNotMistakenForCiphertext()
    {
        using var source = await Device.CreateAsync();
        var path = "tombstones/" + Guid.NewGuid().ToString("N");
        var bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, 0x000001A031454E51);
        Assert.False(SyncEncryptedFileCodec.IsEncrypted(bytes));
        source.Cloud.Seed(path, bytes);
        var status = await source.Service.RefreshEncryptionStatusAsync();
        await source.Service.ChangeEncryptionAsync(true, Password, status.Target);
        await source.Service.ChangeEncryptionAsync(false, null, status.Target);
        Assert.Equal(bytes, source.Cloud.Files[path]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task InvalidEncryptionManifest_BlocksSyncBeforeDataChanges(int scenario)
    {
        using var source = await Device.CreateAsync();
        var status = await source.Service.RefreshEncryptionStatusAsync();
        await source.Service.ChangeEncryptionAsync(true, Password, status.Target);
        var read = await SyncEncryptionManager.ReadManifestAsync(source.Cloud);
        var bad = scenario switch
        {
            0 => read.Manifest with { EncryptionVersion = 99 },
            1 => read.Manifest with { E2eeKdfIterations = 1 },
            2 => read.Manifest with { E2eeEnabled = false, EncryptionConversionTo = false },
            _ => read.Manifest with { E2eeKeyCheck = "invalid base64" },
        };
        source.Cloud.Seed("manifest.json", JsonSerializer.SerializeToUtf8Bytes(bad, SyncManifestJsonContext.Default.SyncManifest));
        source.Cloud.Writes.Clear();
        Assert.Equal(SyncCycleAbortKind.UnsupportedManifest, (await source.Service.RunAsync())!.AbortKind);
        Assert.Empty(source.Cloud.Writes);
    }

    [Fact]
    public void EncryptedMetadata_BindsPathKeyAndNonce()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var bytes = Encoding.UTF8.GetBytes("private list name");
        var one = SyncEncryptedFileCodec.Encrypt("todo_lists/a.json", bytes, key);
        var two = SyncEncryptedFileCodec.Encrypt("todo_lists/a.json", bytes, key);
        Assert.NotEqual(one, two);
        Assert.DoesNotContain("private list name", Encoding.UTF8.GetString(one));
        Assert.Equal(bytes, SyncEncryptedFileCodec.Decrypt("todo_lists/a.json", one, key));
        Assert.ThrowsAny<CryptographicException>(() => SyncEncryptedFileCodec.Decrypt("categories/a.json", one, key));
        Assert.ThrowsAny<CryptographicException>(() => SyncEncryptedFileCodec.Decrypt("todo_lists/a.json", one, RandomNumberGenerator.GetBytes(32)));
        one[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => SyncEncryptedFileCodec.Decrypt("todo_lists/a.json", one, key));
    }

    [Fact]
    public async Task EnableUnlockAndDisable_RoundTripAllDomainsAndAttachmentBytes()
    {
        using var source = await Device.CreateAsync(); using var peer = await Device.CreateAsync(source.Cloud);
        var (note, task, attachment) = await source.SeedAsync();
        var status = await source.Service.RefreshEncryptionStatusAsync();
        await source.Service.ChangeEncryptionAsync(true, Password, status.Target);
        AssertEncrypted(source.Cloud);
        Assert.DoesNotContain(Password, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(source.KeyFile)));

        source.Cloud.Writes.Clear();
        var locked = await peer.Service.RunAsync();
        Assert.Equal(SyncCycleAbortKind.E2eeKeyMismatch, locked!.AbortKind);
        Assert.Empty(source.Cloud.Writes);
        Assert.Empty(await peer.Db.NewNoteService().GetAllAsync());
        await Assert.ThrowsAsync<SyncEncryptionPasswordException>(() => peer.Service.UnlockEncryptionAsync("wrong password", locked.EncryptionTarget!));
        Assert.False(File.Exists(peer.KeyFile));
        await peer.Service.UnlockEncryptionAsync(Password, locked.EncryptionTarget!);
        Assert.True((await peer.Service.RunAsync())!.Ok);
        Assert.Equal("private note", (await peer.Db.NewNoteService().GetAllAsync()).Single(n => n.Uuid == note.Uuid).Title);
        var downloaded = (await peer.Db.NewTodoRepository().GetTaskByUuidAsync(task.Uuid))!;
        Assert.Equal("private task notes", downloaded.Notes);
        Assert.Single(await peer.Db.NewTodoRepository().GetStepsAsync(downloaded.Id));
        Assert.Equal("private attachment", await File.ReadAllTextAsync(Path.Combine(peer.Db.Paths.AttachmentsDir, attachment.Sha)));
        source.Cloud.Writes.Clear();
        Assert.True((await source.Service.RunAsync())!.Ok);
        Assert.True((await peer.Service.RunAsync())!.Ok);
        Assert.Empty(source.Cloud.Writes);

        var final = await source.Service.ChangeEncryptionAsync(false, null, status.Target);
        Assert.False(final.Enabled);
        Assert.Null(final.ConversionTo);
        foreach (var file in source.Cloud.Files.Where(f => SyncEncryptionManager.IsManagedPath(f.Key)))
            Assert.False(IsEncrypted(file.Key, file.Value));
        Assert.True((await peer.Service.RunAsync())!.Ok);
        Assert.Equal("private note", (await peer.Db.NewNoteService().GetAllAsync()).Single(n => n.Uuid == note.Uuid).Title);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InterruptedConversion_BlocksSyncAndResumesWithoutRewritingCompletedFiles(bool enable)
    {
        using var source = await Device.CreateAsync();
        await source.SeedAsync();
        var status = await source.Service.RefreshEncryptionStatusAsync();
        if (!enable) await source.Service.ChangeEncryptionAsync(true, Password, status.Target);
        var attempts = 0;
        source.Cloud.Writes.Clear();
        source.Cloud.WriteFailure = path => SyncEncryptionManager.IsManagedPath(path) && ++attempts == 2
            ? new SyncDriverException(SyncDriverErrorKind.NetworkUnavailable, "injected connection loss") : null;
        await Assert.ThrowsAsync<SyncDriverException>(() => source.Service.ChangeEncryptionAsync(enable, enable ? Password : null, status.Target));
        var pending = await SyncEncryptionManager.ReadManifestAsync(source.Cloud);
        Assert.Equal(enable, pending.Manifest.EncryptionConversionTo);
        Assert.True(pending.Manifest.E2eeEnabled);
        var completedFile = Assert.Single(source.Cloud.Writes, SyncEncryptionManager.IsManagedPath);
        var completedBytes = source.Cloud.Files[completedFile].ToArray();
        source.Cloud.WriteFailure = null;
        source.Cloud.Writes.Clear();
        var blocked = await source.Service.RunAsync();
        Assert.Equal(SyncCycleAbortKind.EncryptionConversionRequired, blocked!.AbortKind);
        Assert.Empty(source.Cloud.Writes);
        await source.Service.ChangeEncryptionAsync(enable, null, status.Target);
        Assert.Equal(completedBytes, source.Cloud.Files[completedFile]);
        Assert.DoesNotContain(completedFile, source.Cloud.Writes);
        var final = await SyncEncryptionManager.ReadManifestAsync(source.Cloud);
        Assert.Null(final.Manifest.EncryptionConversionTo);
        Assert.Equal(enable, final.Manifest.E2eeEnabled);
        Assert.True((await source.Service.RunAsync())!.Ok);
    }

    [Fact]
    public async Task EmptyEncryptedDestination_StillRejectsWrongPassword()
    {
        using var source = await Device.CreateAsync(); using var peer = await Device.CreateAsync(source.Cloud);
        var status = await source.Service.RefreshEncryptionStatusAsync();
        await source.Service.ChangeEncryptionAsync(true, Password, status.Target);
        Assert.Single(source.Cloud.Files); // verifier lives in manifest, not a sentinel user file
        var blocked = await peer.Service.RunAsync();
        Assert.Equal(SyncCycleAbortKind.E2eeKeyMismatch, blocked!.AbortKind);
        await Assert.ThrowsAsync<SyncEncryptionPasswordException>(() => peer.Service.UnlockEncryptionAsync("incorrect", blocked.EncryptionTarget!));
        await peer.Service.UnlockEncryptionAsync(Password, blocked.EncryptionTarget!);
    }

    [Fact]
    public async Task KeyCache_IsProtectedAndBoundToTargetAndSalt()
    {
        using var source = await Device.CreateAsync();
        var key = RandomNumberGenerator.GetBytes(32);
        var store = new SyncEncryptionKeyStore(source.Db.Paths, NullLogger<SyncEncryptionKeyStore>.Instance);
        await store.SaveAsync("target one", "salt one", key);
        var disk = await File.ReadAllBytesAsync(source.KeyFile);
        Assert.DoesNotContain(Convert.ToBase64String(key), Encoding.UTF8.GetString(disk));
        Assert.Equal(key, await store.LoadAsync("target one", "salt one"));
        Assert.Null(await store.LoadAsync("target two", "salt one"));
        Assert.Null(await store.LoadAsync("target one", "salt two"));
        await store.ForgetAsync("target one");
        Assert.Null(await store.LoadAsync("target one", "salt one"));
    }

    [Fact]
    public async Task Restart_RemembersKey_AndCorruptKeyCacheCanBeUnlockedAgain()
    {
        using var source = await Device.CreateAsync();
        await source.SeedAsync();
        var status = await source.Service.RefreshEncryptionStatusAsync();
        await source.Service.ChangeEncryptionAsync(true, Password, status.Target);
        using (var restarted = source.CreateService()) Assert.True((await restarted.RunAsync())!.Ok);
        await File.WriteAllBytesAsync(source.KeyFile, [1, 2, 3]);
        using var recovered = source.CreateService();
        Assert.Equal(SyncCycleAbortKind.E2eeKeyMismatch, (await recovered.RunAsync())!.AbortKind);
        await recovered.UnlockEncryptionAsync(Password, status.Target);
        Assert.True((await recovered.RunAsync())!.Ok);
    }

    [Fact]
    public async Task EncryptionTargetChange_CannotReusePriorConfirmationOrKey()
    {
        using var source = await Device.CreateAsync();
        var status = await source.Service.RefreshEncryptionStatusAsync();
        await source.Service.ChangeEncryptionAsync(true, Password, status.Target);
        var original = await source.Store.LoadAsync();
        await source.Service.SaveConfigurationAsync(original with { FolderUrl = "https://dav.example.com/other/" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.Service.ChangeEncryptionAsync(false, null, status.Target));
        Assert.Equal(SyncCycleAbortKind.E2eeKeyMismatch, (await source.Service.RunAsync())!.AbortKind);
    }

    [Theory]
    [InlineData(SyncEngine.NotesFolder)]
    [InlineData(SyncEngine.TodoTasksFolder)]
    public async Task IncompleteListing_PreventsAnyCloudEncryptionMutation(string folder)
    {
        using var source = await Device.CreateAsync(); await source.SeedAsync();
        var status = await source.Service.RefreshEncryptionStatusAsync();
        source.Cloud.Writes.Clear(); source.Cloud.TruncatedFolders.Add(folder);
        await Assert.ThrowsAsync<SyncDriverException>(() => source.Service.ChangeEncryptionAsync(true, Password, status.Target));
        Assert.Empty(source.Cloud.Writes);
        Assert.False((await SyncEncryptionManager.ReadManifestAsync(source.Cloud)).Manifest.E2eeEnabled);
    }

    [Fact]
    public async Task CorruptFile_PreflightDoesNotStartConversion()
    {
        using var source = await Device.CreateAsync(); await source.SeedAsync();
        var status = await source.Service.RefreshEncryptionStatusAsync();
        source.Cloud.SeedText("categories/" + Guid.NewGuid().ToString("N") + ".json", "not-json");
        source.Cloud.Writes.Clear();
        await Assert.ThrowsAsync<InvalidDataException>(() => source.Service.ChangeEncryptionAsync(true, Password, status.Target));
        Assert.Empty(source.Cloud.Writes);
    }

    [Fact]
    public async Task ManifestChange_BlocksAnAlreadyStartedPlaintextWriter()
    {
        var cloud = new FakeSyncDriver();
        var plain = JsonSerializer.SerializeToUtf8Bytes(new SyncManifest(), SyncManifestJsonContext.Default.SyncManifest);
        cloud.Seed("manifest.json", plain);
        var cycle = new SyncStorageDriver(cloud, null, plain);
        cloud.SeedText("manifest.json", "{\"formatVersion\":1,\"e2eeEnabled\":true}");
        await Assert.ThrowsAsync<SyncDriverException>(() => cycle.WriteFileAsync("notes/" + Guid.NewGuid().ToString("N") + ".qnote", [1, 2]));
        Assert.Empty(cloud.Writes);
    }

    [Fact]
    public async Task DecryptionProgressDoesNotAnnounceSuccessBeforeManifestFinalization()
    {
        using var source = await Device.CreateAsync(); await source.SeedAsync();
        var status = await source.Service.RefreshEncryptionStatusAsync();
        await source.Service.ChangeEncryptionAsync(true, Password, status.Target);
        var sawFinalProgressWithPendingMarker = false;
        var progress = new InlineProgress(value =>
        {
            if (value.Total > 0 && value.Completed == value.Total)
            {
                var manifest = JsonSerializer.Deserialize(source.Cloud.Files["manifest.json"], SyncManifestJsonContext.Default.SyncManifest)!;
                sawFinalProgressWithPendingMarker = manifest.EncryptionConversionTo == false;
            }
        });
        var done = await source.Service.ChangeEncryptionAsync(false, null, status.Target, progress);
        Assert.True(sawFinalProgressWithPendingMarker);
        Assert.False(done.Enabled);
        Assert.Null(done.ConversionTo);
    }

    private static bool IsEncrypted(string path, byte[] bytes) => path.EndsWith(".qnote", StringComparison.Ordinal)
        ? QnoteContainerCodec.IsEncrypted(bytes) : SyncEncryptedFileCodec.IsEncrypted(bytes);

    private static void AssertEncrypted(FakeSyncDriver cloud)
    {
        Assert.All(cloud.Files.Where(f => SyncEncryptionManager.IsManagedPath(f.Key)), file =>
        {
            Assert.True(IsEncrypted(file.Key, file.Value), file.Key);
            Assert.DoesNotContain("private", Encoding.UTF8.GetString(file.Value));
        });
    }

    private sealed class InlineProgress(Action<SyncEncryptionProgress> callback) : IProgress<SyncEncryptionProgress>
    { public void Report(SyncEncryptionProgress value) => callback(value); }

    private sealed class Device : IDisposable
    {
        public TestDatabase Db { get; } = new();
        public FakeSyncDriver Cloud { get; }
        public SyncConfigurationStore Store { get; }
        public SyncService Service { get; }
        public string KeyFile => Path.Combine(Db.Paths.Root, "sync-encryption-keys.bin");
        private Device(FakeSyncDriver? cloud = null)
        {
            Cloud = cloud ?? new(); Store = new(Db.Paths);
            Service = CreateService();
        }
        public static async Task<Device> CreateAsync(FakeSyncDriver? cloud = null)
        {
            var device = new Device(cloud);
            await device.Store.SaveAsync(new SyncConfiguration { Provider = SyncProvider.WebDav,
                FolderUrl = "https://dav.example.com/encrypted/", Username = "synthetic-user", Password = "synthetic-app-secret" });
            return device;
        }
        public SyncService CreateService() => new(Store, new Tokens(), Db.NewRepository(), new SyncStateStore(Db.Factory), Db.Paths,
            NullLoggerFactory.Instance, new ThrottleGate(10000, TimeSpan.FromMinutes(30)),
            new BackupService(Db.Paths, Db.Factory, Db.NewRepository(), Db.NewTodoRepository(), Db.NewSearchService(),
                Db.NewSettingsService(), Db.NewImageService(), new SchemaInitializer(Db.Factory), NullLogger<BackupService>.Instance),
            driverFactory: _ => Cloud, todos: Db.NewTodoRepository());
        public async Task<(Note Note, TodoTask Task, TodoAttachment Attachment)> SeedAsync()
        {
            var note = await Db.NewNoteService().CreateAsync();
            note = (await Db.NewNoteService().UpdateAsync(note with { Title = "private note", Content = "private body" }))!;
            var todo = Db.NewTodoServiceWithAttachments();
            var task = await todo.CreateTaskAsync(SchemaInitializer.WorkListUuid, "private task");
            await todo.UpdateTaskAsync(task.Id, task.Title, "private task notes");
            await todo.AddStepAsync(task.Id, "private step");
            using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("private attachment"));
            var attachment = await todo.AddAttachmentAsync(task.Id, "private.txt", bytes);
            Assert.True((await Service.RunAsync())!.Ok);
            return (note, task, attachment);
        }
        public void Dispose() { Service.Dispose(); Db.Dispose(); }
    }

    private sealed class Tokens : ITokenProvider
    {
        public Task<AccountInfo?> GetSignedInAccountAsync(CancellationToken ct = default) => Task.FromResult<AccountInfo?>(null);
        public Task<AuthResult> AcquireSilentAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AuthResult> AcquireInteractiveAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SignOutAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
