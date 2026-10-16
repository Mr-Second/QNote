using QNote.Services;

namespace QNote.Tests;

/// <summary>
/// Pure-function contract of <see cref="TodoRecurrence"/> (PRD Decision 10):
/// preset + <c>custom:N:unit</c> rule validation/parsing, and the
/// <see cref="TodoRecurrence.Advance"/> matrix — local-time advancement
/// (wall-clock preserved for timed dues, local-midnight markers kept for
/// all-day dues), weekend skipping, and month/year day-clamping. Expectations
/// are built through the local zone at each date, so the suite is green on any
/// runner timezone (same pattern as TodoRepositoryTests).
/// </summary>
public sealed class TodoRecurrenceTests
{
    // ---------- IsValid / TryParseCustom / MakeCustom ----------

    [Theory]
    [InlineData(TodoRecurrence.Daily)]
    [InlineData(TodoRecurrence.Weekdays)]
    [InlineData(TodoRecurrence.Weekly)]
    [InlineData(TodoRecurrence.Monthly)]
    [InlineData(TodoRecurrence.Yearly)]
    public void IsValid_AcceptsPresets(string rule) => Assert.True(TodoRecurrence.IsValid(rule));

    [Theory]
    [InlineData("custom:1:day")]
    [InlineData("custom:42:week")]
    [InlineData("custom:999:month")]
    [InlineData("custom:2:year")]
    public void IsValid_AcceptsCustomForms(string rule) => Assert.True(TodoRecurrence.IsValid(rule));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("daily ")]          // no whitespace tolerance
    [InlineData("DAILY")]           // no case tolerance
    [InlineData("custom:")]          // no interval/unit
    [InlineData("custom:0:day")]     // interval must be >= 1
    [InlineData("custom:1000:day")]  // interval must be <= 999
    [InlineData("custom:1:century")] // unit must be one of the four
    [InlineData("custom:1:day:x")]   // exactly three segments
    [InlineData("custom:abc:day")]   // interval must parse as an integer
    [InlineData("custam:1:day")]     // prefix must match exactly
    [InlineData("Custom:1:day")]     // prefix is case-sensitive
    public void IsValid_RejectsMalformedRules(string? rule) => Assert.False(TodoRecurrence.IsValid(rule));

    [Fact]
    public void TryParseCustom_ExtractsIntervalAndUnit()
    {
        Assert.True(TodoRecurrence.TryParseCustom("custom:3:week", out var interval, out var unit));
        Assert.Equal(3, interval);
        Assert.Equal("week", unit);
    }

    [Fact]
    public void TryParseCustom_RejectsPresetsAndMalformed()
    {
        Assert.False(TodoRecurrence.TryParseCustom("weekly", out _, out _));
        Assert.False(TodoRecurrence.TryParseCustom("custom:0:day", out var interval, out _));
        Assert.Equal(0, interval);
        Assert.False(TodoRecurrence.TryParseCustom(null, out _, out _));
    }

    [Fact]
    public void MakeCustom_RoundTripsThroughTryParse()
    {
        var rule = TodoRecurrence.MakeCustom(2, "month");
        Assert.Equal("custom:2:month", rule);
        Assert.True(TodoRecurrence.TryParseCustom(rule, out var interval, out var unit));
        Assert.Equal(2, interval);
        Assert.Equal("month", unit);
    }

    [Fact]
    public void CustomUnits_MatchTheDialogDropdownOrder()
        => Assert.Equal(["day", "week", "month", "year"], TodoRecurrence.CustomUnits);

    // ---------- Advance: timed dues keep the local wall clock ----------

    [Fact]
    public void Advance_Daily_Timed_AddsOneLocalDay_KeepsWallClock()
    {
        var from = LocalOf(2027, 1, 5, 9, 30);
        Assert.Equal(LocalOf(2027, 1, 6, 9, 30), TodoRecurrence.Advance(TodoRecurrence.Daily, from, isAllDay: false));
    }

    [Fact]
    public void Advance_Weekly_Timed_AddsSevenLocalDays_KeepsWallClock()
    {
        var from = LocalOf(2027, 1, 5, 18, 0);
        Assert.Equal(LocalOf(2027, 1, 12, 18, 0), TodoRecurrence.Advance(TodoRecurrence.Weekly, from, isAllDay: false));
    }

    [Fact]
    public void Advance_Weekdays_Timed_SkipsTheWeekend()
    {
        // Friday 2026-10-16 -> Monday 2026-10-19 (no Sat/Sun dues).
        var from = LocalOf(2026, 10, 16, 8, 0);
        Assert.Equal(LocalOf(2026, 10, 19, 8, 0), TodoRecurrence.Advance(TodoRecurrence.Weekdays, from, isAllDay: false));
    }

    [Fact]
    public void Advance_Weekdays_AllDay_LandsOnMondayMidnight()
    {
        // The all-day marker stays local-midnight even across a weekend skip.
        var from = LocalOf(2026, 10, 16, 0, 0);
        var next = TodoRecurrence.Advance(TodoRecurrence.Weekdays, from, isAllDay: true);
        Assert.Equal(LocalOf(2026, 10, 19, 0, 0), next);
    }

    [Fact]
    public void Advance_Monthly_ClampsShortMonths()
    {
        // Jan 31 + 1 month = Feb 28 (2027 is not a leap year).
        var from = LocalOf(2027, 1, 31, 9, 0);
        Assert.Equal(LocalOf(2027, 2, 28, 9, 0), TodoRecurrence.Advance(TodoRecurrence.Monthly, from, isAllDay: false));
    }

    [Fact]
    public void Advance_Yearly_ClampsLeapDays()
    {
        // Feb 29 2024 + 1 year = Feb 28 2025.
        var from = LocalOf(2024, 2, 29, 12, 0);
        Assert.Equal(LocalOf(2025, 2, 28, 12, 0), TodoRecurrence.Advance(TodoRecurrence.Yearly, from, isAllDay: false));
    }

    // ---------- Advance: all-day dues keep the local-midnight marker ----------

    [Fact]
    public void Advance_Daily_AllDay_AdvancesTheLocalDateAtMidnight()
    {
        var from = LocalOf(2026, 10, 9, 0, 0);
        Assert.Equal(LocalOf(2026, 10, 10, 0, 0), TodoRecurrence.Advance(TodoRecurrence.Daily, from, isAllDay: true));
    }

    // ---------- Advance: custom:N:unit ----------

    [Theory]
    [InlineData("custom:2:day", 2027, 3, 1, 2027, 3, 3)]
    [InlineData("custom:2:week", 2027, 3, 1, 2027, 3, 15)]
    [InlineData("custom:3:month", 2027, 3, 31, 2027, 6, 30)] // clamp: Mar 31 + 3mo = Jun 30
    [InlineData("custom:1:year", 2024, 2, 29, 2025, 2, 28)]  // clamp: leap day + 1y
    public void Advance_Custom_Timed_AdvancesByIntervalAndClamps(
        string rule, int y0, int m0, int d0, int y1, int m1, int d1)
    {
        var from = LocalOf(y0, m0, d0, 7, 45);
        Assert.Equal(LocalOf(y1, m1, d1, 7, 45), TodoRecurrence.Advance(rule, from, isAllDay: false));
    }

    [Fact]
    public void Advance_UnknownRule_Throws()
    {
        var from = LocalOf(2026, 10, 9, 9, 0);
        Assert.Throws<ArgumentException>(() => TodoRecurrence.Advance("bogus", from, isAllDay: false));
    }

    /// <summary>
    /// A UTC instant for an unspecified LOCAL wall-clock date/time — the same
    /// conversion rule <see cref="TodoRecurrence.Advance"/> applies on its way
    /// back, so expectations stay valid in any runner timezone.
    /// </summary>
    private static DateTimeOffset LocalOf(int year, int month, int day, int hour, int minute = 0)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }
}
