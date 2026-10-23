using QNote.Text;

namespace QNote.Services;

// The notes VM tests exercise persistence/selection, not MRT or UI language.
internal static class AppStrings
{
    public static string GetString(string key) => key;
    public static string GetFormat(string key, params object?[] args) => key;
}

internal static class AppLanguage
{
    public static TimeStrings TimeStrings => TimeStrings.En;
}
