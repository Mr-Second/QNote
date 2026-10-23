using Microsoft.Data.Sqlite;
using QNote.Models;

namespace QNote.Data;

/// <summary>
/// SQLite-backed <c>sync_state</c> persistence (schema v8, PRD decision 11). The
/// table's PRIMARY KEY is (entry_kind, entry_uuid); kinds partition the keyspace.
/// The static <c>*WithinAsync</c> overloads exist because the sync state of an
/// entry MUST share the transaction with the data write it describes — writing it
/// separately would recreate the "库写了、状态没写" window the sidecar design was
/// rejected for. All values are parameterized (database-guidelines).
/// </summary>
public sealed class SyncStateStore : ISyncStateStore
{
    private readonly DbConnectionFactory _factory;

    public SyncStateStore(DbConnectionFactory factory) => _factory = factory;

    /// <summary>One active target shares the table. A switch invalidates its old ETags atomically.</summary>
    public async Task<bool> PrepareTargetAsync(string target, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();
        await using var read = conn.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT Value FROM settings WHERE Key = 'sync.activeTarget';";
        var previous = await read.ExecuteScalarAsync(ct) as string;
        if (!string.Equals(previous, target, StringComparison.Ordinal))
        {
            await using var reset = conn.CreateCommand();
            reset.Transaction = tx;
            reset.CommandText = "DELETE FROM sync_state; DELETE FROM settings WHERE Key = 'sync.pairedTarget'; " +
                "INSERT INTO settings (Key, Value) VALUES ('sync.activeTarget', $target) " +
                "ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
            reset.Parameters.AddWithValue("$target", target);
            await reset.ExecuteNonQueryAsync(ct);
        }
        await using var paired = conn.CreateCommand();
        paired.Transaction = tx;
        paired.CommandText = "SELECT Value FROM settings WHERE Key = 'sync.pairedTarget';";
        var pairedTarget = await paired.ExecuteScalarAsync(ct) as string;
        await tx.CommitAsync(ct);
        return !string.Equals(pairedTarget, target, StringComparison.Ordinal);
    }

    public async Task MarkTargetPairedAsync(string target, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO settings (Key, Value) VALUES ('sync.pairedTarget', $target) " +
            "ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
        cmd.Parameters.AddWithValue("$target", target);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Explicit local replacement must be newer than the cloud versions it supersedes.</summary>
    public async Task RestampLocalNotesAsync(long updatedMs, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE notes SET UpdatedAt = MAX(UpdatedAt, $updated) WHERE DeletedAt IS NULL; " +
            "UPDATE categories SET UpdatedAt = MAX(UpdatedAt, $updated) WHERE DeletedAt IS NULL;";
        cmd.Parameters.AddWithValue("$updated", updatedMs);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Only an explicit first-pairing choice may discard the local note domain.</summary>
    public async Task ReplaceLocalNotesAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // Keep content-addressed image files: todo and other notes may share them.
        cmd.CommandText = "DELETE FROM notes_fts; DELETE FROM note_images; DELETE FROM notes; " +
            "DELETE FROM categories; DELETE FROM sync_state;";
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<SyncStateEntry?> GetAsync(SyncEntryKind kind, string entryUuid, CancellationToken ct = default)
    {
        ValidateKind(kind);
        if (string.IsNullOrEmpty(entryUuid))
            throw new ArgumentException("The entry uuid must not be empty.", nameof(entryUuid));

        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT entry_uuid, remote_id, etag, last_synced_at FROM sync_state " +
            "WHERE entry_kind = $kind AND entry_uuid = $uuid;";
        cmd.Parameters.AddWithValue("$kind", ToDb(kind));
        cmd.Parameters.AddWithValue("$uuid", entryUuid);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapRow(kind, reader) : null;
    }

    public async Task<IReadOnlyList<SyncStateEntry>> GetByKindAsync(SyncEntryKind kind, CancellationToken ct = default)
    {
        ValidateKind(kind);

        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT entry_uuid, remote_id, etag, last_synced_at FROM sync_state " +
            "WHERE entry_kind = $kind ORDER BY entry_uuid;";
        cmd.Parameters.AddWithValue("$kind", ToDb(kind));

        var list = new List<SyncStateEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapRow(kind, reader));
        return list;
    }

    public async Task UpsertAsync(SyncStateEntry entry, CancellationToken ct = default)
    {
        Validate(entry);

        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();
        await UpsertWithinAsync(conn, tx, entry, ct);
        await tx.CommitAsync(ct);
    }

    public async Task DeleteAsync(SyncEntryKind kind, string entryUuid, CancellationToken ct = default)
    {
        ValidateKind(kind);
        if (string.IsNullOrEmpty(entryUuid))
            throw new ArgumentException("The entry uuid must not be empty.", nameof(entryUuid));

        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();
        await DeleteWithinAsync(conn, tx, kind, entryUuid, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Upserts the bookkeeping row inside the CALLER's transaction (PRD decision
    /// 11): the sync state commits or rolls back together with the data write it
    /// describes. Instance methods are for standalone use only.
    /// </summary>
    public static async Task UpsertWithinAsync(
        SqliteConnection conn, SqliteTransaction tx, SyncStateEntry entry, CancellationToken ct = default)
    {
        Validate(entry);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO sync_state (entry_kind, entry_uuid, remote_id, etag, last_synced_at)
            VALUES ($kind, $uuid, $remoteId, $etag, $syncedAt)
            ON CONFLICT(entry_kind, entry_uuid) DO UPDATE SET
                remote_id = excluded.remote_id,
                etag = excluded.etag,
                last_synced_at = excluded.last_synced_at;
            """;
        cmd.Parameters.AddWithValue("$kind", ToDb(entry.EntryKind));
        cmd.Parameters.AddWithValue("$uuid", entry.EntryUuid);
        cmd.Parameters.AddWithValue("$remoteId", entry.RemoteId);
        cmd.Parameters.AddWithValue("$etag", entry.Etag);
        cmd.Parameters.AddWithValue("$syncedAt", entry.LastSyncedAtMs);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Drops the bookkeeping row inside the CALLER's transaction.</summary>
    public static async Task DeleteWithinAsync(
        SqliteConnection conn, SqliteTransaction tx, SyncEntryKind kind, string entryUuid, CancellationToken ct = default)
    {
        ValidateKind(kind);
        if (string.IsNullOrEmpty(entryUuid))
            throw new ArgumentException("The entry uuid must not be empty.", nameof(entryUuid));

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM sync_state WHERE entry_kind = $kind AND entry_uuid = $uuid;";
        cmd.Parameters.AddWithValue("$kind", ToDb(kind));
        cmd.Parameters.AddWithValue("$uuid", entryUuid);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static SyncStateEntry MapRow(SyncEntryKind kind, SqliteDataReader reader) => new()
    {
        EntryKind = kind,
        EntryUuid = reader.GetString(0),
        RemoteId = reader.GetString(1),
        Etag = reader.GetString(2),
        LastSyncedAtMs = reader.GetInt64(3),
    };

    /// <summary>The TEXT values are the storage contract (schema v8 DDL consumers
    /// may inspect the table directly) — mapping lives here and nowhere else.</summary>
    private static string ToDb(SyncEntryKind kind) => kind switch
    {
        SyncEntryKind.Note => "note",
        SyncEntryKind.Category => "category",
        SyncEntryKind.TodoList => "todo_list",
        SyncEntryKind.TodoTask => "todo_task",
        SyncEntryKind.TodoStep => "todo_step",
        SyncEntryKind.TodoAttachment => "todo_attachment",
        _ => throw new ArgumentException($"Unknown sync entry kind: {kind}.", nameof(kind)),
    };

    private static void ValidateKind(SyncEntryKind kind)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentException($"Unknown sync entry kind: {kind}.", nameof(kind));
    }

    private static void Validate(SyncStateEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateKind(entry.EntryKind);
        if (string.IsNullOrEmpty(entry.EntryUuid))
            throw new ArgumentException("The entry uuid must not be empty.", nameof(entry));
    }
}
