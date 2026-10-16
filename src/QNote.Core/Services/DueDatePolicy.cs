namespace QNote.Services;

/// <summary>
/// Reminder defaults when a due date is set on a todo task (PRD Decision 4):
/// setting a due date WITH a time (<c>isAllDay = false</c>) turns「到期时提醒」ON
/// by default — the user can turn it off in the detail pane — while an all-day due
/// date defaults to NO reminder. The default is re-applied on every due-date change
/// unless the caller passes an explicit reminder choice (the detail-pane toggle),
/// so re-scheduling never silently keeps a stale reminder state.
/// </summary>
public static class DueDatePolicy
{
    /// <summary>The reminder default for a due date being set: timed → on, all-day → off.</summary>
    public static bool DefaultReminder(bool isAllDay) => !isAllDay;
}
