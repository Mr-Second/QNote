using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using QNote.Data;
using QNote.Models;
using QNote.Text;

namespace QNote.Services;

/// <summary>
/// Full-text search over the FTS5 shadow table with app-side bigram tokenization
/// (parity-map decision ③). The FTS corpus is written by <see cref="INoteRepository"/>
/// inside the notes write transaction; this service covers only the read path
/// (bm25-ranked <see cref="SearchAsync"/>), a concurrency-guarded full
/// <see cref="RebuildIndexAsync"/>, and the count-only startup <see cref="ReconcileAsync"/>
/// (Qt parity: same-count-but-stale never self-heals).
/// </summary>
public sealed class SearchService : ISearchService
{
    private const string ListPreviewLength = "120";

    private readonly DbConnectionFactory _factory;
    private readonly INoteRepository _notes;
    private readonly ILogger<SearchService> _log;

    /// <summary>Rebuild concurrency guard (parity: Qt <c>m_rebuilding</c>).</summary>
    private int _rebuilding;

    public SearchService(DbConnectionFactory factory, INoteRepository notes, ILogger<SearchService> log)
    {
        _factory = factory;
        _notes = notes;
        _log = log;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<NoteSummary>> SearchAsync(string query, string? category = null, SearchSortOrder sort = SearchSortOrder.Relevance, CancellationToken ct = default)
    {
        var ftsQuery = BuildMatchExpression(query);
        if (ftsQuery.Length == 0)
            return [];

        // ponytail: one MATCH scan, optional SQL-side category filter. No
        // post-filter, no paging (Qt parity). Title ×10 weighting via bm25 weights.
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();

        var orderBy = sort switch
        {
            SearchSortOrder.NewestFirst => "n.UpdatedAt DESC",
            SearchSortOrder.OldestFirst => "n.UpdatedAt ASC",
            _ => "bm25(notes_fts, 10.0, 1.0)",
        };

        var hasCategory = !string.IsNullOrWhiteSpace(category);
        cmd.CommandText =
            "SELECT n.Id, n.Uuid, n.Title, substr(n.PlainText, 1, $len) AS Preview, n.Category, n.CreatedAt, n.UpdatedAt " +
            "FROM notes_fts f JOIN notes n ON n.Id = f.rowid " +
            "WHERE notes_fts MATCH $q" + (hasCategory ? " AND n.Category = $cat" : "") + " " +
            $"ORDER BY {orderBy};";
        cmd.Parameters.AddWithValue("$q", ftsQuery);
        cmd.Parameters.AddWithValue("$len", ListPreviewLength);
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
                Preview = reader.GetString(3),
                Category = reader.GetString(4),
                CreatedAt = ParseUtc(reader.GetString(5)),
                UpdatedAt = ParseUtc(reader.GetString(6)),
            });
        }

        return list;
    }

    /// <inheritdoc/>
    public async Task RebuildIndexAsync(CancellationToken ct = default)
    {
        // Single-flight: overlapping rebuilds would thrash the table for no gain.
        if (Interlocked.CompareExchange(ref _rebuilding, 1, 0) != 0)
        {
            _log.LogDebug("Rebuild already in progress, skipping.");
            return;
        }

        try
        {
            var entries = await _notes.GetAllForIndexAsync(ct);
            await using var conn = _factory.OpenWrite();
            await using var tx = conn.BeginTransaction();

            await using (var clear = conn.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM notes_fts;";
                await clear.ExecuteNonQueryAsync(ct);
            }

            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                await using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO notes_fts (rowid, title, body) VALUES ($id, $title, $body);";
                ins.Parameters.AddWithValue("$id", e.Id);
                ins.Parameters.AddWithValue("$title", BigramTokenizer.Tokenize(e.Title));
                ins.Parameters.AddWithValue("$body", BigramTokenizer.Tokenize(e.PlainText));
                await ins.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            _log.LogInformation("Rebuilt FTS index ({Count} entries).", entries.Count);
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation is normal control flow, not an error to report
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FTS rebuild failed.");
            throw;
        }
        finally
        {
            Volatile.Write(ref _rebuilding, 0);
        }
    }

    /// <inheritdoc/>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        long noteCount;
        long ftsCount;

        await using (var conn = _factory.OpenRead())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT (SELECT COUNT(*) FROM notes), (SELECT COUNT(*) FROM notes_fts);";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            noteCount = reader.GetInt64(0);
            ftsCount = reader.GetInt64(1);
        }

        if (noteCount == ftsCount)
            return;

        _log.LogInformation("FTS reconcile: notes={Notes} fts={Fts} → rebuild.", noteCount, ftsCount);
        await RebuildIndexAsync(ct);
    }

    /// <summary>
    /// Bigram-tokenize the raw query, then quote each token so FTS5 treats them as
    /// literal phrases (no phrase/boolean/wildcard parsing - Qt parity: only
    /// whitespace-separated tokens, implicitly AND-ed). Returns empty for a blank
    /// or token-less query (short-circuits the caller).
    /// </summary>
    private static string BuildMatchExpression(string query)
    {
        var tokenized = BigramTokenizer.Tokenize(query);
        if (tokenized.Length == 0)
            return string.Empty;

        var sb = new StringBuilder(tokenized.Length + 8);
        foreach (var tok in tokenized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // Each token becomes a quoted phrase. An embedded double-quote is doubled
            // per FTS5 string-literal escaping - no injection surface.
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append('"').Append(tok.Replace("\"", "\"\"")).Append('"');
        }

        return sb.ToString();
    }

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
