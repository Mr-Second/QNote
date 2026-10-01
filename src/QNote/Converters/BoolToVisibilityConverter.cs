using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace QNote.Converters;

/// <summary>
/// <see cref="bool"/> → <see cref="Visibility"/>. Pass <c>ConverterParameter="Invert"</c>
/// to flip the mapping (true → Collapsed).
/// </summary>
public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (parameter is string s && string.Equals(s, "Invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
