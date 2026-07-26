using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace QNote.Converters;

/// <summary>
/// <c>#RRGGBB</c> string → <see cref="SolidColorBrush"/>. Null/empty/invalid input
/// falls back to <c>TextFillColorPrimaryBrush</c> (resolved at use time so runtime
/// theme switches stay correct). NOTE: do NOT return
/// <see cref="Microsoft.UI.Xaml.DependencyProperty.UnsetValue"/> here — compiled
/// <c>x:Bind</c> casts the converter result straight to <see cref="Brush"/> and
/// crashes on UnsetValue (classic {Binding} swallows it, x:Bind does not).
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string hex && TryParse(hex, out var color))
            return new SolidColorBrush(color);
        return (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorPrimaryBrush"];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

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
