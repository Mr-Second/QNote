using QNote.Models;

namespace QNote.Text;

/// <summary>
/// List-card timestamp formatting (pure, unit-testable — the VMs delegate here).
/// Three styles: Relative (Qt parity), Tiered (the original WinUI default),
/// Full. Qt's 12-hour "hh" is treated as a bug and rendered as 24-hour "HH".
/// The Relative word forms come from <see cref="TimeStrings"/> (default:
/// <see cref="TimeStrings.Zh"/>, keeping the historical behavior); Presentation
/// passes the language-appropriate instance.
/// </summary>
public static class NoteTimeFormatter
{
    public static string Format(DateTimeOffset value, NoteTimeFormat format, DateTimeOffset? now = null, TimeStrings? strings = null)
    {
        var local = value.ToLocalTime();
        var current = (now ?? DateTimeOffset.Now).ToLocalTime();
        var words = strings ?? TimeStrings.Zh;

        switch (format)
        {
            case NoteTimeFormat.Relative:
            {
                var diff = current - local;
                if (diff.TotalSeconds < 60)
                    return words.JustNow;
                if (diff.TotalHours < 1)
                    return string.Format(words.MinutesAgoFormat, (int)diff.TotalMinutes);
                if (diff.TotalDays < 1)
                    return string.Format(words.HoursAgoFormat, (int)diff.TotalHours);
                if (diff.TotalDays < 7)
                    return string.Format(words.DaysAgoFormat, (int)diff.TotalDays);
                return local.ToString("MM-dd");
            }
            case NoteTimeFormat.Full:
                return local.ToString("yyyy-MM-dd HH:mm");
            default: // Tiered
                if (local.Date == current.Date)
                    return local.ToString("HH:mm");
                if (local.Year == current.Year)
                    return local.ToString("MM-dd HH:mm");
                return local.ToString("yyyy-MM-dd");
        }
    }
}
