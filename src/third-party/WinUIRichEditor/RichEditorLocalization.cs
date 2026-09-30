using System;
using System.Collections.Generic;
using System.Globalization;

namespace WinUIRichEditor;

/// <summary>
/// Key-based UI strings for the editor's built-in chrome (context menus, toolbar, dialogs).
/// Korean and English ship in-box; the language is picked from the OS UI culture at startup.
/// Hosts can switch via <see cref="Language"/> or add/override languages with <see cref="Register"/>:
/// <code>
/// RichEditorLocalization.Register("ja", new Dictionary&lt;string, string&gt; { ["Copy"] = "コピー", ... });
/// RichEditorLocalization.Language = "ja";
/// </code>
/// Unknown languages and missing keys fall back to English. Plain dictionaries (no .resx /
/// satellite assemblies) keep this Native-AOT safe.
/// </summary>
public static class RichEditorLocalization
{
    private static readonly Dictionary<string, Dictionary<string, string>> Tables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = new()
        {
            // Clipboard / editing
            ["Cut"] = "Cut",
            ["Copy"] = "Copy",
            ["Paste"] = "Paste",
            ["Delete"] = "Delete",
            ["SelectAll"] = "Select All",
            ["Undo"] = "Undo",
            ["Redo"] = "Redo",
            // Character formatting
            ["CharacterFormat"] = "Font Style",
            ["Bold"] = "Bold",
            ["Italic"] = "Italic",
            ["Underline"] = "Underline",
            ["Strikethrough"] = "Strikethrough",
            ["FontSize"] = "Font Size",
            ["FontSizeIncrease"] = "Larger",
            ["FontSizeDecrease"] = "Smaller",
            ["TextColor"] = "Text Color",
            ["Highlight"] = "Highlight",
            ["FontFamily"] = "Font",
            ["ClearFormatting"] = "Clear Formatting",
            ["ColorBlack"] = "Black",
            ["ColorRed"] = "Red",
            ["ColorBlue"] = "Blue",
            ["ColorGreen"] = "Green",
            ["ColorGray"] = "Gray",
            ["HighlightYellow"] = "Yellow",
            ["HighlightGreen"] = "Light Green",
            ["HighlightPink"] = "Pink",
            ["HighlightSkyBlue"] = "Sky Blue",
            ["HighlightNone"] = "None",
            ["NoHighlight"] = "No Highlight",
            ["AutoColor"] = "Automatic (default)",
            ["CellBackground"] = "Cell Background…",
            ["AltText"] = "Alt Text…",
            ["Apply"] = "Apply",
            ["FormatPainter"] = "Format Painter",
            ["FormatPainterTip"] = "Format Painter (select source, click, then select target)",
            // Paragraph formatting
            ["Paragraph"] = "Paragraph",
            ["ParagraphStyle"] = "Paragraph Style",
            ["ParagraphFormat"] = "Paragraph",
            ["Alignment"] = "Alignment",
            ["AlignLeft"] = "Left",
            ["AlignCenter"] = "Center",
            ["AlignRight"] = "Right",
            ["AlignJustify"] = "Justify",
            ["List"] = "List",
            ["BulletList"] = "Bulleted List",
            ["NumberedList"] = "Numbered List",
            ["BulletStyle"] = "Bullet Style",
            ["NumberStyle"] = "Number Style",
            ["Heading"] = "Heading",
            ["Heading1"] = "Heading 1",
            ["Heading2"] = "Heading 2",
            ["Heading3"] = "Heading 3",
            ["Heading4"] = "Heading 4",
            ["Heading5"] = "Heading 5",
            ["Heading6"] = "Heading 6",
            ["BodyText"] = "Body",
            ["Quote"] = "Quote",
            ["Indent"] = "Indent",
            ["IndentIncrease"] = "Increase Indent",
            ["IndentDecrease"] = "Decrease Indent",
            ["Margin"] = "Margin",
            ["MarginTop"] = "Top Margin",
            ["MarginAuto"] = "Auto (one line)",
            ["MarginBottom"] = "Bottom Margin",
            ["MarginLeft"] = "Left Margin",
            ["MarginRight"] = "Right Margin",
            ["LineSpacing"] = "Line Spacing",
            // Links
            ["Hyperlink"] = "Hyperlink",
            ["OpenLink"] = "Open Link",
            ["EditLink"] = "Edit Link...",
            ["RemoveLink"] = "Remove Link",
            ["InsertLink"] = "Insert Link...",
            ["CopyLink"] = "Copy Link",
            // Insert
            ["InsertTable"] = "Insert Table",
            ["InsertImage"] = "Insert Image...",
            ["InsertDivider"] = "Insert Divider",
            ["DragToSelectSize"] = "Drag to choose size",
            // Images
            ["ImageSize"] = "Size",
            ["OriginalSize"] = "Original Size",
            ["HalfSize"] = "1/2 Size",
            ["ThirdSize"] = "1/3 Size",
            ["QuarterSize"] = "1/4 Size",
            ["ReplaceImage"] = "Replace Image...",
            ["SaveImageAs"] = "Save As...",
            ["SelectImage"] = "Select Image",
            ["InlineWithText"] = "Inline with Text",
            ["SaveImage"] = "Save Image",
            // Tables
            ["InsertRowAbove"] = "Insert Row Above",
            ["InsertRowBelow"] = "Insert Row Below",
            ["DeleteRow"] = "Delete Row",
            ["InsertColumnLeft"] = "Insert Column Left",
            ["InsertColumnRight"] = "Insert Column Right",
            ["DeleteColumn"] = "Delete Column",
            ["MergeCells"] = "Merge Cells",
            ["UnmergeCells"] = "Unmerge Cells",
            ["CellVerticalAlign"] = "Cell Vertical Alignment",
            ["VAlignTop"] = "Top",
            ["VAlignCenter"] = "Middle", // vertical alignment (Excel/CSS); converged with upstream
            ["VAlignBottom"] = "Bottom",
            ["SelectCell"] = "Select Cell",
            ["DeleteTable"] = "Delete Table",
            ["TableOps"] = "Table",
            // Dialogs
            ["OK"] = "OK",
            ["Cancel"] = "Cancel",
            // Find / replace + status
            ["Find"] = "Find",
            ["FindNext"] = "Next",
            ["FindPrevious"] = "Previous",
            ["Replace"] = "Replace",
            ["ReplaceAll"] = "Replace All",
            ["ToggleReplace"] = "Toggle Replace",
            ["MatchCase"] = "Match Case",
            ["NotFound"] = "Not found",
            ["ReplacedFormat"] = "Replaced {0}",
            ["StatusFormat"] = "Chars {0}   Words {1}   Ln {2}, Col {3}",
            // Page / zoom (RichEditorView chrome)
            ["Fit"] = "Fit",
            ["FitWidth"] = "Fit width",
            ["ZoomTip"] = "View zoom (Ctrl+0 = fit width)",
            ["PaperContinuous"] = "Continuous",
            ["PaperTip"] = "Paper size (Continuous = reflows to width)",
            ["PageOutline"] = "Outline",
            ["OrientPortrait"] = "Portrait",
            ["OrientLandscape"] = "Landscape",
            ["OrientationTip"] = "Page orientation",
            ["MarginNarrowest"] = "Narrowest 5 mm",
            ["MarginNarrow"] = "Narrow 10 mm",
            ["MarginNormal"] = "Normal 15 mm",
            ["MarginWide"] = "Wide 20 mm",
            ["MarginWidest"] = "Widest 30 mm",
            ["MarginTip"] = "Page margins, in millimetres (the band the header and footer sit in)",
            // File actions (RichEditorView)
            ["Export"] = "Export (JSON / .flow / HTML)",
            ["Import"] = "Import",
            ["Print"] = "Print",
            ["PageCountFormat"] = "{0} page(s)",
            ["ImageLimitWarning"] = "⚠ {0} images — exceeds the recommended {1} (may slow down)",
        },
        ["ko"] = new()
        {
            // Clipboard / editing
            ["Cut"] = "잘라내기",
            ["Copy"] = "복사",
            ["Paste"] = "붙여넣기",
            ["Delete"] = "삭제",
            ["SelectAll"] = "모두 선택",
            ["Undo"] = "실행 취소",
            ["Redo"] = "다시 실행",
            // Character formatting
            ["CharacterFormat"] = "글자 모양",
            ["Bold"] = "굵게",
            ["Italic"] = "기울임",
            ["Underline"] = "밑줄",
            ["Strikethrough"] = "취소선",
            ["FontSize"] = "글자 크기",
            ["FontSizeIncrease"] = "글자 크게",
            ["FontSizeDecrease"] = "글자 작게",
            ["TextColor"] = "글자 색",
            ["Highlight"] = "형광펜",
            ["FontFamily"] = "글꼴",
            ["ClearFormatting"] = "서식 지우기",
            ["ColorBlack"] = "검정",
            ["ColorRed"] = "빨강",
            ["ColorBlue"] = "파랑",
            ["ColorGreen"] = "초록",
            ["ColorGray"] = "회색",
            ["HighlightYellow"] = "노랑",
            ["HighlightGreen"] = "연두",
            ["HighlightPink"] = "분홍",
            ["HighlightSkyBlue"] = "하늘",
            ["HighlightNone"] = "없음",
            ["NoHighlight"] = "형광펜 없음",
            ["AutoColor"] = "자동(기본색)",
            ["CellBackground"] = "셀 배경색…",
            ["AltText"] = "대체 텍스트…",
            ["Apply"] = "적용",
            ["FormatPainter"] = "서식 복사",
            ["FormatPainterTip"] = "서식 복사 (선택 후 클릭 → 대상 선택)",
            // Paragraph formatting
            ["Paragraph"] = "문단",
            ["ParagraphStyle"] = "문단 스타일",
            ["ParagraphFormat"] = "문단 모양",
            ["Alignment"] = "정렬",
            ["AlignLeft"] = "왼쪽",
            ["AlignCenter"] = "가운데",
            ["AlignRight"] = "오른쪽",
            ["AlignJustify"] = "양쪽 맞춤",
            ["List"] = "목록",
            ["BulletList"] = "글머리표",
            ["NumberedList"] = "번호 매기기",
            ["BulletStyle"] = "글머리표 모양",
            ["NumberStyle"] = "번호 모양",
            ["Heading"] = "제목",
            ["Heading1"] = "제목 1",
            ["Heading2"] = "제목 2",
            ["Heading3"] = "제목 3",
            ["Heading4"] = "제목 4",
            ["Heading5"] = "제목 5",
            ["Heading6"] = "제목 6",
            ["BodyText"] = "본문",
            ["Quote"] = "인용",
            ["Indent"] = "들여쓰기",
            ["IndentIncrease"] = "들여쓰기 +",
            ["IndentDecrease"] = "내어쓰기 -",
            ["Margin"] = "여백",
            ["MarginTop"] = "위 여백",
            ["MarginAuto"] = "자동 (한 줄)",
            ["MarginBottom"] = "아래 여백",
            ["MarginLeft"] = "왼쪽 여백",
            ["MarginRight"] = "오른쪽 여백",
            ["LineSpacing"] = "줄 간격",
            // Links
            ["Hyperlink"] = "하이퍼링크",
            ["OpenLink"] = "링크 열기",
            ["EditLink"] = "링크 편집...",
            ["RemoveLink"] = "링크 제거",
            ["InsertLink"] = "링크 삽입...",
            ["CopyLink"] = "링크 복사",
            // Insert
            ["InsertTable"] = "표 삽입",
            ["InsertImage"] = "이미지 삽입...",
            ["InsertDivider"] = "구분선 삽입",
            ["DragToSelectSize"] = "끌어서 크기 선택",
            // Images
            ["ImageSize"] = "크기",
            ["OriginalSize"] = "원본 크기로",
            ["HalfSize"] = "1/2 크기",
            ["ThirdSize"] = "1/3 크기",
            ["QuarterSize"] = "1/4 크기",
            ["ReplaceImage"] = "이미지 교체...",
            ["SaveImageAs"] = "다른 이름으로 저장...",
            ["SelectImage"] = "이미지 선택",
            ["SaveImage"] = "이미지 저장",
            ["InlineWithText"] = "글자처럼 취급",
            // Tables
            ["InsertRowAbove"] = "위에 행 삽입",
            ["InsertRowBelow"] = "아래에 행 삽입",
            ["DeleteRow"] = "행 삭제",
            ["InsertColumnLeft"] = "왼쪽에 열 삽입",
            ["InsertColumnRight"] = "오른쪽에 열 삽입",
            ["DeleteColumn"] = "열 삭제",
            ["MergeCells"] = "셀 병합",
            ["UnmergeCells"] = "셀 병합 해제",
            ["CellVerticalAlign"] = "셀 세로 정렬",
            ["VAlignTop"] = "위",
            ["VAlignCenter"] = "가운데",
            ["VAlignBottom"] = "아래",
            ["SelectCell"] = "셀 선택",
            ["DeleteTable"] = "표 삭제",
            ["TableOps"] = "표",
            // Dialogs
            ["OK"] = "확인",
            ["Cancel"] = "취소",
            // Find / replace + status
            ["Find"] = "찾기",
            ["FindNext"] = "다음",
            ["FindPrevious"] = "이전",
            ["Replace"] = "바꾸기",
            ["ReplaceAll"] = "모두 바꾸기",
            ["ToggleReplace"] = "바꾸기 전환",
            ["MatchCase"] = "대소문자 구분",
            ["NotFound"] = "찾을 수 없음",
            ["ReplacedFormat"] = "{0}개 바꿈",
            ["StatusFormat"] = "글자 {0}   단어 {1}   줄 {2}, 칸 {3}",
            // Page / zoom (RichEditorView chrome)
            ["Fit"] = "맞춤",
            ["FitWidth"] = "폭 맞춤",
            ["ZoomTip"] = "보기 배율 (Ctrl+0 = 폭 맞춤)",
            ["PaperContinuous"] = "연속",
            ["PaperTip"] = "용지 크기 (연속 = 폭에 맞춰 흐름)",
            ["PageOutline"] = "쪽 윤곽",
            ["OrientPortrait"] = "세로",
            ["OrientLandscape"] = "가로",
            ["OrientationTip"] = "용지 방향",
            ["MarginNarrowest"] = "아주 좁게 5mm",
            ["MarginNarrow"] = "좁게 10mm",
            ["MarginNormal"] = "보통 15mm",
            ["MarginWide"] = "넓게 20mm",
            ["MarginWidest"] = "아주 넓게 30mm",
            ["MarginTip"] = "쪽 여백 (mm, 머리글·바닥글이 앉는 띠)",
            // File actions (RichEditorView)
            ["Export"] = "내보내기 (JSON / .flow / HTML)",
            ["Import"] = "가져오기",
            ["Print"] = "인쇄",
            ["PageCountFormat"] = "{0}페이지",
            ["ImageLimitWarning"] = "⚠ 이미지 {0}개 — 권장 {1}개 초과 (성능 저하 가능)",
        },
    };

    private static string _language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    /// <summary>
    /// Active UI language as an ISO 639-1 two-letter code. Defaults to the OS UI culture.
    /// Unregistered languages fall back to English per key. Setting this raises
    /// <see cref="LanguageChanged"/> so live chrome (e.g. <see cref="Controls.RichEditorToolbar"/>) rebuilds.
    /// </summary>
    public static string Language
    {
        get => _language;
        set
        {
            if (string.Equals(_language, value, StringComparison.OrdinalIgnoreCase)) return;
            _language = value;
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>Raised after <see cref="Language"/> changes or new strings are registered for the active language.</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>
    /// Adds a language or overrides strings of an existing one. Entries are merged per key, so a
    /// partial dictionary is fine — missing keys fall back to English. See the class doc for usage.
    /// </summary>
    public static void Register(string language, IReadOnlyDictionary<string, string> strings)
    {
        if (!Tables.TryGetValue(language, out var table))
            Tables[language] = table = new Dictionary<string, string>();
        foreach (var (key, value) in strings)
            table[key] = value;
        if (string.Equals(language, _language, StringComparison.OrdinalIgnoreCase))
            LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Returns the string for <paramref name="key"/> in the active language,
    /// falling back to English, then to the key itself.</summary>
    public static string GetString(string key)
    {
        if (Tables.TryGetValue(_language, out var table) && table.TryGetValue(key, out var s)) return s;
        if (Tables["en"].TryGetValue(key, out var en)) return en;
        return key;
    }
}
