using QNote.Models;

namespace QNote.Services;

/// <summary>Category management (port of the Qt CategoryManager).</summary>
public interface ICategoryService
{
    Task<IReadOnlyList<Category>> GetAllAsync(CancellationToken ct = default);
}
