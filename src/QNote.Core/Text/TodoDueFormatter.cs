using System.Globalization;

namespace QNote.Text;

/// <summary>
/// Pure due-date display formatting for todo task cards (MS To Do style):
/// 今天 18:00 / 今天 · 全天 / 昨天 18:00 / 周五 14:00 / 10月6日 — plus the
/// overdue judgment (timed = past instant, all-day = a date strictly before
/// today). All conversions go through <see cref="TimeZoneInfo.Local"/> so the
/// semantics match <c>TodoService.LocalNextMidnightUtc</c> (the 今日 boundary).
/// Word forms come from <see cref="TodoDueStrings"/> (default: zh).
/// </summary>
public static class TodoDueFormatter
{
    /// <summary>Card text for a due instant; all-day tasks render the whole-day form.</summary>
    public static string Format(DateTimeOffset dueUtc, bool isAllDay, DateTimeOffset nowUtc, TodoDueStrings? strings = null)
    {
        var s = strings ?? TodoDueStrings.Zh;
        var due = TimeZoneInfo.ConvertTime(dueUtc, TimeZoneInfo.Local);
        var now = TimeZoneInfo.ConvertTime(nowUtc, TimeZoneInfo.Local);

        var word = DayWord(due, now, s, out var withinWeek);
        if (isAllDay)
        {
            return withinWeek
                ? word + s.AllDaySuffix
                : due.ToString(s.DateOnlyFormat, CultureInfo.CurrentCulture);
        }
        return withinWeek
            ? word + " " + due.ToString(s.TimeFormat, CultureInfo.CurrentCulture)
            : due.ToString(s.DateTimeFormat, CultureInfo.CurrentCulture);
    }

    /// <summary>Short form for the detail pane's 截止日期 value: the day word only (今天 / 周五 / 10月6日).</summary>
    public static string FormatShort(DateTimeOffset dueUtc, bool isAllDay, DateTimeOffset nowUtc, TodoDueStrings? strings = null)
    {
        var s = strings ?? TodoDueStrings.Zh;
        var due = TimeZoneInfo.ConvertTime(dueUtc, TimeZoneInfo.Local);
        var now = TimeZoneInfo.ConvertTime(nowUtc, TimeZoneInfo.Local);

        var word = DayWord(due, now, s, out var withinWeek);
        return withinWeek ? word : due.ToString(s.DateOnlyFormat, CultureInfo.CurrentCulture);
    }

    /// <summary>Bare localized date (创建于 footer etc.): 10月7日 / Oct 7.</summary>
    public static string FormatDatePart(DateTimeOffset valueUtc, TodoDueStrings? strings = null)
    {
        var s = strings ?? TodoDueStrings.Zh;
        var local = TimeZoneInfo.ConvertTime(valueUtc, TimeZoneInfo.Local);
        return local.ToString(s.DateOnlyFormat, CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Overdue judgment: a timed task is overdue once its instant passed; an
    /// all-day task is overdue only from the NEXT local day (due date &lt; today).
    /// </summary>
    public static bool IsOverdue(DateTimeOffset dueUtc, bool isAllDay, DateTimeOffset nowUtc)
    {
        var due = TimeZoneInfo.ConvertTime(dueUtc, TimeZoneInfo.Local);
        var now = TimeZoneInfo.ConvertTime(nowUtc, TimeZoneInfo.Local);
        return isAllDay ? due.Date < now.Date : due < now;
    }

    /// <summary>
    /// Relative day word: 今天/明天/昨天 for the adjacent days, the weekday name
    /// within a week (mirrors MS To Do's 周五 14:00 form), else null (caller falls
    /// back to a bare date) — signalled via <paramref name="withinWeek"/>.
    /// </summary>
    private static string DayWord(DateTimeOffset localDue, DateTimeOffset localNow, TodoDueStrings s, out bool withinWeek)
    {
        // DateTime.CompareTo returns only -1/0/1 — use the TimeSpan day count.
        var delta = (localDue.Date - localNow.Date).Days;
        switch (delta)
        {
            case 0:
                withinWeek = true;
                return s.TodayWord;
            case 1:
                withinWeek = true;
                return s.TomorrowWord;
            case -1:
                withinWeek = true;
                return s.YesterdayWord;
        }

        // |delta| within 2..6 → weekday name; beyond a week → caller formats the date.
        if (Math.Abs(delta) <= 6)
        {
            withinWeek = true;
            return s.WeekdayShort[(int)localDue.DayOfWeek];
        }

        withinWeek = false;
        return string.Empty;
    }
}
