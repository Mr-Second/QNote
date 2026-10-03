using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace QNote.Services;

/// <summary>
/// Code-side app-string lookup over the Windows App SDK's MRT Core
/// <see cref="ResourceManager"/> (XAML strings localize through <c>x:Uid</c>
/// instead). Deliberately NOT the OS <c>Windows.ApplicationModel.Resources
/// .ResourceLoader</c>: that one is UWP-only and crashes in unpackaged runs
/// (it probes for a hardcoded <c>resources.pri</c> the WinAppSDK build no
/// longer emits). A fresh resource context is created per lookup so the
/// language always re-resolves — a cached context would pin the language it
/// was created with across a runtime PrimaryLanguageOverride switch.
/// </summary>
public static class AppStrings
{
    private static readonly ResourceManager Manager = new();
    private static ResourceMap? _subtree;

    private static ResourceMap Subtree => _subtree ??= Manager.MainResourceMap.GetSubtree("Resources");

    /// <summary>
    /// Localized string for <paramref name="key"/>. Falls back to the key itself
    /// when the resource infrastructure or the key is missing — a visible
    /// (English-ish) key on screen beats an empty control, and it fails loudly
    /// in smoke tests.
    /// </summary>
    public static string GetString(string key)
    {
        try
        {
            return Subtree.TryGetValue(key, Manager.CreateResourceContext())?.ValueAsString ?? key;
        }
        catch
        {
            return key; // PRI not ready (very early startup) — degrade visibly
        }
    }

    /// <summary>Localized <see cref="string.Format"/> template lookup + format.</summary>
    public static string GetFormat(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, GetString(key), args);
}
