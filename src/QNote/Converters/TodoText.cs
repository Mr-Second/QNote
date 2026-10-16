using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using QNote.Services;
using QNote.Text;
using QNote.ViewModels;

namespace QNote.Converters;

/// <summary>
/// x:Bind function library for the todo page's localized display text and
/// state-driven brushes. The view models stay string-free (headless-testable);
/// these static functions compose resw strings (<see cref="AppStrings"/>) and
/// <see cref="TodoDueFormatter"/> output at bind time. x:Bind re-evaluates a
/// function when any argument's property path raises change, and the page
/// re-navigates on language switches, so per-element lookups stay correct.
/// </summary>
public static class TodoText
{
    private static TodoDueStrings Strings => AppLanguage.IsChinese ? TodoDueStrings.Zh : TodoDueStrings.En;

    private static DateTimeOffset Now => DateTimeOffset.Now;

    /// <summary>Nav row / hero title: resw key for smart views, resolved data name for lists.</summary>
    public static string NavTitleText(string titleKey, string title) =>
        titleKey.Length > 0 ? AppStrings.GetString(titleKey) : TodoListDisplayNames.Resolve(title);

    /// <summary>Group-cap label (纯 resw key).</summary>
    public static string ResKeyText(string key) => AppStrings.GetString(key);

    /// <summary>Task-card due text: 今天 18:00 / 今天 · 全天 / 昨天 18:00 / 周五 14:00 / 10月6日.</summary>
    public static string DueText(DateTimeOffset? due, bool isAllDay) =>
        due is { } d ? TodoDueFormatter.Format(d, isAllDay, Now, Strings) : string.Empty;

    /// <summary>Detail-pane 截止日期 value: the day word only; 无 when unset.</summary>
    public static string DueValueText(DateTimeOffset? due, bool isAllDay) =>
        due is { } d ? TodoDueFormatter.FormatShort(d, isAllDay, Now, Strings)
            : AppStrings.GetString("TodoNoneValue");

    /// <summary>创建于 footer text.</summary>
    public static string CreatedAtText(DateTimeOffset created) =>
        AppStrings.GetFormat("TodoCreatedAtFormat", TodoDueFormatter.FormatDatePart(created, Strings));

    /// <summary>Reminder status value: 将于 今天 18:00 提醒 (empty when the reminder is off).</summary>
    public static string ReminderStatus(bool reminderOn, DateTimeOffset? due, bool isAllDay) =>
        reminderOn && due is { } d
            ? AppStrings.GetFormat("TodoReminderStatusFormat", TodoDueFormatter.Format(d, isAllDay, Now, Strings))
            : string.Empty;

    /// <summary>Hero date line under the 今日 title (10月9日 · 星期五).</summary>
    public static string HeroDate(bool isToday) =>
        isToday ? AppStrings.GetFormat("TodoHeroDateFormat", Now.ToString("MMMM d"), Now.ToString("dddd")) : string.Empty;

    /// <summary>固定到"开始" is a user-list affordance (list-options ⋯ menu,
    /// 2026-10-16): hidden on smart views and the group cap.</summary>
    public static Visibility ListOnlyVisibility(TodoNavKind? kind) =>
        kind == TodoNavKind.List ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>显示智能列表 row (2026-10-16): 全部 only, and only while at
    /// least one smart list IS hidden (no dead menu rows).</summary>
    public static Visibility ShowSmartListRowVisibility(TodoNavKind? kind, int hiddenCount) =>
        kind == TodoNavKind.All && hiddenCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------- steps / recurrence / attachments (stage ⑤) ----------

    /// <summary>重复 row value: 每天/工作日/每周/每月/每年, 每 N 天/周/月/年 for a
    /// custom rule, or 无 when unset. DOT-FREE keys: MRT code-side TryGetValue
    /// cannot resolve dotted keys (x:Uid compiles TodoRepeatDaily.Text into a
    /// nested scope — a leaf lookup for the dotted name misses and falls back
    /// to the raw key, which once rendered "TodoRepeatDaily.Text" on screen).</summary>
    public static string RecurrenceText(string? rule)
    {
        if (TodoRecurrence.TryParseCustom(rule, out var interval, out var unit))
        {
            var unitText = AppStrings.GetString(unit switch
            {
                "day" => "TodoRecurrenceUnitDay",
                "week" => "TodoRecurrenceUnitWeek",
                "month" => "TodoRecurrenceUnitMonth",
                _ => "TodoRecurrenceUnitYear",
            });
            if (!AppLanguage.IsChinese && interval > 1)
                unitText += "s"; // all four units pluralize regularly
            return AppStrings.GetFormat("TodoRecurrenceCustomFormat", interval, unitText);
        }
        return rule switch
        {
            "daily" => AppStrings.GetString("TodoRecurrenceDaily"),
            "weekdays" => AppStrings.GetString("TodoRecurrenceWeekdays"),
            "weekly" => AppStrings.GetString("TodoRecurrenceWeekly"),
            "monthly" => AppStrings.GetString("TodoRecurrenceMonthly"),
            "yearly" => AppStrings.GetString("TodoRecurrenceYearly"),
            _ => AppStrings.GetString("TodoNoneValue"),
        };
    }

    /// <summary>Task-row metadata line (due + repeat + steps in ONE inline run —
    /// real To Do rows never stack them; user 2026-10-15): visible when any exists.</summary>
    public static Visibility MetaLineVisibility(bool hasDue, bool hasRecurrence, bool hasSteps) =>
        hasDue || hasRecurrence || hasSteps ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>已完成 accordion chevron: down collapsed, flipped expanded.</summary>
    public static double ChevronRotation(bool expanded) => expanded ? 180 : 0;

    /// <summary>添加文件 row value: the attachment count, or 无 when empty.</summary>
    public static string AttachmentCountText(int count) =>
        count > 0 ? AppStrings.GetFormat("TodoAttachmentCountFormat", count) : AppStrings.GetString("TodoNoneValue");

    /// <summary>
    /// Attachment-row type indicator: glyph by file-name extension. Official
    /// Fluent codepoints (verified against the learn.microsoft.com icon list):
    /// EB9F Photo2, E90B MusicInfo, E714 Video, F012 ZipFolder, E943 Code,
    /// E7C3 Page (plain text), E8A5 Document (default).
    /// </summary>
    public static string AttachmentGlyph(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg"
                or ".ico" or ".tif" or ".tiff" or ".heic" or ".avif" => "\uEB9F",
            ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" or ".aac"
                or ".wma" or ".opus" => "\uE90B",
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm"
                or ".flv" or ".m4v" => "\uE714",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2"
                or ".xz" or ".iso" => "\uF012",
            ".js" or ".mjs" or ".ts" or ".tsx" or ".jsx" or ".py" or ".cs" or ".java"
                or ".c" or ".cpp" or ".h" or ".hpp" or ".go" or ".rs" or ".rb"
                or ".php" or ".swift" or ".kt" or ".sql" or ".sh" or ".ps1"
                or ".json" or ".xml" or ".html" or ".htm" or ".css" or ".yml"
                or ".yaml" or ".toml" or ".ini" => "\uE943",
            ".txt" or ".md" or ".log" or ".rtf" or ".csv" => "\uE7C3",
            _ => "\uE8A5",
        };
    }

    /// <summary>Task-row step badge: N/M (only called when steps exist).</summary>
    public static string StepsText(int done, int total) => AppStrings.GetFormat("TodoStepsFormat", done, total);

    /// <summary>Step title color: dimmed once checked.</summary>
    public static Brush StepTitleBrush(bool completed) =>
        (Brush)HexToBrushConverter.ResolveThemeBrush(completed ? "TextFillColorTertiaryBrush" : "TextFillColorPrimaryBrush");

    /// <summary>Step title strikethrough once checked.</summary>
    public static Windows.UI.Text.TextDecorations StepTitleDecorations(bool completed) =>
        completed ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None;

    // ---------- state-driven brushes (resolved per call — theme-switch rule) ----------

    /// <summary>Circle-checkbox ring: accent tint while completed, tertiary otherwise.</summary>
    public static Brush CheckStrokeBrush(bool completed) =>
        (Brush)HexToBrushConverter.ResolveThemeBrush(completed ? "AccentFillColorDefaultBrush" : "TextFillColorTertiaryBrush");

    /// <summary>Circle-checkbox disc: accent fill while completed, transparent otherwise.</summary>
    public static Brush CheckFillBrush(bool completed) =>
        completed
            ? (Brush)HexToBrushConverter.ResolveThemeBrush("AccentFillColorDefaultBrush")
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    /// <summary>Circle-checkbox check-mark opacity (0 while incomplete).</summary>
    public static double CheckIconOpacity(bool completed) => completed ? 1 : 0;

    /// <summary>Star glyph: filled while important, outline otherwise (E734 / E735).</summary>
    public static string StarGlyph(bool important) => important ? "\uE735" : "\uE734";

    /// <summary>Star color: amber while important (TodoStarBrush), tertiary otherwise.</summary>
    public static Brush StarBrush(bool important) =>
        (Brush)HexToBrushConverter.ResolveThemeBrush(important ? "TodoStarBrush" : "TextFillColorTertiaryBrush");

    /// <summary>Due text color: overdue red / completed tertiary / normal secondary.</summary>
    public static Brush DueBrush(bool overdue, bool completed) =>
        (Brush)HexToBrushConverter.ResolveThemeBrush(completed switch
        {
            false when overdue => "SystemFillColorCriticalBrush",
            false => "TextFillColorSecondaryBrush",
            _ => "TextFillColorTertiaryBrush",
        });

    /// <summary>Nav row background: soft accent while selected, transparent otherwise.</summary>
    public static Brush NavRowBackground(bool selected) =>
        selected
            ? (Brush)HexToBrushConverter.ResolveThemeBrush("BrandAccentSubtleFillBrush")
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    /// <summary>Nav icon color: accent while selected, secondary otherwise.</summary>
    public static Brush NavIconBrush(bool selected) =>
        (Brush)HexToBrushConverter.ResolveThemeBrush(selected ? "BrandAccentBrush" : "TextFillColorSecondaryBrush");

    /// <summary>Nav row title color: accent while selected, primary otherwise.</summary>
    public static Brush NavTitleBrush(bool selected) =>
        (Brush)HexToBrushConverter.ResolveThemeBrush(selected ? "BrandAccentBrush" : "TextFillColorPrimaryBrush");
}
