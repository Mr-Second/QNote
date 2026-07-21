namespace QNote.Services;

/// <summary>
/// Localized UI strings (port of the Qt TranslationManager). MVP-1 is zh-only;
/// English <c>.resw</c> + runtime language switch are an M2 task.
/// </summary>
public interface ILocalizationService
{
    string GetString(string key);
}
