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
/// Slots the font has no glyph for fall through to the <see cref="VectorMap"/>: table row/column
/// operation icons from Material Design Icons (path data embedded below — neither CM_EDITOR nor
/// Segoe Fluent Icons ships those, so the table context menu used to render bare text items).
/// See <c>docs/third-party-notices.md</c> for all icon sources and licenses.
/// </para>
/// <para>
/// Precedence: a provider return value wins over the built-in vector/Segoe icon. Returning
/// <c>null</c> falls through to the vendored icon for that slot (kept for slots with no
/// match, e.g. the two More buttons stay Segoe dots).
/// </para>
/// <para>
/// Both forms are monochrome and follow the control's <c>Foreground</c> (FontIcon by default,
/// PathIcon by construction), so they recolor with the theme — and high contrast — automatically.
/// </para>
/// </summary>
public static class QNoteIcons
{
    /// <summary>The bundled CM_EDITOR icon font (the file's real internal family name is
    /// <c>iconfont</c> — <c>CM_EDITOR</c> is only the CSS alias). Ships at Assets/Fonts/CM_EDITOR.ttf.</summary>
    private static readonly FontFamily CmEditor = new("ms-appx:///Assets/Fonts/CM_EDITOR.ttf#iconfont");

    /// <summary>Builds the icon element for <paramref name="icon"/>, or <c>null</c> when the slot has no
    /// mapping — the vendored built-in icon is then used.</summary>
    public static UIElement? Create(RichEditorIcon icon, double box = 18)
        => Map.TryGetValue(icon, out var glyph)
            ? new FontIcon { FontFamily = CmEditor, Glyph = char.ConvertFromUtf32(glyph), FontSize = box }
            : VectorMap.TryGetValue(icon, out var data)
                ? RichEditorIconRenderer.CreatePathIcon(data, box)
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

    // Table-structure slots: CM_EDITOR (a KindEditor-era set) and Segoe Fluent Icons both lack
    // row/column operation glyphs, so the table context menu rendered with bare text items. These
    // come from Material Design Icons (@mdi/svg 7.4.47, Pictogrammers Free License — free to use
    // and distribute; attribution appreciated, see the license text bundled with the MDI package).
    // MDI paths are FILLED shapes authored on a 24×24 grid with comma-separated numbers — both are
    // fine for the vendored PathMarkup parser (SkipSeparators eats commas) and PathIcon (fills the
    // geometry with the item's Foreground). Rendered through RichEditorIconRenderer.CreatePathIcon
    // so they scale to the menu's icon size and inherit theme/high-contrast ink for free.
    private const string TableRowPlusBefore =
        "M22,14A2,2 0 0,0 20,12H4A2,2 0 0,0 2,14V21H4V19H8V21H10V19H14V21H16V19H20V21H22V14M4,14H8V17H4V14M10,14H14V17H10V14M20,14V17H16V14H20M11,10H13V7H16V5H13V2H11V5H8V7H11V10Z";
    private const string TableRowPlusAfter =
        "M22,10A2,2 0 0,1 20,12H4A2,2 0 0,1 2,10V3H4V5H8V3H10V5H14V3H16V5H20V3H22V10M4,10H8V7H4V10M10,10H14V7H10V10M20,10V7H16V10H20M11,14H13V17H16V19H13V22H11V19H8V17H11V14Z";
    private const string TableRowRemove =
        "M9.41,13L12,15.59L14.59,13L16,14.41L13.41,17L16,19.59L14.59,21L12,18.41L9.41,21L8,19.59L10.59,17L8,14.41L9.41,13M22,9A2,2 0 0,1 20,11H4A2,2 0 0,1 2,9V6A2,2 0 0,1 4,4H20A2,2 0 0,1 22,6V9M4,9H8V6H4V9M10,9H14V6H10V9M16,9H20V6H16V9Z";
    private const string TableColumnPlusBefore =
        "M13,2A2,2 0 0,0 11,4V20A2,2 0 0,0 13,22H22V2H13M20,10V14H13V10H20M20,16V20H13V16H20M20,4V8H13V4H20M9,11H6V8H4V11H1V13H4V16H6V13H9V11Z";
    private const string TableColumnPlusAfter =
        "M11,2A2,2 0 0,1 13,4V20A2,2 0 0,1 11,22H2V2H11M4,10V14H11V10H4M4,16V20H11V16H4M4,4V8H11V4H4M15,11H18V8H20V11H23V13H20V16H18V13H15V11Z";
    private const string TableColumnRemove =
        "M4,2H11A2,2 0 0,1 13,4V20A2,2 0 0,1 11,22H4A2,2 0 0,1 2,20V4A2,2 0 0,1 4,2M4,10V14H11V10H4M4,16V20H11V16H4M4,4V8H11V4H4M17.59,12L15,9.41L16.41,8L19,10.59L21.59,8L23,9.41L20.41,12L23,14.59L21.59,16L19,13.41L16.41,16L15,14.59L17.59,12Z";
    private const string TableRemove =
        "M15.46,15.88L16.88,14.46L19,16.59L21.12,14.46L22.54,15.88L20.41,18L22.54,20.12L21.12,21.54L19,19.41L16.88,21.54L15.46,20.12L17.59,18L15.46,15.88M4,3H18A2,2 0 0,1 20,5V12.08C18.45,11.82 16.92,12.18 15.68,13H12V17H13.08C12.97,17.68 12.97,18.35 13.08,19H4A2,2 0 0,1 2,17V5A2,2 0 0,1 4,3M4,7V11H10V7H4M12,7V11H18V7H12M4,13V17H10V13H4Z";

    private static readonly Dictionary<RichEditorIcon, string> VectorMap = new()
    {
        [RichEditorIcon.InsertRowAbove] = TableRowPlusBefore,
        [RichEditorIcon.InsertRowBelow] = TableRowPlusAfter,
        [RichEditorIcon.DeleteRow] = TableRowRemove,
        [RichEditorIcon.InsertColumnLeft] = TableColumnPlusBefore,
        [RichEditorIcon.InsertColumnRight] = TableColumnPlusAfter,
        [RichEditorIcon.DeleteColumn] = TableColumnRemove,
        [RichEditorIcon.DeleteTable] = TableRemove,
    };
}
