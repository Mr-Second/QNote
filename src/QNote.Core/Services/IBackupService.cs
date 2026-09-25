using QNote.Models;

namespace QNote.Services;

/// <summary>
/// Backup / restore of the whole data set (notes + categories + settings + original
/// images) as a single <c>.qns</c> file — a ZIP archive with optional AES-256
/// encryption via LibArchive.Net (PRD D1, port of the Qt BackupManager without the
/// external <c>7za.exe</c> child process).
///
/// Archive layout (entries at the root, Qt parity): <c>manifest.json</c>,
/// <c>qnote.db</c> (a consistent <c>VACUUM INTO</c> snapshot of the live database),
/// <c>images/&lt;sha256&gt;.&lt;ext&gt;</c> for every original image.
/// </summary>
public interface IBackupService
{
    /// <summary>File extension of the backup container ("QNote Snapshot").</summary>
    const string ArchiveExtension = ".qns";

    /// <summary>Default backup file name: <c>QNote-backup-yyyyMMdd-HHmmss.qns</c> (Qt parity).</summary>
    string SuggestedBackupFileName();

    /// <summary>
    /// Writes a full backup to <paramref name="destinationPath"> (created or
    /// overwritten). When <paramref name="password"/> is non-empty the archive is
    /// AES-256 encrypted. Reports a 0–1 fraction through <paramref name="progress"/>.
    /// </summary>
    Task BackupAsync(
        string destinationPath,
        string? password = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// True when the archive has encrypted entries (precheck before asking the user
    /// for a password — replaces the Qt build's stderr-scraping probe).
    /// Throws <see cref="BackupException"/> when the file is missing or unreadable.
    /// </summary>
    Task<bool> IsEncryptedAsync(string archivePath, CancellationToken ct = default);

    /// <summary>
    /// Compares the backup against the current database (by note uuid) and validates
    /// manifest/schema compatibility. Throws <see cref="BackupException"/> with
    /// <see cref="BackupErrorKind.PasswordRequired"/> / <see cref="BackupErrorKind.WrongPassword"/>
    /// / <see cref="BackupErrorKind.UnsupportedVersion"/> etc. on expected failures.
    /// </summary>
    Task<RestoreAnalysis> AnalyzeAsync(string archivePath, string? password = null, CancellationToken ct = default);

    /// <summary>
    /// Restores from a backup archive. <see cref="RestoreMode.Overwrite"/> first writes
    /// an automatic unencrypted backup of the CURRENT data into <c>backups\</c> (PRD D4),
    /// then swaps the database file and images; merge modes import per note (PRD D2).
    /// Every mode finishes with a full FTS rebuild so search works immediately in the
    /// current session (PRD D3 — fixes the Qt "restart to search" gap).
    /// </summary>
    Task RestoreAsync(
        string archivePath,
        RestoreMode mode,
        string? password = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}
