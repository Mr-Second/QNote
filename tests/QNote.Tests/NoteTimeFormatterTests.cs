using QNote.Models;
using QNote.Text;

namespace QNote.Tests;

/// <summary>
/// List-card timestamp formatting: Relative (Qt parity), Tiered (default),
/// Full. Local times; <paramref name="now"/> is injected for determinism.
/// </summary>
public sealed class NoteTimeFormatterTests
{
    // Machine-local offset for a given date — keeps the tests timezone-independent
    // (the formatter renders in machine-local time; CI runners are UTC, dev is UTC+8).
    private static TimeSpan LocalOffset(int year, int month, int day) =>
        TimeZoneInfo.Local.GetUtcOffset(new DateTime(year, month, day));

    private static readonly DateTimeOffset Now = new(2026, 7, 26, 15, 30, 0, LocalOffset(2026, 7, 26));

    [Theory]
    [InlineData(10, "刚刚")]            // 10 seconds ago
    [InlineData(59, "刚刚")]
    public void Format_Relative_UnderAMinute_JustNow(int secondsAgo, string expected) =>
        Assert.Equal(expected, NoteTimeFormatter.Format(Now.AddSeconds(-secondsAgo), NoteTimeFormat.Relative, Now));

    [Fact]
    public void Format_Relative_MinutesHoursDays()
    {
        Assert.Equal("3分钟前", NoteTimeFormatter.Format(Now.AddMinutes(-3), NoteTimeFormat.Relative, Now));
        Assert.Equal("5小时前", NoteTimeFormatter.Format(Now.AddHours(-5), NoteTimeFormat.Relative, Now));
        Assert.Equal("2天前", NoteTimeFormatter.Format(Now.AddDays(-2), NoteTimeFormat.Relative, Now));
    }

    [Fact]
    public void Format_Relative_OlderThanAWeek_FallsBackToMonthDay() =>
        Assert.Equal("07-10", NoteTimeFormatter.Format(Now.AddDays(-16), NoteTimeFormat.Relative, Now));

    [Fact] // Default parameter keeps the zh word forms (historical behavior).
    public void Format_Relative_DefaultsToZhWordForms()
    {
        Assert.Equal("刚刚", NoteTimeFormatter.Format(Now.AddSeconds(-10), NoteTimeFormat.Relative, Now));
        Assert.Equal("3分钟前", NoteTimeFormatter.Format(Now.AddMinutes(-3), NoteTimeFormat.Relative, Now));
        Assert.Equal("5小时前", NoteTimeFormatter.Format(Now.AddHours(-5), NoteTimeFormat.Relative, Now));
        Assert.Equal("2天前", NoteTimeFormatter.Format(Now.AddDays(-2), NoteTimeFormat.Relative, Now));
    }

    [Fact] // Presentation passes TimeStrings.En when the UI language is English.
    public void Format_Relative_EnglishStrings()
    {
        Assert.Equal("just now", NoteTimeFormatter.Format(Now.AddSeconds(-10), NoteTimeFormat.Relative, Now, TimeStrings.En));
        Assert.Equal("3 min ago", NoteTimeFormatter.Format(Now.AddMinutes(-3), NoteTimeFormat.Relative, Now, TimeStrings.En));
        Assert.Equal("5 hr ago", NoteTimeFormatter.Format(Now.AddHours(-5), NoteTimeFormat.Relative, Now, TimeStrings.En));
        Assert.Equal("2 d ago", NoteTimeFormatter.Format(Now.AddDays(-2), NoteTimeFormat.Relative, Now, TimeStrings.En));
        // Date-style branches are language-independent.
        Assert.Equal("07-10", NoteTimeFormatter.Format(Now.AddDays(-16), NoteTimeFormat.Relative, Now, TimeStrings.En));
        Assert.Equal("2026-07-26 09:05",
            NoteTimeFormatter.Format(new DateTimeOffset(2026, 7, 26, 9, 5, 0, LocalOffset(2026, 7, 26)), NoteTimeFormat.Full, Now, TimeStrings.En));
    }

    [Fact]
    public void Format_Tiered_Today_ShowsTimeOnly() =>
        Assert.Equal("09:05", NoteTimeFormatter.Format(new DateTimeOffset(2026, 7, 26, 9, 5, 0, LocalOffset(2026, 7, 26)), NoteTimeFormat.Tiered, Now));

    [Fact]
    public void Format_Tiered_SameYear_ShowsMonthDayTime() =>
        Assert.Equal("03-02 09:05", NoteTimeFormatter.Format(new DateTimeOffset(2026, 3, 2, 9, 5, 0, LocalOffset(2026, 3, 2)), NoteTimeFormat.Tiered, Now));

    [Fact]
    public void Format_Tiered_OtherYear_ShowsDateOnly() =>
        Assert.Equal("2025-12-31", NoteTimeFormatter.Format(new DateTimeOffset(2025, 12, 31, 23, 59, 0, LocalOffset(2025, 12, 31)), NoteTimeFormat.Tiered, Now));

    [Fact] // Qt renders 12-hour "hh" — treated as a bug; we emit 24-hour.
    public void Format_Full_AlwaysComplete24H() =>
        Assert.Equal("2026-07-26 09:05", NoteTimeFormatter.Format(new DateTimeOffset(2026, 7, 26, 9, 5, 0, LocalOffset(2026, 7, 26)), NoteTimeFormat.Full, Now));
}
