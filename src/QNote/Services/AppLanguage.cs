using Microsoft.UI.Dispatching;
using QNote.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.System.UserProfile;

namespace QNote.Services;

/// <summary>
/// Applies the language setting ("system" / "zh" / "en") to every runtime surface:
/// <list type="bullet">
/// <item><c>Microsoft.Windows.Globalization.ApplicationLanguages
/// .PrimaryLanguageOverride</c> — drives resw resolution for BOTH <c>x:Uid</c>
/// (evaluated when an element loads) and the code-side <see cref="AppStrings"/>
/// lookups. The WinAppSDK (<c>Microsoft.*</c>) flavor is required: the OS
/// <c>Windows.Globalization</c> one throws in unpackaged runs. NEVER assign an
/// empty string — the WinAppSDK flavor rejects <c>""</c> with E_INVALIDARG and
/// the override cannot be unset (microsoft/WindowsAppSDK#5335; the OS flavor's
/// clear-on-empty does not carry over for packaged apps). System mode is
/// instead resolved to a concrete tag, mirroring the resw fallback semantics;
/// the override is re-asserted from the settings DB on every launch and
/// switch, so the "unset" state is never needed.</item>
/// <item>WinUI framework control chrome (ToggleSwitch On/Off, TextBox context
/// menu, …) — resolves through a stack the <c>Microsoft.*</c> override does
/// not feed. Packaged runs set the OS flavor (<c>Windows.Globalization
/// .ApplicationLanguages.PrimaryLanguageOverride</c>) in parallel; unpackaged
/// runs redirect the chrome via a thread-level Win32 MUI language list (see
/// <see cref="Apply"/>). The two stacks never cross-feed, so both branches are
/// needed for chrome to follow the app language everywhere.</item>
/// <item><see cref="WinUIRichEditor.RichEditorLocalization.Language"/> — the
/// vendored editor's string table (the zh-Hans table is registered at startup
/// by <see cref="Controls.EditorLocalization"/>; "en" ships built-in). Set
/// BEFORE re-navigating, so rebuilt chrome reads the new table.</item>
/// </list>
/// Call BEFORE any XAML loads at startup, and again on a runtime switch before
/// the root frame re-navigates (x:Uid resolves only at element load). MUST run
/// on the UI thread — the unpackaged chrome redirect below sets THREAD-local
/// state.
/// </summary>
public static class AppLanguage
{
    /// <summary>True when the applied language is Chinese (zh-Hans strings / word forms).</summary>
    public static bool IsChinese { get; private set; }

    /// <summary>Language-appropriate relative-time word forms for the note list.</summary>
    public static TimeStrings TimeStrings => IsChinese ? TimeStrings.Zh : TimeStrings.En;

    public static void Apply(string mode)
    {
        // The unpackaged branch below sets THREAD-local MUI state — applying it
        // from a background thread would silently leave the UI thread's chrome
        // in the system language. Both current call sites are UI-thread, so
        // this only guards future callers.
        Debug.Assert(DispatcherQueue.GetForCurrentThread() is not null,
            "AppLanguage.Apply must run on the UI thread — the unpackaged MUI redirect is thread-local.");

        IsChinese = mode switch
        {
            "zh" => true,
            "en" => false,
            _ => PrefersChineseSystemLanguage(),
        };

        // Empty string is NOT a valid clear for this API flavor (E_INVALIDARG,
        // WinAppSDK #5335) — always resolve to a concrete tag. Semantics are
        // preserved: "system" mirrors the resw fallback (Chinese-preference
        // system → zh-Hans, everything else → English).
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride =
            IsChinese ? "zh-Hans" : "en-US";

        // The XAML framework's OWN control strings (ToggleSwitch On/Off, etc.)
        // resolve through a different stack than the override above.
        if (PackageIdentity.IsPackaged())
        {
            // Packaged: the OS-flavor override drives framework chrome. Its
            // setter throws without package identity (WinAppSDK #1687), hence
            // the guard; failure is non-fatal (chrome only).
            try
            {
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride =
                    IsChinese ? "zh-Hans" : "en-US";
            }
            catch
            {
                // Non-fatal: framework chrome only.
            }
        }
        else
        {
            // Unpackaged: the OS-flavor setter cannot be used (WinAppSDK
            // #1687), so redirect framework chrome through the OTHER stack it
            // reads — classic Win32 MUI (LoadMUILibrary on Microsoft.ui.xaml
            // .dll's .mui satellites). Its language fallback is
            // thread > process > user, and the framework only ever seeds the
            // PROCESS-level list, so a THREAD-level list set here on the UI
            // thread always wins and is never clobbered (verified empirically
            // across the route-2 spike: the process list stayed empty in every
            // snapshot, the thread list kept our tag first). New chrome
            // re-resolves per lookup, so runtime switches refresh live — same
            // semantics as the x:Uid re-navigate flow. NOTE the DIFFERENT tag
            // mapping than the PLO assignments above: MUI needs the concrete
            // .mui folder tag "zh-CN" (the payload ships en-GB/en-us/zh-CN
            // only), while the PLO tags stay "zh-Hans"/"en-US" (the resw
            // language names).
            try
            {
                // "tag\0" + the string marshaler's own terminator form the
                // double-null-terminated multi-string the API expects.
                var applied = SetThreadPreferredUILanguages(MuiLanguageName,
                    (IsChinese ? "zh-CN" : "en-US") + "\0", out _);
                // The API returns FALSE on failure instead of throwing; the
                // release contract stays silent + non-fatal (Debug.Assert
                // compiles out of Release), but dev builds must not swallow a
                // failed set unnoticed.
                Debug.Assert(applied,
                    $"SetThreadPreferredUILanguages failed: {Marshal.GetLastWin32Error()} — framework chrome stays in the system language.");
            }
            catch
            {
                // Non-fatal: framework chrome only (mirrors the packaged setter).
            }
        }

        WinUIRichEditor.RichEditorLocalization.Language = IsChinese ? "zh-Hans" : "en";
    }

    /// <summary>
    /// System mode: mirror the resw fallback semantics — a Chinese-preference
    /// system resolves to zh-Hans, everything else to the neutral English file.
    /// </summary>
    private static bool PrefersChineseSystemLanguage()
    {
        try
        {
            var languages = GlobalizationPreferences.Languages;
            return languages.Count > 0
                && languages[0].StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// MUI_LANGUAGE_NAME (winnls.h = 0x8 — NOT 1; a wrong flag value makes the
    /// setter fail silently with no languages set): the language-NAME form of
    /// the preferred-UI-language lists, matching the .mui folder names.
    /// </summary>
    private const uint MuiLanguageName = 0x8;

    // Classic DllImport (not LibraryImport) — no unsafe blocks needed
    // (csharp-conventions), mirroring PackageIdentity.cs.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool SetThreadPreferredUILanguages(uint dwFlags, string pwszLanguagesList, out uint pulNumLanguages);
}
