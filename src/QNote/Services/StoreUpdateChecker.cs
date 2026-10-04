using Microsoft.Extensions.Logging;
using Windows.Services.Store;

namespace QNote.Services;

/// <summary>
/// <see cref="IStoreUpdateChecker"/> over <see cref="StoreContext"/>. Microsoft's
/// documented in-app update probe (learn: "Download and install package updates
/// for your app"); the API is rate-limited internally (1 check / 30 min,
/// 10 / 24 h — over the limit it serves the last known state, never throws),
/// which no QNote caller can approach: the check runs at most once per
/// session.
///
/// WinUI 3 desktop gotchas (WindowsAppSDK discussion #3292): the context must
/// be associated with the app window via IInitializeWithWindow or calls can
/// fail with 0x80070578, and it must be created on the UI thread. The check
/// itself stays off the launch critical path (first-activation + 5 s, see
/// MainWindow), so the UI-thread requirement costs nothing.
/// </summary>
public sealed class StoreUpdateChecker : IStoreUpdateChecker
{
    private readonly ILogger<StoreUpdateChecker> _log;

    public StoreUpdateChecker(ILogger<StoreUpdateChecker> log) => _log = log;

    public async Task<StoreUpdateInfo?> CheckAsync()
    {
        try
        {
            var context = StoreContext.GetDefault();
            WinRT.Interop.InitializeWithWindow.Initialize(context, App.WindowHandle);

            var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();
            if (updates.Count == 0)
                return null;

            // The main package is the only non-optional entry QNote ships; the
            // version comes from the update package identity itself.
            if (updates[0].Package is not { } package)
            {
                _log.LogWarning("Store reported {Count} update(s) but the package was unavailable.", updates.Count);
                return null;
            }

            var version = package.Id.Version;
            var text = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            _log.LogInformation("Store update available: {Version}.", text);
            return new StoreUpdateInfo(text);
        }
        catch (Exception ex)
        {
            // Offline, Store services down, or a dev-registered package without
            // Store identity — all "cannot tell", never worth user attention.
            _log.LogInformation(ex, "Store update check unavailable (best-effort, ignored).");
            return null;
        }
    }
}
