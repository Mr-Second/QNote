using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace QNote.Controls;

/// <summary>One entry of the 526-color palette (ported from the Qt assets/colors.json).</summary>
public sealed class PaletteColor
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("pinyin")]
    public required string Pinyin { get; init; }

    [JsonPropertyName("hex")]
    public required string Hex { get; init; }

    /// <summary>Swatch brush for the flyout grid (parsed once at load).</summary>
    [JsonIgnore]
    public SolidColorBrush Brush { get; internal set; } = null!;

    [JsonIgnore]
    internal Color Color { get; set; }
}

/// <summary>Loads the color palette shipped as an app asset (<c>Assets/colors.json</c>).</summary>
public static class ColorPalette
{
    private static readonly Lazy<IReadOnlyList<PaletteColor>> _colors = new(Load);

    /// <summary>All 526 colors, loaded once.</summary>
    public static IReadOnlyList<PaletteColor> Colors => _colors.Value;

    private static IReadOnlyList<PaletteColor> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "colors.json");
        var colors = JsonSerializer.Deserialize<List<PaletteColor>>(File.ReadAllText(path))
            ?? new List<PaletteColor>();
        foreach (var c in colors)
        {
            var hex = c.Hex.TrimStart('#');
            c.Color = Color.FromArgb(0xFF,
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16));
            c.Brush = new SolidColorBrush(c.Color);
        }
        return colors;
    }
}
