namespace QNote.Text;

/// <summary>
/// Language word forms + date/time patterns for <see cref="TodoDueFormatter"/>.
/// Same pattern as <see cref="TimeStrings"/>: Core owns the pure format logic,
/// the record carries the localized word forms (composition-style formats that
/// resw cannot express cleanly), and Presentation picks the record for the
/// active language (see <c>AppLanguage.IsChinese</c>).
/// </summary>
public sealed record TodoDueStrings(
    string TodayWord,
    string TomorrowWord,
    string YesterdayWord,
    string AllDaySuffix,
    string TimeFormat,
    string DateOnlyFormat,
    string DateTimeFormat,
    string[] WeekdayShort)
{
    /// <summary>Index order matches <see cref="DayOfWeek"/> (Sunday first).</summary>
    public static readonly TodoDueStrings Zh = new(
        TodayWord: "今天",
        TomorrowWord: "明天",
        YesterdayWord: "昨天",
        AllDaySuffix: " · 全天",
        TimeFormat: "HH:mm",
        DateOnlyFormat: "M月d日",
        DateTimeFormat: "M月d日 HH:mm",
        WeekdayShort: ["周日", "周一", "周二", "周三", "周四", "周五", "周六"]);

    public static readonly TodoDueStrings En = new(
        TodayWord: "Today",
        TomorrowWord: "Tomorrow",
        YesterdayWord: "Yesterday",
        AllDaySuffix: " · All day",
        TimeFormat: "h:mm tt",
        DateOnlyFormat: "MMM d",
        DateTimeFormat: "MMM d, h:mm tt",
        WeekdayShort: ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"]);
}
