using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace QNote.Converters;

/// <summary>
/// 主题 catalog + pane-overlay brushes for the todo list-options menu
/// (user 2026-10-16: real-To-Do parity — solid swatches AND image cells).
/// Ids persist on <c>todo_lists.Theme</c> / the smart-view settings CSV;
/// 0 = the default app gradient (no overlay). The catalog also drives the
/// flyout's swatch grid (id → preview brush / image thumbnail).
/// </summary>
public static class TodoTheme
{
    /// <summary>One selectable style: a solid color or a bundled wallpaper.</summary>
    public sealed record ThemeOption(int Id, string ColorHex, string? ImageUri)
    {
        public bool IsImage => ImageUri is not null;
    }

    /// <summary>
    /// The pickable styles in menu order: 8 solids + 6 wallpapers. Solid ids
    /// are 1..8, image ids 101..106 (stable — they persist in the DB/settings).
    /// </summary>
    public static readonly ThemeOption[] Options =
    [
        new(1, "#2564CF", null),
        new(2, "#038387", null),
        new(3, "#744DA9", null),
        new(4, "#C239B3", null),
        new(5, "#D13438", null),
        new(6, "#CA5010", null),
        new(7, "#0B6A0B", null),
        new(8, "#4A5459", null),
        new(101, "#1A2048", "ms-appx:///Assets/Themes/theme-1.jpg"),
        new(102, "#0C2E34", "ms-appx:///Assets/Themes/theme-2.jpg"),
        new(103, "#301216", "ms-appx:///Assets/Themes/theme-3.jpg"),
        new(104, "#0A1838", "ms-appx:///Assets/Themes/theme-4.jpg"),
        new(105, "#240E38", "ms-appx:///Assets/Themes/theme-5.jpg"),
        new(106, "#0C0E14", "ms-appx:///Assets/Themes/theme-6.jpg"),
    ];

    /// <summary>Swatch-grid cell preview brush: the default cell (id 0) shows
    /// the REAL app gradient (resolved against the window theme); others their
    /// solid color / wallpaper.</summary>
    public static Brush CellPreviewBrush(int id)
    {
        if (id == 0)
            return HexToBrushConverter.ResolveThemeBrush("TodoPaneBackgroundBrush");
        return CellBrush(id) ?? HexToBrushConverter.ResolveThemeBrush("TodoPaneBackgroundBrush");
    }

    /// <summary>Cell preview / overlay brush for an id; null for the default
    /// gradient (id 0) — the caller keeps its ThemeResource background.</summary>
    public static Brush? CellBrush(int id)
    {
        var option = Options.FirstOrDefault(o => o.Id == id);
        if (option is null)
            return null;
        if (option.ImageUri is { } uri)
        {
            var brush = new ImageBrush
            {
                ImageSource = new BitmapImage(new Uri(uri)),
                Stretch = Stretch.UniformToFill,
            };
            return brush;
        }
        return FrozenSolid(option.ColorHex);
    }

    /// <summary>B-pane overlay brush (x:Bind function): the pane keeps its
    /// ThemeResource gradient; a non-default theme paints a full-pane overlay.</summary>
    public static Brush? PaneOverlayBrush(int themeId) => CellBrush(themeId);

    /// <summary>Overlay visibility (x:Bind function): hidden for the default
    /// id and entirely suppressed in High Contrast (HC keeps its
    /// SystemColorWindowColor pane so HC text contrast is never themed over).</summary>
    public static Visibility PaneOverlayVisibility(int themeId, bool isHighContrast) =>
        themeId > 0 && !isHighContrast ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Swatch-grid cell check-ring visibility (x:Bind function).</summary>
    public static Visibility IsActiveVisibility(int cellId, int activeId) =>
        cellId == activeId ? Visibility.Visible : Visibility.Collapsed;

    private static SolidColorBrush FrozenSolid(string colorHex)
    {
        HexToBrushConverter.TryParse(colorHex, out var color);
        return new SolidColorBrush(color with { A = 255 });
    }
}
