using Microsoft.Extensions.Logging;
using QNote.Models;

namespace QNote.Services;

/// <summary>
/// Full-text search over the FTS5 shadow table with app-side bigram tokenization
/// and title-weighted bm25. Placeholder: the whole engine is the search task
/// (decision ③); the FTS5 table already exists in the schema.
/// </summary>
public sealed class SearchService : ISearchService
{
    private readonly ILogger<SearchService> _log;

    public SearchService(ILogger<SearchService> log) => _log = log;

    public Task<IReadOnlyList<Note>> SearchAsync(string query, string? category = null, CancellationToken ct = default) =>
        throw new NotImplementedException("TODO(search-task): FTS5 + app-side bigram query.");

    public Task RebuildIndexAsync(CancellationToken ct = default) =>
        throw new NotImplementedException("TODO(search-task): FTS5 index rebuild + reconcile.");
}
