using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace QNote.Converters;

/// <summary>
/// <c>#RRGGBB</c> string → <see cref="SolidColorBrush"/>. Null/empty/invalid input
/// falls back to the CURRENT theme's <c>TextFillColorPrimaryBrush</c>. NOTE: do NOT
/// return <see cref="DependencyProperty.UnsetValue"/> — compiled <c>x:Bind</c>
/// casts the converter result straight to <see cref="Brush"/> and crashes on
/// UnsetValue (classic {Binding} swallows it, x:Bind does not).
/// </summary>
public sealed partial class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string hex && TryParse(hex, out var color))
            return new SolidColorBrush(color);
        return ResolveThemeBrush("TextFillColorPrimaryBrush");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    /// <summary>
    /// Resolve a theme brush against the window's ACTUAL theme, not
    /// <c>Application.Current.Resources[key]</c> — an app-level lookup always uses
    /// the application theme and ignores the root element's <c>RequestedTheme</c>
    /// override, so it returns the Light brush (black text) while the window runs
    /// dark. The Light/Dark variants live in the ThemeDictionaries of the merged
    /// XamlControlsResources — walk the merged dictionaries to find them.
    /// </summary>
    internal static Brush ResolveThemeBrush(string key)
    {
        var actual = (App.Window?.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
        // WinUI convention: XamlControlsResources' DARK dictionary is keyed
        // "Default" (Light/HighContrast are literal) — NOT "Dark".
        var themeKey = actual == ElementTheme.Light ? "Light" : "Default";

        if (TryThemeDict(Application.Current.Resources, themeKey, key, out var found))
            return found;
        foreach (var merged in Application.Current.Resources.MergedDictionaries)
            if (TryThemeDict(merged, themeKey, key, out found))
                return found;

        return (Brush)Application.Current.Resources[key];

        static bool TryThemeDict(ResourceDictionary dict, string themeKey, string key, out Brush brush)
        {
            brush = null!;
            if (dict.ThemeDictionaries.TryGetValue(themeKey, out var themed)
                && themed is ResourceDictionary themeDict
                && themeDict.TryGetValue(key, out var value)
                && value is Brush b)
            {
                brush = b;
                return true;
            }
            return false;
        }
    }

    public static bool TryParse(string hex, out Windows.UI.Color color)
    {
        color = default;
        if (hex.Length == 7 && hex[0] == '#'
            && uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            color = Windows.UI.Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }
        return false;
    }
}
