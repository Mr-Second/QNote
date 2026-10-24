using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace QNote.Sync;

public sealed record SyncManifestRead(SyncManifest Manifest, byte[]? Bytes, string Etag);
public sealed record SyncEncryptionProgress(int Completed, int Total);

/// <summary>Resumable, target-wide encryption conversion over the ordinary transport.</summary>
public static class SyncEncryptionManager
{
    private static readonly string[] Folders = [SyncEngine.NotesFolder, SyncEngine.CategoriesFolder,
        SyncEngine.TombstonesFolder, SyncEngine.TodoListsFolder, SyncEngine.TodoTasksFolder];

    public static async Task<SyncManifestRead> ReadManifestAsync(ISyncDriver driver, CancellationToken ct = default)
    {
        var read = await driver.ReadFileWithEtagAsync(SyncEngine.ManifestPath, ct);
        var manifest = read is null ? new SyncManifest()
            : JsonSerializer.Deserialize(read.Content, SyncManifestJsonContext.Default.SyncManifest)
                ?? throw new InvalidDataException("The encryption manifest is empty.");
        ValidateManifest(manifest);
        return new SyncManifestRead(manifest, read?.Content, read?.Etag ?? "");
    }

    private static void ValidateManifest(SyncManifest manifest)
    {
        if (manifest.FormatVersion != SyncManifest.CurrentFormatVersion)
            throw new InvalidDataException("Unsupported sync manifest format.");
        if (manifest.EncryptionConversionTo is not null && (!manifest.E2eeEnabled
            || !Guid.TryParseExact(manifest.EncryptionConversionId, "N", out _)))
            throw new InvalidDataException("Invalid encryption conversion state.");
        if (manifest.E2eeEnabled)
        {
            if (manifest.EncryptionVersion != 1 || manifest.E2eeKdfIterations != SyncKeyDeriver.Iterations
                || string.IsNullOrEmpty(manifest.E2eeKeyCheck))
                throw new InvalidDataException("Unsupported sync encryption parameters.");
            byte[] salt;
            try { salt = Convert.FromBase64String(manifest.E2eeSalt ?? ""); }
            catch (FormatException ex) { throw new InvalidDataException("Invalid encryption salt.", ex); }
            if (salt.Length != SyncKeyDeriver.SaltByteCount) throw new InvalidDataException("Invalid encryption salt.");
            try
            {
                if (!SyncEncryptedFileCodec.IsEncrypted(Convert.FromBase64String(manifest.E2eeKeyCheck!)))
                    throw new InvalidDataException("Invalid encryption key verifier.");
            }
            catch (FormatException ex) { throw new InvalidDataException("Invalid encryption key verifier.", ex); }
        }
    }

    public static async Task ConvertAsync(ISyncDriver driver, SyncManifestRead expected, SyncManifest encryption,
        byte[] key, bool enable, IProgress<SyncEncryptionProgress>? progress = null, CancellationToken ct = default)
    {
        if (key.Length != SyncKeyDeriver.KeyByteCount) throw new ArgumentException("Invalid encryption key length.", nameof(key));
        ValidateManifest(encryption with { E2eeEnabled = true });
        driver = new EncryptionConversionDriver(driver);
        var current = await ReadManifestAsync(driver, ct);
        if (!SameBytes(current.Bytes, expected.Bytes)) throw Changed();
        if (!SyncEncryptedFileCodec.VerifyKey(encryption.E2eeKeyCheck, key)) throw new CryptographicException("The encryption key could not be verified.");
        if (current.Manifest.EncryptionConversionTo is { } pending && pending != enable)
            throw new InvalidOperationException("A conversion in the other direction must finish first.");
        if (current.Manifest.EncryptionConversionTo is null && current.Manifest.E2eeEnabled == enable) return;
        var files = await ListManagedFilesAsync(driver, ct);
        // Validate the entire source before placing the barrier or changing a file.
        foreach (var file in files)
        {
            var read = await driver.ReadFileAsync(file.Path, ct) ?? throw new InvalidDataException("A cloud file disappeared during encryption preflight.");
            _ = ToPlain(file.Path, read, key);
        }
        var marker = current.Manifest.EncryptionConversionTo is null ? encryption with
        {
            E2eeEnabled = true, EncryptionConversionTo = enable, EncryptionConversionId = Guid.NewGuid().ToString("N"),
        } : current.Manifest;
        if (current.Manifest.EncryptionConversionTo is null)
            current = await WriteManifestAsync(driver, current, marker, ct);
        progress?.Report(new(0, files.Count));
        var completed = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var read = await driver.ReadFileWithEtagAsync(file.Path, ct)
                ?? throw new InvalidDataException("A cloud file disappeared during encryption conversion.");
            var wasEncrypted = IsEncrypted(file.Path, read.Content);
            var plain = ToPlain(file.Path, read.Content, key);
            if (wasEncrypted != enable)
            {
                await AssertManifestAsync(driver, current, ct);
                var converted = FromPlain(file.Path, plain, enable ? key : null);
                await driver.WriteFileAsync(file.Path, converted, EmptyToNull(read.Etag), ct);
            }
            progress?.Report(new(++completed, files.Count));
        }
        // A resumed operation re-lists; files added by an already in-flight peer
        // must also be valid and in the final representation before success.
        var finalFiles = await ListManagedFilesAsync(driver, ct);
        foreach (var file in finalFiles)
        {
            var bytes = await driver.ReadFileAsync(file.Path, ct) ?? throw Changed();
            if (IsEncrypted(file.Path, bytes) != enable) throw Changed();
            _ = ToPlain(file.Path, bytes, key);
        }
        await WriteManifestAsync(driver, current, enable ? marker with
        { EncryptionConversionTo = null, EncryptionConversionId = null }
            : new SyncManifest
            {
                E2eeSalt = marker.E2eeSalt, EncryptionVersion = 1, E2eeKdfIterations = SyncKeyDeriver.Iterations,
                EncryptionDisabledProof = SyncEncryptedFileCodec.CreateDisabledProof(key, marker.E2eeSalt!),
            }, ct);
    }

    public static bool IsManagedPath(string path)
    {
        if (SyncTodoPaths.TryDescribe(path, out _, out _, out _)) return true;
        if (SyncTodoPaths.IsEntryPath(path, SyncEngine.NotesFolder, ".qnote")
            || SyncTodoPaths.IsEntryPath(path, SyncEngine.CategoriesFolder, ".json")) return true;
        const string prefix = "tombstones/";
        return path.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(path[prefix.Length..], "N", out _);
    }

    private static async Task<List<DriverFile>> ListManagedFilesAsync(ISyncDriver driver, CancellationToken ct)
    {
        var files = new List<DriverFile>();
        foreach (var folder in Folders)
        {
            var listing = await driver.ListAsync(folder, ct);
            if (listing.IsTruncated) throw new SyncDriverException(SyncDriverErrorKind.ServiceError, "An incomplete listing blocks encryption conversion.");
            files.AddRange(listing.Files.Where(f => IsManagedPath(f.Path)));
        }
        return files;
    }

    private static bool IsEncrypted(string path, byte[] bytes) => path.EndsWith(".qnote", StringComparison.Ordinal)
        ? QnoteContainerCodec.IsEncrypted(bytes) : SyncEncryptedFileCodec.IsEncrypted(bytes);

    private static byte[] ToPlain(string path, byte[] bytes, byte[] key)
    {
        if (path.EndsWith(".qnote", StringComparison.Ordinal))
        {
            var payload = QnoteContainerCodec.Decode(bytes, QnoteContainerCodec.IsEncrypted(bytes) ? key : null);
            if (payload.Header.Uuid.ToString("N") != Path.GetFileNameWithoutExtension(path))
                throw new InvalidDataException("A container does not match its cloud identity.");
            return QnoteContainerCodec.Encode(payload.Header, payload.Body, payload.Entries);
        }
        var plain = SyncEncryptedFileCodec.IsEncrypted(bytes) ? SyncEncryptedFileCodec.Decrypt(path, bytes, key) : bytes;
        if (path.StartsWith("tombstones/", StringComparison.Ordinal))
        {
            if (plain.Length != sizeof(long)) throw new InvalidDataException("Invalid deletion record.");
            _ = DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64LittleEndian(plain));
        }
        else
        {
            try
            {
                using var document = JsonDocument.Parse(plain);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid cloud metadata.");
            }
            catch (JsonException)
            {
                // Parser diagnostics can echo malformed literal contents. Keep
                // user data out of conversion errors and their rolling logs.
                throw new InvalidDataException("Cloud metadata is invalid; conversion was blocked.");
            }
        }
        return plain;
    }

    private static byte[] FromPlain(string path, byte[] bytes, byte[]? key)
    {
        if (key is null) return bytes;
        if (!path.EndsWith(".qnote", StringComparison.Ordinal)) return SyncEncryptedFileCodec.Encrypt(path, bytes, key);
        var payload = QnoteContainerCodec.Decode(bytes);
        return QnoteContainerCodec.Encode(payload.Header, payload.Body, payload.Entries, key);
    }

    private static async Task<SyncManifestRead> WriteManifestAsync(ISyncDriver driver, SyncManifestRead expected,
        SyncManifest next, CancellationToken ct)
    {
        var current = await AssertManifestAsync(driver, expected, ct);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, SyncManifestJsonContext.Default.SyncManifest);
        var write = await driver.WriteFileAsync(SyncEngine.ManifestPath, bytes, EmptyToNull(current.Etag), ct);
        return new SyncManifestRead(next, bytes, write.Etag);
    }

    private static async Task<SyncManifestRead> AssertManifestAsync(ISyncDriver driver, SyncManifestRead expected, CancellationToken ct)
    {
        var current = await ReadManifestAsync(driver, ct);
        if (!SameBytes(current.Bytes, expected.Bytes)) throw Changed();
        return current;
    }

    private static bool SameBytes(byte[]? left, byte[]? right) => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;
    private static SyncDriverException Changed() => new(SyncDriverErrorKind.PreconditionFailed, "Cloud data or encryption configuration changed; retry the conversion.");
}
