namespace QNote.Controls;

/// <summary>One selectable Segoe Fluent Icons glyph (key = hex codepoint).</summary>
public sealed record GlyphOption(string Key, string Glyph);

/// <summary>
/// Curated Segoe Fluent Icons preset for category icons (custom image icons are
/// out of scope — they depend on the ImageManager task).
/// </summary>
public static class IconCatalog
{
    public static IReadOnlyList<GlyphOption> Options { get; } = new[]
    {
        "E8FD", "E821", "E80F", "E734", "E8A5", "E8D3",
        "E8EC", "E71B", "E709", "E7BA", "E8F1", "E895",
    }.Select(key => new GlyphOption(key, ToGlyph(key))).ToArray();

    /// <summary>"E821" → "\uE821"; falls back to the generic list glyph on bad input.</summary>
    public static string ToGlyph(string iconKey) =>
        iconKey.Length > 0 && int.TryParse(iconKey, System.Globalization.NumberStyles.HexNumber, null, out var cp)
            ? char.ConvertFromUtf32(cp)
            : "\uE8FD";
}
