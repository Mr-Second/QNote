using Microsoft.Extensions.Logging;
using QNote.Infrastructure;
using QNote.Update;

namespace QNote.Services;

/// <summary>
/// <see cref="IStartupUpdateCheck"/> over the channel-appropriate services
/// (see the interface doc for the zero-drift Store contract). Constructed
/// with the package-identity flag (same pattern as SettingsViewModel) so the
/// dispatch is headless-testable.
/// </summary>
public sealed class StartupUpdateCheck : IStartupUpdateCheck
{
    private readonly IGitHubUpdateService _gitHub;
    private readonly IStoreUpdateChecker _store;
    private readonly bool _isPackaged;
    private readonly ILogger<StartupUpdateCheck> _log;

    public StartupUpdateCheck(
        IGitHubUpdateService gitHub,
        IStoreUpdateChecker store,
        bool isPackaged,
        ILogger<StartupUpdateCheck> log)
    {
        _gitHub = gitHub;
        _store = store;
        _isPackaged = isPackaged;
        _log = log;
    }

    public async Task<StartupUpdateInfo?> CheckAsync()
    {
        try
        {
            return _isPackaged
                ? await CheckStoreAsync()
                : await CheckPortableAsync();
        }
        catch (Exception ex)
        {
            // Contract: never throw — a failed check is a silent session.
            _log.LogWarning(ex, "Startup update check failed (best-effort, ignored).");
            return null;
        }
    }

    private async Task<StartupUpdateInfo?> CheckPortableAsync()
    {
        var result = await _gitHub.CheckLatestAsync();
        if (result.Status != UpdateCheckStatus.UpdateAvailable || result.ReleaseUrl is not { } url)
            return null;

        return new StartupUpdateInfo(result.LatestVersion!, result.ReleaseNotes, new Uri(url));
    }

    private async Task<StartupUpdateInfo?> CheckStoreAsync()
    {
        // The Store backend is the ONLY trigger (zero drift): no Store update,
        // no dialog — even when a GitHub tag is already ahead of a pending
        // Store certification.
        var store = await _store.CheckAsync();
        if (store is not { } update)
            return null;

        // Version for display: "1.5.1.0" → "v1.5.1" (GitHub tag look; the trailing
        // Revision is structurally 0 in every QNote release).
        var parts = update.Version.Split('.');
        var display = parts.Length >= 3 ? $"v{parts[0]}.{parts[1]}.{parts[2]}" : "v" + update.Version;

        // Changelog: borrowed from the GitHub release, shown only when its tag
        // matches the Store version — a mismatched (newer) GitHub tag must not
        // pass off another version's notes. GitHub being unreachable just
        // yields a version-only dialog.
        string? notes = null;
        var gitHub = await _gitHub.CheckLatestAsync();
        if (gitHub.LatestVersion is { } tag
            && VersionTag.Compare(tag, update.Version) == VersionTagComparison.Same)
        {
            notes = gitHub.ReleaseNotes;
        }

        return new StartupUpdateInfo(display, notes, StoreLink.ProductPage);
    }
}
