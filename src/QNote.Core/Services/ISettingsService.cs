using QNote.Models;

namespace QNote.Services;

/// <summary>Typed access to app settings (port of the Qt SettingsManager).</summary>
public interface ISettingsService
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, string value, CancellationToken ct = default);

    /// <summary>
    /// The full typed snapshot. Cached after the first read — callers may invoke
    /// freely; the cache is refreshed by <see cref="SaveAsync"/>.
    /// </summary>
    Task<AppSettings> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Persists the whole snapshot in one transaction, refreshes the cache, then
    /// raises <see cref="Changed"/> so views/VMs re-apply display settings live.
    /// </summary>
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);

    /// <summary>
    /// Drops the cached snapshot, re-reads it from disk, and raises <see cref="Changed"/>
    /// with the fresh values. Used by overwrite-restore, which swaps the database file
    /// beneath the cache (a normal <see cref="LoadAsync"/> would keep serving the stale
    /// pre-restore snapshot).
    /// </summary>
    Task<AppSettings> ReloadAsync(CancellationToken ct = default);

    /// <summary>
    /// Raised on the caller's thread after <see cref="SaveAsync"/> commits.
    /// (Not a static event — subscribers hold the singleton via DI, so lifetimes
    /// stay sane; Qt parity: SettingsManager.settingsChanged.)
    /// </summary>
    event Action<AppSettings>? Changed;
}
