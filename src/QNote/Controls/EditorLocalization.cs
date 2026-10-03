using WinUIRichEditor;

namespace QNote.Controls;

/// <summary>
/// Registers the zh-Hans string table for the vendored WinUIRichEditor chrome
/// (context menus, toolbar tooltips, dialogs, find bar). This lives in QNote (the
/// host) — the vendored copy only ships <c>en</c> and <c>ko</c>, and QNote's UI is
/// zh-Hans source per project convention. Call <see cref="Register"/> once at
/// startup, before any editor chrome is built; the ACTIVE language is chosen by
/// <see cref="Services.AppLanguage"/> per the language setting (en = the editor's
/// built-in table).
/// </summary>
public static class EditorLocalization
{
    /// <summary>Registers the zh-Hans table. Does NOT change the active language.</summary>
    public static void Register()
    {
        RichEditorLocalization.Register("zh-Hans", new Dictionary<string, string>
        {
            // Clipboard / editing
            ["Cut"] = "剪切",
            ["Copy"] = "复制",
            ["Paste"] = "粘贴",
            ["Delete"] = "删除",
            ["SelectAll"] = "全选",
            ["Undo"] = "撤销",
            ["Redo"] = "重做",
            // Character formatting
            ["CharacterFormat"] = "字体样式",
            ["Bold"] = "粗体",
            ["Italic"] = "斜体",
            ["Underline"] = "下划线",
            ["Strikethrough"] = "删除线",
            ["FontSize"] = "字号",
            ["FontSizeIncrease"] = "增大字号",
            ["FontSizeDecrease"] = "减小字号",
            ["TextColor"] = "文字颜色",
            ["Highlight"] = "高亮",
            ["FontFamily"] = "字体",
            ["ClearFormatting"] = "清除格式",
            ["ColorBlack"] = "黑色",
            ["ColorRed"] = "红色",
            ["ColorBlue"] = "蓝色",
            ["ColorGreen"] = "绿色",
            ["ColorGray"] = "灰色",
            ["HighlightYellow"] = "黄色",
            ["HighlightGreen"] = "浅绿",
            ["HighlightPink"] = "粉色",
            ["HighlightSkyBlue"] = "天蓝",
            ["HighlightNone"] = "无",
            ["NoHighlight"] = "无高亮",
            ["AutoColor"] = "自动（默认）",
            ["CellBackground"] = "单元格背景色…",
            ["AltText"] = "替换文字…",
            ["Apply"] = "应用",
            ["FormatPainter"] = "格式刷",
            ["FormatPainterTip"] = "格式刷（选择源格式后点击，再选择目标）",
            // Paragraph formatting
            ["Paragraph"] = "段落",
            ["ParagraphStyle"] = "段落样式",
            ["ParagraphFormat"] = "段落",
            ["Alignment"] = "对齐",
            ["AlignLeft"] = "左对齐",
            ["AlignCenter"] = "居中",
            ["AlignRight"] = "右对齐",
            ["AlignJustify"] = "两端对齐",
            ["List"] = "列表",
            ["BulletList"] = "无序列表",
            ["NumberedList"] = "有序列表",
            ["BulletStyle"] = "项目符号样式",
            ["NumberStyle"] = "编号样式",
            ["Heading"] = "标题",
            ["Heading1"] = "标题 1",
            ["Heading2"] = "标题 2",
            ["Heading3"] = "标题 3",
            ["Heading4"] = "标题 4",
            ["Heading5"] = "标题 5",
            ["Heading6"] = "标题 6",
            ["BodyText"] = "正文",
            ["Quote"] = "引用",
            ["Indent"] = "缩进",
            ["IndentIncrease"] = "增加缩进",
            ["IndentDecrease"] = "减少缩进",
            ["Margin"] = "边距",
            ["MarginTop"] = "上边距",
            ["MarginAuto"] = "自动（一行）",
            ["MarginBottom"] = "下边距",
            ["MarginLeft"] = "左边距",
            ["MarginRight"] = "右边距",
            ["LineSpacing"] = "行距",
            // Links
            ["Hyperlink"] = "超链接",
            ["OpenLink"] = "打开链接",
            ["EditLink"] = "编辑链接…",
            ["RemoveLink"] = "移除链接",
            ["InsertLink"] = "插入链接…",
            ["CopyLink"] = "复制链接",
            // Insert
            ["InsertTable"] = "插入表格",
            ["InsertImage"] = "插入图片…",
            ["InsertDivider"] = "插入分隔线",
            ["DragToSelectSize"] = "拖动选择大小",
            // Images
            ["ImageSize"] = "大小",
            ["OriginalSize"] = "原始大小",
            ["HalfSize"] = "1/2 大小",
            ["ThirdSize"] = "1/3 大小",
            ["QuarterSize"] = "1/4 大小",
            ["ReplaceImage"] = "替换图片…",
            ["SaveImageAs"] = "另存为…",
            ["SelectImage"] = "选择图片",
            ["InlineWithText"] = "嵌入文字",
            ["SaveImage"] = "保存图片",
            // Tables
            ["InsertRowAbove"] = "在上方插入行",
            ["InsertRowBelow"] = "在下方插入行",
            ["DeleteRow"] = "删除行",
            ["InsertColumnLeft"] = "在左侧插入列",
            ["InsertColumnRight"] = "在右侧插入列",
            ["DeleteColumn"] = "删除列",
            ["MergeCells"] = "合并单元格",
            ["UnmergeCells"] = "拆分单元格",
            ["CellVerticalAlign"] = "单元格垂直对齐",
            ["VAlignTop"] = "顶端对齐",
            ["VAlignCenter"] = "垂直居中",
            ["VAlignBottom"] = "底端对齐",
            ["SelectCell"] = "选择单元格",
            ["DeleteTable"] = "删除表格",
            ["TableOps"] = "表格",
            // Dialogs
            ["OK"] = "确定",
            ["Cancel"] = "取消",
            // Find / replace + status
            ["Find"] = "查找",
            ["FindNext"] = "下一个",
            ["FindPrevious"] = "上一个",
            ["Replace"] = "替换",
            ["ReplaceAll"] = "全部替换",
            ["ToggleReplace"] = "切换替换",
            ["MatchCase"] = "区分大小写",
            ["NotFound"] = "未找到",
            ["ReplacedFormat"] = "已替换 {0} 处",
            ["StatusFormat"] = "字符 {0}   词 {1}   行 {2}, 列 {3}",
            // Page / zoom (RichEditorView chrome)
            ["Fit"] = "适应",
            ["FitWidth"] = "适应宽度",
            ["ZoomTip"] = "视图缩放（Ctrl+0 = 适应宽度）",
            ["PaperContinuous"] = "连续",
            ["PaperTip"] = "纸张大小（连续 = 按宽度重排）",
            ["PageOutline"] = "页边",
            ["OrientPortrait"] = "纵向",
            ["OrientLandscape"] = "横向",
            ["OrientationTip"] = "纸张方向",
            ["MarginNarrowest"] = "最窄 5 mm",
            ["MarginNarrow"] = "窄 10 mm",
            ["MarginNormal"] = "普通 15 mm",
            ["MarginWide"] = "宽 20 mm",
            ["MarginWidest"] = "最宽 30 mm",
            ["MarginTip"] = "页边距，单位毫米（页眉和页脚所在的区域）",
            // File actions (RichEditorView)
            ["Export"] = "导出（JSON / .flow / HTML）",
            ["Import"] = "导入",
            ["Print"] = "打印",
            ["PageCountFormat"] = "{0} 页",
            ["ImageLimitWarning"] = "⚠ {0} 张图片 — 超过建议的 {1} 张（可能变慢）",
        });
    }
}
