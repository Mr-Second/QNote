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
/// The todo domain (todo_lists + todo_tasks + todo_steps + todo_attachments
/// rows, schema v7) rides inside the archived <c>qnote.db</c> snapshot; the
/// attachment BYTES travel as <c>attachments/</c> entries (the same shape as
/// <c>images/</c>), and nothing is filtered at backup time.
///
/// Restore consistency contract (PRD: 恢复后数据一致; todo domain, Requirement 8):
/// <list type="bullet">
/// <item>Overwrite: an automatic pre-restore backup is written to
/// <c>AppPaths.BackupsDir</c> (D4), pooled SQLite handles are released
/// (<see cref="SqliteConnection.ClearAllPools"/> — the live DB is WAL-mode, so its
/// <c>-wal</c>/<c>-shm</c> sidecars must be deleted or their frames would replay onto
/// the restored file), the DB file and <c>images\</c>/<c>attachments\</c> are swapped
/// wholesale (the todo tables come along by construction), the schema is migrated
/// forward if the backup predates this build, the settings cache is reloaded, and
/// the FTS index is rebuilt (D3).</item>
/// <item>Merge / import-new: notes are imported per uuid through the repository (so
/// the FTS write path stays in-transaction), categories merge by name, and
/// <c>note_images</c> rows + original files are carried over content-addressed. The
/// todo domain merges per uuid with <c>UpdatedAt</c> LWW (epoch-ms integers in
/// storage, compared as UTC instants) — lists first, then tasks, then the
/// stage-⑤ children (steps + attachments) relinked through the tasks' live-id
/// map; the catch-up reminder marker travels with the winning task row, and
/// attachment bytes whose row loses its last reference are pruned.</item>
/// </list>
/// Every restore mode ends with a best-effort todo reminder reconcile: restored
/// data may want toasts the OS schedule does not know about (imported tasks with
/// reminders) or may no longer want ones it still holds (tasks the archive lacked).
/// </summary>
public sealed class BackupService : IBackupService
{
    private const string DbEntryName = "qnote.db";
    private const string ManifestEntryName = "manifest.json";
    private const string ImagesEntryPrefix = "images/";
    private const string AttachmentsEntryPrefix = "attachments/";

    /// <summary>
    /// Auto-backup retention (PRD 待定项 → decided): keep only the newest N
    /// pre-restore backups in <c>backups\</c>; older ones are pruned after each
    /// successful write so the directory cannot grow without bound.
    /// </summary>
    private const int AutoBackupRetention = 5;

    /// <summary>
    /// Oldest backup schema the merge path can read per-note. v8 is the floor:
    /// the merge reader is v8-only by decision (no type probes for pre-v8 TEXT
    /// timestamps or the missing DeletedAt column). Overwrite restore is NOT gated
    /// here — it swaps the file and migrates it forward.
    /// </summary>
    private const long MinMergeableSchema = 8;

    private readonly AppPaths _paths;
    private readonly DbConnectionFactory _factory;
    private readonly INoteRepository _notes;
    private readonly ITodoRepository _todo;
    private readonly ISearchService _search;
    private readonly ISettingsService _settings;
    private readonly IImageService _images;
    private readonly SchemaInitializer _schema;
    private readonly ILogger<BackupService> _log;
    private readonly ITodoReminderCoordinator? _reminders;

    public BackupService(
        AppPaths paths,
        DbConnectionFactory factory,
        INoteRepository notes,
        ITodoRepository todo,
        ISearchService search,
        ISettingsService settings,
        IImageService images,
        SchemaInitializer schema,
        ILogger<BackupService> log,
        ITodoReminderCoordinator? reminders = null)
    {
        _paths = paths;
        _factory = factory;
        _notes = notes;
        _todo = todo;
        _search = search;
        _settings = settings;
        _images = images;
        _schema = schema;
        _log = log;
        _reminders = reminders;
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

    public async Task<string> CreateAutoBackupAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_paths.BackupsDir);
        var path = Path.Combine(_paths.BackupsDir,
            $"auto-backup-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}{IBackupService.ArchiveExtension}");
        await BackupAsync(path, ct: ct);
        PruneAutoBackups();
        return path;
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

        try
        {
            await Task.Run(() => BackupCore(destinationPath, password, progress, ct), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The dialog tells the user "查看日志" — the failure must actually BE
            // in the log (2026-10-16: a non-ASCII destination failure was invisible).
            _log.LogError(ex, "Backup to {Path} failed.", destinationPath);
            throw;
        }
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
                // Stream-based open (see BackupCore): the path-based reader ctor
                // cannot open non-ASCII paths on Windows.
                return ProbeEncrypted(archivePath);
            }
            catch (ApplicationException ex)
            {
                throw new BackupException(BackupErrorKind.NotAnArchive,
                    $"Not a readable archive: {archivePath}", ex);
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
        try
        {
            if (mode == RestoreMode.Overwrite)
                await RestoreOverwriteAsync(archivePath, password, progress, ct);
            else
                await RestoreMergeAsync(archivePath, mode, password, progress, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same "查看日志" contract as BackupAsync — failures must be logged.
            _log.LogError(ex, "{Mode} restore from {Path} failed.", mode, archivePath);
            throw;
        }

        // Post-restore todo reminder reconcile (best-effort, same semantics as the
        // launch reconcile): a restored task set may want toasts the OS schedule
        // does not hold yet, and the schedule may hold toasts for tasks the
        // restored data no longer has. Reminder trouble never fails the restore.
        if (_reminders is not null)
        {
            try
            {
                await _reminders.ReconcileAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // The restore itself completed; a cancelled reconcile only means
                // the next launch reconcile sweeps the schedule.
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Post-restore todo reminder reconcile failed (best-effort).");
            }
        }
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
            // The archive file is opened as a .NET FileStream and handed to
            // libarchive as a STREAM: LibArchive.Net 0.3.1's PATH-based ctors
            // marshal non-ASCII paths through the native ANSI codepage on
            // Windows — a 中文 destination either fails to open or lands under a
            // mojibake name that File.Move then cannot find (live repro
            // 2026-10-16). Entry SOURCE paths (AddFile/AddFiles) are unaffected
            // (managed reads), verified by the non-ASCII round-trip test.
            using (var archiveStream = new FileStream(tempArchive, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new LibArchiveWriter(
                archiveStream,
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
                            progress?.Report(0.3 + 0.35 * (p.TotalBytes > 0 ? (double)p.BytesProcessed / p.TotalBytes : 1.0))));
                }

                // Todo attachment bytes (stage ⑤): content-addressed files under
                // attachments/ — the todo_attachments ROWS ride inside the DB
                // snapshot; these entries carry the bytes the rows reference.
                ct.ThrowIfCancellationRequested();
                var attachments = Directory.Exists(_paths.AttachmentsDir)
                    ? new DirectoryInfo(_paths.AttachmentsDir).EnumerateFiles()
                        .Where(f => !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                        .ToList()
                    : [];
                if (attachments.Count > 0)
                {
                    writer.AddFiles(
                        attachments,
                        pathMapper: f => AttachmentsEntryPrefix + f.Name,
                        progress: new Progress<FileProgress>(p =>
                            progress?.Report(0.65 + 0.35 * (p.TotalBytes > 0 ? (double)p.BytesProcessed / p.TotalBytes : 1.0))));
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
        await CreateAutoBackupAsync(ct);
        progress?.Report(0.4);

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

        // Wholesale file-store swaps: merge-free overwrite must leave DB rows ↔
        // disk exactly as the backup had it, so current-only originals are
        // removed too. A store absent from the archive restores as EMPTY (its
        // rows rode the DB file swap above).
        SwapStoreDirectory(Path.Combine(staging, "images"), _paths.ImagesDir);
        SwapStoreDirectory(Path.Combine(staging, "attachments"), _paths.AttachmentsDir);
    }

    /// <summary>
    /// Replaces <paramref name="liveDir"/> with the staged directory's files
    /// wholesale: current-only files are deleted, staged files copied over. A
    /// missing staged dir means the archive held none — the live dir is wiped.
    /// </summary>
    private static void SwapStoreDirectory(string stagedDir, string liveDir)
    {
        Directory.CreateDirectory(liveDir);
        foreach (var file in Directory.EnumerateFiles(liveDir))
            TryDeleteFile(file);

        if (!Directory.Exists(stagedDir))
            return;
        foreach (var file in Directory.EnumerateFiles(stagedDir))
            File.Copy(file, Path.Combine(liveDir, Path.GetFileName(file)), overwrite: true);
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
            var backupLists = await Task.Run(() => BackupDatabaseReader.ReadTodoLists(stagedDb), ct);
            var backupTasks = await Task.Run(() => BackupDatabaseReader.ReadTodoTasks(stagedDb), ct);

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
                        Uuid = category.Uuid,
                        CreatedAt = category.CreatedAt,
                        UpdatedAt = category.UpdatedAt,
                    }, ct);
                }
            }
            progress?.Report(0.15);

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
                        // ImportNoteAsync, not UpdateAsync: a merge must land the
                        // archived row as-is (uuid + both timestamps), and it is the
                        // only writer that can CLEAR a tombstone — which is exactly
                        // what restoring a note this device deleted has to do.
                        await _notes.ImportNoteAsync(note with { Id = existing.Id }, ct: ct);
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
                    var created = await _notes.ImportNoteAsync(note, ct: ct);
                    await RestoreNoteImagesAsync(staging, created.Id, ImagesOf(backupImages, note.Id), ct);
                    imported++;
                }

                done++;
                progress?.Report(0.15 + 0.55 * ((double)done / Math.Max(1, backupNotes.Count)));
            }

            // Todo domain (Requirement 8): per-uuid LWW merge / import-new, lists
            // FIRST so every imported task's ListUuid resolves; then tasks, whose
            // live-id map relinks the stage-⑤ children (steps + attachments) that
            // reference them. Timestamps compare as UTC instants (the epoch-ms
            // storage shape is the repo's concern).
            var todoImported = await RestoreTodoListsAsync(backupLists, mode, progress, ct);
            var (tasksImported, tasksUpdated, tasksSkipped, localTaskIdByBackupId) =
                await RestoreTodoTasksAsync(backupTasks, mode, progress, ct);

            var backupSteps = await Task.Run(() => BackupDatabaseReader.ReadTodoSteps(stagedDb), ct);
            var (stepsImported, stepsUpdated, stepsSkipped) =
                await RestoreTodoStepsAsync(backupSteps, localTaskIdByBackupId, mode, progress, ct);

            var backupAttachments = await Task.Run(() => BackupDatabaseReader.ReadTodoAttachments(stagedDb), ct);
            var (attachmentsImported, attachmentsUpdated, attachmentsSkipped) =
                await RestoreTodoAttachmentsAsync(staging, backupAttachments, localTaskIdByBackupId, mode, progress, ct);

            // D3: search must work in THIS session, not only after a restart (Qt gap).
            await _search.RebuildIndexAsync(ct);
            progress?.Report(1.0);
            _log.LogInformation(
                "{Mode} restore from {Path} completed: {Imported} imported, {Updated} updated, {Skipped} skipped; " +
                "todo: {TodoLists} lists, {TaskImported} tasks imported, {TaskUpdated} updated, {TaskSkipped} skipped; " +
                "steps: {StepsImported}/{StepsUpdated}/{StepsSkipped}; attachments: {AttachImported}/{AttachUpdated}/{AttachSkipped}.",
                mode, archivePath, imported, updated, skipped,
                todoImported, tasksImported, tasksUpdated, tasksSkipped,
                stepsImported, stepsUpdated, stepsSkipped,
                attachmentsImported, attachmentsUpdated, attachmentsSkipped);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// Merges the archive's todo lists by uuid: absent locally → import (both
    /// modes); present → Merge overwrites the local row whole when the archive's
    /// <c>UpdatedAt</c> is strictly newer, import-new skips. Returns the number of
    /// imported (newly-created) lists.
    /// </summary>
    private async Task<int> RestoreTodoListsAsync(
        List<TodoList> backupLists, RestoreMode mode, IProgress<double>? progress, CancellationToken ct)
    {
        if (backupLists.Count == 0)
            return 0;

        var current = await _todo.GetListUuidTimestampsAsync(ct);
        var imported = 0;
        var done = 0;
        foreach (var list in backupLists)
        {
            ct.ThrowIfCancellationRequested();
            if (!current.ContainsKey(list.Uuid))
            {
                await _todo.ImportListAsync(list, ct);
                imported++;
            }
            else if (mode == RestoreMode.Merge
                && list.UpdatedAt > current[list.Uuid].UpdatedAt)
            {
                // Archive row is newer → it wins whole-row (the upsert keeps the
                // live Id; LWW decided here, identity carried by the uuid).
                await _todo.ImportListAsync(list, ct);
                // not counted as "imported" — the row already existed locally
            }

            done++;
            progress?.Report(0.7 + 0.05 * ((double)done / backupLists.Count));
        }
        return imported;
    }

    /// <summary>
    /// Merges the archive's todo tasks by uuid (same LWW semantics as the lists;
    /// the catch-up reminder marker travels with the winning row). Also builds
    /// the staged-Id → live-Id map the step/attachment children need to relink.
    /// Returns (imported, updated, skipped, localTaskIdByBackupId).
    /// </summary>
    private async Task<(int Imported, int Updated, int Skipped, Dictionary<long, long> LocalTaskIdByBackupId)> RestoreTodoTasksAsync(
        List<TodoTask> backupTasks, RestoreMode mode, IProgress<double>? progress, CancellationToken ct)
    {
        var localTaskIdByBackupId = new Dictionary<long, long>();
        if (backupTasks.Count == 0)
            return (0, 0, 0, localTaskIdByBackupId);

        var current = await _todo.GetTaskUuidTimestampsAsync(ct);
        var imported = 0;
        var updated = 0;
        var skipped = 0;
        var done = 0;
        foreach (var task in backupTasks)
        {
            ct.ThrowIfCancellationRequested();
            if (current.TryGetValue(task.Uuid, out var existing))
            {
                localTaskIdByBackupId[task.Id] = existing.Id;
                if (mode == RestoreMode.Merge && task.UpdatedAt > existing.UpdatedAt)
                {
                    await _todo.ImportTaskAsync(task, ct);
                    updated++;
                }
                else
                {
                    skipped++;
                }
            }
            else
            {
                var created = await _todo.ImportTaskAsync(task, ct);
                localTaskIdByBackupId[task.Id] = created.Id;
                imported++;
            }

            done++;
            progress?.Report(0.75 + 0.15 * ((double)done / backupTasks.Count));
        }
        return (imported, updated, skipped, localTaskIdByBackupId);
    }

    /// <summary>
    /// Merges the archive's todo steps by uuid (same LWW semantics as the tasks).
    /// Each archived step's <c>TaskId</c> is remapped to the live task id before
    /// the upsert, so steps follow their task whether it was imported, updated
    /// or left alone locally. Returns (imported, updated, skipped) counts.
    /// </summary>
    private async Task<(int Imported, int Updated, int Skipped)> RestoreTodoStepsAsync(
        List<TodoStep> backupSteps,
        IReadOnlyDictionary<long, long> localTaskIdByBackupId,
        RestoreMode mode,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        if (backupSteps.Count == 0)
            return (0, 0, 0);

        var current = await _todo.GetStepUuidTimestampsAsync(ct);
        var imported = 0;
        var updated = 0;
        var skipped = 0;
        var done = 0;
        foreach (var archivedStep in backupSteps)
        {
            ct.ThrowIfCancellationRequested();
            if (!localTaskIdByBackupId.TryGetValue(archivedStep.TaskId, out var localTaskId))
            {
                // The archive holds a step whose task it doesn't carry — a
                // dangling row with nothing to link to locally.
                _log.LogWarning("Skipping archived step {Uuid}: its parent task is not in the archive.", archivedStep.Uuid);
                skipped++;
                continue;
            }
            var step = archivedStep with { TaskId = localTaskId };

            if (current.TryGetValue(step.Uuid, out var existing))
            {
                if (mode == RestoreMode.Merge && step.UpdatedAt > existing.UpdatedAt)
                {
                    await _todo.ImportStepAsync(step, ct);
                    updated++;
                }
                else
                {
                    skipped++;
                }
            }
            else
            {
                await _todo.ImportStepAsync(step, ct);
                imported++;
            }

            done++;
            progress?.Report(0.9 + 0.04 * ((double)done / backupSteps.Count));
        }
        return (imported, updated, skipped);
    }

    /// <summary>
    /// Merges the archive's todo attachments by uuid (same LWW semantics as the
    /// steps) and carries the BYTES from the archive's <c>attachments/</c>
    /// entries into the live content-addressed store. When a merge-update swaps
    /// the row's sha, the losing bytes are pruned if that was their last
    /// reference (same event-driven rule as the service delete path). Returns
    /// (imported, updated, skipped) counts.
    /// </summary>
    private async Task<(int Imported, int Updated, int Skipped)> RestoreTodoAttachmentsAsync(
        string staging,
        List<TodoAttachment> backupAttachments,
        IReadOnlyDictionary<long, long> localTaskIdByBackupId,
        RestoreMode mode,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        if (backupAttachments.Count == 0)
            return (0, 0, 0);

        var current = await _todo.GetAttachmentUuidTimestampsAsync(ct);
        var imported = 0;
        var updated = 0;
        var skipped = 0;
        var done = 0;
        foreach (var archivedAttachment in backupAttachments)
        {
            ct.ThrowIfCancellationRequested();
            if (!localTaskIdByBackupId.TryGetValue(archivedAttachment.TaskId, out var localTaskId))
            {
                _log.LogWarning("Skipping archived attachment {Uuid}: its parent task is not in the archive.", archivedAttachment.Uuid);
                skipped++;
                continue;
            }
            var attachment = archivedAttachment with { TaskId = localTaskId };

            if (current.TryGetValue(attachment.Uuid, out var existing))
            {
                if (mode == RestoreMode.Merge && attachment.UpdatedAt > existing.UpdatedAt)
                {
                    // Read the losing row BEFORE the upsert overwrites it: its sha
                    // is the prune candidate once the winner points elsewhere.
                    var losing = await _todo.GetAttachmentByUuidAsync(attachment.Uuid, ct);
                    RestoreAttachmentFile(staging, attachment.Sha);
                    await _todo.ImportAttachmentAsync(attachment, ct);
                    if (losing is { } loser
                        && !string.Equals(loser.Sha, attachment.Sha, StringComparison.Ordinal)
                        && await _todo.CountAttachmentRefsAsync(loser.Sha, ct) == 0)
                    {
                        TryDeleteFile(Path.Combine(_paths.AttachmentsDir, loser.Sha));
                    }
                    updated++;
                }
                else
                {
                    skipped++;
                }
            }
            else
            {
                RestoreAttachmentFile(staging, attachment.Sha);
                await _todo.ImportAttachmentAsync(attachment, ct);
                imported++;
            }

            done++;
            progress?.Report(0.94 + 0.05 * ((double)done / backupAttachments.Count));
        }
        return (imported, updated, skipped);
    }

    /// <summary>
    /// Carries one archived attachment's bytes into the live store.
    /// Content-addressed: an existing file is already the same bytes; a missing
    /// staged file keeps the row (metadata survives, same policy as note
    /// images). The sha gates on 64-hex before any path is built — a crafted
    /// archive's DB row must never steer the file system (zip-slip cousin).
    /// </summary>
    private void RestoreAttachmentFile(string staging, string sha)
    {
        if (!IsHexDigest(sha))
        {
            _log.LogWarning("Archived attachment has a malformed sha '{Sha}'; its bytes are not restored.", sha);
            return;
        }

        var target = Path.Combine(_paths.AttachmentsDir, sha);
        if (File.Exists(target))
            return;

        var stagedFile = Path.Combine(staging, "attachments", sha);
        if (!File.Exists(stagedFile))
        {
            _log.LogWarning("Backup lacks attachment bytes {Sha}; importing the row without them.", sha);
            return;
        }
        Directory.CreateDirectory(_paths.AttachmentsDir);
        File.Copy(stagedFile, target);
    }

    /// <summary>True for a 64-char hex sha256 (same gate as <see cref="ImageService"/>).</summary>
    private static bool IsHexDigest(string value)
    {
        if (value.Length != 64)
            return false;
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }
        return true;
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
            encrypted = ProbeEncrypted(archivePath);
        }
        catch (ApplicationException ex)
        {
            throw new BackupException(BackupErrorKind.NotAnArchive,
                $"Not a readable archive: {archivePath}", ex);
        }

        if (encrypted && string.IsNullOrEmpty(password))
            throw new BackupException(BackupErrorKind.PasswordRequired,
                "The archive is encrypted; a password is required.");

        var staging = Path.Combine(Path.GetTempPath(), $"qnote-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            using var readerStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new LibArchiveReader(readerStream, password: password);
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
                        => SafeEntryTarget(staging, "images", name[ImagesEntryPrefix.Length..]),
                    _ when name.StartsWith(AttachmentsEntryPrefix, StringComparison.Ordinal)
                        => SafeEntryTarget(staging, "attachments", name[AttachmentsEntryPrefix.Length..]),
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
                ? new BackupException(BackupErrorKind.WrongPassword, "Wrong password, or the archive is damaged.", ex)
                : new BackupException(BackupErrorKind.CorruptArchive, "The archive is damaged and cannot be extracted.", ex);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    /// <summary>
    /// Image/attachment entry names must be plain file names — anything else is
    /// dropped. ':' is rejected too: it is invalid in NTFS file names and is the
    /// alternate data stream separator, so a crafted "C:x" name would otherwise
    /// write an ADS.
    /// </summary>
    private static string? SafeEntryTarget(string staging, string subdirectory, string fileName)
    {
        if (fileName.Length == 0
            || fileName.Contains('/')
            || fileName.Contains(':')
            || fileName.Contains("..", StringComparison.Ordinal))
            return null;
        return Path.Combine(staging, subdirectory, fileName);
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
            throw new BackupException(BackupErrorKind.MissingDatabase, "The archive does not contain qnote.db.");

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
                throw new BackupException(BackupErrorKind.CorruptArchive,
                    "The backup manifest (manifest.json) could not be parsed.", ex);
            }
        }

        long schemaVersion;
        try
        {
            schemaVersion = BackupDatabaseReader.GetUserVersion(stagedDb);
        }
        catch (SqliteException ex)
        {
            throw new BackupException(BackupErrorKind.CorruptArchive,
                "qnote.db inside the backup is not a valid database.", ex);
        }

        // Cross-check: a manifest claiming a NEWER schema than the DB it ships is
        // nonsense — trust the DB, but still reject anything newer than this build.
        var effective = Math.Max(schemaVersion, manifest?.SchemaVersion ?? 0);
        if (effective > SchemaInitializer.CurrentVersion)
        {
            throw new BackupException(BackupErrorKind.UnsupportedVersion,
                $"The backup was created by a newer version of QNote (database schema v{effective}; this build supports v{SchemaInitializer.CurrentVersion}). Upgrade the app, then restore again.",
                schemaFound: (int)effective,
                schemaSupported: (int)SchemaInitializer.CurrentVersion);
        }
        if (requireMergeable && effective < MinMergeableSchema)
        {
            // Decision 9 (forward-only compatibility): a pre-v8 archive stores
            // notes/categories timestamps as TEXT ISO and has no DeletedAt. The
            // row-by-row merge reader is v8-only, so this fails LOUDLY instead of
            // silently coercing types. Overwrite restore still works: it replaces
            // the DB file wholesale and SchemaInitializer migrates it forward.
            throw new BackupException(BackupErrorKind.UnsupportedVersion,
                $"The backup's database schema (v{effective}) is too old to merge note-by-note (this build merges v{MinMergeableSchema}+); use overwrite restore instead, or export a fresh backup with this version.",
                schemaFound: (int)effective,
                schemaSupported: (int)SchemaInitializer.CurrentVersion);
        }

        return manifest;
    }

    // ---------- Helpers ----------

    private static string StagedDbPath(string staging) => Path.Combine(staging, DbEntryName);

    /// <summary>
    /// Detects encrypted (WinZip-AES) entries without a password. The archive is
    /// opened as a .NET FileStream (see BackupCore — the path-based reader ctor
    /// cannot open non-ASCII paths on Windows). The stream reader is SEQUENTIAL,
    /// unlike the random-access path reader: its entry walk throws a passphrase
    /// <see cref="ApplicationException"/> on the FIRST encrypted entry — which is
    /// itself the encryption signal.
    /// </summary>
    private static bool ProbeEncrypted(string archivePath)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new LibArchiveReader(stream, password: null);
        try
        {
            // HasEncryptedEntries reflects only headers already read — walk them all.
            foreach (var entry in reader.Entries())
                _ = entry.Name;
            return reader.HasEncryptedEntries() > 0;
        }
        catch (ApplicationException ex) when (IsPassphraseError(ex))
        {
            return true; // an entry demanding a passphrase IS the encryption signal
        }
    }

    /// <summary>
    /// libarchive's passphrase errors come from the NATIVE library (there is no
    /// managed constant to pin): "Passphrase required for this entry" /
    /// "Incorrect passphrase". Pinned by the encrypted-archive tests — a
    /// LibArchive.Net upgrade that changes the wording fails them loudly.
    /// </summary>
    private static bool IsPassphraseError(ApplicationException ex) =>
        ex.Message.Contains("assphrase", StringComparison.Ordinal);

    private static BackupException NotFound(string archivePath) =>
        new(BackupErrorKind.ArchiveNotFound, $"Archive file not found: {archivePath}");

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
