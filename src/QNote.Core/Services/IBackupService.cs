namespace QNote.Services;

/// <summary>Backup / restore (port of the Qt BackupManager).</summary>
public interface IBackupService
{
    /// <summary>Back up the data set to a <c>.qnotebak</c> archive (ZIP + optional AES-GCM).</summary>
    Task BackupAsync(
        string destinationPath,
        string? password = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    /// <summary>Restore from a <c>.qnotebak</c> archive.</summary>
    Task RestoreAsync(
        string archivePath,
        string? password = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}
