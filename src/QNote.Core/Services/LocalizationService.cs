namespace QNote.Services;

/// <summary>
/// Localization façade. Skeleton returns the key unchanged (MVP-1 is zh-only with
/// Chinese source strings in XAML). Wires to <c>.resw</c> + runtime switch in M2.
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    public string GetString(string key) => key;
}
