using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace WinUIRichEditor.Controls;

/// <summary>One vector-icon layer: an SVG path <c>d</c> string plus whether it should be filled
/// (solid, e.g. an arrowhead or dot) rather than stroked (outline). Used by
/// <see cref="RichEditorIconRenderer"/>.</summary>
public readonly record struct IconLayer(string Data, bool Fill);

// QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): a small PUBLIC helper that renders vector icon
// layers with the SAME pipeline the built-in ToolbarIcons uses (PathMarkup.Parse → Path → Canvas(24×24)
// → Viewbox(box)), but with a theme-aware ink. This lets a host (QNote's RichEditorIcons.Provider) turn
// its own SVG path `d` strings into toolbar-ready elements without duplicating the parser (which is
// trim/AOT-hostile to reimplement via XamlReader). Kept deliberately host-agnostic: nothing here
// references the host app.
//
// Ink follows the toolbar's effective theme (the toolbar re-builds its content on ActualThemeChanged —
// see RichEditorToolbar's QNOTE dark-mode patch), so a snapshot brush resolved at build time is correct.
// High contrast (P2): contrast themes replace the hand-mixed inks with the live system text color.
public static class RichEditorIconRenderer
{
    // Dark-theme hook, installed by the first RichEditorToolbar (the only thing that knows the control's
    // ActualTheme). Defaults to the app theme for hosts that build icon trees outside a toolbar.
    internal static Func<bool>? IsDarkTheme { get; set; }

    // Contrast-theme hook, installed by the first RichEditorToolbar alongside IsDarkTheme.
    internal static Func<bool>? IsHighContrast { get; set; }

    // Light `#3C4043` / dark `#E8EAED` — matches the vendored ToolbarIcons ink and the toolbar's visual
    // spec. Rebuilt when the effective theme flips (solid brushes are thread-affine; keep per thread).
    [ThreadStatic] private static SolidColorBrush? _ink;
    [ThreadStatic] private static bool? _inkIsDark;
    [ThreadStatic] private static bool? _inkHighContrast;

    private static SolidColorBrush Ink
    {
        get
        {
            bool dark = IsDarkTheme?.Invoke() ?? false;
            bool highContrast = IsHighContrast?.Invoke() ?? false;
            if (_inkIsDark != dark || _inkHighContrast != highContrast)
            {
                _inkIsDark = dark;
                _inkHighContrast = highContrast;
                _ink = null;
            }
            return _ink ??= new SolidColorBrush(highContrast
                ? new Windows.UI.ViewManagement.UISettings().UIElementColor(
                    Windows.UI.ViewManagement.UIElementType.WindowText)
                : dark
                    ? Windows.UI.Color.FromArgb(255, 0xE8, 0xEA, 0xED)
                    : Windows.UI.Color.FromArgb(255, 0x3C, 0x40, 0x43));
        }
    }

    /// <summary>Builds a <see cref="Viewbox"/> (aspect-locked to <paramref name="box"/> on a 24×24 grid)
    /// wrapping the given layers. Each layer is one SVG path <c>d</c> rendered as a stroke (outline) or a
    /// fill (solid); the stroke weight is Lucide's native 2. The ink follows the toolbar's effective theme.
    /// Reuses the vendored path parser (no XAML runtime type lookup, trim/AOT safe). Returns a fresh
    /// element on every call (a control has one parent).</summary>
    public static UIElement Create(double box, IconLayer[] layers) => Create(box, 2.0, layers);

    /// <summary>As <see cref="Create(double, IconLayer[])"/>, with an explicit stroke weight.</summary>
    public static UIElement Create(double box, double strokeThickness, IconLayer[] layers)
    {
        var ink = Ink;
        var canvas = new Canvas { Width = 24, Height = 24 };
        foreach (var layer in layers)
        {
            var path = new XamlPath { Data = PathMarkup.Parse(layer.Data) };
            if (layer.Fill)
            {
                path.Fill = ink;
            }
            else
            {
                path.Stroke = ink;
                path.StrokeThickness = strokeThickness;
                path.StrokeStartLineCap = path.StrokeEndLineCap = PenLineCap.Round;
                path.StrokeLineJoin = PenLineJoin.Round;
            }
            canvas.Children.Add(path);
        }
        return new Viewbox
        {
            Width = box, Height = box, Child = canvas, Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    /// <summary>Builds a <see cref="PathIcon"/> from SVG path data — the MENU-safe icon form
    /// (<c>MenuFlyoutItem.Icon</c> accepts only an IconElement, while <see cref="Create"/> returns a
    /// Viewbox for toolbar faces). Path data on a 24×24 grid scales to <paramref name="box"/> DIP so a
    /// menu item shows the same apparent size as its FontIcon siblings; the icon inherits the item's
    /// Foreground, so it follows the theme (and high contrast) by construction. Filled-path packs
    /// (MDI-style) render directly; stroke packs need their strokes baked into the data.</summary>
    public static PathIcon CreatePathIcon(string data, double box = 18)
    {
        const double Grid = 24; // the packs QNote bundles are authored on a 24×24 grid
        var geometry = PathMarkup.Parse(data);
        var scale = box / Grid;
        geometry.Transform = new ScaleTransform { ScaleX = scale, ScaleY = scale };
        return new PathIcon { Data = geometry };
    }
}
