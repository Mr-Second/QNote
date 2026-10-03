namespace QNote.Text;

/// <summary>
/// Localized word forms for <see cref="NoteTimeFormatter"/>'s Relative style.
/// The plural slots are <see cref="string.Format"/> templates over the count.
/// Compact English forms ("min"/"hr"/"d") are deliberately number-neutral —
/// no singular/plural branching.
/// </summary>
public sealed record TimeStrings(
    string JustNow,
    string MinutesAgoFormat,
    string HoursAgoFormat,
    string DaysAgoFormat)
{
    /// <summary>Chinese (product source language; the historical default).</summary>
    public static readonly TimeStrings Zh = new("刚刚", "{0}分钟前", "{0}小时前", "{0}天前");

    /// <summary>English.</summary>
    public static readonly TimeStrings En = new("just now", "{0} min ago", "{0} hr ago", "{0} d ago");
}
