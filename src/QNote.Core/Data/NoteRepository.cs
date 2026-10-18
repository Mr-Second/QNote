using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using QNote.Models;
using QNote.Text;

namespace QNote.Data;

/// <summary>
/// SQLite-backed note/category persistence over the single <c>qnote.db</c>. All
/// queries are parameterized and every connection/command/reader is disposed via
/// <c>await using</c> (see database-guidelines). Timestamps are stored as
/// millisecond-epoch integers (schema v8 sync contract) and read back as UTC.
/// Soft-deleted rows (<c>DeletedAt</c> set) are invisible to every read path; the
/// <c>notes_fts</c> FTS5 shadow is maintained in the SAME transaction as each
/// notes mutation (delete-by-rowid + insert on upsert), so the table and its index
/// cannot drift.
/// </summary>
public sealed class NoteRepository : INoteRepository
{
    /// <summary>Max characters of PlainText projected into a list <see cref="NoteSummary.Preview"/>.</summary>
    private const int PreviewLength = 120;

    /// <summary>Marks a row as live — appended to every synced-table WHERE clause.</summary>
    private const string NotDeleted = "DeletedAt IS NULL";

    private const DateTimeStyles UtcStyles =
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    private const string NoteColumns =
        "Id, Uuid, Title, Category, CreatedAt, UpdatedAt, DeletedAt, PlainText, Content";

    private readonly DbConnectionFactory _factory;

    public NoteRepository(DbConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<Note>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT {NoteColumns} FROM notes WHERE {NotDeleted} ORDER BY UpdatedAt DESC;";
        var list = new List<Note>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapNote(reader));

        return list;
    }

    public async Task<IReadOnlyList<NoteSummary>> GetSummariesAsync(string? category = null, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        var hasCategory = !string.IsNullOrEmpty(category);
        cmd.CommandText =
            "SELECT Id, Uuid, Title, substr(PlainText, 1, $len) AS Preview, Category, CreatedAt, UpdatedAt " +
            $"FROM notes WHERE {NotDeleted}" + (hasCategory ? " AND Category = $cat" : "") + " " +
            "ORDER BY UpdatedAt DESC;";
        cmd.Parameters.AddWithValue("$len", PreviewLength);
        if (hasCategory)
            cmd.Parameters.AddWithValue("$cat", category);

        var list = new List<NoteSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new NoteSummary
            {
                Id = reader.GetInt64(0),
                Uuid = reader.GetString(1),
                Title = reader.GetString(2),
                // U+FFFC (RichEdit image placeholder) renders as "obj" boxes in the
                // list — previews are text-only, strip it at projection time.
                Preview = reader.GetString(3).Replace("￼", string.Empty),
                Category = reader.GetString(4),
                CreatedAt = FromDbMs(reader.GetInt64(5)),
                UpdatedAt = FromDbMs(reader.GetInt64(6)),
            });
        }

        return list;
    }

    public async Task<Note?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT {NoteColumns} FROM notes WHERE Id = $id AND {NotDeleted};";
        cmd.Parameters.AddWithValue("$id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapNote(reader) : null;
    }

    public async Task<Note> CreateAsync(Note note, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        long id;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO notes (Uuid, Title, Content, PlainText, Category, CreatedAt, UpdatedAt) " +
                "VALUES ($uuid, $title, $content, $plainText, $category, $createdAt, $updatedAt) " +
                "RETURNING Id;";
            cmd.Parameters.AddWithValue("$uuid", note.Uuid);
            cmd.Parameters.AddWithValue("$title", note.Title);
            cmd.Parameters.AddWithValue("$content", note.Content);
            cmd.Parameters.AddWithValue("$plainText", note.PlainText);
            cmd.Parameters.AddWithValue("$category", note.Category);
            cmd.Parameters.AddWithValue("$createdAt", ToDbMs(note.CreatedAt));
            cmd.Parameters.AddWithValue("$updatedAt", ToDbMs(note.UpdatedAt));

            var result = await cmd.ExecuteScalarAsync(ct)
                ?? throw new InvalidOperationException("INSERT ... RETURNING Id returned no value.");
            id = (long)result;
        }

        await UpsertFtsAsync(conn, tx, id, note.Title, note.PlainText, ct);
        await tx.CommitAsync(ct);

        return note with { Id = id };
    }

    public async Task UpdateAsync(Note note, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "UPDATE notes SET Title = $title, Content = $content, PlainText = $plainText, Category = $category, UpdatedAt = $updatedAt " +
                $"WHERE Id = $id AND {NotDeleted};";
            cmd.Parameters.AddWithValue("$title", note.Title);
            cmd.Parameters.AddWithValue("$content", note.Content);
            cmd.Parameters.AddWithValue("$plainText", note.PlainText);
            cmd.Parameters.AddWithValue("$category", note.Category);
            cmd.Parameters.AddWithValue("$updatedAt", ToDbMs(note.UpdatedAt));
            cmd.Parameters.AddWithValue("$id", note.Id);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        await UpsertFtsAsync(conn, tx, note.Id, note.Title, note.PlainText, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Soft-deletes a note: stamps <c>DeletedAt</c> AND <c>UpdatedAt</c> with the
    /// same instant, drops the FTS shadow row (so search can never surface a
    /// tombstone and the notes/fts row counts stay aligned — see
    /// <c>SearchService.ReconcileAsync</c>), and deliberately KEEPS the
    /// <c>note_images</c> rows: their originals stay on disk as long as the
    /// tombstone exists, because a peer may still be holding the note live and
    /// pruning the bytes would break an undo-able delete.
    /// Always returns an empty orphan set — nothing is prunable on a soft delete.
    /// </summary>
    public async Task<IReadOnlyList<string>> DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        var now = ToDbMs(DateTimeOffset.UtcNow);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                $"UPDATE notes SET DeletedAt = $now, UpdatedAt = $now WHERE Id = $id AND {NotDeleted};\n" +
                "DELETE FROM notes_fts WHERE rowid = $id;";
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return [];
    }

    /// <inheritdoc/>
    public async Task<Note> ImportNoteAsync(Note note, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        long id;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            // Upsert keyed on Uuid with EVERY timestamp taken from the caller
            // verbatim — unlike Create/Update, which exist for local editing and
            // re-stamp identity/time. Sync (L1) is the only writer of this shape:
            // re-applying a .qnote container must be idempotent, so a repeat import
            // of unchanged content rewrites the same values and changes nothing.
            cmd.CommandText =
                "INSERT INTO notes (Uuid, Title, Content, PlainText, Category, CreatedAt, UpdatedAt, DeletedAt) " +
                "VALUES ($uuid, $title, $content, $plainText, $category, $createdAt, $updatedAt, $deletedAt) " +
                "ON CONFLICT(Uuid) DO UPDATE SET " +
                "Title = excluded.Title, Content = excluded.Content, PlainText = excluded.PlainText, " +
                "Category = excluded.Category, CreatedAt = excluded.CreatedAt, UpdatedAt = excluded.UpdatedAt, " +
                "DeletedAt = excluded.DeletedAt " +
                "RETURNING Id;";
            cmd.Parameters.AddWithValue("$uuid", note.Uuid);
            cmd.Parameters.AddWithValue("$title", note.Title);
            cmd.Parameters.AddWithValue("$content", note.Content);
            cmd.Parameters.AddWithValue("$plainText", note.PlainText);
            cmd.Parameters.AddWithValue("$category", note.Category);
            cmd.Parameters.AddWithValue("$createdAt", ToDbMs(note.CreatedAt));
            cmd.Parameters.AddWithValue("$updatedAt", ToDbMs(note.UpdatedAt));
            cmd.Parameters.AddWithValue("$deletedAt",
                note.DeletedAt is { } deleted ? ToDbMs(deleted) : DBNull.Value);

            var result = await cmd.ExecuteScalarAsync(ct)
                ?? throw new InvalidOperationException("Note import upsert returned no value.");
            id = (long)result;
        }

        // A tombstone must not be searchable: drop the FTS row instead of writing one.
        if (note.DeletedAt is null)
            await UpsertFtsAsync(conn, tx, id, note.Title, note.PlainText, ct);
        else
            await DeleteFtsRowAsync(conn, tx, id, ct);

        await tx.CommitAsync(ct);
        return note with { Id = id };
    }

    public async Task<IReadOnlyList<NoteIndexEntry>> GetAllForIndexAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        // Tombstones are excluded so a full rebuild reproduces exactly the row set
        // the incremental write path indexes — otherwise the very next
        // ReconcileAsync count check would see a mismatch and rebuild forever.
        cmd.CommandText = $"SELECT Id, Title, PlainText FROM notes WHERE {NotDeleted} ORDER BY Id;";

        var list = new List<NoteIndexEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new NoteIndexEntry
            {
                Id = reader.GetInt64(0),
                Title = reader.GetString(1),
                PlainText = reader.GetString(2),
            });
        }

        return list;
    }

    public async Task<IReadOnlyDictionary<string, (long Id, DateTimeOffset UpdatedAt)>> GetUuidTimestampsAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT Uuid, Id, UpdatedAt FROM notes WHERE {NotDeleted};";

        var map = new Dictionary<string, (long, DateTimeOffset)>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            map[reader.GetString(0)] = (reader.GetInt64(1), FromDbMs(reader.GetInt64(2)));
        return map;
    }

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT Id, Name, IconKey, Color, SortOrder, Uuid, CreatedAt, UpdatedAt, DeletedAt " +
            $"FROM categories WHERE {NotDeleted} ORDER BY SortOrder;";

        var list = new List<Category>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapCategory(reader));

        return list;
    }

    public async Task<Category> CreateCategoryAsync(Category category, CancellationToken ct = default)
    {
        // Uuid identity (schema v7): kept when the caller supplies one, generated
        // here otherwise. This is the single choke point every creation path goes
        // through (service, backup merge), so no categories row ever carries an
        // empty identity.
        if (category.Uuid.Length == 0)
            category = category with { Uuid = Guid.NewGuid().ToString("N") };

        // v8: a category now carries its own LWW clock. The caller's stamps are
        // kept when it supplies them (import paths); a fresh UI creation passes
        // none and gets "now".
        var now = DateTimeOffset.UtcNow;
        var createdAt = category.CreatedAt == default ? now : category.CreatedAt;
        var updatedAt = category.UpdatedAt == default ? createdAt : category.UpdatedAt;

        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO categories (Name, IconKey, Color, Uuid, SortOrder, CreatedAt, UpdatedAt) " +
            "VALUES ($name, $icon, $color, $uuid, (SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM categories WHERE DeletedAt IS NULL), $created, $updated) " +
            "RETURNING Id, SortOrder;";
        cmd.Parameters.AddWithValue("$name", category.Name);
        cmd.Parameters.AddWithValue("$icon", category.IconKey);
        cmd.Parameters.AddWithValue("$color", category.Color);
        cmd.Parameters.AddWithValue("$uuid", category.Uuid);
        cmd.Parameters.AddWithValue("$created", ToDbMs(createdAt));
        cmd.Parameters.AddWithValue("$updated", ToDbMs(updatedAt));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("INSERT INTO categories ... RETURNING returned no row.");
        return category with
        {
            Id = reader.GetInt64(0),
            SortOrder = reader.GetInt32(1),
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
        };
    }

    public async Task UpdateCategoryAsync(Category category, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        string oldName;
        await using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = $"SELECT Name FROM categories WHERE Id = $id AND {NotDeleted};";
            read.Parameters.AddWithValue("$id", category.Id);
            oldName = (string?)(await read.ExecuteScalarAsync(ct))
                ?? throw new InvalidOperationException($"Category {category.Id} does not exist.");
        }

        var now = ToDbMs(DateTimeOffset.UtcNow);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                $"UPDATE categories SET Name = $name, IconKey = $icon, Color = $color, UpdatedAt = $now " +
                $"WHERE Id = $id AND {NotDeleted};";
            cmd.Parameters.AddWithValue("$name", category.Name);
            cmd.Parameters.AddWithValue("$icon", category.IconKey);
            cmd.Parameters.AddWithValue("$color", category.Color);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$id", category.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Name-link fan-out: notes point at the category by NAME, so a rename must
        // move every note in the same transaction (parity-map §1; stronger than the
        // Qt build's app-side fan-out, which could drift on a mid-write crash).
        // Tombstoned notes are included on purpose: they still carry the old name
        // and would otherwise resurrect under a name that no longer exists.
        if (!string.Equals(oldName, category.Name, StringComparison.Ordinal))
        {
            await using var fanOut = conn.CreateCommand();
            fanOut.Transaction = tx;
            fanOut.CommandText = "UPDATE notes SET Category = $new, UpdatedAt = $now WHERE Category = $old;";
            fanOut.Parameters.AddWithValue("$new", category.Name);
            fanOut.Parameters.AddWithValue("$old", oldName);
            fanOut.Parameters.AddWithValue("$now", now);
            await fanOut.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Soft-deletes a category AND every live note under it (Qt parity: destructive,
    /// caller confirms first) — all tombstones share one instant, and their FTS
    /// rows go. <c>note_images</c> rows are kept with their notes (see
    /// <see cref="DeleteAsync"/>), so nothing is prunable: empty orphan set.
    /// </summary>
    public async Task<IReadOnlyList<string>> DeleteCategoryAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        string? name;
        await using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = $"SELECT Name FROM categories WHERE Id = $id AND {NotDeleted};";
            read.Parameters.AddWithValue("$id", id);
            name = (string?)(await read.ExecuteScalarAsync(ct));
        }

        if (name is null)
        {
            await tx.CommitAsync(ct);
            return [];
        }

        var now = ToDbMs(DateTimeOffset.UtcNow);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            // FTS rows go first (they key off notes.Id) — a tombstone must not be
            // searchable; then both tombstone stamps; the category row goes last.
            // Same single instant everywhere so the three writes cannot look like
            // three separate mutations downstream.
            cmd.CommandText =
                "DELETE FROM notes_fts WHERE rowid IN (SELECT Id FROM notes WHERE Category = $name);\n" +
                $"UPDATE notes SET DeletedAt = $now, UpdatedAt = $now WHERE Category = $name AND {NotDeleted};\n" +
                $"UPDATE categories SET DeletedAt = $now, UpdatedAt = $now WHERE Id = $id AND {NotDeleted};";
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return [];
    }

    public async Task ReorderCategoriesAsync(IReadOnlyList<long> orderedIds, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        // SortOrder is synced data (it IS the sidebar order), so a reorder must move
        // UpdatedAt or the peer's LWW would keep the old order forever.
        var now = ToDbMs(DateTimeOffset.UtcNow);
        for (var i = 0; i < orderedIds.Count; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"UPDATE categories SET SortOrder = $ord, UpdatedAt = $now WHERE Id = $id AND {NotDeleted};";
            cmd.Parameters.AddWithValue("$ord", i);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$id", orderedIds[i]);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountNotesByCategoryAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT Category, COUNT(*) FROM notes WHERE {NotDeleted} GROUP BY Category;";

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            map[reader.GetString(0)] = reader.GetInt32(1);

        return map;
    }

    public async Task AddNoteImagesAsync(long noteId, IReadOnlyList<NoteImage> images, CancellationToken ct = default)
    {
        if (images.Count == 0)
            return;

        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        foreach (var image in images)
            await UpsertNoteImageAsync(conn, tx, noteId, image, ct);

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<string>> SyncNoteImagesAsync(
        long noteId, IReadOnlyList<string> referencedSha256, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        var existing = await GetNoteImagesAsync(conn, tx, noteId, ct);
        var referenced = referencedSha256.ToHashSet(StringComparer.Ordinal);
        var dropped = existing.Where(i => !referenced.Contains(i.Sha256)).Select(i => i.Sha256).ToList();

        foreach (var sha in dropped)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM note_images WHERE note_id = $id AND sha256 = $sha;";
            cmd.Parameters.AddWithValue("$id", noteId);
            cmd.Parameters.AddWithValue("$sha", sha);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var orphans = await FindOrphansAsync(conn, tx, dropped, ct);
        await tx.CommitAsync(ct);
        return orphans;
    }

    public async Task<IReadOnlyList<NoteImage>> GetNoteImagesAsync(long noteId, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        return await GetNoteImagesAsync(conn, null, noteId, ct);
    }

    public async Task<IReadOnlyDictionary<string, NoteImage>> GetImageMetadataByShaAsync(
        IReadOnlyList<string> sha256, CancellationToken ct = default)
    {
        var map = new Dictionary<string, NoteImage>(StringComparer.Ordinal);
        var distinct = sha256.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
            return map;

        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        var parameters = string.Join(", ", distinct.Select((_, i) => $"$sha{i}"));
        // Includes display_bytes: the paste-adoption path copies this row (with its
        // blob) onto the adopting note, so the copied image renders without a WIC pass.
        cmd.CommandText =
            "SELECT note_id, sha256, ext, byte_size, width, height, created_at, display_bytes " +
            $"FROM note_images WHERE sha256 IN ({parameters});";
        for (var i = 0; i < distinct.Count; i++)
            cmd.Parameters.AddWithValue($"$sha{i}", distinct[i]);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // Any note's row carries the same content-addressed metadata; first wins.
            map.TryAdd(reader.GetString(1), MapNoteImage(reader, withDisplay: true));
        }
        return map;
    }

    public async Task<IReadOnlyDictionary<string, string>> FindOriginalShaByDisplayHashAsync(
        IReadOnlyList<string> displaySha256, CancellationToken ct = default)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (displaySha256.Count == 0)
            return map;

        // The caller hashes the bytes the editor handed it, so a note holding one
        // unmapped image sends this query over every display blob in the library.
        // The result feeds a clone-link onto the adopting note and, from there,
        // SyncNoteImagesAsync's orphan prune — a value that is not a content
        // address would make that prune delete an unrelated file. Same 64-hex gate
        // as ImageService (the filesystem-boundary rule in database-guidelines).
        var wanted = displaySha256
            .Where(IsSha256Hex)
            .ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0)
            return map;

        // Full-table scan (accepted v1 cost — only called when a note holds unlinked
        // pasted images): the display hash is not a column, so every blob must be
        // hashed in memory to match. Rows of different notes sharing the same original
        // (same display bytes) all resolve to the same address; the first wins, which
        // is exactly the load path's first-wins rule for identical display copies.
        // Tombstoned notes' rows are EXCLUDED: adopting from one would link a live
        // note to bytes that a tombstone purge is allowed to delete.
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT ni.sha256, ni.display_bytes FROM note_images ni " +
            $"JOIN notes n ON n.Id = ni.note_id WHERE n.{NotDeleted} AND ni.display_bytes IS NOT NULL;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var display = (byte[])reader[1];
            var displaySha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(display));
            if (wanted.Contains(displaySha))
                map.TryAdd(displaySha, reader.GetString(0));
        }
        return map;
    }

    private static async Task<IReadOnlyList<NoteImage>> GetNoteImagesAsync(
        SqliteConnection conn, SqliteTransaction? tx, long noteId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT note_id, sha256, ext, byte_size, width, height, created_at " +
            "FROM note_images WHERE note_id = $id ORDER BY created_at DESC;";
        cmd.Parameters.AddWithValue("$id", noteId);

        var list = new List<NoteImage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapNoteImage(reader));
        return list;
    }

    public async Task<IReadOnlyList<NoteImage>> GetNoteImagesWithDisplayAsync(
        long noteId, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT note_id, sha256, ext, byte_size, width, height, created_at, display_bytes " +
            "FROM note_images WHERE note_id = $id ORDER BY created_at DESC;";
        cmd.Parameters.AddWithValue("$id", noteId);

        var list = new List<NoteImage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapNoteImage(reader, withDisplay: true));
        return list;
    }

    private static async Task UpsertNoteImageAsync(
        SqliteConnection conn, SqliteTransaction tx, long noteId, NoteImage image, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // DO UPDATE (not DO NOTHING): a re-import of the same (note, sha) refreshes
        // metadata and backfills a display copy onto a legacy blob-less row; the
        // COALESCE never lets a null overwrite bytes we already have.
        cmd.CommandText =
            "INSERT INTO note_images (note_id, sha256, ext, byte_size, width, height, created_at, display_bytes) " +
            "VALUES ($id, $sha, $ext, $size, $w, $h, $created, $display) " +
            "ON CONFLICT(note_id, sha256) DO UPDATE SET " +
            "ext = excluded.ext, byte_size = excluded.byte_size, " +
            "width = excluded.width, height = excluded.height, " +
            "display_bytes = COALESCE(excluded.display_bytes, note_images.display_bytes);";
        cmd.Parameters.AddWithValue("$id", noteId);
        cmd.Parameters.AddWithValue("$sha", image.Sha256);
        cmd.Parameters.AddWithValue("$ext", image.Ext);
        cmd.Parameters.AddWithValue("$size", image.ByteSize);
        cmd.Parameters.AddWithValue("$w", image.Width);
        cmd.Parameters.AddWithValue("$h", image.Height);
        cmd.Parameters.AddWithValue("$created", ToDbString(image.CreatedAt));
        cmd.Parameters.AddWithValue("$display", (object?)image.DisplayBytes ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Of the given candidate addresses, the ones that no longer appear in
    /// <c>note_images</c> for any note — i.e. whose original files can be deleted.
    /// Evaluated inside the caller's transaction so it sees the post-delete state.
    /// Deliberately counts TOMBSTONED notes too: a soft delete keeps its
    /// <c>note_images</c> rows and its bytes, so an undo (or a peer still holding
    /// the note live) cannot lose the images. Reclaiming bytes is a separate,
    /// explicit tombstone-purge operation, not a side effect of this query.
    /// </summary>
    private static async Task<IReadOnlyList<string>> FindOrphansAsync(
        SqliteConnection conn, SqliteTransaction tx, IEnumerable<string> candidates, CancellationToken ct)
    {
        var orphans = new List<string>();
        foreach (var sha in candidates.Distinct(StringComparer.Ordinal))
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "SELECT COUNT(*) FROM note_images WHERE sha256 = $sha;";
            cmd.Parameters.AddWithValue("$sha", sha);
            if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L) == 0)
                orphans.Add(sha);
        }
        return orphans;
    }

    /// <summary>Maps the 7-column projection (no display_bytes).</summary>
    private static NoteImage MapNoteImage(DbDataReader reader) => new()
    {
        NoteId = reader.GetInt64(0),
        Sha256 = reader.GetString(1),
        Ext = reader.GetString(2),
        ByteSize = reader.GetInt64(3),
        Width = reader.GetInt32(4),
        Height = reader.GetInt32(5),
        CreatedAt = ParseUtc(reader.GetString(6)),
    };

    /// <summary>Maps the 8-column projection (created_at, then display_bytes).</summary>
    private static NoteImage MapNoteImage(DbDataReader reader, bool withDisplay)
    {
        if (!withDisplay)
            return MapNoteImage(reader);
        return new NoteImage
        {
            NoteId = reader.GetInt64(0),
            Sha256 = reader.GetString(1),
            Ext = reader.GetString(2),
            ByteSize = reader.GetInt64(3),
            Width = reader.GetInt32(4),
            Height = reader.GetInt32(5),
            CreatedAt = ParseUtc(reader.GetString(6)),
            DisplayBytes = reader.IsDBNull(7) ? null : (byte[])reader[7],
        };
    }

    /// <summary>
    /// Upsert one row into <c>notes_fts</c> (delete-by-rowid + insert) inside the
    /// given transaction. Both title and body are bigram-tokenized here so the index
    /// never sees raw CJK text (parity: indexing/querying share the same transform).
    /// </summary>
    private static async Task UpsertFtsAsync(
        SqliteConnection conn, SqliteTransaction tx, long id, string title, string plainText, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "DELETE FROM notes_fts WHERE rowid = $id;\n" +
            "INSERT INTO notes_fts (rowid, title, body) VALUES ($id, $title, $body);";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", BigramTokenizer.Tokenize(title));
        cmd.Parameters.AddWithValue("$body", BigramTokenizer.Tokenize(plainText));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeleteFtsRowAsync(
        SqliteConnection conn, SqliteTransaction tx, long id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM notes_fts WHERE rowid = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static Note MapNote(DbDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Uuid = reader.GetString(1),
        Title = reader.GetString(2),
        Category = reader.GetString(3),
        CreatedAt = FromDbMs(reader.GetInt64(4)),
        UpdatedAt = FromDbMs(reader.GetInt64(5)),
        DeletedAt = reader.IsDBNull(6) ? null : FromDbMs(reader.GetInt64(6)),
        PlainText = reader.GetString(7),
        Content = reader.GetString(8),
    };

    private static Category MapCategory(DbDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = reader.GetString(1),
        IconKey = reader.GetString(2),
        Color = reader.GetString(3),
        SortOrder = reader.GetInt32(4),
        Uuid = reader.GetString(5),
        CreatedAt = FromDbMs(reader.GetInt64(6)),
        UpdatedAt = FromDbMs(reader.GetInt64(7)),
        DeletedAt = reader.IsDBNull(8) ? null : FromDbMs(reader.GetInt64(8)),
    };

    // note_images.created_at is the one timestamp v8 left as TEXT: it is a display
    // sort key for one note's images, never an LWW input, and migrating it would
    // touch a table the sync engine does not own.
    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, UtcStyles);

    private static string ToDbString(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static long ToDbMs(DateTimeOffset value) =>
        value.ToUniversalTime().ToUnixTimeMilliseconds();

    private static DateTimeOffset FromDbMs(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms);

    private static bool IsSha256Hex(string value)
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
}
