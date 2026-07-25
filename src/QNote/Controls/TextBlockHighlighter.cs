using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using QNote.Text;

namespace QNote.Controls;

/// <summary>
/// Highlights whole-keyword matches in a <see cref="TextBlock"/> by rebuilding its
/// <see cref="TextBlock.Inlines"/> from <see cref="HighlightSplitter"/> runs. This
/// is the parity path for client-side literal highlighting (A-search §9): the whole
/// raw keyword, case-insensitive substring, never tokenized, accepting that CJK
/// bigram/AND matches may show no highlight when the keyword is non-contiguous.
/// </summary>
/// <remarks>
/// The hit run is painted with the accent brush as foreground so it reads cleanly on
/// both the normal and selected item backgrounds across Light/Dark/HighContrast.
/// </remarks>
public static class TextBlockHighlighter
{
    /// <summary>Source text to split into runs. Set instead of <see cref="TextBlock.Text"/>.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text",
        typeof(string),
        typeof(TextBlockHighlighter),
        new PropertyMetadata(string.Empty, OnChanged));

    /// <summary>Whole keyword to highlight (case-insensitive substring).</summary>
    public static readonly DependencyProperty KeywordProperty = DependencyProperty.RegisterAttached(
        "Keyword",
        typeof(string),
        typeof(TextBlockHighlighter),
        new PropertyMetadata(string.Empty, OnChanged));

    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);

    public static string GetKeyword(DependencyObject d) => (string)d.GetValue(KeywordProperty);
    public static void SetKeyword(DependencyObject d, string value) => d.SetValue(KeywordProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb)
            return;

        var text = GetText(tb);
        var keyword = GetKeyword(tb);
        tb.Inlines.Clear();

        // ponytail: resolve BrandAccentBrush at use time, not cached, so a runtime
        // Light/Dark/HighContrast switch picks up the right brush. Cost: one dict
        // lookup per render - negligible for a list of summaries.
        var hitBrush = (Brush)Application.Current.Resources["BrandAccentBrush"];

        foreach (var (segment, isHit) in HighlightSplitter.Split(text, keyword))
        {
            var run = new Run { Text = segment };
            if (isHit)
                run.Foreground = hitBrush;
            tb.Inlines.Add(run);
        }
    }
}
