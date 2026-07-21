namespace QNote.Services;

/// <summary>Typed access to app settings (port of the Qt SettingsManager).</summary>
public interface ISettingsService
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, string value, CancellationToken ct = default);
}
