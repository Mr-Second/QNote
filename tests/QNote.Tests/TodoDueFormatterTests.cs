using QNote.Text;

namespace QNote.Tests;

/// <summary>
/// <see cref="TodoDueFormatter"/> display forms (今天 18:00 / 昨天 / 周五 14:00 /
/// date fallback) and the overdue judgment. All instants are built through
/// <see cref="TimeZoneInfo.Local"/> (TodoServiceTests pattern) so the suite is
/// timezone-independent; culture-formatted segments are only asserted where
/// they are culture-stable (numeric patterns in the zh strings record).
/// </summary>
public sealed class TodoDueFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 4, 30, 0, TimeSpan.Zero);

    private static DateTimeOffset LocalAt(int dayOffset, int hour, int minute = 0)
    {
        var tz = TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(Now, tz).Date.AddDays(dayOffset).AddHours(hour).AddMinutes(minute);
        return new DateTimeOffset(
            DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz.GetUtcOffset(local)).ToUniversalTime();
    }

    private static string Zh(DateTimeOffset? due, bool allDay = false) =>
        TodoDueFormatter.Format(due!.Value, allDay, Now, TodoDueStrings.Zh);

    private static string ZhShort(DateTimeOffset? due, bool allDay = false) =>
        TodoDueFormatter.FormatShort(due!.Value, allDay, Now, TodoDueStrings.Zh);

    // ---------- timed forms ----------

    [Fact]
    public void Timed_Today_YieldsTodayWordPlusTime() =>
        Assert.Equal("今天 18:00", Zh(LocalAt(0, 18)));

    [Fact]
    public void Timed_Tomorrow_YieldsTomorrowWordPlusTime() =>
        Assert.Equal("明天 09:30", Zh(LocalAt(1, 9, 30)));

    [Fact]
    public void Timed_Yesterday_YieldsYesterdayWordPlusTime() =>
        Assert.Equal("昨天 18:00", Zh(LocalAt(-1, 18)));

    [Fact]
    public void Timed_WithinWeek_YieldsWeekdayPlusTime()
    {
        var due = LocalAt(3, 14); // 2026-10-12 local
        var weekday = TodoDueStrings.Zh.WeekdayShort[(int)due.ToLocalTime().DayOfWeek];
        Assert.Equal($"{weekday} 14:00", Zh(due));
    }

    [Fact]
    public void Timed_BeyondWeek_FallsBackToDateAndTime()
    {
        var due = LocalAt(10, 14); // 2026-10-19 local
        var expected = $"{due.ToLocalTime().Month}月{due.ToLocalTime().Day}日 14:00"; // "M月d日 HH:mm"
        Assert.Equal(expected, Zh(due));
    }

    // ---------- all-day forms ----------

    [Fact]
    public void AllDay_Today_YieldsTodayAllDay() =>
        Assert.Equal("今天 · 全天", Zh(LocalAt(0, 0), allDay: true));

    [Fact]
    public void AllDay_Yesterday_YieldsYesterdayAllDay() =>
        Assert.Equal("昨天 · 全天", Zh(LocalAt(-1, 0), allDay: true));

    [Fact]
    public void AllDay_BeyondWeek_FallsBackToDateOnly()
    {
        var due = LocalAt(-10, 0);
        var expected = $"{due.ToLocalTime().Month}月{due.ToLocalTime().Day}日"; // "M月d日"
        Assert.Equal(expected, Zh(due, allDay: true));
    }

    // ---------- short + date-part forms ----------

    [Fact]
    public void FormatShort_Today_YieldsDayWord() =>
        Assert.Equal("今天", ZhShort(LocalAt(0, 18)));

    [Fact]
    public void FormatShort_TomorrowEn_YieldsDayWord() =>
        Assert.Equal("Tomorrow", TodoDueFormatter.FormatShort(LocalAt(1, 10), false, Now, TodoDueStrings.En));

    [Fact]
    public void AllDay_TodayEn_YieldsTodayAllDay() =>
        Assert.Equal("Today · All day", TodoDueFormatter.Format(LocalAt(0, 0), true, Now, TodoDueStrings.En));

    [Fact]
    public void FormatDatePart_LocalMonthDay()
    {
        var value = LocalAt(-2, 12);
        Assert.Equal($"{value.ToLocalTime().Month}月{value.ToLocalTime().Day}日",
            TodoDueFormatter.FormatDatePart(value, TodoDueStrings.Zh));
    }

    // ---------- overdue judgment ----------

    [Fact]
    public void IsOverdue_TimedPast_True_TimedTodayLater_False()
    {
        Assert.True(TodoDueFormatter.IsOverdue(LocalAt(0, 2), false, Now));   // earlier today
        Assert.False(TodoDueFormatter.IsOverdue(LocalAt(0, 23), false, Now)); // later today
    }

    [Fact]
    public void IsOverdue_AllDayToday_False_AllDayYesterday_True()
    {
        Assert.False(TodoDueFormatter.IsOverdue(LocalAt(0, 0), true, Now));
        Assert.True(TodoDueFormatter.IsOverdue(LocalAt(-1, 0), true, Now));
    }

    [Fact]
    public void IsOverdue_FutureTimed_False()
    {
        Assert.False(TodoDueFormatter.IsOverdue(LocalAt(2, 10), false, Now));
        Assert.False(TodoDueFormatter.IsOverdue(LocalAt(2, 0), true, Now));
    }
}
