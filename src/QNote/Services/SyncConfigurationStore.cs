using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using QNote.Infrastructure;
using QNote.Sync;

namespace QNote.Services;

public enum SyncProvider { OneDrive, WebDav }
public enum SyncPairingChoice { Merge, CloudReplacesLocal, LocalReplacesCloud }

/// <summary>Per-device configuration. It never enters settings or backup archives.</summary>
public sealed record SyncConfiguration
{
    public SyncProvider Provider { get; init; }
    public string FolderUrl { get; init; } = "";
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";

    public override string ToString() => $"Sync configuration ({Provider})";

    public SyncConfiguration Validate()
    {
        if (!Enum.IsDefined(Provider))
            throw new ArgumentException("The sync provider is invalid.");
        if (Provider == SyncProvider.OneDrive)
            return this;
        var uri = WebDavDriver.NormalizeFolderUrl(FolderUrl);
        if (string.IsNullOrWhiteSpace(Username) || Username.Contains(':') || Username.Any(char.IsControl)
            || string.IsNullOrEmpty(Password) || Password.Any(char.IsControl))
            throw new ArgumentException("WebDAV requires a username and an application password.");
        return this with { FolderUrl = uri.AbsoluteUri, Username = Username.Trim() };
    }
}

[JsonSerializable(typeof(SyncConfiguration))]
internal sealed partial class SyncConfigurationJsonContext : JsonSerializerContext;

/// <summary>Current-user DPAPI works for both MSIX and portable installations.</summary>
public sealed class SyncConfigurationStore
{
    private readonly string _path;
    public SyncConfigurationStore(AppPaths paths) => _path = Path.Combine(paths.Root, "sync-configuration.bin");

    public async Task<SyncConfiguration> LoadAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Sync credentials require Windows DPAPI.");
        if (!File.Exists(_path))
            return new SyncConfiguration();
        var encrypted = await File.ReadAllBytesAsync(_path, ct);
        var plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try
        {
            return (JsonSerializer.Deserialize(plaintext, SyncConfigurationJsonContext.Default.SyncConfiguration)
                ?? throw new InvalidDataException("The sync configuration is empty.")).Validate();
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public async Task SaveAsync(SyncConfiguration configuration, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Sync credentials require Windows DPAPI.");
        configuration = configuration.Validate();
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(configuration, SyncConfigurationJsonContext.Default.SyncConfiguration);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, encrypted, ct);
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
