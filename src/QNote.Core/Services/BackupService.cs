using Microsoft.Extensions.Logging;

namespace QNote.Services;

/// <summary>
/// Backup / restore as a managed ZIP + AES-GCM <c>.qnotebak</c> container
/// (decision ⑤). Placeholder: the crypto/archive pipeline is the backup task.
/// </summary>
public sealed class BackupService : IBackupService
{
    private readonly ILogger<BackupService> _log;

    public BackupService(ILogger<BackupService> log) => _log = log;

    public Task BackupAsync(
        string destinationPath,
        string? password = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        throw new NotImplementedException("TODO(backup-task): ZIP + AES-GCM backup.");

    public Task RestoreAsync(
        string archivePath,
        string? password = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        throw new NotImplementedException("TODO(backup-task): decrypt + unzip + validate + restore.");
}
