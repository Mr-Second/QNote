namespace QNote.Services;

/// <summary>
/// Recurrence-rule pure functions (PRD Decision 10). Preset rules are a closed
/// enum encoded as a string column — no RRULE — matching the preset menu
/// (每天/工作日/每周/每月/每年); the 自定义 dialog (real-To-Do parity, user
/// ruling 2026-10-15) adds an open-ended <c>custom:N:unit</c> form (N = 1..999,
/// unit = day/week/month/year). <see cref="Advance"/> computes the next
/// occurrence's due instant from the completed occurrence's due (or the
/// completion moment when the task had no due), advancing in LOCAL time so
/// all-day dues stay local-midnight markers (see TodoTask.DueAt contract).
/// </summary>
public static class TodoRecurrence
{
    public const string Daily = "daily";
    public const string Weekdays = "weekdays";
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";
    public const string Yearly = "yearly";

    /// <summary>Prefix of the 自定义 rule form <c>custom:N:unit</c>.</summary>
    public const string CustomPrefix = "custom:";

    /// <summary>Custom-rule units, in the 重复周期 dialog's dropdown order.</summary>
    public static readonly string[] CustomUnits = ["day", "week", "month", "year"];

    /// <summary>All valid PRESET rule codes (the 重复 menu order).</summary>
    public static readonly string[] All = [Daily, Weekdays, Weekly, Monthly, Yearly];

    public static bool IsValid(string? rule) =>
        rule is not null &&
        (All.Contains(rule, StringComparer.Ordinal) || TryParseCustom(rule, out _, out _));

    /// <summary>Parses <c>custom:N:unit</c>; N must be an integer in 1..999.</summary>
    public static bool TryParseCustom(string? rule, out int interval, out string unit)
    {
        interval = 0;
        unit = string.Empty;
        if (rule is null || !rule.StartsWith(CustomPrefix, StringComparison.Ordinal))
            return false;
        var parts = rule.Split(':');
        if (parts.Length != 3)
            return false;
        if (!int.TryParse(parts[1], out var n) || n is < 1 or > 999)
            return false;
        if (!CustomUnits.Contains(parts[2], StringComparer.Ordinal))
            return false;
        interval = n;
        unit = parts[2];
        return true;
    }

    /// <summary>Composes the stored rule string for the 自定义 dialog result.</summary>
    public static string MakeCustom(int interval, string unit) => $"{CustomPrefix}{interval}:{unit}";

    /// <summary>
    /// The next occurrence's due instant after a completion.
    /// <paramref name="fromUtc"/> is the completed occurrence's due — or the
    /// completion instant when it had none (a repeat without a due repeats
    /// from completion time). All-day tasks advance by local DATE and keep the
    /// local-midnight marker; timed tasks keep the wall-clock time.
    /// </summary>
    public static DateTimeOffset Advance(string rule, DateTimeOffset fromUtc, bool isAllDay)
    {
        var tz = TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(fromUtc, tz);

        if (rule == Weekdays)
        {
            // Skip Sat/Sun whole days (an all-day weekend due lands on Monday).
            do
            {
                local = local.AddDays(1);
            } while (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        }
        else
        {
            // AddMonths/AddYears clamp the day automatically (Jan 31 + 1mo = Feb 28/29).
            var date = rule switch
            {
                Daily => local.Date.AddDays(1),
                Weekly => local.Date.AddDays(7),
                Monthly => local.Date.AddMonths(1),
                Yearly => local.Date.AddYears(1),
                _ => AdvanceCustom(rule, local),
            };
            local = isAllDay ? date : date + local.TimeOfDay;
        }

        var unspecified = DateTime.SpecifyKind(local.DateTime, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, tz.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    private static DateTime AdvanceCustom(string rule, DateTimeOffset local)
    {
        if (!TryParseCustom(rule, out var interval, out var unit))
            throw new ArgumentException($"Unknown recurrence rule '{rule}'.", nameof(rule));
        return unit switch
        {
            "day" => local.Date.AddDays(interval),
            "week" => local.Date.AddDays(7L * interval),
            "month" => local.Date.AddMonths(interval),
            _ => local.Date.AddYears(interval),
        };
    }
}
