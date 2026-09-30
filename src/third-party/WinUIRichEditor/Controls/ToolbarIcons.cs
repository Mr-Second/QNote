using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinUIRichEditor.Controls;

// Built-in icon factory: maps a RichEditorIcon to a Segoe Fluent Icons glyph rendered as a FontIcon.
// Monochrome, themes via Foreground, crisp at any zoom, no asset files — the low-cost default. The symbol
// font lacks good glyphs for some rich-text actions (format painter, indent, numbered list, table
// structure, divider); those return 0 so the chrome falls back to its text glyph. A host can override
// any slot via RichEditorIcons.Provider, which takes precedence over this factory.
internal static class ToolbarIcons
{
    // Segoe Fluent Icons ships on Windows 10 1809+/11; on older builds it resolves to Segoe MDL2 Assets,
    // which shares these codepoints, so the same glyphs render either way.
    private static readonly FontFamily SymbolFont = new("Segoe Fluent Icons");

    // Private-use-area codepoints (0 = no good symbol-font glyph; caller uses its text fallback). Kept as
    // ints so the source carries no literal PUA characters.
    private static int Code(RichEditorIcon icon) => icon switch
    {
        RichEditorIcon.Bold => 0xE8DD,
        RichEditorIcon.Italic => 0xE8DB,
        RichEditorIcon.Underline => 0xE8DC,
        RichEditorIcon.Strikethrough => 0xEDE0,
        RichEditorIcon.TextColor => 0xE8D3,        // FontColor
        RichEditorIcon.Highlight => 0xE7E6,        // Highlight
        RichEditorIcon.BulletList => 0xE8FD,       // BulletedList
        RichEditorIcon.Undo => 0xE7A7,
        RichEditorIcon.Redo => 0xE7A6,
        RichEditorIcon.Cut => 0xE8C6,
        RichEditorIcon.Copy => 0xE8C8,
        RichEditorIcon.Paste => 0xE77F,
        RichEditorIcon.Delete => 0xE74D,
        RichEditorIcon.SelectAll => 0xE8B3,
        RichEditorIcon.AlignLeft => 0xE8E4,
        RichEditorIcon.AlignCenter => 0xE8E3,
        RichEditorIcon.AlignRight => 0xE8E2,
        RichEditorIcon.InsertImage => 0xE8B9,      // Pictures
        RichEditorIcon.ReplaceImage => 0xE8B9,     // Pictures
        RichEditorIcon.InsertDivider => 0xE738,    // Remove (a clean horizontal bar = the rule glyph)
        RichEditorIcon.OpenLink => 0xE8A7,         // OpenInNewWindow
        RichEditorIcon.EditLink => 0xE70F,         // Edit
        RichEditorIcon.RemoveLink => 0xE74D,       // Delete (unlink)
        RichEditorIcon.CopyLink => 0xE71B,         // Link
        RichEditorIcon.InsertLink => 0xE71B,       // Link
        RichEditorIcon.SaveImageAs => 0xE74E,      // Save
        // Export/Import: a tray with an arrow leaving / entering it — the same pictures the upstream peer
        // draws as paths (its ToolbarIcons), where Save/OpenFile read as "save" and "open a folder".
        RichEditorIcon.Export => 0xE898,           // Upload
        RichEditorIcon.Import => 0xE896,           // Download
        RichEditorIcon.Print => 0xE749,
        RichEditorIcon.CharacterFormat => 0xE8D2,  // Font
        RichEditorIcon.FontSizeIncrease => 0xE8E8, // FontIncrease
        RichEditorIcon.FontSizeDecrease => 0xE8E7, // FontDecrease
        RichEditorIcon.Find => 0xE721,             // Search
        _ => 0, // FormatPainter, NumberedList, IndentIncrease/Decrease, InsertTable, and the
                // table-structure ops have no reliable symbol-font glyph -> text fallback.
    };

    // Letter-shaped glyphs (B/I/U/S, the font "A") are drawn edge-to-edge in their em square, unlike
    // object icons (save, print, picture…) which carry built-in padding — at equal FontSize the letters
    // read visibly LARGER. Down-size them so the whole strip has one apparent icon size.
    private static double SizeFor(RichEditorIcon icon, double baseSize) => icon switch
    {
        RichEditorIcon.Bold or RichEditorIcon.Italic or RichEditorIcon.Underline
            or RichEditorIcon.Strikethrough or RichEditorIcon.CharacterFormat
            or RichEditorIcon.FontSizeIncrease or RichEditorIcon.FontSizeDecrease => baseSize - 3,
        _ => baseSize,
    };

    // ---- vector icons (the toolbar) ----------------------------------------------------------------
    // The toolbar draws its own icons, the same pictures as the upstream peer's ToolbarIcons: stroke paths on
    // a 24×24 canvas, 1.5 stroke with round caps, #3C4043 ink, scaled into a box. One weight and size across
    // the strip, and no text stand-ins where the symbol font had no glyph (indent, numbered list, format
    // painter, table). The context menu keeps the Segoe glyphs below — a MenuFlyoutItem icon must be an
    // IconElement. B/I/U/S return null: as upstream, they are styled letters (the toolbar's text fallback).
    private static readonly Windows.UI.Color InkColor = Windows.UI.Color.FromArgb(255, 0x3C, 0x40, 0x43);

    public static UIElement? CreateVector(RichEditorIcon icon, double box = 18) => icon switch
    {
        RichEditorIcon.FormatPainter => Build(box,
            ("M4 5 H15 V9 H4 Z", false),
            ("M15 7 H18 V11 H10.5 V13", false),
            ("M8.5 13 H12.5 V20 H8.5 Z", false)),
        RichEditorIcon.BulletList => Build(box,
            ("M9 7 H20 M9 12 H20 M9 17 H20", false),
            ("M4.5 7 m-1.4 0 a1.4 1.4 0 1 0 2.8 0 a1.4 1.4 0 1 0 -2.8 0 Z M4.5 12 m-1.4 0 a1.4 1.4 0 1 0 2.8 0 a1.4 1.4 0 1 0 -2.8 0 Z M4.5 17 m-1.4 0 a1.4 1.4 0 1 0 2.8 0 a1.4 1.4 0 1 0 -2.8 0 Z", true)),
        RichEditorIcon.NumberedList => Build(box,
            ("M9 7 H20 M9 12 H20 M9 17 H20", false),
            ("M3 5.6 L4.3 5 V9 M2.6 16 H4.6 M2.6 19 H4.6", false),
            ("M2.7 11 Q3 10.2 3.9 10.2 Q4.9 10.2 4.9 11.1 Q4.9 12 2.7 13.8 H4.9", false)),
        // Up/down double arrow beside stacked text lines — the universal line-spacing glyph.
        RichEditorIcon.LineSpacing => Build(box,
            ("M5 7 V17 M11 6 H21 M11 12 H21 M11 18 H21", false),
            ("M5 3 L8 7 L2 7 Z M5 21 L8 17 L2 17 Z", true)),
        RichEditorIcon.IndentIncrease => Build(box,
            ("M4 6 H20 M4 18 H20 M11 12 H20", false),
            ("M4 9 L8 12 L4 15 Z", true)),
        RichEditorIcon.IndentDecrease => Build(box,
            ("M4 6 H20 M4 18 H20 M11 12 H20", false),
            ("M8 9 L4 12 L8 15 Z", true)),
        // A page with its content box inset — the margin band around it (same paths as upstream's).
        RichEditorIcon.PageMargin => Build(box,
            ("M6 3 H18 V21 H6 Z", false),
            ("M9 7 H15 V17 H9 Z", false)),
        // The quote bar the editor draws, beside the lines it marks (same path as upstream's).
        RichEditorIcon.Quote => Build(box,
            ("M5 5 V19", false),
            ("M9 7 H20 M9 12 H20 M9 17 H16", false)),
        RichEditorIcon.InsertTable => Build(box,
            ("M3 5 H21 V19 H3 Z M3 11 H21 M3 15 H21 M9 5 V19 M15 5 V19", false)),
        RichEditorIcon.InsertImage => Build(box,
            ("M3 5 H21 V19 H3 Z", false),
            ("M3 16 L9 11 L13 15 L16 12 L21 16", false),
            ("M9 10 m-1.6 0 a1.6 1.6 0 1 0 3.2 0 a1.6 1.6 0 1 0 -3.2 0 Z", true)),
        RichEditorIcon.InsertDivider => Build(box,
            ("M4 12 H20", false)),
        RichEditorIcon.Undo => Build(box,
            ("M5 11 H14 A4.5 4.5 0 0 1 14 20 H9", false),
            ("M5 11 L9 7.5 L9 14.5 Z", true)),
        RichEditorIcon.Redo => Build(box,
            ("M19 11 H10 A4.5 4.5 0 0 0 10 20 H15", false),
            ("M19 11 L15 7.5 L15 14.5 Z", true)),
        RichEditorIcon.Highlight => Build(box,
            ("M13 4 L20 11 L12 19 H6 L4 17 Z M6 19 L11 14", false)),
        // Export: a tray with text leaving it (up-and-out arrow). Import: a tray taking text in.
        RichEditorIcon.Export => Build(box,
            ("M5 14 V19 H19 V14", false),
            ("M12 16 V5", false),
            ("M12 3 L8 8 H16 Z", true)),
        RichEditorIcon.Import => Build(box,
            ("M5 14 V19 H19 V14", false),
            ("M12 4 V13", false),
            ("M12 16 L8 11 H16 Z", true)),
        // Print: printer body with a top feed sheet and an output sheet.
        RichEditorIcon.Print => Build(box,
            ("M7 8 V4 H17 V8", false),
            ("M5 8 H19 V16 H17", false),
            ("M7 16 H5 V8", false),
            ("M7 13 H17 V20 H7 Z", false)),
        // Not in the upstream set (its toolbar has no find button, and clears formatting with a ✕): a
        // magnifier, and a T with a cross — drawn in the same hand.
        RichEditorIcon.Find => Build(box,
            ("M10.5 4 a6.5 6.5 0 1 0 0 13 a6.5 6.5 0 1 0 0 -13 Z", false),
            ("M15.5 15.5 L20.5 20.5", false)),
        RichEditorIcon.ClearFormatting => Build(box,
            ("M4 5 H15 M9.5 5 V19", false),
            ("M14.5 14 L20 19.5 M20 14 L14.5 19.5", false)),
        _ => null,
    };

    // A layer is one path: stroke (outline) or fill (solid shapes like arrowheads and dots).
    private static UIElement Build(double box, params (string Data, bool Fill)[] layers)
    {
        var ink = new SolidColorBrush(InkColor);
        var canvas = new Canvas { Width = 24, Height = 24 };
        foreach (var (data, fill) in layers)
        {
            var path = new Microsoft.UI.Xaml.Shapes.Path { Data = PathMarkup.Parse(data) };
            if (fill) path.Fill = ink;
            else
            {
                path.Stroke = ink;
                // Scaled by the Viewbox with the canvas: ~1.1px at the 18px box, crisp rather than heavy.
                path.StrokeThickness = 1.5;
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

    // A FontIcon for the slot, or null when no good glyph exists (caller falls back to text). Default
    // 15px (object icons); letter glyphs get down-sized in SizeFor so they don't read larger.
    public static UIElement? Create(RichEditorIcon icon, double size = 15)
    {
        int code = Code(icon);
        if (code == 0) return null;
        return new FontIcon
        {
            FontFamily = SymbolFont,
            Glyph = ((char)code).ToString(),
            FontSize = SizeFor(icon, size),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }
}
