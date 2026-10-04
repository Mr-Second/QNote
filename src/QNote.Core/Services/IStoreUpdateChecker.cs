namespace QNote.Services;

/// <summary>
/// A Store-backend update available for download, as reported by
/// <see cref="IStoreUpdateChecker"/>. <see cref="Version"/> is the full
/// four-part package version string (e.g. "1.5.1.0").
/// </summary>
public sealed record StoreUpdateInfo(string Version);

/// <summary>
/// Store-backend update probe (packaged runs only). Backs the startup update
/// check's zero-drift Store path: the dialog may only appear when the Store
/// itself can serve the update, so a GitHub tag ahead of a pending Store
/// certification never produces a dead-end "update" dialog. Failures are
/// values, not exceptions: null means "no update / cannot tell" — callers
/// simply stay silent.
/// </summary>
public interface IStoreUpdateChecker
{
    /// <summary>
    /// Query the Store backend for packages of the current app with updates
    /// available for download. Returns null when there is nothing to show
    /// (up to date, Store service unreachable, or a dev-registered package
    /// with no Store identity — those throw inside and are swallowed).
    /// </summary>
    Task<StoreUpdateInfo?> CheckAsync();
}
