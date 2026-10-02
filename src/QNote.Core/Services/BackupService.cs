using System.Globalization;
using System.Text.Json;
using LibArchive.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using QNote.Data;
using QNote.Data.Schema;
using QNote.Infrastructure;
using QNote.Models;

namespace QNote.Services;

/// <summary>
/// Backup / restore over a LibArchive.Net ZIP + optional AES-256 container
/// (<c>.qns</c>, PRD D1–D4). Everything archive- or DB-snapshot-related runs on a
/// background thread (the LibArchive API is synchronous); the repository / search /
/// settings calls stay async.
///
/// Restore consistency contract (PRD: 恢复后数据一致):
/// <list type="bullet">
/// <item>Overwrite: an automatic pre-restore backup is written to
/// <c>AppPaths.BackupsDir</c> (D4), pooled SQLite handles are released
/// (<see cref="SqliteConnection.ClearAllPools"/> — the live DB is WAL-mode, so its
/// <c>-wal</c>/<c>-shm</c> sidecars must be deleted or their frames would replay onto
/// the restored file), the DB file and <c>images\</c> are swapped wholesale, the
/// schema is migrated forward if the backup predates this build, the settings cache
/// is reloaded, and the FTS index is rebuilt (D3).</item>
/// <item>Merge / import-new: notes are imported per uuid through the repository (so
/// the FTS write path stays in-transaction), categories merge by name, and
/// <c>note_images</c> rows + original files are carried over content-addressed.</item>
/// </list>
/// </summary>
public sealed class BackupService : IBackupService
{
    private const string DbEntryName = "qnote.db";
    private const string ManifestEntryName = "manifest.json";
    private const string ImagesEntryPrefix = "images/";

    /// <summary>
    /// Auto-backup retention (PRD 待定项 → decided): keep only the newest N
    /// pre-restore backups in <c>backups\</c>; older ones are pruned after each
    /// successful write so the directory cannot grow without bound.
    /// </summary>
    private const int AutoBackupRetention = 5;

    /// <summary>Oldest backup schema the merge path can read per-note (v2 introduced PlainText).</summary>
    private const long MinMergeableSchema = 2;

    private readonly AppPaths _paths;
    private readonly DbConnectionFactory _factory;
    private readonly INoteRepository _notes;
    private readonly ISearchService _search;
    private readonly ISettingsService _settings;
    private readonly IImageService _images;
    private readonly SchemaInitializer _schema;
    private readonly ILogger<BackupService> _log;

    public BackupService(
        AppPaths paths,
        DbConnectionFactory factory,
        INoteRepository notes,
        ISearchService search,
        ISettingsService settings,
        IImageService images,
        SchemaInitializer schema,
        ILogger<BackupService> log)
    {
        _paths = paths;
        _factory = factory;
        _notes = notes;
        _search = search;
        _settings = settings;
        _images = images;
        _schema = schema;
        _log = log;
    }

    /// <inheritdoc/>
    public string SuggestedBackupFileName() =>
        $"QNote-backup-{DateTime.Now:yyyyMMdd-HHmmss}{IBackupService.ArchiveExtension}";

    /// <inheritdoc/>
    public IReadOnlyList<string> ListAutoBackups()
    {
        try
        {
            // The timestamp file name sorts lexicographically = chronologically.
            return Directory.EnumerateFiles(_paths.BackupsDir, $"auto-backup-*{IBackupService.ArchiveExtension}")
                .OrderDescending(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "Listing auto-backups failed");
            return [];
        }
    }

    /// <inheritdoc/>
    public async Task BackupAsync(
        string destinationPath,
        string? password = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("Destination path is empty.", nameof(destinationPath));

        await Task.Run(() => BackupCore(destinationPath, password, progress, ct), ct);
        _log.LogInformation("Backup written to {Path} (encrypted: {Encrypted})", destinationPath, !string.IsNullOrEmpty(password));
    }

    /// <inheritdoc/>
    public Task<bool> IsEncryptedAsync(string archivePath, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (!File.Exists(archivePath))
                throw NotFound(archivePath);

            try
            {
                using var reader = new LibArchiveReader(archivePath);
                // HasEncryptedEntries only reflects headers already read — walk them all.
                foreach (var entry in reader.Entries())
                    _ = entry.Name;
                return reader.HasEncryptedEntries() > 0;
            }
            catch (ApplicationException ex)
            {
                throw new BackupException(BackupErrorKind.NotAnArchive,
                    $"无法读取备份文件（不是有效的归档）：{archivePath}", ex);
            }
        }, ct);

    /// <inheritdoc/>
    public async Task<RestoreAnalysis> AnalyzeAsync(string archivePath, string? password = null, CancellationToken ct = default)
    {
        var staging = await Task.Run(() => ExtractToStaging(archivePath, password), ct);
        try
        {
            var manifest = await Task.Run(() => ValidateStaging(staging, requireMergeable: true), ct);
            var backupUuids = await Task.Run(() => BackupDatabaseReader.ReadUuidMap(StagedDbPath(staging)), ct);
            var currentUuids = await _notes.GetUuidTimestampsAsync(ct);

            var conflict = 0;
            var added = 0;
            foreach (var uuid in backupUuids.Keys)
            {
                if (currentUuids.ContainsKey(uuid))
                    conflict++;
                else
                    added++;
            }
            var currentOnly = currentUuids.Keys.Count(uuid => !backupUuids.ContainsKey(uuid));

            return new RestoreAnalysis(
                backupUuids.Count,
                conflict,
                added,
                currentOnly,
                ParseManifestDate(manifest));
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <inheritdoc/>
    public async Task RestoreAsync(
        string archivePath,
        RestoreMode mode,
        string? password = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (mode == RestoreMode.Overwrite)
            await RestoreOverwriteAsync(archivePath, password, progress, ct);
        else
            await RestoreMergeAsync(archivePath, mode, password, progress, ct);
    }

    // ---------- Backup ----------

    private void BackupCore(string destinationPath, string? password, IProgress<double>? progress, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Write to a sibling temp file first: a crash/cancel mid-archive must never
        // leave a truncated file at the user's chosen path.
        var tempArchive = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var dbSnapshot = Path.Combine(Path.GetTempPath(), $"qnote-backup-db-{Guid.NewGuid():N}.db");
        try
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(0.05);

            // Consistent snapshot of the live WAL-mode database. VACUUM INTO also
            // folds the WAL in, so the archived qnote.db is complete on its own.
            SnapshotDatabase(dbSnapshot);
            progress?.Report(0.15);

            ct.ThrowIfCancellationRequested();
            using (var writer = new LibArchiveWriter(
                tempArchive,
                ArchiveFormat.Zip,
                password: string.IsNullOrEmpty(password) ? null : password,
                encryption: EncryptionType.AES256))
            {
                var manifest = new BackupManifest
                {
                    AppVersion = typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Encrypted = !string.IsNullOrEmpty(password),
                    SchemaVersion = (int)SchemaInitializer.CurrentVersion,
                };
                writer.AddEntry(ManifestEntryName,
                    JsonSerializer.SerializeToUtf8Bytes(manifest, BackupJsonContext.Default.BackupManifest));

                writer.AddFile(dbSnapshot, DbEntryName);
                progress?.Report(0.3);

                ct.ThrowIfCancellationRequested();
                var images = Directory.Exists(_paths.ImagesDir)
                    ? new DirectoryInfo(_paths.ImagesDir).EnumerateFiles()
                        .Where(f => !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                        .ToList()
                    : [];
                if (images.Count > 0)
                {
                    writer.AddFiles(
                        images,
                        pathMapper: f => ImagesEntryPrefix + f.Name,
                        progress: new Progress<FileProgress>(p =>
                            progress?.Report(0.3 + 0.7 * (p.TotalBytes > 0 ? (double)p.BytesProcessed / p.TotalBytes : 1.0))));
                }
            }

            File.Move(tempArchive, destinationPath, overwrite: true);
            progress?.Report(1.0);
        }
        finally
        {
            TryDeleteFile(tempArchive);
            TryDeleteFile(dbSnapshot);
        }
    }

    private void SnapshotDatabase(string snapshotPath)
    {
        using var conn = _factory.OpenRead();
        using var cmd = conn.CreateCommand();
        // VACUUM INTO takes a string expression — a bound parameter is valid and
        // keeps the temp path out of the SQL text.
        cmd.CommandText = "VACUUM INTO $path;";
        cmd.Parameters.AddWithValue("$path", snapshotPath);
        cmd.ExecuteNonQuery();
    }

    // ---------- Restore: overwrite ----------

    private async Task RestoreOverwriteAsync(string archivePath, string? password, IProgress<double>? progress, CancellationToken ct)
    {
        // D4: safety net before anything destructive — unencrypted, into backups\.
        Directory.CreateDirectory(_paths.BackupsDir);
        var autoPath = Path.Combine(_paths.BackupsDir,
            $"auto-backup-{DateTime.Now:yyyyMMdd-HHmmss}{IBackupService.ArchiveExtension}");
        await Task.Run(
            () => BackupCore(autoPath, password: null,
                new Progress<double>(p => progress?.Report(0.4 * p)), ct), ct);
        PruneAutoBackups();

        var staging = await Task.Run(() => ExtractToStaging(archivePath, password), ct);
        try
        {
            // Overwrite swaps the DB file, so ANY schema ≤ current is fine — the
            // post-swap migration brings it forward. requireMergeable: false.
            await Task.Run(() => ValidateStaging(staging, requireMergeable: false), ct);
            progress?.Report(0.55);

            await Task.Run(() => ReplaceDataFiles(staging), ct);
            progress?.Report(0.8);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }

        // Migrate forward if the backup predates this build, then refresh everything
        // that caches state from the old file, then rebuild FTS (D3).
        await Task.Run(() => _schema.EnsureCreated(), ct);
        await _settings.ReloadAsync(ct);
        progress?.Report(0.9);
        await _search.RebuildIndexAsync(ct);
        progress?.Report(1.0);
        _log.LogInformation("Overwrite restore from {Path} completed.", archivePath);
    }

    private void ReplaceDataFiles(string staging)
    {
        // Release pooled connection handles — on Windows they pin the DB file.
        SqliteConnection.ClearAllPools();

        // Delete the WAL sidecars of the OLD database first: otherwise their frames
        // would be replayed onto the restored file and corrupt it logically.
        TryDeleteFile(_paths.DatabasePath + "-wal");
        TryDeleteFile(_paths.DatabasePath + "-shm");
        File.Copy(StagedDbPath(staging), _paths.DatabasePath, overwrite: true);

        // Wholesale image swap: merge-free overwrite must leave note_images ↔ disk
        // exactly as the backup had it, so current-only originals are removed too.
        Directory.CreateDirectory(_paths.ImagesDir);
        foreach (var file in Directory.EnumerateFiles(_paths.ImagesDir))
            TryDeleteFile(file);

        var stagedImages = Path.Combine(staging, "images");
        if (!Directory.Exists(stagedImages))
            return;
        foreach (var file in Directory.EnumerateFiles(stagedImages))
            File.Copy(file, Path.Combine(_paths.ImagesDir, Path.GetFileName(file)), overwrite: true);
    }

    private void PruneAutoBackups()
    {
        try
        {
            var stale = new DirectoryInfo(_paths.BackupsDir)
                .EnumerateFiles($"auto-backup-*{IBackupService.ArchiveExtension}")
                .OrderByDescending(f => f.Name) // timestamped names sort newest-first
                .Skip(AutoBackupRetention);
            foreach (var file in stale)
            {
                file.Delete();
                _log.LogInformation("Pruned old auto-backup {Path}", file.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retention is housekeeping — a locked file must not fail the restore.
            _log.LogWarning(ex, "Auto-backup pruning failed; continuing.");
        }
    }

    // ---------- Restore: merge / import-new ----------

    private async Task RestoreMergeAsync(
        string archivePath, RestoreMode mode, string? password, IProgress<double>? progress, CancellationToken ct)
    {
        var staging = await Task.Run(() => ExtractToStaging(archivePath, password), ct);
        try
        {
            await Task.Run(() => ValidateStaging(staging, requireMergeable: true), ct);
            progress?.Report(0.1);

            var stagedDb = StagedDbPath(staging);
            var backupNotes = await Task.Run(() => BackupDatabaseReader.ReadNotes(stagedDb), ct);
            var backupCategories = await Task.Run(() => BackupDatabaseReader.ReadCategories(stagedDb), ct);
            var backupImages = await Task.Run(() => BackupDatabaseReader.ReadNoteImagesByNote(stagedDb), ct);

            var currentUuids = await _notes.GetUuidTimestampsAsync(ct);

            // Categories merge by NAME (notes link to categories by name — schema §v4).
            var currentCategoryNames = (await _notes.GetCategoriesAsync(ct))
                .Select(c => c.Name)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var category in backupCategories.OrderBy(c => c.SortOrder))
            {
                if (currentCategoryNames.Add(category.Name))
                {
                    await _notes.CreateCategoryAsync(new Category
                    {
                        Name = category.Name,
                        IconKey = category.IconKey,
                        Color = category.Color,
                    }, ct);
                }
            }
            progress?.Report(0.2);

            var imported = 0;
            var updated = 0;
            var skipped = 0;
            var done = 0;
            foreach (var note in backupNotes)
            {
                ct.ThrowIfCancellationRequested();
                if (currentUuids.TryGetValue(note.Uuid, out var existing))
                {
                    // Merge keeps the newer UpdatedAt; import-new skips conflicts outright.
                    if (mode == RestoreMode.Merge && note.UpdatedAt > existing.UpdatedAt)
                    {
                        await _notes.UpdateAsync(note with { Id = existing.Id }, ct);
                        await RestoreNoteImagesAsync(staging, existing.Id, ImagesOf(backupImages, note.Id), ct);
                        updated++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
                else
                {
                    var created = await _notes.CreateAsync(note, ct);
                    await RestoreNoteImagesAsync(staging, created.Id, ImagesOf(backupImages, note.Id), ct);
                    imported++;
                }

                done++;
                progress?.Report(0.2 + 0.7 * ((double)done / Math.Max(1, backupNotes.Count)));
            }

            // D3: search must work in THIS session, not only after a restart (Qt gap).
            await _search.RebuildIndexAsync(ct);
            progress?.Report(1.0);
            _log.LogInformation(
                "{Mode} restore from {Path} completed: {Imported} imported, {Updated} updated, {Skipped} skipped.",
                mode, archivePath, imported, updated, skipped);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// Carries a restored note's original images over: copies the original files
    /// from the staging dir (content-addressed — an existing file is already the
    /// same bytes), links the rows to the (new) note id, drops stale links on a
    /// merge-update, and prunes originals no note references any more.
    /// </summary>
    private async Task RestoreNoteImagesAsync(
        string staging, long noteId, IReadOnlyList<NoteImage> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
            return;

        foreach (var row in rows)
        {
            var stagedFile = Path.Combine(staging, "images", $"{row.Sha256}.{row.Ext}");
            var target = Path.Combine(_paths.ImagesDir, $"{row.Sha256}.{row.Ext}");
            if (File.Exists(target))
                continue;
            if (!File.Exists(stagedFile))
            {
                // The archive is missing an original its note_images row references —
                // keep the row (metadata is still useful) and move on.
                _log.LogWarning("Backup lacks original image {Sha}.{Ext}; linking metadata only.", row.Sha256, row.Ext);
                continue;
            }
            Directory.CreateDirectory(_paths.ImagesDir);
            File.Copy(stagedFile, target);
        }

        await _notes.AddNoteImagesAsync(noteId,
            rows.Select(r => r with { NoteId = noteId }).ToArray(), ct);

        // On a merge-update the newer note may reference FEWER images — drop the
        // stale links and prune originals no note uses any more.
        var orphans = await _notes.SyncNoteImagesAsync(noteId,
            rows.Select(r => r.Sha256).ToArray(), ct);
        await _images.DeleteOriginalsAsync(orphans, ct);
    }

    private static IReadOnlyList<NoteImage> ImagesOf(
        Dictionary<long, List<NoteImage>> byNote, long backupNoteId) =>
        byNote.TryGetValue(backupNoteId, out var rows) ? rows : [];

    // ---------- Archive extraction & validation ----------

    /// <summary>
    /// Extracts the recognized entries (manifest, qnote.db, images/*) into a fresh
    /// temp dir and returns its path. Entry names are whitelisted by construction —
    /// no archive path can escape the staging dir (zip-slip safe).
    /// </summary>
    private string ExtractToStaging(string archivePath, string? password)
    {
        if (!File.Exists(archivePath))
            throw NotFound(archivePath);

        // Header pass without a password: detect encryption up front so an encrypted
        // archive asked to open passwordless fails as PasswordRequired, not as a
        // mid-extraction decryption error.
        bool encrypted;
        try
        {
            using var probe = new LibArchiveReader(archivePath);
            foreach (var entry in probe.Entries())
                _ = entry.Name;
            encrypted = probe.HasEncryptedEntries() > 0;
        }
        catch (ApplicationException ex)
        {
            throw new BackupException(BackupErrorKind.NotAnArchive,
                $"无法读取备份文件（不是有效的归档）：{archivePath}", ex);
        }

        if (encrypted && string.IsNullOrEmpty(password))
            throw new BackupException(BackupErrorKind.PasswordRequired, "该备份已加密，需要密码。");

        var staging = Path.Combine(Path.GetTempPath(), $"qnote-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            using var reader = new LibArchiveReader(archivePath, password: password);
            foreach (var entry in reader.Entries())
            {
                if (!entry.IsRegularFile)
                    continue;

                var name = entry.Name.Replace('\\', '/');
                string? target = name switch
                {
                    ManifestEntryName => Path.Combine(staging, ManifestEntryName),
                    DbEntryName => Path.Combine(staging, DbEntryName),
                    _ when name.StartsWith(ImagesEntryPrefix, StringComparison.Ordinal)
                        => SafeImageTarget(staging, name[ImagesEntryPrefix.Length..]),
                    _ => null, // unknown entries are ignored (forward compatibility)
                };
                if (target is null)
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var output = File.Create(target);
                entry.Stream.CopyTo(output);
            }
            return staging;
        }
        catch (ApplicationException ex)
        {
            TryDeleteDirectory(staging);
            // A decryption failure on an encrypted archive is a wrong password (or a
            // damaged file — indistinguishable here); anything else is corruption.
            throw encrypted
                ? new BackupException(BackupErrorKind.WrongPassword, "密码错误，或备份文件已损坏。", ex)
                : new BackupException(BackupErrorKind.CorruptArchive, "备份文件已损坏，无法解包。", ex);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    /// <summary>
    /// Image entry names must be plain file names — anything else is dropped.
    /// ':' is rejected too: it is invalid in NTFS file names and is the alternate
    /// data stream separator, so a crafted "C:x" name would otherwise write an ADS.
    /// </summary>
    private static string? SafeImageTarget(string staging, string fileName)
    {
        if (fileName.Length == 0
            || fileName.Contains('/')
            || fileName.Contains(':')
            || fileName.Contains("..", StringComparison.Ordinal))
            return null;
        return Path.Combine(staging, "images", fileName);
    }

    /// <summary>
    /// Reads and validates the manifest + staged DB schema version. Returns the
    /// manifest when present (manifest.json is OPTIONAL — Qt-parity archives and
    /// hand-built ones may lack it; the DB's own user_version is the fallback).
    /// </summary>
    private BackupManifest? ValidateStaging(string staging, bool requireMergeable)
    {
        var stagedDb = StagedDbPath(staging);
        if (!File.Exists(stagedDb))
            throw new BackupException(BackupErrorKind.CorruptArchive, "备份中缺少 qnote.db。");

        BackupManifest? manifest = null;
        var manifestPath = Path.Combine(staging, ManifestEntryName);
        if (File.Exists(manifestPath))
        {
            try
            {
                manifest = JsonSerializer.Deserialize(
                    File.ReadAllBytes(manifestPath), BackupJsonContext.Default.BackupManifest);
            }
            catch (JsonException ex)
            {
                throw new BackupException(BackupErrorKind.CorruptArchive, "备份清单 manifest.json 无法解析。", ex);
            }
        }

        long schemaVersion;
        try
        {
            schemaVersion = BackupDatabaseReader.GetUserVersion(stagedDb);
        }
        catch (SqliteException ex)
        {
            throw new BackupException(BackupErrorKind.CorruptArchive, "备份中的 qnote.db 不是有效的数据库。", ex);
        }

        // Cross-check: a manifest claiming a NEWER schema than the DB it ships is
        // nonsense — trust the DB, but still reject anything newer than this build.
        var effective = Math.Max(schemaVersion, manifest?.SchemaVersion ?? 0);
        if (effective > SchemaInitializer.CurrentVersion)
        {
            throw new BackupException(BackupErrorKind.UnsupportedVersion,
                $"备份由更新版本的 QNote 创建（数据库结构 v{effective}，当前支持 v{SchemaInitializer.CurrentVersion}），请升级应用后再恢复。");
        }
        if (requireMergeable && effective < MinMergeableSchema)
        {
            throw new BackupException(BackupErrorKind.UnsupportedVersion,
                $"备份的数据库结构过旧（v{effective}），无法逐条合并；请改用覆盖恢复。");
        }

        return manifest;
    }

    // ---------- Helpers ----------

    private static string StagedDbPath(string staging) => Path.Combine(staging, DbEntryName);

    private static BackupException NotFound(string archivePath) =>
        new(BackupErrorKind.ArchiveNotFound, $"备份文件不存在：{archivePath}");

    private static DateTimeOffset? ParseManifestDate(BackupManifest? manifest) =>
        manifest is not null
        && DateTimeOffset.TryParse(manifest.CreatedAt, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created)
            ? created
            : null;

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp/sidecar cleanup is best-effort; leftovers are harmless.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp staging cleanup is best-effort.
        }
    }
}
