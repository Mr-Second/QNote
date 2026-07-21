using Microsoft.Extensions.Logging;
using QNote.Data;
using QNote.Models;

namespace QNote.Services;

/// <summary>
/// Category management. Skeleton: read path delegates to the repository; add /
/// rename / delete / reorder land in the categories task.
/// </summary>
public sealed class CategoryService : ICategoryService
{
    private readonly INoteRepository _repo;
    private readonly ILogger<CategoryService> _log;

    public CategoryService(INoteRepository repo, ILogger<CategoryService> log)
    {
        _repo = repo;
        _log = log;
    }

    public Task<IReadOnlyList<Category>> GetAllAsync(CancellationToken ct = default) =>
        _repo.GetCategoriesAsync(ct);
}
