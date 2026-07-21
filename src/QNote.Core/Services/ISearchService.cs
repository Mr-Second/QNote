using QNote.Models;

namespace QNote.Services;

/// <summary>Full-text search (port of the Qt SearchManager / Xapian → FTS5).</summary>
public interface ISearchService
{
    Task<IReadOnlyList<Note>> SearchAsync(string query, string? category = null, CancellationToken ct = default);

    Task RebuildIndexAsync(CancellationToken ct = default);
}
