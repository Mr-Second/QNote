using System.Runtime.InteropServices;
using System.Text;

namespace QNote;

/// <summary>
/// Package-identity detection — the portable-mode switch (no marker files):
/// packaged runs (Store / MSIX / dev-registered) keep the default %APPDATA%
/// data root, the WinRT StartupTask, and a hidden update-check row; unpackaged
/// runs are portable (<c>&lt;exedir&gt;\data</c> + HKCU Run key + manual update
/// check). Uses the canonical Win32 check (<c>GetCurrentPackageFullName</c> —
/// the AppLifecycle projection in WinAppSDK 2.5.1 exposes no
/// <c>AppInfo.IsPackaged</c> member). Pinned to the manifest, this resolves
/// without touching the WinAppSDK runtime, so it is safe in the App constructor.
/// Return-code contract: packaged + null buffer → <c>ERROR_INSUFFICIENT_BUFFER</c>,
/// unpackaged → <c>APPMODEL_ERROR_NO_PACKAGE</c>, and ANY unexpected code also
/// reads as packaged — the zero-regression direction (today's behavior: a
/// misclassified packaged run would silently relocate the data root to
/// <c>&lt;exedir&gt;\data</c>, which is worse than a portable run falling back
/// to %APPDATA%).
/// </summary>
internal static class PackageIdentity
{
    private const uint AppModelErrorNoPackage = 15700;

    /// <summary>True when the process carries MSIX package identity.</summary>
    public static bool IsPackaged()
    {
        var length = 0;
        return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
    }

    // Classic DllImport (not LibraryImport) — no unsafe blocks needed
    // (csharp-conventions).
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);
}
