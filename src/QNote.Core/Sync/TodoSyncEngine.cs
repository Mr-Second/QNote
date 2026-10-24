using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QNote.Data;
using QNote.Data.Schema;
using QNote.Infrastructure;
using QNote.Models;
using QNote.Services;
using static QNote.Sync.SyncTodoPaths;

namespace QNote.Sync;

/// <summary>Task aggregates use the shared transport, codec, agreement and LWW policy.</summary>
internal sealed class TodoSyncEngine(ITodoSyncRepository repository, ISyncStateStore state,
    ISyncDriver driver, AppPaths paths, ILogger log, string conflictSuffix)
{
    private sealed record Entry(TodoList? List, TodoTaskDetail? Detail, IReadOnlyList<QnoteEntry>? Blobs = null)
    {
        public string Uuid => List?.Uuid ?? Detail!.Task.Uuid;
        public long Updated => (List?.UpdatedAt ?? Detail!.Task.UpdatedAt).ToUnixTimeMilliseconds();
        public long? Deleted => (List?.DeletedAt ?? Detail?.Task.DeletedAt)?.ToUnixTimeMilliseconds();
        public SyncEntryKind Kind => List is null ? SyncEntryKind.TodoTask : SyncEntryKind.TodoList;
        public string Body => List is { } list
            ? JsonSerializer.Serialize(list with { Id = 0 }, SyncTodoJsonContext.Default.TodoList)
            : SyncTodoTaskBody.FromDetail(Detail!).Encode();
    }

    public async Task<SyncCycleResult> RunAsync(DriverListing listFiles, DriverListing taskFiles,
        DriverListing deletionFiles, byte[]? key, CancellationToken ct)
    {
        var snapshot = await repository.ReadSyncSnapshotAsync(ct);
        var errors = new List<string>();
        var uploaded = 0; var downloaded = 0; var deleted = 0; var applied = 0; var conflicts = 0;
        foreach (var kind in new[] { SyncEntryKind.TodoList, SyncEntryKind.TodoTask })
        {
            var locals = kind == SyncEntryKind.TodoList
                ? snapshot.Lists.Select(l => new Entry(l, null)).ToDictionary(e => e.Uuid, StringComparer.Ordinal)
                : snapshot.Tasks.Select(t => new Entry(null, t)).ToDictionary(e => e.Uuid, StringComparer.Ordinal);
            var folder = Folder(kind);
            var extension = kind == SyncEntryKind.TodoList ? ".json" : ".qnote";
            var files = (kind == SyncEntryKind.TodoList ? listFiles : taskFiles).Files
                .Where(f => IsEntryPath(f.Path, folder, extension))
                .ToDictionary(f => Path.GetFileNameWithoutExtension(f.Path), StringComparer.Ordinal);
            var deletionPrefix = DeletionPrefix(kind);
            var tombstones = deletionFiles.Files.Where(f => f.Path.StartsWith("tombstones/" + deletionPrefix, StringComparison.Ordinal)
                    && IsUuid(f.Path[("tombstones/" + deletionPrefix).Length..]))
                .ToDictionary(f => f.Path[("tombstones/" + deletionPrefix).Length..], StringComparer.Ordinal);
            var agreements = (await state.GetByKindAsync(kind, ct)).ToDictionary(s => s.EntryUuid, StringComparer.Ordinal);
            foreach (var uuid in locals.Keys.Concat(files.Keys).Concat(tombstones.Keys).Distinct(StringComparer.Ordinal).ToArray())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    locals.TryGetValue(uuid, out var local);
                    files.TryGetValue(uuid, out var file);
                    tombstones.TryGetValue(uuid, out var tombstone);
                    agreements.TryGetValue(uuid, out var agreement);
                    var pristineBuiltin = agreement is null && local?.List is { } seeded
                        && seeded.Uuid is SchemaInitializer.WorkListUuid or SchemaInitializer.LifeListUuid
                        && seeded.UpdatedAt == seeded.CreatedAt;
                    long? cloudDeleted = null;
                    if (tombstone is not null)
                    {
                        var bytes = await driver.ReadFileAsync(tombstone.Path, ct);
                        if (bytes is not { Length: sizeof(long) }) throw new InvalidDataException("Invalid TODO deletion record.");
                        cloudDeleted = BinaryPrimitives.ReadInt64LittleEndian(bytes);
                        _ = DateTimeOffset.FromUnixTimeMilliseconds(cloudDeleted.Value);
                    }
                    // The deletion marker is the authoritative cloud version. A
                    // leftover container after a partial delete cannot resurrect it.
                    if (cloudDeleted is { } removed && (local is null || pristineBuiltin || local.Updated <= removed))
                    {
                        if (agreement?.LastSyncedAtMs != removed || local is { Deleted: null })
                        {
                            await repository.ApplySyncDeletionAsync(kind, uuid, removed,
                                Agreement(kind, uuid, tombstone!.Path, tombstone.Etag, removed), ct, local?.Updated ?? -1);
                            if (local is not null) applied++;
                        }
                        continue;
                    }
                    if (local?.Deleted is { } localDeleted)
                    {
                        // A newer remote edit may legitimately supersede a local deletion.
                        var remote = cloudDeleted is null && file is not null ? await ReadAsync(kind, uuid, file, key, ct) : null;
                        if (remote is not null && remote.Updated > localDeleted)
                        {
                            await PullAsync(remote, file!, local, ct); downloaded++;
                        }
                        else
                        {
                            if (cloudDeleted != localDeleted)
                            {
                                var bytes = new byte[sizeof(long)]; BinaryPrimitives.WriteInt64LittleEndian(bytes, localDeleted);
                                var write = await driver.WriteFileAsync(DeletionPath(kind, uuid), bytes, ct: ct);
                                await state.UpsertAsync(Agreement(kind, uuid, write.RemoteId, write.Etag, localDeleted), ct);
                                uploaded++;
                            }
                            if (file is not null) { await driver.DeleteFileAsync(file.Path, ct); deleted++; }
                        }
                        continue;
                    }
                    if (local is null)
                    {
                        if (file is not null)
                        {
                            var remote = await ReadAsync(kind, uuid, file, key, ct);
                            await PullAsync(remote, file, null, ct); downloaded++;
                        }
                        continue;
                    }
                    if (file is null || cloudDeleted is not null)
                    {
                        await PushAsync(local, null, key, ct); uploaded++;
                        if (tombstone is not null) await driver.DeleteFileAsync(tombstone.Path, ct);
                        continue;
                    }
                    if (agreement is not null && UsableEtag(file.Etag) && file.Etag == agreement.Etag
                        && local.Updated == agreement.LastSyncedAtMs) continue;
                    var cloud = await ReadAsync(kind, uuid, file, key, ct);
                    if (local.Body == cloud.Body)
                    {
                        await state.UpsertAsync(Agreement(kind, uuid, file.Path, file.Etag, local.Updated), ct);
                        continue;
                    }
                    var localWins = !pristineBuiltin && (local.Updated > cloud.Updated || local.Updated == cloud.Updated
                        && ConflictResolver.Resolve(Digest(local.Body), Digest(cloud.Body)) == ConflictWinner.Local);
                    var bothChanged = agreement is null || local.Updated == cloud.Updated
                        || local.Updated > agreement.LastSyncedAtMs && cloud.Updated > agreement.LastSyncedAtMs;
                    if (kind == SyncEntryKind.TodoTask && bothChanged)
                    {
                        var loser = localWins ? cloud : local;
                        var copy = ConflictCopy(loser);
                        if (!locals.ContainsKey(copy.Uuid) && files.TryGetValue(copy.Uuid, out var existingCopy))
                        {
                            var preserved = await ReadAsync(kind, copy.Uuid, existingCopy, key, ct);
                            await PullAsync(preserved, existingCopy, null, ct);
                            locals[copy.Uuid] = preserved;
                            downloaded++;
                        }
                        else if (!locals.ContainsKey(copy.Uuid))
                        {
                            await SaveBlobsAsync(loser, ct);
                            await repository.ApplySyncTaskAsync(copy.Detail!, null, ct, -1);
                            await PushAsync(copy, null, key, ct); uploaded++; conflicts++;
                            locals[copy.Uuid] = copy;
                        }
                    }
                    if (localWins) { await PushAsync(local, file.Etag, key, ct); uploaded++; }
                    else { await PullAsync(cloud, file, local, ct); downloaded++; }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Conditional-write failures are retried on the next cycle after
                    // a fresh listing, never by overwriting a newly observed version.
                    errors.Add($"TODO {kind} {uuid}: {ex.Message}");
                    log.LogError(ex, "TODO sync failed for {Kind} {Uuid}.", kind, uuid);
                }
            }
        }
        return new SyncCycleResult
        {
            Uploaded = uploaded, Downloaded = downloaded, DeletedRemote = deleted,
            TombstonesApplied = applied, ConflictCopies = conflicts, Errors = errors,
        };
    }

    private async Task<Entry> ReadAsync(SyncEntryKind kind, string uuid, DriverFile file, byte[]? key, CancellationToken ct)
    {
        var bytes = await driver.ReadFileAsync(file.Path, ct) ?? throw new InvalidDataException("A TODO entry disappeared during sync.");
        if (kind == SyncEntryKind.TodoList)
        {
            var list = JsonSerializer.Deserialize(bytes, SyncTodoJsonContext.Default.TodoList)
                ?? throw new InvalidDataException("Invalid TODO list JSON.");
            if (list.Uuid != uuid || list.DeletedAt is not null || string.IsNullOrWhiteSpace(list.Name))
                throw new InvalidDataException("Invalid TODO list identity or state.");
            return new Entry(list with { Id = 0 }, null);
        }
        var payload = QnoteContainerCodec.Decode(bytes, key);
        var body = JsonSerializer.Deserialize(payload.Body, SyncTodoJsonContext.Default.SyncTodoTaskBody)
            ?? throw new InvalidDataException("Invalid TODO task body.");
        if (payload.Header.Uuid.ToString("N") != uuid || body.Task.Uuid != uuid
            || body.Task.UpdatedAt.ToUnixTimeMilliseconds() != payload.Header.UpdatedAtMs
            || body.Task.CreatedAt.ToUnixTimeMilliseconds() != payload.Header.CreatedAtMs
            || payload.Header.DeletedAtMs != 0 || body.Task.DeletedAt is not null || !IsUuid(body.Task.ListUuid)
            || string.IsNullOrWhiteSpace(body.Task.Title)
            || body.Steps is null || body.Attachments is null)
            throw new InvalidDataException("Invalid TODO task identity, parent or clock.");
        if (body.Steps.Any(s => !IsUuid(s.Uuid) || s.TaskUuid != uuid || s.DeletedAt is not null || s.UpdatedAt > body.Task.UpdatedAt)
            || body.Attachments.Any(a => !IsUuid(a.Uuid) || a.TaskUuid != uuid || a.DeletedAt is not null || a.ByteSize < 0
                || a.UpdatedAt > body.Task.UpdatedAt || string.IsNullOrEmpty(a.FileName) || !IsSha(a.Sha))
            || body.Steps.Select(s => s.Uuid).Distinct(StringComparer.Ordinal).Count() != body.Steps.Length
            || body.Attachments.Select(a => a.Uuid).Distinct(StringComparer.Ordinal).Count() != body.Attachments.Length
            || body.Attachments.Sum(a => a.ByteSize) > TodoService.MaxTaskAttachmentBytes)
            throw new InvalidDataException("Invalid TODO child metadata.");
        var blobs = payload.Entries.ToDictionary(e => e.Sha256, StringComparer.OrdinalIgnoreCase);
        if (payload.Entries.Any(e => e.Kind != QnoteEntryKind.Attachment)
            || body.Attachments.Any(a => !blobs.TryGetValue(a.Sha, out var blob) || blob.Data.LongLength != a.ByteSize)
            || blobs.Keys.Any(sha => !body.Attachments.Any(a => a.Sha.Equals(sha, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("TODO attachment bytes do not match their metadata.");
        return new Entry(null, new TodoTaskDetail(body.Task with { Id = 0, CatchUpFiredAt = null }, body.Steps, body.Attachments), payload.Entries);
    }

    private async Task PushAsync(Entry entry, string? etag, byte[]? key, CancellationToken ct)
    {
        var bytes = entry.List is not null ? Encoding.UTF8.GetBytes(entry.Body)
            : QnoteContainerCodec.Encode(new QnoteHeader
            {
                Uuid = Guid.ParseExact(entry.Uuid, "N"), CreatedAtMs = entry.Detail!.Task.CreatedAt.ToUnixTimeMilliseconds(),
                UpdatedAtMs = entry.Updated, DeletedAtMs = 0,
            }, entry.Body, await BlobsAsync(entry, ct), key);
        var write = await driver.WriteFileAsync(FilePath(entry.Kind, entry.Uuid), bytes, UsableEtag(etag) ? etag : null, ct);
        await state.UpsertAsync(Agreement(entry.Kind, entry.Uuid, write.RemoteId, write.Etag, entry.Updated), ct);
    }

    private async Task PullAsync(Entry cloud, DriverFile file, Entry? local, CancellationToken ct)
    {
        var agreement = Agreement(cloud.Kind, cloud.Uuid, file.Path, file.Etag, cloud.Updated);
        if (cloud.List is { } list) await repository.ApplySyncListAsync(list, agreement, ct, local?.Updated ?? -1);
        else
        {
            await SaveBlobsAsync(cloud, ct);
            var task = cloud.Detail!.Task;
            // Catch-up state belongs to this device, and does not participate in
            // the transport digest or generate another outgoing task change.
            task = task with { CatchUpFiredAt = local?.Detail?.Task.DueAt == task.DueAt ? local?.Detail?.Task.CatchUpFiredAt : null };
            await repository.ApplySyncTaskAsync(cloud.Detail with { Task = task }, agreement, ct, local?.Updated ?? -1);
        }
    }

    private async Task<IReadOnlyList<QnoteEntry>> BlobsAsync(Entry entry, CancellationToken ct)
    {
        if (entry.Blobs is { } existing) return existing;
        if (entry.Detail is null) return [];
        if (entry.Detail.Attachments.Sum(a => a.ByteSize) > TodoService.MaxTaskAttachmentBytes)
            throw new InvalidDataException("Task attachments exceed the supported size.");
        var blobs = new List<QnoteEntry>();
        foreach (var attachment in entry.Detail.Attachments.DistinctBy(a => a.Sha, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsSha(attachment.Sha)) throw new InvalidDataException("Invalid local attachment hash.");
            var bytes = await File.ReadAllBytesAsync(Path.Combine(paths.AttachmentsDir, attachment.Sha), ct);
            if (bytes.LongLength != attachment.ByteSize || !Digest(bytes).Equals(attachment.Sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Local attachment bytes are missing or corrupt.");
            blobs.Add(new QnoteEntry { Kind = QnoteEntryKind.Attachment, Sha256 = attachment.Sha, FileName = attachment.FileName, Data = bytes });
        }
        return blobs;
    }

    private async Task SaveBlobsAsync(Entry entry, CancellationToken ct)
    {
        var store = new TodoAttachmentStore(paths);
        foreach (var blob in await BlobsAsync(entry, ct))
        {
            using var stream = new MemoryStream(blob.Data, writable: false);
            await store.SaveAsync(stream, ct);
        }
    }

    private Entry ConflictCopy(Entry loser)
    {
        // Both devices derive the same copy identities for the same losing
        // version, so retries and equal-clock races do not multiply copies.
        var uuid = StableUuid(loser.Uuid + ":" + Digest(loser.Body));
        var detail = loser.Detail!;
        return new Entry(null, new TodoTaskDetail(detail.Task with
        {
            Id = 0, Uuid = uuid, Title = detail.Task.Title + conflictSuffix,
        }, detail.Steps.Select(s => s with { Id = 0, Uuid = StableUuid(uuid + ":" + s.Uuid), TaskId = 0, TaskUuid = uuid }).ToArray(),
            detail.Attachments.Select(a => a with { Id = 0, Uuid = StableUuid(uuid + ":" + a.Uuid), TaskId = 0, TaskUuid = uuid }).ToArray()), loser.Blobs);
    }

    private static bool IsUuid(string value) => Guid.TryParseExact(value, "N", out _);
    private static bool IsSha(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool UsableEtag(string? value) => EntityTagHeaderValue.TryParse(value, out var etag) && !etag.IsWeak && etag.Tag != "*";
    private static string Digest(string value) => Digest(Encoding.UTF8.GetBytes(value));
    private static string Digest(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static string StableUuid(string value) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16)).ToString("N");
    private static SyncStateEntry Agreement(SyncEntryKind kind, string uuid, string remote, string etag, long at) => new()
    { EntryKind = kind, EntryUuid = uuid, RemoteId = remote, Etag = etag, LastSyncedAtMs = at };
}
