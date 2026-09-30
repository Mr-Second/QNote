using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinUIRichEditor.Controls;

namespace QNote.Controls;

/// <summary>
/// QNote's host-side icon provider for the vendored WinUIRichEditor chrome. Maps
/// <see cref="RichEditorIcon"/> slots to glyphs from the bundled <b>CM_EDITOR</b> icon font
/// (<c>Assets/Fonts/CM_EDITOR.ttf</c>) — the icon set the user selected (the HarmonyOS ChordMail
/// editor's KindEditor theme, <c>KindEditor-plugin/theme.css</c>). Install once at startup, before
/// any editor chrome is built: <c>RichEditorIcons.Provider = icon =&gt; QNoteIcons.Create(icon);</c>.
/// <para>
/// Precedence: a provider return value wins over the built-in vector/Segoe icon. Returning
/// <c>null</c> falls through to the vendored icon for that slot (kept for slots the font has no
/// match for, e.g. the two More buttons stay Segoe dots).
/// </para>
/// <para>
/// The glyph is monochrome and follows the control's <c>Foreground</c> (the FontIcon default), so it
/// recolors with the theme automatically — no per-theme ink needed.
/// </para>
/// </summary>
public static class QNoteIcons
{
    /// <summary>The bundled CM_EDITOR icon font (the file's real internal family name is
    /// <c>iconfont</c> — <c>CM_EDITOR</c> is only the CSS alias). Ships at Assets/Fonts/CM_EDITOR.ttf.</summary>
    private static readonly FontFamily CmEditor = new("ms-appx:///Assets/Fonts/CM_EDITOR.ttf#iconfont");

    /// <summary>Builds the icon element for <paramref name="icon"/>, or <c>null</c> when the slot has no
    /// glyph mapping — the vendored built-in icon is then used.</summary>
    public static UIElement? Create(RichEditorIcon icon, double box = 18)
        => Map.TryGetValue(icon, out var glyph)
            ? new FontIcon { FontFamily = CmEditor, Glyph = char.ConvertFromUtf32(glyph), FontSize = box }
            : null;

    // CM_EDITOR code points (KindEditor-plugin/theme.css in the ChordMail editor). All are 4-hex BMP
    // private-use code points (U+E6xx / U+EAxx).
    private const int Bold = 0xE614;
    private const int Italic = 0xE61D;
    private const int Underline = 0xE635;
    private const int Strikethrough = 0xE63A;
    private const int TextColor = 0xE61F;
    private const int Highlight = 0xE618;
    private const int ClearFormatting = 0xE615;
    private const int FormatPainter = 0xE639;
    private const int Undo = 0xE62B;
    private const int Redo = 0xE629;
    private const int AlignLeft = 0xE617;
    private const int AlignCenter = 0xE612;
    private const int AlignRight = 0xE613;
    private const int AlignJustify = 0xEABD;
    private const int IndentIncrease = 0xE61A;
    private const int IndentDecrease = 0xE622;
    private const int Quote = 0xE63D;
    private const int InsertImage = 0xE627;
    private const int ReplaceImage = 0xE627;
    private const int InsertTable = 0xE634;
    private const int InsertDivider = 0xE610;
    private const int LineSpacing = 0xE62D;

    private static readonly Dictionary<RichEditorIcon, int> Map = new()
    {
        [RichEditorIcon.Bold] = Bold,
        [RichEditorIcon.Italic] = Italic,
        [RichEditorIcon.Underline] = Underline,
        [RichEditorIcon.Strikethrough] = Strikethrough,
        [RichEditorIcon.ClearFormatting] = ClearFormatting,
        [RichEditorIcon.FormatPainter] = FormatPainter,
        [RichEditorIcon.Undo] = Undo,
        [RichEditorIcon.Redo] = Redo,
        [RichEditorIcon.TextColor] = TextColor,
        [RichEditorIcon.Highlight] = Highlight,
        [RichEditorIcon.AlignLeft] = AlignLeft,
        [RichEditorIcon.AlignCenter] = AlignCenter,
        [RichEditorIcon.AlignRight] = AlignRight,
        [RichEditorIcon.AlignJustify] = AlignJustify,
        [RichEditorIcon.IndentIncrease] = IndentIncrease,
        [RichEditorIcon.IndentDecrease] = IndentDecrease,
        [RichEditorIcon.Quote] = Quote,
        [RichEditorIcon.InsertImage] = InsertImage,
        [RichEditorIcon.ReplaceImage] = ReplaceImage,
        [RichEditorIcon.InsertTable] = InsertTable,
        [RichEditorIcon.InsertDivider] = InsertDivider,
        [RichEditorIcon.LineSpacing] = LineSpacing,
        // NOTE: More / MoreVertical are intentionally NOT mapped — the CM_EDITOR font has no three-dot
        // glyph (its theme.css lists \e6f3 but the shipped font lacks that code point). Returning null lets
        // the vendored chrome fall back to its text glyph (⋮), so the two row-level More buttons render.
    };
}
