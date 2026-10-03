namespace QNote.Services;

/// <summary>
/// Maps <see cref="CategoryException"/> kinds to the localized message shown in
/// the category error dialogs (shared by the page VM's create/update/delete
/// endpoints, mirroring <see cref="QNote.Views.BackupErrorText"/>). Core's
/// exception messages are English diagnostics for the log — user-visible
/// text always resolves here.
/// </summary>
internal static class CategoryErrorText
{
    public static string Describe(CategoryException ex) => ex.Kind switch
    {
        CategoryErrorKind.BlankName => AppStrings.GetString("CategoryErrorNameBlank"),
        CategoryErrorKind.DuplicateName => AppStrings.GetFormat("CategoryErrorNameDuplicate", CategoryDisplayNames.Resolve(ex.Name ?? "")),
        CategoryErrorKind.NotFound => AppStrings.GetString("CategoryErrorNotFound"),
        CategoryErrorKind.BuiltInRename => AppStrings.GetFormat("CategoryErrorBuiltInRename", CategoryDisplayNames.Resolve(ex.Name ?? "")),
        CategoryErrorKind.BuiltInDelete => AppStrings.GetFormat("CategoryErrorBuiltInDelete", CategoryDisplayNames.Resolve(ex.Name ?? "")),
        CategoryErrorKind.InvalidColor => AppStrings.GetString("CategoryErrorInvalidColor"),
        _ => AppStrings.GetString("CreateCategoryFailed"),
    };
}
