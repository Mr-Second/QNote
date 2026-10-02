using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using QNote.Models;

namespace QNote.Converters;

/// <summary>
/// <see cref="NoteListDensity"/> → row metrics for the note-list item template.
/// <c>ConverterParameter</c> picks the metric: <c>"Padding"</c> (template-root
/// <see cref="Thickness"/>) or <c>"Spacing"</c> (StackPanel spacing, double).
/// Standard reproduces the original hardcoded 10,8 / 3 exactly. The <c>partial</c>
/// modifier is load-bearing for NativeAOT publishes (CsWinRT1028).
/// </summary>
public sealed partial class ListDensityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var density = value is NoteListDensity d ? d : NoteListDensity.Standard;
        return (parameter as string) switch
        {
            "Spacing" => density switch
            {
                NoteListDensity.Compact => 1.0,
                NoteListDensity.Comfortable => 5.0,
                _ => 3.0,
            },
            _ => density switch // "Padding"
            {
                NoteListDensity.Compact => new Thickness(10, 4, 10, 4),
                NoteListDensity.Comfortable => new Thickness(10, 12, 10, 12),
                _ => new Thickness(10, 8, 10, 8),
            },
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
