using Windows.UI.Text;

namespace WinUIRichEditor.Documents;

/// <summary>Helpers for the WinRT <see cref="FontWeight"/> struct, which (being a projected WinRT value
/// type) has no equality operators — comparisons must go through its numeric <c>Weight</c>. The model
/// only ever uses Normal/Bold, so "bold-ish" is any weight at or above SemiBold (600).</summary>
internal static class FontExtensions
{
    /// <summary><see langword="true"/> when the weight is bold (≥ 600 / SemiBold).</summary>
    public static bool IsBold(this FontWeight w) => w.Weight >= 600;
}

/// <summary>Plain <see cref="FontWeight"/> struct values for the model/formatter layer. Built as struct
/// literals (not <c>Microsoft.UI.Text.FontWeights</c>, a WinRT static) so document model construction and
/// HTML/JSON/RTF parsing run without activating the WinUI runtime — keeps that layer headless-testable.</summary>
internal static class FontWeightValues
{
    public static readonly FontWeight Normal = new() { Weight = 400 };
    public static readonly FontWeight Bold = new() { Weight = 700 };
}
