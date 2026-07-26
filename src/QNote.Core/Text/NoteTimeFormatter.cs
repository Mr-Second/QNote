using QNote.Models;

namespace QNote.Text;

/// <summary>
/// List-card timestamp formatting (pure, unit-testable — the VMs delegate here).
/// Three styles: Relative (Qt parity), Tiered (the original WinUI default),
/// Full. Qt's 12-hour "hh" is treated as a bug and rendered as 24-hour "HH".
/// </summary>
public static class NoteTimeFormatter
{
    public static string Format(DateTimeOffset value, NoteTimeFormat format, DateTimeOffset? now = null)
    {
        var local = value.ToLocalTime();
        var current = (now ?? DateTimeOffset.Now).ToLocalTime();

        switch (format)
        {
            case NoteTimeFormat.Relative:
            {
                var diff = current - local;
                if (diff.TotalSeconds < 60)
                    return "刚刚";
                if (diff.TotalHours < 1)
                    return $"{(int)diff.TotalMinutes}分钟前";
                if (diff.TotalDays < 1)
                    return $"{(int)diff.TotalHours}小时前";
                if (diff.TotalDays < 7)
                    return $"{(int)diff.TotalDays}天前";
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
