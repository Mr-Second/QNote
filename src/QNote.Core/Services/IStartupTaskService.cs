namespace QNote.Services;

/// <summary>
/// OS-level startup-task state — a mirror of <c>Windows.ApplicationModel.StartupTaskState</c>
/// so Core (net10.0, no WinRT) can reason about it. The OS state is the single source
/// of truth for launch-at-startup (Windows Terminal pattern); nothing is persisted to
/// the settings table.
/// </summary>
public enum StartupTaskStatus
{
    /// <summary>Registered but not enabled.</summary>
    Disabled = 0,

    /// <summary>The user disabled it in Task Manager / Settings; only the user can re-enable.</summary>
    DisabledByUser = 1,

    /// <summary>Will launch at sign-in.</summary>
    Enabled = 2,

    /// <summary>Disabled by group policy; the toggle is locked.</summary>
    DisabledByPolicy = 3,

    /// <summary>Enabled by group policy; the toggle is locked on.</summary>
    EnabledByPolicy = 4,

    /// <summary>Query failed (e.g. no package identity); treat the toggle as unavailable.</summary>
    Unavailable = 5,
}

/// <summary>
/// Launch-at-Windows-startup via the MSIX <c>windows.startupTask</c> extension.
/// Abstraction so the settings view-model stays free of WinRT — the Presentation
/// implementation wraps <c>Windows.ApplicationModel.StartupTask</c> (same pattern
/// as <see cref="IGlobalHotkey"/>).
/// </summary>
public interface IStartupTaskService
{
    /// <summary>Read the current OS state.</summary>
    Task<StartupTaskStatus> GetStateAsync(CancellationToken ct = default);

    /// <summary>
    /// Ask the OS to enable startup. Packaged desktop apps get no consent dialog.
    /// Returns the resulting state — if it is not <see cref="StartupTaskStatus.Enabled"/>
    /// (e.g. <see cref="StartupTaskStatus.DisabledByUser"/>), the caller must revert the UI.
    /// </summary>
    Task<StartupTaskStatus> RequestEnableAsync(CancellationToken ct = default);

    /// <summary>Disable startup; returns the resulting state.</summary>
    Task<StartupTaskStatus> DisableAsync(CancellationToken ct = default);
}
