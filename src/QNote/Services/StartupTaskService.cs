using Microsoft.Extensions.Logging;
using Windows.ApplicationModel;

namespace QNote.Services;

/// <summary>
/// Presentation-side <see cref="IStartupTaskService"/> over the WinRT
/// <see cref="StartupTask"/> API (requires the <c>-windows</c> TFM + package identity,
/// hence it cannot live in Core). The OS state is the single source of truth — no
/// SQLite key. All failures are expected outcomes (no package identity, platform
/// quirks): logged + mapped to <see cref="StartupTaskStatus.Unavailable"/>, never thrown.
/// <see cref="RequestEnableAsync"/> must be called on the UI thread (WinRT requirement).
/// </summary>
public sealed class StartupTaskService : IStartupTaskService
{
    /// <summary>Must match the <c>TaskId</c> declared in Package.appxmanifest.</summary>
    private const string TaskId = "QNoteStartup";

    private readonly ILogger<StartupTaskService> _log;

    public StartupTaskService(ILogger<StartupTaskService> log) => _log = log;

    public async Task<StartupTaskStatus> GetStateAsync(CancellationToken ct = default)
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId).AsTask(ct);
            return Map(task.State);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "StartupTask.GetAsync failed — treating launch-at-startup as unavailable.");
            return StartupTaskStatus.Unavailable;
        }
    }

    public async Task<StartupTaskStatus> RequestEnableAsync(CancellationToken ct = default)
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId).AsTask(ct);
            // Packaged desktop apps get NO consent dialog — this applies silently.
            var state = await task.RequestEnableAsync().AsTask(ct);
            _log.LogInformation("StartupTask enable requested; resulting state: {State}", state);
            return Map(state);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "StartupTask.RequestEnableAsync failed.");
            return StartupTaskStatus.Unavailable;
        }
    }

    public async Task<StartupTaskStatus> DisableAsync(CancellationToken ct = default)
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId).AsTask(ct);
            task.Disable();
            _log.LogInformation("StartupTask disabled.");
            return Map(task.State);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "StartupTask.Disable failed.");
            return StartupTaskStatus.Unavailable;
        }
    }

    private static StartupTaskStatus Map(StartupTaskState state) => state switch
    {
        StartupTaskState.Disabled => StartupTaskStatus.Disabled,
        StartupTaskState.DisabledByUser => StartupTaskStatus.DisabledByUser,
        StartupTaskState.Enabled => StartupTaskStatus.Enabled,
        StartupTaskState.DisabledByPolicy => StartupTaskStatus.DisabledByPolicy,
        StartupTaskState.EnabledByPolicy => StartupTaskStatus.EnabledByPolicy,
        _ => StartupTaskStatus.Unavailable,
    };
}
