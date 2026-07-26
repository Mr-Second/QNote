using QNote.Models;

namespace QNote.Services;

/// <summary>
/// Read-side full-text search over the FTS5 shadow (port of the Qt SearchManager /
/// Xapian, parity-map decision ③). The FTS write path is maintained inside the
/// note repository's write transaction, NOT through this service; this interface
/// covers only query, full rebuild, and startup reconcile.
/// </summary>
public interface ISearchService
{
    /// <summary>
    /// Full-text search over title+body, optionally scoped to a category (SQL-side
    /// filter, not an engine post-filter). Blank / whitespace query → empty.
    /// <paramref name="sort"/> picks the ORDER BY: bm25 relevance (title ×10,
    /// default) or UpdatedAt new→old / old→new. Returns list projections; the
    /// full note is lazy-loaded on selection. No phrase / boolean / wildcard
    /// syntax (Qt parity).
    /// </summary>
    Task<IReadOnlyList<NoteSummary>> SearchAsync(string query, string? category = null, SearchSortOrder sort = SearchSortOrder.Relevance, CancellationToken ct = default);

    /// <summary>Full rebuild of the FTS index from the notes table (concurrency-guarded).</summary>
    Task RebuildIndexAsync(CancellationToken ct = default);

    /// <summary>Startup reconcile (Qt parity): notes-vs-FTS counts differ → <see cref="RebuildIndexAsync"/>.</summary>
    Task ReconcileAsync(CancellationToken ct = default);
}
