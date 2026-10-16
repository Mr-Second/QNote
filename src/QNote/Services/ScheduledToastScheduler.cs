using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using QNote.Text;
using Windows.UI.Notifications;

namespace QNote.Services;

/// <summary>
/// Presentation-side <see cref="IToastScheduler"/> (task 10-04): raw WinRT
/// scheduling + the hand-rolled COM activator in <see cref="TodoToastComServer"/>
/// — the AOT-safe execution layer (see that file for why the Toolkit's
/// <c>ToastNotificationManagerCompat</c> Emit-based path — and even its
/// Emit-free old API — cannot pass the NativeAOT publish gate). Content still
/// comes from <see cref="ToastContentBuilder"/> (pure XML generation, no
/// Compat dependency; only <c>GetXml()</c> is used — <b>never</b>
/// <c>Show()</c>/<c>Schedule()</c>, which route through the poisoned Compat
/// static constructor).
///
/// One code path covers both channels (PRD Decision 3): the notifier resolves
/// the AUMID by package identity (<see cref="TodoToastComServer.CreateToastNotifier"/>).
/// Scheduled toasts are held by the OS and fire with the app closed;
/// <c>Tag = task uuid</c>, <c>Group = "todo"</c> so cancels are precise (PRD
/// acceptance row 5).
///
/// Every member is best-effort: WinRT failures (elevated process — a WinAppSDK
/// limitation, the notification platform refusing, registry problems) are
/// logged (Warning) and swallowed — reminder trouble never crashes the app.
/// Failures self-heal: a failed schedule is re-asserted by the next launch
/// reconcile, a failed cancel by the next stale sweep.
/// </summary>
public sealed class ScheduledToastScheduler : IToastScheduler
{
    /// <summary>OS toast group for every todo reminder toast (the Tag's key half).</summary>
    private const string Group = "todo";

    private readonly ILogger<ScheduledToastScheduler> _log;

    public ScheduledToastScheduler(ILogger<ScheduledToastScheduler> log) => _log = log;

    public Task ScheduleAsync(TodoReminder reminder, CancellationToken ct = default) =>
        RunBestEffort(() =>
        {
            // Replace semantics (IToastScheduler contract): drop any pending toast
            // for this uuid first — AddToSchedule does not deduplicate by tag, so
            // a re-dated task would otherwise stack a second toast.
            RemoveFromSchedule(reminder.TaskUuid);

            var scheduled = new ScheduledToastNotification(BuildContent(reminder).GetXml(), reminder.DueAt)
            {
                Tag = reminder.TaskUuid, // Toast Tag cap is 64 chars since 15063; uuid = 32 hex
                Group = Group,
            };
            TodoToastComServer.CreateToastNotifier().AddToSchedule(scheduled);
        }, $"schedule '{reminder.Title}' ({reminder.TaskUuid})");

    public Task CancelAsync(string taskUuid, CancellationToken ct = default) =>
        RunBestEffort(() => RemoveFromSchedule(taskUuid), $"cancel ({taskUuid})");

    public Task<IReadOnlyList<string>> GetScheduledTaskUuidsAsync(CancellationToken ct = default)
    {
        try
        {
            IReadOnlyList<string> uuids = EnumerateScheduledUuids();
            return Task.FromResult(uuids);
        }
        catch (Exception ex)
        {
            // Enumerate failure degrades to "nothing is scheduled": reconcile's
            // stale sweep skips this launch and the fill re-asserts in-horizon
            // reminders (ScheduleAsync replaces) — self-healing, never fatal.
            _log.LogWarning(ex, "Enumerating scheduled toasts failed (best-effort).");
            IReadOnlyList<string> empty = [];
            return Task.FromResult(empty);
        }
    }

    public Task ShowNowAsync(TodoReminder reminder, CancellationToken ct = default) =>
        RunBestEffort(() =>
        {
            var toast = new ToastNotification(BuildContent(reminder).GetXml())
            {
                Tag = reminder.TaskUuid,
                Group = Group,
            };
            TodoToastComServer.CreateToastNotifier().Show(toast);
        }, $"show-now '{reminder.Title}' ({reminder.TaskUuid})");

    // ---------- helpers ----------

    /// <summary>
    /// Toast content: line 1 = task title (data), line 2 = the localized due
    /// line. The launch arguments carry the task uuid — wired through the REAL
    /// <see cref="ToastContentBuilder.AddToastActivationInfo"/> overload; the
    /// null-args overload is the documented trap (it swallows the activation
    /// payload the click-through deep link needs — research/
    /// reminder-notification-mechanism.md).
    /// </summary>
    private static ToastContentBuilder BuildContent(TodoReminder reminder)
    {
        var dueText = TodoDueFormatter.Format(
            reminder.DueAt, reminder.IsAllDay, DateTimeOffset.Now,
            AppLanguage.IsChinese ? TodoDueStrings.Zh : TodoDueStrings.En);

        return new ToastContentBuilder()
            .AddText(reminder.Title)
            .AddText(AppStrings.GetFormat("TodoToastDueFormat", dueText))
            .AddToastActivationInfo(
                TodoToastArguments.Build(reminder.TaskUuid), ToastActivationType.Foreground);
    }

    private static List<string> EnumerateScheduledUuids()
    {
        var uuids = new List<string>();
        var notifier = TodoToastComServer.CreateToastNotifier();
        foreach (var scheduled in notifier.GetScheduledToastNotifications())
        {
            if (scheduled.Group == Group && scheduled.Tag.Length > 0)
                uuids.Add(scheduled.Tag);
        }
        return uuids;
    }

    private static void RemoveFromSchedule(string taskUuid)
    {
        var notifier = TodoToastComServer.CreateToastNotifier();
        foreach (var scheduled in notifier.GetScheduledToastNotifications())
        {
            if (scheduled.Tag == taskUuid && scheduled.Group == Group)
                notifier.RemoveFromSchedule(scheduled);
        }
    }

    /// <summary>Log (Warning) + swallow — the best-effort contract of every scheduler call.</summary>
    private Task RunBestEffort(Action action, string operation)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Scheduled toast {Operation} failed (best-effort).", operation);
        }
        return Task.CompletedTask;
    }
}
