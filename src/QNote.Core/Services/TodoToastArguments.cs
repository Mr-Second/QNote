namespace QNote.Services;

/// <summary>
/// Launch-argument codec for todo reminder toasts (task 10-04). Every reminder
/// toast carries <c>todo-task=&lt;uuid&gt;</c> so a click-through activation can
/// deep-link to the task (via <c>ITodoRepository.GetTaskByUuidAsync</c>). The
/// same string arrives through the app's own COM activator
/// (<c>INotificationActivationCallback</c>; see the Presentation-side
/// TodoToastComServer — the hand-rolled, NativeAOT-safe replacement for the
/// Toolkit Compat activator) for BOTH warm clicks (app running: the OS
/// activates the in-process registered class object) and cold launches (app
/// closed: the OS starts the EXE as the COM local server and the activation
/// fires soon after startup). Pure string logic — headless-testable, no WinRT
/// types (Core rule).
/// </summary>
public static class TodoToastArguments
{
    /// <summary>Argument prefix; the value is the task uuid (Guid "N", 32 hex chars).</summary>
    public const string Prefix = "todo-task=";

    /// <summary>The launch-argument string written into every reminder toast.</summary>
    public static string Build(string taskUuid) => Prefix + taskUuid;

    /// <summary>
    /// Extract the task uuid from an activation argument string. Tolerates
    /// additional space/semicolon-separated segments (today the payload is a
    /// single segment authored by <see cref="Build"/>; splitting keeps the
    /// format extensible). A matched prefix with a malformed value is junk —
    /// never partially trusted. Returns <c>null</c> for anything that is not a
    /// well-formed 32-hex task uuid.
    /// </summary>
    public static string? TryParseTaskUuid(string? arguments)
    {
        if (string.IsNullOrEmpty(arguments))
            return null;

        foreach (var segment in arguments.Split(';', ' '))
        {
            if (!segment.StartsWith(Prefix, StringComparison.Ordinal))
                continue;
            var value = segment[Prefix.Length..];
            return value.Length == 32 && value.All(char.IsAsciiHexDigit) ? value : null;
        }
        return null;
    }
}
