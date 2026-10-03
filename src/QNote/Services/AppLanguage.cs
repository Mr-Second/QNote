using QNote.Text;
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
/// switch, so the "unset" state is never needed. The OS flavor
/// (<c>Windows.Globalization</c>) is set in parallel for PACKAGED runs —
/// the XAML framework's own control strings (ToggleSwitch On/Off, …)
/// resolve through it, and the two stacks do not cross-feed.</item>
/// <item><see cref="WinUIRichEditor.RichEditorLocalization.Language"/> — the
/// vendored editor's string table (the zh-Hans table is registered at startup
/// by <see cref="Controls.EditorLocalization"/>; "en" ships built-in). Set
/// BEFORE re-navigating, so rebuilt chrome reads the new table.</item>
/// </list>
/// Call BEFORE any XAML loads at startup, and again on a runtime switch before
/// the root frame re-navigates (x:Uid resolves only at element load).
/// </summary>
public static class AppLanguage
{
    /// <summary>True when the applied language is Chinese (zh-Hans strings / word forms).</summary>
    public static bool IsChinese { get; private set; }

    /// <summary>Language-appropriate relative-time word forms for the note list.</summary>
    public static TimeStrings TimeStrings => IsChinese ? TimeStrings.Zh : TimeStrings.En;

    public static void Apply(string mode)
    {
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
        // resolve through the OS flavor, which the Microsoft.* override does
        // not drive (WinAppSDK keeps the two language stacks separate). The OS
        // setter throws in unpackaged runs (WinAppSDK #1687) — packaged only;
        // unpackaged chrome degrades to system language, app strings don't.
        if (PackageIdentity.IsPackaged())
        {
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
}
