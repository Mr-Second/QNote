using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinUIRichEditor.Controls;

namespace QNote.Controls;

/// <summary>
/// QNote's host-side icon provider for the vendored WinUIRichEditor chrome. Maps
/// <see cref="RichEditorIcon"/> slots to glyphs from the bundled <b>QNoteIcons</b> icon font
/// (<c>Assets/Fonts/QNoteIcons.ttf</c>), synthesized from the project's own hmos editor icon set
/// by scripts/icon-font/build.py (code points live in <see cref="QNoteIconGlyphs"/>). Install
/// once at startup, before any editor chrome is built:
/// <c>RichEditorIcons.Provider = icon =&gt; QNoteIcons.Create(icon);</c>.
/// <para>
/// Slots with no hmos glyph return null and fall through to the vendored built-in icon:
/// SelectAll, OpenLink, RemoveLink, CopyLink, SaveImageAs, Export, Import, Print,
/// CharacterFormat, PageMargin, MoreVertical.
/// </para>
/// <para>
/// Precedence: a provider return value wins over the built-in vector/Segoe icon. FontIcon
/// follows the control's <c>Foreground</c>, so icons recolor with the theme — and high
/// contrast — automatically.
/// </para>
/// </summary>
public static class QNoteIcons
{
    /// <summary>The bundled QNoteIcons icon font (family name matches the file).
    /// Ships at Assets/Fonts/QNoteIcons.ttf.</summary>
    private static readonly FontFamily Font = new("ms-appx:///Assets/Fonts/QNoteIcons.ttf#QNoteIcons");

    /// <summary>Builds the icon element for <paramref name="icon"/>, or <c>null</c> when the slot has no
    /// mapping — the vendored built-in icon is then used.</summary>
    public static UIElement? Create(RichEditorIcon icon, double box = 18)
        => Map.TryGetValue(icon, out var glyph)
            ? new FontIcon { FontFamily = Font, Glyph = char.ConvertFromUtf32(glyph), FontSize = box }
            : null;

    private static readonly Dictionary<RichEditorIcon, int> Map = new()
    {
        [RichEditorIcon.AlignCenter] = QNoteIconGlyphs.AlignCenter,
        [RichEditorIcon.AlignJustify] = QNoteIconGlyphs.AlignJustify,
        [RichEditorIcon.AlignLeft] = QNoteIconGlyphs.AlignLeft,
        [RichEditorIcon.AlignRight] = QNoteIconGlyphs.AlignRight,
        [RichEditorIcon.BodyText] = QNoteIconGlyphs.BodyText,
        [RichEditorIcon.Bold] = QNoteIconGlyphs.Bold,
        [RichEditorIcon.BulletList] = QNoteIconGlyphs.BulletList,
        [RichEditorIcon.ClearFormatting] = QNoteIconGlyphs.ClearFormatting,
        [RichEditorIcon.Copy] = QNoteIconGlyphs.Copy,
        [RichEditorIcon.Cut] = QNoteIconGlyphs.Cut,
        [RichEditorIcon.Delete] = QNoteIconGlyphs.Delete,
        [RichEditorIcon.DeleteColumn] = QNoteIconGlyphs.DeleteColumn,
        [RichEditorIcon.DeleteRow] = QNoteIconGlyphs.DeleteRow,
        [RichEditorIcon.DeleteTable] = QNoteIconGlyphs.DeleteTable,
        [RichEditorIcon.EditLink] = QNoteIconGlyphs.EditLink,
        [RichEditorIcon.Find] = QNoteIconGlyphs.Find,
        [RichEditorIcon.FontSizeDecrease] = QNoteIconGlyphs.FontSizeDecrease,
        [RichEditorIcon.FontSizeIncrease] = QNoteIconGlyphs.FontSizeIncrease,
        [RichEditorIcon.FormatPainter] = QNoteIconGlyphs.FormatPainter,
        [RichEditorIcon.Heading1] = QNoteIconGlyphs.Heading1,
        [RichEditorIcon.Heading2] = QNoteIconGlyphs.Heading2,
        [RichEditorIcon.Heading3] = QNoteIconGlyphs.Heading3,
        [RichEditorIcon.Heading4] = QNoteIconGlyphs.Heading4,
        [RichEditorIcon.Highlight] = QNoteIconGlyphs.Highlight,
        [RichEditorIcon.IndentDecrease] = QNoteIconGlyphs.IndentDecrease,
        [RichEditorIcon.IndentIncrease] = QNoteIconGlyphs.IndentIncrease,
        [RichEditorIcon.InsertColumnLeft] = QNoteIconGlyphs.InsertColumnLeft,
        [RichEditorIcon.InsertColumnRight] = QNoteIconGlyphs.InsertColumnRight,
        [RichEditorIcon.InsertDivider] = QNoteIconGlyphs.InsertDivider,
        [RichEditorIcon.InsertImage] = QNoteIconGlyphs.InsertImage,
        [RichEditorIcon.InsertLink] = QNoteIconGlyphs.InsertLink,
        [RichEditorIcon.InsertRowAbove] = QNoteIconGlyphs.InsertRowAbove,
        [RichEditorIcon.InsertRowBelow] = QNoteIconGlyphs.InsertRowBelow,
        [RichEditorIcon.InsertTable] = QNoteIconGlyphs.InsertTable,
        [RichEditorIcon.Italic] = QNoteIconGlyphs.Italic,
        [RichEditorIcon.LineSpacing] = QNoteIconGlyphs.LineSpacing,
        [RichEditorIcon.MergeCells] = QNoteIconGlyphs.MergeCells,
        [RichEditorIcon.More] = QNoteIconGlyphs.More,
        [RichEditorIcon.NumberedList] = QNoteIconGlyphs.NumberedList,
        [RichEditorIcon.Paste] = QNoteIconGlyphs.Paste,
        [RichEditorIcon.Quote] = QNoteIconGlyphs.Quote,
        [RichEditorIcon.Redo] = QNoteIconGlyphs.Redo,
        [RichEditorIcon.ReplaceImage] = QNoteIconGlyphs.ReplaceImage,
        [RichEditorIcon.Strikethrough] = QNoteIconGlyphs.Strikethrough,
        [RichEditorIcon.TextColor] = QNoteIconGlyphs.TextColor,
        [RichEditorIcon.Underline] = QNoteIconGlyphs.Underline,
        [RichEditorIcon.Undo] = QNoteIconGlyphs.Undo,
        [RichEditorIcon.UnmergeCells] = QNoteIconGlyphs.UnmergeCells,
    };
}
