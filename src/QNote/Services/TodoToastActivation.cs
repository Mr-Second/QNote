using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QNote.Views;

namespace QNote.Services;

/// <summary>
/// OS toast activation wiring for todo reminders (task 10-04). The COM
/// activator (<see cref="TodoNotificationActivator"/> in
/// <see cref="TodoToastComServer"/>, registered by <see cref="Initialize"/> in
/// the app's initial startup code) is the OS's entry point for toast clicks.
/// Delivery paths:
/// <list type="bullet">
/// <item><b>Warm</b> (app running): the OS activates the registered in-process
/// class object — <c>Activate</c> fires in-process, on a COM thread.</item>
/// <item><b>Cold</b> (app closed): the OS starts the EXE as the COM local
/// server (<c>LocalServer32</c> / manifest <c>com:ExeServer</c>, command line
/// carries <c>-ToastActivated</c>); once startup reaches
/// <see cref="Initialize"/>, the pending activation is delivered to the same
/// handler.</item>
/// </list>
/// Both paths land in <see cref="HandleToastActivation"/>: parse the task uuid,
/// marshal to the UI thread, reveal the window (tray/edge-hide aware), switch
/// to the 待办 mode and select the task (deep link via the page VM).
/// An activation that arrives before the window/dispatcher exist is stashed
/// and replayed by <c>App.OnLaunched</c> right after the window is created.
/// The single-instance redirect path never carries toast activations: a warm
/// click connects to the RUNNING process's registered class object directly
/// (REGCLS_MULTIPLEUSE), so no second process is ever spawned for it.
/// </summary>
internal static class TodoToastActivation
{
    /// <summary>Deep link that arrived before the window existed (cold start race).</summary>
    private static string? _pendingTaskUuid;

    /// <summary>
    /// Register the COM activator — call once, at app startup (App ctor, after
    /// the DI container is built so registration failures can be logged).
    /// Best-effort: never throws (click-through degrades to plain launches,
    /// scheduling is unaffected).
    /// </summary>
    public static void Initialize()
    {
        try
        {
            TodoToastComServer.Register(
                App.Services.GetRequiredService<ILogger<App>>());
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<App>>()
                .LogWarning(ex, "Toast activation registration failed (best-effort; click-through degrades to plain launches).");
        }
    }

    /// <summary>
    /// Entry point for BOTH activation paths (COM activator callback).
    /// <paramref name="arguments"/> is the toast's launch-argument string.
    /// </summary>
    internal static void HandleToastActivation(string arguments)
    {
        var taskUuid = TodoToastArguments.TryParseTaskUuid(arguments);
        if (taskUuid is null)
            return; // not a todo reminder payload (e.g. a future button action) — nothing to do

        // COM thread → UI thread. During a cold launch the dispatcher property
        // is assigned only after the window is created — stash and let
        // ReplayPending (from OnLaunched) drive the navigation instead.
        var queue = App.DispatcherQueue;
        if (queue is not null)
            queue.TryEnqueue(() => NavigateToTask(taskUuid));
        else
            _pendingTaskUuid = taskUuid;
    }

    /// <summary>
    /// Replay a cold-start activation that arrived before the window existed.
    /// Call once from <c>App.OnLaunched</c>, right after the window and
    /// dispatcher are assigned.
    /// </summary>
    public static void ReplayPending()
    {
        var taskUuid = Interlocked.Exchange(ref _pendingTaskUuid, null);
        if (taskUuid is not null)
            NavigateToTask(taskUuid);
    }

    private static void NavigateToTask(string taskUuid)
    {
        try
        {
            if (App.Window is MainWindow mainWindow)
                _ = mainWindow.ShowTodoTaskAsync(taskUuid);
            // else: window not built yet — only reachable in the stash window,
            // which ReplayPending owns; dropping here is the safe fallback.
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<App>>()
                .LogWarning(ex, "Toast deep-link navigation failed (best-effort).");
        }
    }
}
