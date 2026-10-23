using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QNote.Data;
using QNote.Infrastructure;
using QNote.Markdown;
using QNote.Models;

namespace QNote.Sync;

/// <summary>
/// The L1 sync engine (task 10-06): one engine, two driver plugs (OneDrive L2 /
/// WebDAV L3 implement <see cref="ISyncDriver"/>). Owns the FULL delta matrix —
/// drivers never decide anything. Scope: notes + categories (the PRD cloud
/// layout); the todo cloud layout is unspecified (PRD open question a), so
/// attachment entries inside note containers are logged + skipped.
///
/// Cycle shape (design, 2026-10-21): three listings first (any truncated listing
/// aborts the cycle BEFORE any mutation — PRD decision 10 block-and-warn), then
/// UPLOAD (cloud has neither container nor tombstone) → DELETE_REMOTE (local
/// tombstone not older than the cloud container) → DELTA (everything else,
/// per-entry LWW on the container header / the category JSON's UpdatedAt).
/// Idempotence is by construction: every upload records sync state with
/// LastSyncedAtMs = the uploaded LOCAL row's UpdatedAt — never DateTime.UtcNow —
/// so a re-run agrees (etag fast path) and writes nothing.
///
/// Deletion semantics: deletions propagate ONLY via tombstone files (the
/// Joplin-listing rule — a missing listing entry is never a deletion). A cloud
/// tombstone always beats a cloud container of the same entry ("cloud deletion
/// decided"). LWW runs on epoch-ms; a tombstone row's UpdatedAt is always its
/// DeletedAt (the schema-v8 delete contract). Delete-vs-edit resolves by plain
/// LWW (an edit newer than the tombstone resurrects the row on the deleting
/// peer via a verbatim container apply).
/// </summary>
public sealed class SyncEngine
{
    /// <summary>
    /// Presentation-supplied display-copy generator (design open question (c)):
    /// container entries carry ORIGINAL bytes only, and a note_images row without
    /// DisplayBytes degrades the editor to a placeholder and drops the reference
    /// on the next save — so Presentation hands the engine a WIC-backed callback
    /// at composition time. Null (L1 tests) = imported rows carry no display copy
    /// (logged degradation). A throwing callback degrades that one image, never
    /// the cycle.
    /// </summary>
    public delegate byte[]? DisplayImageGenerator(byte[] originalBytes, string ext);

    public const string NotesFolder = "notes";
    public const string CategoriesFolder = "categories";
    public const string TombstonesFolder = "tombstones";
    public const string ManifestPath = "manifest.json";
    public const string NoteContainerExtension = ".qnote";
    public const string CategoryFileExtension = ".json";

    /// <summary>
    /// Conflict-copy title suffix FALLBACK (design open question (b), resolved by
    /// user ruling 2026-10-21: localized INJECTION). Core must not own user-visible
    /// wording, so Presentation passes the localized suffix at composition
    /// (resw <c>SyncConflictCopySuffix</c>: zh 「（冲突副本）」 / en " (conflict)");
    /// this literal only covers an unwired composition.
    /// </summary>
    public const string ConflictCopySuffix = " (conflict)";

    private readonly INoteRepository _notes;
    private readonly ISyncStateStore _syncState;
    private readonly ISyncDriver _driver;
    private readonly AppPaths _paths;
    private readonly ILogger<SyncEngine> _log;
    private readonly DisplayImageGenerator? _displayImageGenerator;
    private readonly string _conflictCopyTitleSuffix;

    /// <summary>Per-cycle read cache: files downloaded for probes, reused for the apply.</summary>
    private readonly Dictionary<string, byte[]> _downloadCache = new(StringComparer.Ordinal);

    public SyncEngine(
        INoteRepository notes,
        ISyncStateStore syncState,
        ISyncDriver driver,
        AppPaths paths,
        ILogger<SyncEngine> log,
        DisplayImageGenerator? displayImageGenerator = null,
        string? conflictCopyTitleSuffix = null)
    {
        _notes = notes;
        _syncState = syncState;
        _driver = driver;
        _paths = paths;
        _log = log;
        _displayImageGenerator = displayImageGenerator;
        _conflictCopyTitleSuffix = string.IsNullOrEmpty(conflictCopyTitleSuffix)
            ? ConflictCopySuffix
            : conflictCopyTitleSuffix;
    }

    /// <summary>
    /// Runs one full sync cycle against the driver. <paramref name="e2eeKey"/> is
    /// the derived 32-byte master key (<see cref="SyncKeyDeriver"/>) when the
    /// target is end-to-end encrypted; the cloud manifest's E2EE mode must agree
    /// or the cycle aborts. All-or-nothing per ENTRY, never per cycle: one bad
    /// entry lands in <see cref="SyncCycleResult.Errors"/> and the rest proceed.
    /// </summary>
    public async Task<SyncCycleResult> RunAsync(byte[]? e2eeKey = null, CancellationToken ct = default)
    {
        _downloadCache.Clear();
        var ctx = new CycleContext { Key = e2eeKey };

        try
        {
            // ----- listings (before any mutation; decision 10) -----
            var notesListing = await _driver.ListAsync(NotesFolder, ct);
            var categoriesListing = await _driver.ListAsync(CategoriesFolder, ct);
            var tombstonesListing = await _driver.ListAsync(TombstonesFolder, ct);
            if (notesListing.IsTruncated || categoriesListing.IsTruncated || tombstonesListing.IsTruncated)
            {
                _log.LogWarning("A cloud listing is incomplete; the sync cycle was blocked before any mutation.");
                return SyncCycleResult.Aborted(
                    SyncCycleAbortKind.ListingIncomplete,
                    "A cloud folder listing could not be proven complete; the cycle was blocked before any " +
                    "mutation (deletions are never inferred from a partial listing).");
            }

            // ----- manifest (version + E2EE gate) -----
            var abort = await PrepareManifestAsync(e2eeKey, ct);
            if (abort is not null)
            {
                _log.LogWarning("Sync aborted before any mutation: {Reason}", abort.Errors[0]);
                return abort;
            }

            // ----- local + cloud state -----
            ctx.NoteStamps = (await _notes.GetAllUuidTimestampsAsync(ct))
                .ToDictionary(s => s.Uuid, StringComparer.Ordinal);
            ctx.CategoryStamps = (await _notes.GetAllCategoryUuidTimestampsAsync(ct))
                .ToDictionary(s => s.Uuid, StringComparer.Ordinal);
            ctx.LiveNotes = (await _notes.GetAllAsync(ct))
                .ToDictionary(n => n.Uuid, StringComparer.Ordinal);
            ctx.LiveCategories = (await _notes.GetCategoriesAsync(ct))
                .ToDictionary(c => c.Uuid, StringComparer.Ordinal);
            ctx.NoteState = (await _syncState.GetByKindAsync(SyncEntryKind.Note, ct))
                .ToDictionary(s => s.EntryUuid, StringComparer.Ordinal);
            ctx.CategoryState = (await _syncState.GetByKindAsync(SyncEntryKind.Category, ct))
                .ToDictionary(s => s.EntryUuid, StringComparer.Ordinal);

            ctx.CloudNotes = ParseExtensionListing(notesListing, NotesFolder, NoteContainerExtension);
            ctx.CloudCategories = ParseExtensionListing(categoriesListing, CategoriesFolder, CategoryFileExtension);
            ctx.CloudTombstones = ParseTombstoneListing(tombstonesListing, TombstonesFolder);

            // ----- phase 1: UPLOAD (cloud has neither container nor tombstone) -----
            await UploadNewNotesAsync(ctx, ct);
            await UploadNewCategoriesAsync(ctx, ct);

            // ----- phase 2: DELETE_REMOTE (local tombstone not older than the cloud container) -----
            await DeleteRemoteNotesAsync(ctx, ct);
            await DeleteRemoteCategoriesAsync(ctx, ct);

            // ----- phase 3: DELTA (cloud presence exists; per-entry LWW) -----
            await RunDeltaAsync(ctx, ct);
        }
        catch (SyncDriverException ex)
        {
            ctx.Errors.Add($"The sync driver failed mid-cycle: {ex.Message}");
            _log.LogError(ex, "The sync driver failed mid-cycle.");
        }

        var result = new SyncCycleResult
        {
            Uploaded = ctx.Uploaded,
            Downloaded = ctx.Downloaded,
            DeletedRemote = ctx.DeletedRemote,
            TombstonesApplied = ctx.TombstonesApplied,
            ConflictCopies = ctx.ConflictCopies,
            Errors = ctx.Errors,
        };
        _log.LogInformation(
            "Sync cycle done: uploaded {Uploaded}, downloaded {Downloaded}, deleted-remote {DeletedRemote}, " +
            "tombstones-applied {TombstonesApplied}, conflict-copies {ConflictCopies}, errors {Errors}.",
            result.Uploaded, result.Downloaded, result.DeletedRemote,
            result.TombstonesApplied, result.ConflictCopies, result.Errors.Count);
        return result;
    }

    // ---------- manifest ----------

    /// <summary>Validates (or creates) the cloud manifest. Returns an aborted result, or null to proceed.</summary>
    private async Task<SyncCycleResult?> PrepareManifestAsync(byte[]? key, CancellationToken ct)
    {
        var bytes = await _driver.ReadFileAsync(ManifestPath, ct);
        if (bytes is null)
        {
            // Fresh cloud: pin the E2EE mode now. E2EE first-enable happens at
            // manifest creation — the 32-byte salt is generated exactly once here
            // (PRD decision 12); re-keying an existing target is out of L1 scope.
            var manifest = new SyncManifest
            {
                E2eeEnabled = key is not null,
                E2eeSalt = key is not null
                    ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(SyncKeyDeriver.SaltByteCount))
                    : null,
            };
            await _driver.WriteFileAsync(
                ManifestPath,
                JsonSerializer.SerializeToUtf8Bytes(manifest, SyncManifestJsonContext.Default.SyncManifest),
                ifMatchEtag: null, ct);
            _log.LogInformation("Created the cloud manifest (E2EE: {Enabled}).", manifest.E2eeEnabled);
            return null;
        }

        SyncManifest existing;
        try
        {
            existing = JsonSerializer.Deserialize(bytes, SyncManifestJsonContext.Default.SyncManifest)
                ?? throw new JsonException("The manifest decoded to null.");
        }
        catch (JsonException ex)
        {
            return SyncCycleResult.Aborted(
                SyncCycleAbortKind.UnsupportedManifest,
                $"The cloud manifest.json is not a readable sync manifest: {ex.Message}");
        }

        if (existing.FormatVersion != SyncManifest.CurrentFormatVersion)
        {
            return SyncCycleResult.Aborted(
                SyncCycleAbortKind.UnsupportedManifest,
                $"The cloud manifest declares layout version {existing.FormatVersion}; this build " +
                $"understands v{SyncManifest.CurrentFormatVersion}. Update the app.");
        }

        if (existing.E2eeEnabled != (key is not null))
        {
            return SyncCycleResult.Aborted(
                SyncCycleAbortKind.E2eeKeyMismatch,
                existing.E2eeEnabled
                    ? "The cloud target is end-to-end encrypted but no key was supplied."
                    : "A key was supplied but the cloud target is not end-to-end encrypted.");
        }

        return null;
    }

    // ---------- phase 1: upload new entries ----------

    private async Task UploadNewNotesAsync(CycleContext ctx, CancellationToken ct)
    {
        foreach (var uuid in Ordered(ctx.NoteStamps.Keys))
        {
            if (ctx.CloudNotes.ContainsKey(uuid) || ctx.CloudTombstones.ContainsKey(uuid))
                continue;
            var stamp = ctx.NoteStamps[uuid];
            ctx.NoteState.TryGetValue(uuid, out var state);
            try
            {
                if (stamp.IsLive)
                {
                    if (!ctx.LiveNotes.TryGetValue(uuid, out var live))
                    {
                        Error(ctx, $"Note {uuid}: live stamp but no full row; skipped.");
                        continue;
                    }
                    await UploadNoteAsync(ctx, live, ifMatchEtag: null, state, ct);
                }
                else
                {
                    var write = await _driver.WriteFileAsync(
                        TombstonePath(uuid), TombstoneBytes(ToMs(stamp.DeletedAt!.Value)),
                        ifMatchEtag: null, ct);
                    ctx.Uploaded++;
                    await RecordStateAsync(
                        ctx, SyncEntryKind.Note, uuid, write.RemoteId, write.Etag, ToMs(stamp.UpdatedAt), state, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Error(ctx, $"Note {uuid}: upload failed: {ex.Message}", ex);
            }
        }
    }

    private async Task UploadNewCategoriesAsync(CycleContext ctx, CancellationToken ct)
    {
        foreach (var uuid in Ordered(ctx.CategoryStamps.Keys))
        {
            if (ctx.CloudCategories.ContainsKey(uuid) || ctx.CloudTombstones.ContainsKey(uuid))
                continue;
            var stamp = ctx.CategoryStamps[uuid];
            ctx.CategoryState.TryGetValue(uuid, out var state);
            try
            {
                if (stamp.IsLive)
                {
                    if (!ctx.LiveCategories.TryGetValue(uuid, out var live))
                    {
                        Error(ctx, $"Category {uuid}: live stamp but no full row; skipped.");
                        continue;
                    }
                    await UploadCategoryAsync(ctx, live, ifMatchEtag: null, state, ct);
                }
                else
                {
                    var write = await _driver.WriteFileAsync(
                        TombstonePath(uuid), TombstoneBytes(ToMs(stamp.DeletedAt!.Value)),
                        ifMatchEtag: null, ct);
                    ctx.Uploaded++;
                    await RecordStateAsync(
                        ctx, SyncEntryKind.Category, uuid, write.RemoteId, write.Etag, ToMs(stamp.UpdatedAt), state, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Error(ctx, $"Category {uuid}: upload failed: {ex.Message}", ex);
            }
        }
    }

    // ---------- phase 2: delete remote for local tombstones ----------

    /// <summary>
    /// A local tombstone with a cloud container and no cloud tombstone propagates
    /// the deletion — but ONLY when the local delete is not stale: a cloud edit
    /// NEWER than the local tombstone (C &gt; D) resurrects through DELTA instead
    /// (delete-vs-edit LWW); propagating a stale delete would make the two peers
    /// ping-pong the deletion forever.
    /// </summary>
    private async Task DeleteRemoteNotesAsync(CycleContext ctx, CancellationToken ct)
    {
        foreach (var uuid in Ordered(ctx.NoteStamps.Keys))
        {
            var stamp = ctx.NoteStamps[uuid];
            if (stamp.IsLive || !ctx.CloudNotes.TryGetValue(uuid, out var container))
                continue;
            if (ctx.CloudTombstones.ContainsKey(uuid))
                continue; // both present → the tombstone reconcile in DELTA owns it

            try
            {
                var c = await ProbeNoteUpdatedAtMsAsync(ctx, container, ct);
                if (c is null)
                    continue; // the probe already recorded the error
                var deletedAtMs = ToMs(stamp.DeletedAt!.Value);
                if (c.Value > deletedAtMs)
                    continue; // the cloud edit is newer — DELTA resurrects the row

                ctx.NoteState.TryGetValue(uuid, out var state);
                var write = await _driver.WriteFileAsync(
                    TombstonePath(uuid), TombstoneBytes(deletedAtMs), ifMatchEtag: null, ct);
                await _driver.DeleteFileAsync(container.Path, ct);
                ctx.DeletedRemote++;
                await RecordStateAsync(
                    ctx, SyncEntryKind.Note, uuid, write.RemoteId, write.Etag, deletedAtMs, state, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Error(ctx, $"Note {uuid}: remote deletion failed: {ex.Message}", ex);
            }
        }
    }

    private async Task DeleteRemoteCategoriesAsync(CycleContext ctx, CancellationToken ct)
    {
        foreach (var uuid in Ordered(ctx.CategoryStamps.Keys))
        {
            var stamp = ctx.CategoryStamps[uuid];
            if (stamp.IsLive || !ctx.CloudCategories.TryGetValue(uuid, out var container))
                continue;
            if (ctx.CloudTombstones.ContainsKey(uuid))
                continue;

            try
            {
                var c = await ProbeCategoryUpdatedAtMsAsync(ctx, container, ct);
                if (c is null)
                    continue;
                var deletedAtMs = ToMs(stamp.DeletedAt!.Value);
                if (c.Value > deletedAtMs)
                    continue;

                ctx.CategoryState.TryGetValue(uuid, out var state);
                var write = await _driver.WriteFileAsync(
                    TombstonePath(uuid), TombstoneBytes(deletedAtMs), ifMatchEtag: null, ct);
                await _driver.DeleteFileAsync(container.Path, ct);
                ctx.DeletedRemote++;
                await RecordStateAsync(
                    ctx, SyncEntryKind.Category, uuid, write.RemoteId, write.Etag, deletedAtMs, state, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Error(ctx, $"Category {uuid}: remote deletion failed: {ex.Message}", ex);
            }
        }
    }

    // ---------- phase 3: delta ----------

    private async Task RunDeltaAsync(CycleContext ctx, CancellationToken ct)
    {
        // Tombstone files do not self-identify their kind: the container listings
        // claim their entries first, the local stamps route the remainder, and a
        // fully-propagated deletion (no container, no local row) has nothing to do.
        var scheduled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var uuid in Ordered(ctx.CloudNotes.Keys))
        {
            scheduled.Add(uuid);
            await ProcessNoteDeltaAsync(ctx, uuid, ct);
        }
        foreach (var uuid in Ordered(ctx.CloudCategories.Keys))
        {
            if (scheduled.Add(uuid))
                await ProcessCategoryDeltaAsync(ctx, uuid, ct);
        }
        foreach (var uuid in Ordered(ctx.CloudTombstones.Keys))
        {
            if (!scheduled.Add(uuid))
                continue;
            if (ctx.NoteStamps.ContainsKey(uuid))
                await ProcessNoteDeltaAsync(ctx, uuid, ct);
            else if (ctx.CategoryStamps.ContainsKey(uuid))
                await ProcessCategoryDeltaAsync(ctx, uuid, ct);
        }
    }

    private async Task ProcessNoteDeltaAsync(CycleContext ctx, string uuid, CancellationToken ct)
    {
        try
        {
            ctx.CloudTombstones.TryGetValue(uuid, out var tombstoneFile);
            long? tombMs = null;
            if (tombstoneFile is not null)
            {
                tombMs = await ReadTombstoneMsAsync(ctx, tombstoneFile, ct);
                if (tombMs is null)
                    return; // malformed/missing tombstone — deferred to the next cycle
            }

            var stamp = ctx.NoteStamps.TryGetValue(uuid, out var s) ? s : null;
            var state = ctx.NoteState.TryGetValue(uuid, out var st) ? st : null;

            if (tombMs is { } deletedMs)
            {
                await ProcessNoteTombstoneAsync(ctx, uuid, stamp, state, tombstoneFile!, deletedMs, ct);
                return;
            }

            if (!ctx.CloudNotes.TryGetValue(uuid, out var container))
                return; // tombstone-only uuid with no local presence: nothing to do
            if (stamp is null)
            {
                await DownloadNoteAsync(ctx, uuid, container, state, knownBytes: null, ct);
                return;
            }
            if (!stamp.IsLive)
            {
                // Phase 2 propagated the deletion when the local tombstone was not stale.
                // The remaining case — a cloud edit NEWER than the tombstone (C > D,
                // delete-vs-edit LWW) — resurrects through the verbatim container apply.
                var resurrectMs = await ProbeNoteLwwAsync(ctx, uuid, container, state, ct);
                if (resurrectMs is { } cloudMs && cloudMs > ToMs(stamp.DeletedAt!.Value))
                    await DownloadNoteAsync(ctx, uuid, container, state, knownBytes: null, ct);
                return;
            }

            var live = RequireLiveNote(ctx, uuid);
            if (live is null)
                return;
            var l = ToMs(stamp.UpdatedAt);
            var c = await ProbeNoteLwwAsync(ctx, uuid, container, state, ct);
            if (c is null)
                return;
            await DecideLiveNoteAsync(
                ctx, uuid, live, container, c.Value, l, state, knownBytes: null, allowRetry: true, ct);
        }
        catch (SyncDriverException ex) when (ex.Kind == SyncDriverErrorKind.PreconditionFailed)
        {
            Error(ctx, $"Note {uuid}: a conditional write kept losing the race; deferred to the next sync.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error(ctx, $"Note {uuid}: {ex.Message}", ex);
        }
    }

    private async Task ProcessNoteTombstoneAsync(
        CycleContext ctx, string uuid, SyncRowStamp? stamp, SyncStateEntry? state,
        DriverFile tombstoneFile, long deletedMs, CancellationToken ct)
    {
        ctx.CloudNotes.TryGetValue(uuid, out var container);
        var deletedAt = FromMs(deletedMs);

        if (stamp is null)
        {
            // Cloud deletion decided; nothing local to apply — drop a leftover container.
            if (container is not null)
            {
                await _driver.DeleteFileAsync(container.Path, ct);
                ctx.DeletedRemote++;
            }
            return;
        }

        if (stamp.IsLive)
        {
            var live = RequireLiveNote(ctx, uuid);
            if (live is null)
                return;
            if (deletedAt > stamp.UpdatedAt)
            {
                // Cloud deletion wins: apply the tombstone VERBATIM (DeletedAt = UpdatedAt
                // = T) so the local LWW clock matches the cloud's decision.
                var tombstoned = live with { DeletedAt = deletedAt, UpdatedAt = deletedAt };
                await _notes.ImportNoteAsync(
                    tombstoned,
                    syncState: State(SyncEntryKind.Note, uuid, string.Empty, tombstoneFile.Etag, deletedMs),
                    ct: ct, expected: ExpectedNote(ctx, uuid));
                ctx.TombstonesApplied++;
                if (container is not null)
                {
                    await _driver.DeleteFileAsync(container.Path, ct);
                    ctx.DeletedRemote++;
                }
            }
            else
            {
                // The local edit is newer than the peer's delete: resurrect — the stale
                // tombstone file goes, the live container goes up (overwriting any stale
                // container a crashed peer left behind at the same path).
                await _driver.DeleteFileAsync(tombstoneFile.Path, ct);
                ctx.DeletedRemote++;
                await UploadNoteAsync(ctx, live, ifMatchEtag: null, state, ct);
            }
            return;
        }

        // Local tombstoned: reconcile the two tombstones (both UpdatedAt == DeletedAt).
        var localDeletedAtMs = ToMs(stamp.DeletedAt!.Value);
        if (localDeletedAtMs > deletedMs)
        {
            var write = await _driver.WriteFileAsync(
                tombstoneFile.Path, TombstoneBytes(localDeletedAtMs), tombstoneFile.Etag, ct);
            ctx.Uploaded++;
            await RecordStateAsync(
                ctx, SyncEntryKind.Note, uuid, write.RemoteId, write.Etag, localDeletedAtMs, state, ct);
        }
        else if (deletedMs > localDeletedAtMs)
        {
            // The peer deleted later: re-stamp the local tombstone verbatim (content untouched).
            await _notes.ImportNoteTombstoneAsync(
                uuid, FromMs(deletedMs),
                State(SyncEntryKind.Note, uuid, string.Empty, tombstoneFile.Etag, deletedMs), ct,
                expected: ExpectedNote(ctx, uuid));
            ctx.TombstonesApplied++;
        }
        else
        {
            // Equal: already in agreement — just record it if we never did.
            await RecordStateAsync(
                ctx, SyncEntryKind.Note, uuid, string.Empty, tombstoneFile.Etag, localDeletedAtMs, state, ct);
        }
        if (container is not null)
        {
            await _driver.DeleteFileAsync(container.Path, ct);
            ctx.DeletedRemote++;
        }
    }

    /// <summary>
    /// The live-note LWW decision on an actual (probed or etag-inferred) cloud
    /// timestamp <paramref name="c"/>. Conflict first (both sides moved since the
    /// last agreement), then agree / download / upload. <paramref name="allowRetry"/>
    /// marks the top-level decision: its uploads are conditional on the observed
    /// cloud etag, and a lost race re-resolves ONCE inline against the fresh
    /// bytes (uploads inside the re-resolve are unconditional — the peer's state
    /// is already part of the decision).
    /// </summary>
    private async Task DecideLiveNoteAsync(
        CycleContext ctx, string uuid, Note live, DriverFile container,
        long c, long l, SyncStateEntry? state, byte[]? knownBytes, bool allowRetry, CancellationToken ct)
    {
        var s = state?.LastSyncedAtMs ?? 0;
        var ifMatchEtag = allowRetry ? container.Etag : null;
        var agreeEtag = allowRetry ? container.Etag : string.Empty;

        if (c != s && l > s && c != l)
        {
            // CONFLICT — strict LWW (no tie possible: c != l); the loser becomes a
            // conflict copy (notes only, PRD note-level LWW).
            await ApplyNoteWinnerAsync(
                ctx, uuid, live, container, state,
                localWins: l > c, cloudMs: c, uploadIfMatchEtag: ifMatchEtag,
                knownBytes, allowRetry, ct);
            return;
        }

        if (c == l)
        {
            await ResolveNoteTieAsync(ctx, uuid, live, container, state, knownBytes, allowRetry, ct);
            return;
        }
        if (c > l)
        {
            await DownloadNoteAsync(ctx, uuid, container, state, knownBytes, ct);
            return;
        }

        // c < l: the local edit is newer — push it.
        try
        {
            await UploadNoteAsync(ctx, live, ifMatchEtag, state, ct);
        }
        catch (SyncDriverException ex) when (ex.Kind == SyncDriverErrorKind.PreconditionFailed && allowRetry)
        {
            await ReResolveLiveNoteAsync(ctx, uuid, live, container.Path, state, previousCloudMs: c, ct);
        }
    }

    /// <summary>
    /// Applies an already-decided winner (the both-moved conflict or a resolved
    /// tie): the loser is materialized as a conflict copy BEFORE the winner's
    /// write can destroy it, then the winner stands (local → conditional upload;
    /// cloud → verbatim apply).
    /// </summary>
    private async Task ApplyNoteWinnerAsync(
        CycleContext ctx, string uuid, Note live, DriverFile container, SyncStateEntry? state,
        bool localWins, long cloudMs, string? uploadIfMatchEtag, byte[]? knownBytes, bool allowRetry, CancellationToken ct)
    {
        if (localWins)
        {
            var bytes = knownBytes ?? await CacheOrReadAsync(container.Path, ct);
            if (bytes is null)
            {
                Error(ctx, $"Note {uuid}: the cloud container vanished mid-conflict; deferred.");
                return;
            }
            var (cloudNote, cloudImages) = await DecodeNoteContainerAsync(ctx, uuid, bytes, ct);
            if (cloudNote is null)
                return;
            await CreateConflictCopyAsync(ctx, cloudNote, cloudImages, ct);

            try
            {
                await UploadNoteAsync(ctx, live, uploadIfMatchEtag, state, ct);
            }
            catch (SyncDriverException ex) when (ex.Kind == SyncDriverErrorKind.PreconditionFailed && allowRetry)
            {
                await ReResolveLiveNoteAsync(ctx, uuid, live, container.Path, state, previousCloudMs: cloudMs, ct);
            }
        }
        else
        {
            await CreateConflictCopyAsync(
                ctx, live, await _notes.GetNoteImagesWithDisplayAsync(live.Id, ct), ct);
            await DownloadNoteAsync(ctx, uuid, container, state, knownBytes, ct);
        }
    }

    /// <summary>
    /// Equal LWW stamps (C == L): usually agreement — but a drifted cloud ETag
    /// means the bytes changed without a clock change: either a benign re-encode
    /// (identical content, different DEFLATE bytes) or a same-millisecond
    /// different-content tie. Download and compare; identical content refreshes
    /// the agreement, different content goes through the deterministic
    /// <see cref="ConflictResolver"/> tiebreak (loser → conflict copy).
    /// </summary>
    private async Task ResolveNoteTieAsync(
        CycleContext ctx, string uuid, Note live, DriverFile container, SyncStateEntry? state,
        byte[]? knownBytes, bool allowRetry, CancellationToken ct)
    {
        var l = ToMs(live.UpdatedAt);
        // An absent validator (WebDAV degradation) or first pairing cannot
        // prove agreement merely because both clocks happen to be equal.
        var drifted = state is null || state.Etag.Length == 0 || container.Etag != state.Etag;
        if (!drifted)
        {
            await RecordStateAsync(ctx, SyncEntryKind.Note, uuid, string.Empty,
                allowRetry ? container.Etag : string.Empty, l, state, ct);
            return;
        }

        var bytes = knownBytes ?? await CacheOrReadAsync(container.Path, ct);
        if (bytes is null)
        {
            Error(ctx, $"Note {uuid}: the cloud container vanished mid-cycle; deferred to the next sync.");
            return;
        }
        var (cloudNote, cloudImages) = await DecodeNoteContainerAsync(ctx, uuid, bytes, ct);
        if (cloudNote is null)
            return;

        var localDigest = NoteDigest(live);
        var cloudDigest = NoteDigest(cloudNote);
        if (localDigest == cloudDigest)
        {
            // Benign: identical content under different bytes (e.g. a peer on a
            // different runtime re-encoded it). Refresh the agreement — no copy, no upload.
            await RecordStateAsync(ctx, SyncEntryKind.Note, uuid, string.Empty, container.Etag, l, state, ct);
            return;
        }

        _log.LogInformation(
            "Note {Uuid}: equal LWW stamps with different content; tiebreaking by content digest.", uuid);
        var localWins = ConflictResolver.Resolve(localDigest, cloudDigest) == ConflictWinner.Local;
        await ApplyNoteWinnerAsync(
            ctx, uuid, live, container, state,
            localWins, cloudMs: l, uploadIfMatchEtag: allowRetry ? container.Etag : null,
            knownBytes: bytes, allowRetry, ct);
    }

    /// <summary>
    /// Inline re-resolve after a lost conditional write (design: a peer overwrote
    /// the cloud file under us): read the fresh cloud state, re-run the decision
    /// once with retries off. When the fresh cloud timestamp still equals the one
    /// the failed decision was based on, the losing side has already been
    /// materialized — the winner is pushed unconditionally instead of re-running
    /// the conflict branch (which would duplicate the copy).
    /// </summary>
    private async Task ReResolveLiveNoteAsync(
        CycleContext ctx, string uuid, Note live, string containerPath, SyncStateEntry? state,
        long previousCloudMs, CancellationToken ct)
    {
        _log.LogInformation("Note {Uuid}: the cloud file changed under us; re-resolving inline.", uuid);
        var bytes = await _driver.ReadFileAsync(containerPath, ct);
        if (bytes is null)
        {
            // The peer's write vanished (a concurrent delete won): plain upload, nothing to merge.
            await UploadNoteAsync(ctx, live, ifMatchEtag: null, state, ct);
            return;
        }
        QnoteHeader header;
        try
        {
            header = QnoteContainerCodec.ReadHeader(bytes);
        }
        catch (QnoteContainerException ex)
        {
            Error(ctx, $"Note {uuid}: the re-resolved container is unreadable: {ex.Message}", ex);
            return;
        }
        if (header.Uuid.ToString("N") != uuid)
        {
            Error(ctx, $"Note {uuid}: the re-resolved container carries uuid {header.Uuid:N}; skipped.");
            return;
        }
        if (header.UpdatedAtMs == previousCloudMs)
        {
            // Same cloud version the failed decision already saw (and copied if it lost).
            await UploadNoteAsync(ctx, live, ifMatchEtag: null, state, ct);
            return;
        }
        await DecideLiveNoteAsync(
            ctx, uuid, live,
            new DriverFile(containerPath, string.Empty, -1),
            header.UpdatedAtMs, ToMs(live.UpdatedAt), state,
            knownBytes: bytes, allowRetry: false, ct);
    }

    // ---------- categories: delta ----------

    private async Task ProcessCategoryDeltaAsync(CycleContext ctx, string uuid, CancellationToken ct)
    {
        try
        {
            ctx.CloudTombstones.TryGetValue(uuid, out var tombstoneFile);
            long? tombMs = null;
            if (tombstoneFile is not null)
            {
                tombMs = await ReadTombstoneMsAsync(ctx, tombstoneFile, ct);
                if (tombMs is null)
                    return;
            }

            var stamp = ctx.CategoryStamps.TryGetValue(uuid, out var s) ? s : null;
            var state = ctx.CategoryState.TryGetValue(uuid, out var st) ? st : null;

            if (tombMs is { } deletedMs)
            {
                await ProcessCategoryTombstoneAsync(ctx, uuid, stamp, state, tombstoneFile!, deletedMs, ct);
                return;
            }

            if (!ctx.CloudCategories.TryGetValue(uuid, out var container))
                return;
            if (stamp is null)
            {
                await DownloadCategoryAsync(ctx, uuid, container, state, ct);
                return;
            }
            if (!stamp.IsLive)
            {
                var resurrectMs = await ProbeCategoryUpdatedAtMsAsync(ctx, container, ct);
                if (resurrectMs is { } cloudMs && cloudMs > ToMs(stamp.DeletedAt!.Value))
                    await DownloadCategoryAsync(ctx, uuid, container, state, ct);
                return;
            }

            var live = RequireLiveCategory(ctx, uuid);
            if (live is null)
                return;
            var l = ToMs(stamp.UpdatedAt);
            var c = await ProbeCategoryUpdatedAtMsAsync(ctx, container, ct);
            if (c is null)
                return;
            await DecideLiveCategoryAsync(
                ctx, uuid, live, container, c.Value, l, state, allowRetry: true, ct);
        }
        catch (SyncDriverException ex) when (ex.Kind == SyncDriverErrorKind.PreconditionFailed)
        {
            Error(ctx, $"Category {uuid}: a conditional write kept losing the race; deferred to the next sync.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error(ctx, $"Category {uuid}: {ex.Message}", ex);
        }
    }

    private async Task ProcessCategoryTombstoneAsync(
        CycleContext ctx, string uuid, SyncRowStamp? stamp, SyncStateEntry? state,
        DriverFile tombstoneFile, long deletedMs, CancellationToken ct)
    {
        ctx.CloudCategories.TryGetValue(uuid, out var container);
        var deletedAt = FromMs(deletedMs);

        if (stamp is null)
        {
            if (container is not null)
            {
                await _driver.DeleteFileAsync(container.Path, ct);
                ctx.DeletedRemote++;
            }
            return;
        }

        if (stamp.IsLive)
        {
            var live = RequireLiveCategory(ctx, uuid);
            if (live is null)
                return;
            if (deletedAt > stamp.UpdatedAt)
            {
                await _notes.ImportCategoryTombstoneAsync(
                    uuid, deletedAt,
                    State(SyncEntryKind.Category, uuid, string.Empty, tombstoneFile.Etag, deletedMs), ct,
                    expected: ExpectedCategory(ctx, uuid));
                ctx.TombstonesApplied++;
                if (container is not null)
                {
                    await _driver.DeleteFileAsync(container.Path, ct);
                    ctx.DeletedRemote++;
                }
            }
            else
            {
                await _driver.DeleteFileAsync(tombstoneFile.Path, ct);
                ctx.DeletedRemote++;
                await UploadCategoryAsync(ctx, live, ifMatchEtag: null, state, ct);
            }
            return;
        }

        var localDeletedAtMs = ToMs(stamp.DeletedAt!.Value);
        if (localDeletedAtMs > deletedMs)
        {
            var write = await _driver.WriteFileAsync(
                tombstoneFile.Path, TombstoneBytes(localDeletedAtMs), tombstoneFile.Etag, ct);
            ctx.Uploaded++;
            await RecordStateAsync(
                ctx, SyncEntryKind.Category, uuid, write.RemoteId, write.Etag, localDeletedAtMs, state, ct);
        }
        else if (deletedMs > localDeletedAtMs)
        {
            await _notes.ImportCategoryTombstoneAsync(
                uuid, FromMs(deletedMs),
                State(SyncEntryKind.Category, uuid, string.Empty, tombstoneFile.Etag, deletedMs), ct,
                expected: ExpectedCategory(ctx, uuid));
            ctx.TombstonesApplied++;
        }
        else
        {
            await RecordStateAsync(
                ctx, SyncEntryKind.Category, uuid, string.Empty, tombstoneFile.Etag, localDeletedAtMs, state, ct);
        }
        if (container is not null)
        {
            await _driver.DeleteFileAsync(container.Path, ct);
            ctx.DeletedRemote++;
        }
    }

    /// <summary>Categories: plain LWW, NO conflict copies (design). Same retry shape as notes.</summary>
    private async Task DecideLiveCategoryAsync(
        CycleContext ctx, string uuid, Category local, DriverFile container,
        long c, long l, SyncStateEntry? state, bool allowRetry, CancellationToken ct)
    {
        var s = state?.LastSyncedAtMs ?? 0;
        var ifMatchEtag = allowRetry ? container.Etag : null;
        var agreeEtag = allowRetry ? container.Etag : string.Empty;

        if (c != s && l > s && c != l)
        {
            await ApplyCategoryWinnerAsync(
                ctx, uuid, local, container, state,
                localWins: l > c, cloudMs: c, uploadIfMatchEtag: ifMatchEtag, allowRetry, ct);
            return;
        }

        if (c == l)
        {
            await ResolveCategoryTieAsync(ctx, uuid, local, container, state, allowRetry, ct);
            return;
        }
        if (c > l)
        {
            await DownloadCategoryAsync(ctx, uuid, container, state, ct);
            return;
        }

        try
        {
            await UploadCategoryAsync(ctx, local, ifMatchEtag, state, ct);
        }
        catch (SyncDriverException ex) when (ex.Kind == SyncDriverErrorKind.PreconditionFailed && allowRetry)
        {
            await ReResolveLiveCategoryAsync(ctx, uuid, local, container.Path, state, previousCloudMs: c, ct);
        }
    }

    /// <summary>Categories: plain LWW, NO conflict copies (design) — the loser is overwritten/discarded.</summary>
    private async Task ApplyCategoryWinnerAsync(
        CycleContext ctx, string uuid, Category local, DriverFile container, SyncStateEntry? state,
        bool localWins, long cloudMs, string? uploadIfMatchEtag, bool allowRetry, CancellationToken ct)
    {
        if (localWins)
        {
            try
            {
                await UploadCategoryAsync(ctx, local, uploadIfMatchEtag, state, ct);
            }
            catch (SyncDriverException ex) when (ex.Kind == SyncDriverErrorKind.PreconditionFailed && allowRetry)
            {
                await ReResolveLiveCategoryAsync(ctx, uuid, local, container.Path, state, previousCloudMs: cloudMs, ct);
            }
        }
        else
        {
            await DownloadCategoryAsync(ctx, uuid, container, state, ct);
        }
    }

    /// <summary>
    /// Equal stamps for a category: usually agreement; a drifted ETag means a
    /// benign re-encode (identical fields → refresh the agreement) or a
    /// same-millisecond different-content tie → deterministic
    /// <see cref="ConflictResolver"/> tiebreak (loser discarded — no copies).
    /// </summary>
    private async Task ResolveCategoryTieAsync(
        CycleContext ctx, string uuid, Category local, DriverFile container, SyncStateEntry? state,
        bool allowRetry, CancellationToken ct)
    {
        var l = ToMs(local.UpdatedAt);
        var drifted = state is null || state.Etag.Length == 0 || container.Etag != state.Etag;
        if (!drifted)
        {
            await RecordStateAsync(ctx, SyncEntryKind.Category, uuid, string.Empty,
                allowRetry ? container.Etag : string.Empty, l, state, ct);
            return;
        }

        var file = await ReadCategoryFileAsync(ctx, container, ct);
        if (file is null)
            return;
        var localFile = ToCategoryFile(local);
        if (SameCategoryFile(localFile, file))
        {
            await RecordStateAsync(ctx, SyncEntryKind.Category, uuid, string.Empty, container.Etag, l, state, ct);
            return;
        }

        _log.LogInformation(
            "Category {Uuid}: equal LWW stamps with different content; tiebreaking by content digest.", uuid);
        var localWins = ConflictResolver.Resolve(CategoryDigest(localFile), CategoryDigest(file)) == ConflictWinner.Local;
        await ApplyCategoryWinnerAsync(
            ctx, uuid, local, container, state,
            localWins, cloudMs: l, uploadIfMatchEtag: allowRetry ? container.Etag : null, allowRetry, ct);
    }

    private async Task ReResolveLiveCategoryAsync(
        CycleContext ctx, string uuid, Category local, string containerPath, SyncStateEntry? state,
        long previousCloudMs, CancellationToken ct)
    {
        _log.LogInformation("Category {Uuid}: the cloud file changed under us; re-resolving inline.", uuid);
        var bytes = await _driver.ReadFileAsync(containerPath, ct);
        if (bytes is null)
        {
            await UploadCategoryAsync(ctx, local, ifMatchEtag: null, state, ct);
            return;
        }
        SyncCategoryFile file;
        try
        {
            file = JsonSerializer.Deserialize(bytes, SyncCategoryFileJsonContext.Default.SyncCategoryFile)
                ?? throw new JsonException("The category file decoded to null.");
        }
        catch (JsonException ex)
        {
            Error(ctx, $"Category {uuid}: the re-resolved file is unreadable: {ex.Message}", ex);
            return;
        }
        if (file.UpdatedAtMs == previousCloudMs)
        {
            await UploadCategoryAsync(ctx, local, ifMatchEtag: null, state, ct);
            return;
        }
        await DecideLiveCategoryAsync(
            ctx, uuid, local,
            new DriverFile(containerPath, string.Empty, -1),
            file.UpdatedAtMs, ToMs(local.UpdatedAt), state, allowRetry: false, ct);
    }

    // ---------- apply path (download → local) ----------

    private async Task DownloadNoteAsync(
        CycleContext ctx, string uuid, DriverFile container, SyncStateEntry? state, byte[]? knownBytes, CancellationToken ct)
    {
        var bytes = knownBytes ?? await CacheOrReadAsync(container.Path, ct);
        if (bytes is null)
        {
            Error(ctx, $"Note {uuid}: the cloud container vanished mid-cycle; deferred to the next sync.");
            return;
        }
        var (note, images) = await DecodeNoteContainerAsync(ctx, uuid, bytes, ct);
        if (note is null)
            return;

        // Files before the transaction: a crash leaves only orphan files, which the
        // next cycle re-applies (content-addressed, idempotent). The tx then makes
        // the note row + FTS + note_images + sync state atomic (PRD decision 11).
        await _notes.ImportNoteAsync(
            note, images,
            State(SyncEntryKind.Note, uuid, string.Empty, container.Etag, ToMs(note.UpdatedAt)), ct,
            expected: ExpectedNote(ctx, uuid));
        ctx.Downloaded++;
    }

    /// <summary>
    /// Decodes a note container into the local Note plus its image rows; the
    /// original FILES are materialized along the way (content-addressed,
    /// idempotent — always before the caller's import transaction).
    /// </summary>
    private async Task<(Note? Note, List<NoteImage> Images)> DecodeNoteContainerAsync(
        CycleContext ctx, string uuid, byte[] bytes, CancellationToken ct)
    {
        QnotePayload payload;
        try
        {
            payload = QnoteContainerCodec.Decode(bytes, ctx.Key);
        }
        catch (QnoteContainerException ex)
        {
            Error(ctx, $"Note {uuid}: the cloud container is unreadable: {ex.Message}", ex);
            return (null, []);
        }
        if (payload.Header.Uuid.ToString("N") != uuid)
        {
            Error(ctx, $"Note {uuid}: the cloud container carries uuid {payload.Header.Uuid:N}; skipped.");
            return (null, []);
        }

        string title, category, content;
        try
        {
            (title, category, content) = SyncNoteBody.Decode(payload.Body);
        }
        catch (QnoteContainerException ex)
        {
            Error(ctx, $"Note {uuid}: {ex.Message}", ex);
            return (null, []);
        }

        var note = new Note
        {
            Uuid = uuid,
            Title = title,
            Category = category,
            Content = content,
            // PlainText is derived, never transported (the single-authority rule).
            PlainText = MarkdownText.ToPlainText(content),
            CreatedAt = FromMs(payload.Header.CreatedAtMs),
            UpdatedAt = FromMs(payload.Header.UpdatedAtMs),
        };
        var images = await MaterializeAndBuildImageRowsAsync(payload.Entries, ct);
        return (note, images);
    }

    private async Task DownloadCategoryAsync(
        CycleContext ctx, string uuid, DriverFile container, SyncStateEntry? state, CancellationToken ct)
    {
        var file = await ReadCategoryFileAsync(ctx, container, ct);
        if (file is null)
            return;
        if (file.Uuid != uuid)
        {
            Error(ctx, $"Category {uuid}: the cloud file carries uuid {file.Uuid}; skipped.");
            return;
        }

        var category = new Category
        {
            Uuid = file.Uuid,
            Name = file.Name,
            IconKey = file.IconKey,
            Color = file.Color,
            SortOrder = file.SortOrder,
            CreatedAt = FromMs(file.CreatedAtMs),
            UpdatedAt = FromMs(file.UpdatedAtMs),
            DeletedAt = file.DeletedAtMs is { } d ? FromMs(d) : null,
        };
        await _notes.ImportCategoryAsync(
            category,
            State(SyncEntryKind.Category, uuid, string.Empty, container.Etag, file.UpdatedAtMs), ct,
            expected: ExpectedCategory(ctx, uuid));
        ctx.Downloaded++;
    }

    private static SyncImportSnapshot<Note> ExpectedNote(CycleContext ctx, string uuid) =>
        new(ctx.NoteStamps.GetValueOrDefault(uuid), ctx.LiveNotes.GetValueOrDefault(uuid));

    private static SyncImportSnapshot<Category> ExpectedCategory(CycleContext ctx, string uuid) =>
        new(ctx.CategoryStamps.GetValueOrDefault(uuid), ctx.LiveCategories.GetValueOrDefault(uuid));

    // ---------- uploads ----------

    private async Task UploadNoteAsync(
        CycleContext ctx, Note note, string? ifMatchEtag, SyncStateEntry? state, CancellationToken ct)
    {
        var entries = new List<QnoteEntry>();
        var images = await _notes.GetNoteImagesAsync(note.Id, ct);
        foreach (var image in images)
        {
            if (!HexDigest.IsSha256Hex(image.Sha256))
            {
                _log.LogWarning("Note {Uuid}: image sha '{Sha}' is not 64-hex; the entry is omitted.", note.Uuid, image.Sha256);
                continue;
            }
            var fileName = image.Ext.Length == 0 ? image.Sha256 : $"{image.Sha256}.{image.Ext}";
            var path = Path.Combine(_paths.ImagesDir, fileName);
            if (!File.Exists(path))
            {
                _log.LogWarning(
                    "Note {Uuid}: original image {Path} is missing; the entry is omitted (the peer degrades to a placeholder).",
                    note.Uuid, path);
                continue;
            }
            entries.Add(new QnoteEntry
            {
                Kind = QnoteEntryKind.Image,
                Sha256 = image.Sha256,
                // The row stores the extension, not the original file name — the wire
                // name exists to carry the extension back to the peer.
                FileName = image.Ext.Length == 0 ? "image" : $"image.{image.Ext}",
                Data = await File.ReadAllBytesAsync(path, ct),
            });
        }

        var header = new QnoteHeader
        {
            Uuid = Guid.Parse(note.Uuid),
            CreatedAtMs = ToMs(note.CreatedAt),
            UpdatedAtMs = ToMs(note.UpdatedAt),
            DeletedAtMs = 0, // only live rows upload containers
        };
        var body = SyncNoteBody.Encode(note.Title, note.Category, note.Content);
        var write = await _driver.WriteFileAsync(
            NoteContainerPath(note.Uuid),
            QnoteContainerCodec.Encode(header, body, entries, ctx.Key),
            ifMatchEtag, ct);
        ctx.Uploaded++;
        await RecordStateAsync(
            ctx, SyncEntryKind.Note, note.Uuid, write.RemoteId, write.Etag, ToMs(note.UpdatedAt), state, ct);
    }

    private async Task UploadCategoryAsync(
        CycleContext ctx, Category category, string? ifMatchEtag, SyncStateEntry? state, CancellationToken ct)
    {
        var write = await _driver.WriteFileAsync(
            CategoryFilePath(category.Uuid),
            JsonSerializer.SerializeToUtf8Bytes(ToCategoryFile(category), SyncCategoryFileJsonContext.Default.SyncCategoryFile),
            ifMatchEtag, ct);
        ctx.Uploaded++;
        await RecordStateAsync(
            ctx, SyncEntryKind.Category, category.Uuid, write.RemoteId, write.Etag, ToMs(category.UpdatedAt), state, ct);
    }

    private async Task CreateConflictCopyAsync(CycleContext ctx, Note loser, IReadOnlyList<NoteImage> images, CancellationToken ct)
    {
        // Fresh uuid → the copy is an ordinary note from the matrix's point of view
        // (its first cycle plain-uploads it). Original Created/Updated preserved.
        var copy = loser with
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Title = loser.Title + _conflictCopyTitleSuffix,
        };
        await _notes.ImportNoteAsync(copy, images, syncState: null, ct);
        ctx.ConflictCopies++;
        _log.LogInformation("Note {Original}: conflict copy created as {Copy}.", loser.Uuid, copy.Uuid);
    }

    // ---------- probes ----------

    /// <summary>
    /// The cloud LWW timestamp for a note container: the etag fast path (the
    /// listing etag equals the agreement etag ⇒ the content still IS the agreed
    /// content, so C == S — zero reads) or a header probe — a 48-byte range read
    /// when the driver supports it, a full download otherwise (cached for the apply).
    /// </summary>
    private async Task<long?> ProbeNoteLwwAsync(
        CycleContext ctx, string uuid, DriverFile container, SyncStateEntry? state, CancellationToken ct)
    {
        if (state is not null && state.Etag.Length > 0 && container.Etag == state.Etag)
            return state.LastSyncedAtMs;

        if (_driver.SupportsRangeRead)
        {
            var head = await _driver.ReadRangeAsync(container.Path, 0, QnoteContainerCodec.HeaderByteCount, ct);
            if (head is not null && head.Length >= QnoteContainerCodec.HeaderByteCount)
            {
                var header = QnoteContainerCodec.ReadHeader(head);
                if (header.Uuid.ToString("N") != uuid)
                {
                    Error(ctx, $"Note {uuid}: the cloud container carries uuid {header.Uuid:N}; skipped.");
                    return null;
                }
                return header.UpdatedAtMs;
            }
            // A short/absent range read falls through to the full download so the
            // error (if any) is precise.
        }

        var full = await CacheOrReadAsync(container.Path, ct);
        if (full is null)
        {
            Error(ctx, $"Note {uuid}: the cloud container vanished mid-cycle; deferred to the next sync.");
            return null;
        }
        var probed = QnoteContainerCodec.ReadHeader(full);
        if (probed.Uuid.ToString("N") != uuid)
        {
            Error(ctx, $"Note {uuid}: the cloud container carries uuid {probed.Uuid:N}; skipped.");
            return null;
        }
        return probed.UpdatedAtMs;
    }

    /// <summary>Phase-2 probe: just the timestamp (the bytes land in the cache either way).</summary>
    private async Task<long?> ProbeNoteUpdatedAtMsAsync(CycleContext ctx, DriverFile container, CancellationToken ct)
    {
        var full = await CacheOrReadAsync(container.Path, ct);
        if (full is null)
            return null;
        try
        {
            return QnoteContainerCodec.ReadHeader(full).UpdatedAtMs;
        }
        catch (QnoteContainerException ex)
        {
            Error(ctx, $"The cloud container {container.Path} is unreadable: {ex.Message}", ex);
            return null;
        }
    }

    private async Task<long?> ProbeCategoryUpdatedAtMsAsync(CycleContext ctx, DriverFile container, CancellationToken ct)
    {
        var file = await ReadCategoryFileAsync(ctx, container, ct);
        return file?.UpdatedAtMs;
    }

    private async Task<SyncCategoryFile?> ReadCategoryFileAsync(CycleContext ctx, DriverFile container, CancellationToken ct)
    {
        var bytes = await CacheOrReadAsync(container.Path, ct);
        if (bytes is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize(bytes, SyncCategoryFileJsonContext.Default.SyncCategoryFile)
                ?? throw new JsonException("The category file decoded to null.");
        }
        catch (JsonException ex)
        {
            Error(ctx, $"The cloud category file {container.Path} is unreadable: {ex.Message}", ex);
            return null;
        }
    }

    private async Task<long?> ReadTombstoneMsAsync(CycleContext ctx, DriverFile file, CancellationToken ct)
    {
        var bytes = await _driver.ReadFileAsync(file.Path, ct);
        if (bytes is null || bytes.Length != 8)
        {
            Error(ctx, $"The cloud tombstone {file.Path} is missing or malformed " +
                $"({bytes?.Length.ToString() ?? "absent"} bytes); deferred to the next sync.");
            return null;
        }
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    // ---------- images ----------

    /// <summary>
    /// Builds the note_images rows for a downloaded container and materializes
    /// the original FILES along the way (content-addressed, idempotent, always
    /// before the caller's import transaction — a crash leaves only orphan files
    /// that the next cycle re-applies).
    /// </summary>
    private async Task<List<NoteImage>> MaterializeAndBuildImageRowsAsync(IReadOnlyList<QnoteEntry> entries, CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.ImagesDir);
        var images = new List<NoteImage>();
        foreach (var entry in entries)
        {
            if (entry.Kind == QnoteEntryKind.Attachment)
            {
                _log.LogWarning(
                    "Skipping attachment entry {Sha} inside a note container — the todo cloud layout is undecided (L1).",
                    entry.Sha256);
                continue;
            }
            if (!HexDigest.IsSha256Hex(entry.Sha256))
            {
                _log.LogWarning("Skipping image entry with non-hex sha '{Sha}' — it can never reach the filesystem.", entry.Sha256);
                continue;
            }

            var ext = ExtFromFileName(entry.FileName);
            await MaterializeOriginalAsync(entry.Sha256, ext, entry.Data, ct);
            var dims = RtfImagePayload.FromBytes(entry.Data);
            byte[]? display = null;
            if (_displayImageGenerator is not null)
            {
                try
                {
                    display = _displayImageGenerator(entry.Data, ext);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "The display-image generator failed for {Sha}; the row degrades to no display copy.", entry.Sha256);
                }
            }
            images.Add(new NoteImage
            {
                Sha256 = entry.Sha256,
                Ext = ext,
                ByteSize = entry.Data.LongLength,
                Width = dims?.PixelWidth ?? 0,
                Height = dims?.PixelHeight ?? 0,
                DisplayBytes = display,
                CreatedAt = DateTimeOffset.UtcNow, // display sort key only — never an LWW input
            });
        }
        return images;
    }

    /// <summary>Temp write + race-tolerant move (same shape as <c>ImageService.ImportAsync</c>):
    /// the content address IS the identity, so an existing file is already the same bytes.</summary>
    private async Task MaterializeOriginalAsync(string sha, string ext, byte[] data, CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.ImagesDir);
        var fileName = ext.Length == 0 ? sha : $"{sha}.{ext}";
        var path = Path.Combine(_paths.ImagesDir, fileName);
        if (File.Exists(path))
            return;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, data, ct);
            File.Move(temp, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Lost a materialize race — the identical content is already stored.
        }
        finally
        {
            TryDelete(temp);
        }
    }

    // ---------- listing parsing ----------

    private static Dictionary<string, DriverFile> ParseExtensionListing(
        DriverListing listing, string folder, string extension)
    {
        var map = new Dictionary<string, DriverFile>(StringComparer.Ordinal);
        var prefix = folder + "/";
        foreach (var file in listing.Files)
        {
            var name = file.Path.StartsWith(prefix, StringComparison.Ordinal) ? file.Path[prefix.Length..] : file.Path;
            if (name.Length <= extension.Length || !name.EndsWith(extension, StringComparison.Ordinal))
                continue; // a foreign file — not ours to judge, never touched
            map[name[..^extension.Length]] = file;
        }
        return map;
    }

    private static Dictionary<string, DriverFile> ParseTombstoneListing(DriverListing listing, string folder)
    {
        var map = new Dictionary<string, DriverFile>(StringComparer.Ordinal);
        var prefix = folder + "/";
        foreach (var file in listing.Files)
        {
            var name = file.Path.StartsWith(prefix, StringComparison.Ordinal) ? file.Path[prefix.Length..] : file.Path;
            if (name.Length == 0)
                continue;
            map[name] = file;
        }
        return map;
    }

    // ---------- small helpers ----------

    private Note? RequireLiveNote(CycleContext ctx, string uuid)
    {
        if (ctx.LiveNotes.TryGetValue(uuid, out var live))
            return live;
        Error(ctx, $"Note {uuid}: live stamp but no full row; skipped.");
        return null;
    }

    private Category? RequireLiveCategory(CycleContext ctx, string uuid)
    {
        if (ctx.LiveCategories.TryGetValue(uuid, out var live))
            return live;
        Error(ctx, $"Category {uuid}: live stamp but no full row; skipped.");
        return null;
    }

    private async Task RecordStateAsync(
        CycleContext ctx, SyncEntryKind kind, string uuid, string remoteId, string etag,
        long syncedAtMs, SyncStateEntry? existing, CancellationToken ct)
    {
        // Keep the last known remote id when this record has none to offer.
        var effectiveRemoteId = remoteId.Length == 0 ? existing?.RemoteId ?? string.Empty : remoteId;
        if (existing is not null
            && existing.LastSyncedAtMs == syncedAtMs
            && existing.Etag == etag
            && existing.RemoteId == effectiveRemoteId)
        {
            return; // the idempotent fast path: nothing changed, nothing written
        }
        await _syncState.UpsertAsync(
            new SyncStateEntry
            {
                EntryKind = kind,
                EntryUuid = uuid,
                RemoteId = effectiveRemoteId,
                Etag = etag,
                LastSyncedAtMs = syncedAtMs,
            }, ct);
    }

    private async Task<byte[]?> CacheOrReadAsync(string path, CancellationToken ct)
    {
        if (_downloadCache.TryGetValue(path, out var cached))
            return cached;
        var bytes = await _driver.ReadFileAsync(path, ct);
        if (bytes is not null)
            _downloadCache[path] = bytes;
        return bytes;
    }

    private void Error(CycleContext ctx, string message, Exception? ex = null)
    {
        ctx.Errors.Add(message);
        if (ex is null)
            _log.LogError("{Message}", message);
        else
            _log.LogError(ex, "{Message}", message);
    }

    private static SyncStateEntry State(SyncEntryKind kind, string uuid, string remoteId, string etag, long syncedAtMs) => new()
    {
        EntryKind = kind,
        EntryUuid = uuid,
        RemoteId = remoteId,
        Etag = etag,
        LastSyncedAtMs = syncedAtMs,
    };

    private static SyncCategoryFile ToCategoryFile(Category category) => new()
    {
        Uuid = category.Uuid,
        Name = category.Name,
        IconKey = category.IconKey,
        Color = category.Color,
        SortOrder = category.SortOrder,
        CreatedAtMs = ToMs(category.CreatedAt),
        UpdatedAtMs = ToMs(category.UpdatedAt),
        DeletedAtMs = category.DeletedAt is { } deleted ? ToMs(deleted) : null,
    };

    private static string NoteContainerPath(string uuid) => $"{NotesFolder}/{uuid}{NoteContainerExtension}";

    /// <summary>
    /// The canonical content digest for a note tie: sha256 over the exact wire
    /// envelope (title + category + markdown). Both peers compute the same pair
    /// of digests, which is what makes <see cref="ConflictResolver.Resolve"/>
    /// converge without communication.
    /// </summary>
    private static string NoteDigest(Note note) =>
        Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(SyncNoteBody.Encode(note.Title, note.Category, note.Content))));

    private static string CategoryDigest(SyncCategoryFile file) =>
        Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(file, SyncCategoryFileJsonContext.Default.SyncCategoryFile)));

    private static bool SameCategoryFile(SyncCategoryFile a, SyncCategoryFile b) =>
        a.Uuid == b.Uuid && a.Name == b.Name && a.IconKey == b.IconKey && a.Color == b.Color
        && a.SortOrder == b.SortOrder && a.CreatedAtMs == b.CreatedAtMs
        && a.UpdatedAtMs == b.UpdatedAtMs && a.DeletedAtMs == b.DeletedAtMs;

    private static string CategoryFilePath(string uuid) => $"{CategoriesFolder}/{uuid}{CategoryFileExtension}";

    private static string TombstonePath(string uuid) => $"{TombstonesFolder}/{uuid}";

    private static byte[] TombstoneBytes(long deletedAtMs)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, deletedAtMs);
        return bytes;
    }

    private static string ExtFromFileName(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Length == 0 ? string.Empty : ext[1..].ToLowerInvariant();
    }

    private static IEnumerable<string> Ordered(IEnumerable<string> keys) =>
        keys.Order(StringComparer.Ordinal);

    private static long ToMs(DateTimeOffset value) => value.ToUniversalTime().ToUnixTimeMilliseconds();

    private static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }

    /// <summary>Per-cycle mutable state shared by the phases.</summary>
    private sealed class CycleContext
    {
        public byte[]? Key { get; init; }

        public Dictionary<string, SyncRowStamp> NoteStamps { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, SyncRowStamp> CategoryStamps { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, Note> LiveNotes { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, Category> LiveCategories { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, SyncStateEntry> NoteState { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, SyncStateEntry> CategoryState { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, DriverFile> CloudNotes { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, DriverFile> CloudCategories { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, DriverFile> CloudTombstones { get; set; } = new(StringComparer.Ordinal);

        public List<string> Errors { get; } = [];
        public int Uploaded { get; set; }
        public int Downloaded { get; set; }
        public int DeletedRemote { get; set; }
        public int TombstonesApplied { get; set; }
        public int ConflictCopies { get; set; }
    }
}
