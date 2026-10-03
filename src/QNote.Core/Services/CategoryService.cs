using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using QNote.Data;
using QNote.Models;

namespace QNote.Services;

/// <summary>
/// Category management (port of the Qt CategoryManager). Validation lives here at
/// the service boundary; SQL lives in <see cref="INoteRepository"/>. Built-in
/// categories (seeded at schema v4) reject rename/delete; the synthetic "全部" is
/// a UI concept and never reaches this service.
/// </summary>
public sealed partial class CategoryService : ICategoryService
{
    private static readonly HashSet<string> BuiltInNames = new(StringComparer.Ordinal) { "工作", "生活", "重要" };

    private readonly INoteRepository _repo;
    private readonly IImageService _images;
    private readonly ILogger<CategoryService> _log;

    public CategoryService(INoteRepository repo, IImageService images, ILogger<CategoryService> log)
    {
        _repo = repo;
        _images = images;
        _log = log;
    }

    public Task<IReadOnlyList<Category>> GetAllAsync(CancellationToken ct = default) =>
        _repo.GetCategoriesAsync(ct);

    public bool IsBuiltIn(string name) => BuiltInNames.Contains(name);

    public async Task<Category> CreateAsync(string name, string color, string iconKey, CancellationToken ct = default)
    {
        name = ValidateName(name);
        ValidateColor(color);
        await EnsureUniqueAsync(name, excludeId: null, ct);

        var created = await _repo.CreateCategoryAsync(
            new Category { Name = name, Color = color, IconKey = iconKey }, ct);
        _log.LogInformation("Created category '{Name}' ({Id})", created.Name, created.Id);
        return created;
    }

    public async Task UpdateAsync(Category category, CancellationToken ct = default)
    {
        var existing = await FindAsync(category.Id, ct)
            ?? throw new CategoryException(CategoryErrorKind.NotFound, $"Category does not exist (Id={category.Id})");

        var renamed = !string.Equals(existing.Name, category.Name, StringComparison.Ordinal);
        if (renamed)
        {
            if (IsBuiltIn(existing.Name))
                throw new CategoryException(CategoryErrorKind.BuiltInRename,
                    $"Built-in category '{existing.Name}' cannot be renamed", name: existing.Name);
            category = category with { Name = ValidateName(category.Name) };
            await EnsureUniqueAsync(category.Name, category.Id, ct);
        }
        ValidateColor(category.Color);

        await _repo.UpdateCategoryAsync(category with { IconKey = category.IconKey ?? existing.IconKey }, ct);
        _log.LogInformation("Updated category {Id} ('{Old}' → '{New}')", category.Id, existing.Name, category.Name);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        var existing = await FindAsync(id, ct);
        if (existing is null)
            return;
        if (IsBuiltIn(existing.Name))
            throw new CategoryException(CategoryErrorKind.BuiltInDelete,
                $"Built-in category '{existing.Name}' cannot be deleted", name: existing.Name);

        var orphans = await _repo.DeleteCategoryAsync(id, ct);
        await _images.DeleteOriginalsAsync(orphans, ct);
        _log.LogInformation("Deleted category '{Name}' ({Id}) and its notes (pruned {Orphans} original image(s))", existing.Name, id, orphans.Count);
    }

    public Task ReorderAsync(IReadOnlyList<long> orderedIds, CancellationToken ct = default) =>
        _repo.ReorderCategoriesAsync(orderedIds, ct);

    public Task<IReadOnlyDictionary<string, int>> CountNotesAsync(CancellationToken ct = default) =>
        _repo.CountNotesByCategoryAsync(ct);

    private async Task<Category?> FindAsync(long id, CancellationToken ct) =>
        (await _repo.GetCategoriesAsync(ct)).FirstOrDefault(c => c.Id == id);

    private async Task EnsureUniqueAsync(string name, long? excludeId, CancellationToken ct)
    {
        var all = await _repo.GetCategoriesAsync(ct);
        if (all.Any(c => c.Id != excludeId && string.Equals(c.Name, name, StringComparison.Ordinal)))
            throw new CategoryException(CategoryErrorKind.DuplicateName, $"Category '{name}' already exists", name: name);
    }

    private static string ValidateName(string name)
    {
        name = name.Trim();
        return name.Length == 0
            ? throw new CategoryException(CategoryErrorKind.BlankName, "Category name cannot be empty")
            : name;
    }

    private static void ValidateColor(string color)
    {
        if (color.Length > 0 && !HexColorRegex().IsMatch(color))
            throw new CategoryException(CategoryErrorKind.InvalidColor, "Color must be in #RRGGBB format");
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColorRegex();
}
