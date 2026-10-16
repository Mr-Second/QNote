namespace QNote.Controls;

/// <summary>
/// Code points of the <b>QNoteIcons</b> icon font (<c>Assets/Fonts/QNoteIcons.ttf</c>),
/// assigned by scripts/icon-font/build.py over the mapped slot names in alphabetical order
/// starting at U+F500. Deliberately WinUI-free (constants only) so the headless test project
/// can link-compile it and assert it stays in sync with the shipped font and
/// scripts/icon-font/codepoints.json — a missing cmap entry renders as a silent tofu box.
/// <para>
/// EditLink / InsertLink point at the same glyph (link.svg); every other mapped slot has its
/// own glyph. The TodoRepeat* slots are the todo page's repeat-menu calendar set and the
/// TodoTools* slots its list-options menu set (neither are RichEditorIcon members;
/// TodoRepeatWeekend is a spare). Regenerate via build.py, never edit by hand.
/// </para>
/// </summary>
public static class QNoteIconGlyphs
{
    public const int AlignCenter = 0xF500;
    public const int AlignJustify = 0xF501;
    public const int AlignLeft = 0xF502;
    public const int AlignRight = 0xF503;
    public const int BodyText = 0xF504;
    public const int Bold = 0xF505;
    public const int BulletList = 0xF506;
    public const int ClearFormatting = 0xF507;
    public const int Copy = 0xF508;
    public const int Cut = 0xF509;
    public const int Delete = 0xF50A;
    public const int DeleteColumn = 0xF50B;
    public const int DeleteRow = 0xF50C;
    public const int DeleteTable = 0xF50D;
    public const int EditLink = 0xF50E;
    public const int Find = 0xF50F;
    public const int FontSizeDecrease = 0xF510;
    public const int FontSizeIncrease = 0xF511;
    public const int FormatPainter = 0xF512;
    public const int Heading1 = 0xF513;
    public const int Heading2 = 0xF514;
    public const int Heading3 = 0xF515;
    public const int Heading4 = 0xF516;
    public const int Highlight = 0xF517;
    public const int IndentDecrease = 0xF518;
    public const int IndentIncrease = 0xF519;
    public const int InsertColumnLeft = 0xF51A;
    public const int InsertColumnRight = 0xF51B;
    public const int InsertDivider = 0xF51C;
    public const int InsertImage = 0xF51D;
    public const int InsertLink = 0xF51E;
    public const int InsertRowAbove = 0xF51F;
    public const int InsertRowBelow = 0xF520;
    public const int InsertTable = 0xF521;
    public const int Italic = 0xF522;
    public const int LineSpacing = 0xF523;
    public const int MergeCells = 0xF524;
    public const int More = 0xF525;
    public const int NumberedList = 0xF526;
    public const int Paste = 0xF527;
    public const int Quote = 0xF528;
    public const int Redo = 0xF529;
    public const int ReplaceImage = 0xF52A;
    public const int Strikethrough = 0xF52B;
    public const int TextColor = 0xF52C;
    public const int TodoRepeatCustom = 0xF52D;
    public const int TodoRepeatDaily = 0xF52E;
    public const int TodoRepeatMonthly = 0xF52F;
    public const int TodoRepeatWeekdays = 0xF530;
    public const int TodoRepeatWeekend = 0xF531;
    public const int TodoRepeatWeekly = 0xF532;
    public const int TodoRepeatYearly = 0xF533;
    public const int TodoToolsDots = 0xF534;
    public const int TodoToolsEmail = 0xF535;
    public const int TodoToolsEyeOff = 0xF536;
    public const int TodoToolsHideCompleted = 0xF537;
    public const int TodoToolsPin = 0xF538;
    public const int TodoToolsPrint = 0xF539;
    public const int TodoToolsSort = 0xF53A;
    public const int TodoToolsSortByCreated = 0xF53B;
    public const int TodoToolsSortByDueDate = 0xF53C;
    public const int TodoToolsSortByImportance = 0xF53D;
    public const int TodoToolsSortByTitle = 0xF53E;
    public const int TodoToolsTheme = 0xF53F;
    public const int Underline = 0xF540;
    public const int Undo = 0xF541;
    public const int UnmergeCells = 0xF542;

    /// <summary>Slot name → code point for every mapped slot. Mirrors
    /// scripts/icon-font/codepoints.json exactly; the font-consistency test asserts the two
    /// never drift apart.</summary>
    public static readonly IReadOnlyDictionary<string, int> SlotCodepoints = new Dictionary<string, int>
    {
        ["AlignCenter"] = AlignCenter,
        ["AlignJustify"] = AlignJustify,
        ["AlignLeft"] = AlignLeft,
        ["AlignRight"] = AlignRight,
        ["BodyText"] = BodyText,
        ["Bold"] = Bold,
        ["BulletList"] = BulletList,
        ["ClearFormatting"] = ClearFormatting,
        ["Copy"] = Copy,
        ["Cut"] = Cut,
        ["Delete"] = Delete,
        ["DeleteColumn"] = DeleteColumn,
        ["DeleteRow"] = DeleteRow,
        ["DeleteTable"] = DeleteTable,
        ["EditLink"] = EditLink,
        ["Find"] = Find,
        ["FontSizeDecrease"] = FontSizeDecrease,
        ["FontSizeIncrease"] = FontSizeIncrease,
        ["FormatPainter"] = FormatPainter,
        ["Heading1"] = Heading1,
        ["Heading2"] = Heading2,
        ["Heading3"] = Heading3,
        ["Heading4"] = Heading4,
        ["Highlight"] = Highlight,
        ["IndentDecrease"] = IndentDecrease,
        ["IndentIncrease"] = IndentIncrease,
        ["InsertColumnLeft"] = InsertColumnLeft,
        ["InsertColumnRight"] = InsertColumnRight,
        ["InsertDivider"] = InsertDivider,
        ["InsertImage"] = InsertImage,
        ["InsertLink"] = InsertLink,
        ["InsertRowAbove"] = InsertRowAbove,
        ["InsertRowBelow"] = InsertRowBelow,
        ["InsertTable"] = InsertTable,
        ["Italic"] = Italic,
        ["LineSpacing"] = LineSpacing,
        ["MergeCells"] = MergeCells,
        ["More"] = More,
        ["NumberedList"] = NumberedList,
        ["Paste"] = Paste,
        ["Quote"] = Quote,
        ["Redo"] = Redo,
        ["ReplaceImage"] = ReplaceImage,
        ["Strikethrough"] = Strikethrough,
        ["TextColor"] = TextColor,
        ["TodoRepeatCustom"] = TodoRepeatCustom,
        ["TodoRepeatDaily"] = TodoRepeatDaily,
        ["TodoRepeatMonthly"] = TodoRepeatMonthly,
        ["TodoRepeatWeekdays"] = TodoRepeatWeekdays,
        ["TodoRepeatWeekend"] = TodoRepeatWeekend,
        ["TodoRepeatWeekly"] = TodoRepeatWeekly,
        ["TodoRepeatYearly"] = TodoRepeatYearly,
        ["TodoToolsDots"] = TodoToolsDots,
        ["TodoToolsEmail"] = TodoToolsEmail,
        ["TodoToolsEyeOff"] = TodoToolsEyeOff,
        ["TodoToolsHideCompleted"] = TodoToolsHideCompleted,
        ["TodoToolsPin"] = TodoToolsPin,
        ["TodoToolsPrint"] = TodoToolsPrint,
        ["TodoToolsSort"] = TodoToolsSort,
        ["TodoToolsSortByCreated"] = TodoToolsSortByCreated,
        ["TodoToolsSortByDueDate"] = TodoToolsSortByDueDate,
        ["TodoToolsSortByImportance"] = TodoToolsSortByImportance,
        ["TodoToolsSortByTitle"] = TodoToolsSortByTitle,
        ["TodoToolsTheme"] = TodoToolsTheme,
        ["Underline"] = Underline,
        ["Undo"] = Undo,
        ["UnmergeCells"] = UnmergeCells,
    };
}
