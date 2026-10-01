namespace QNote.Services;

/// <summary>Manual update-check outcome against the GitHub Releases channel.</summary>
public enum UpdateCheckStatus
{
    /// <summary>A newer release exists; <see cref="UpdateCheckResult.ReleaseUrl"/> points at its page.</summary>
    UpdateAvailable = 0,

    /// <summary>No release newer than the running build.</summary>
    UpToDate = 1,

    /// <summary>The check failed (network / API / parse); <see cref="UpdateCheckResult.Reason"/> carries the technical cause.</summary>
    Failed = 2,
}

/// <summary>
/// Result of one manual update check (设置 → 常规 → 检查更新, unpackaged only).
/// Failures are values, not exceptions (error-handling spec): the technical
/// detail rides in <see cref="Reason"/> and the log; the UI shows a friendly
/// zh summary.
/// </summary>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string? ReleaseUrl,
    string? LatestVersion,
    string? Reason)
{
    public static UpdateCheckResult Failed(string reason) =>
        new(UpdateCheckStatus.Failed, null, null, reason);
}

/// <summary>
/// Reads the latest GitHub release and compares it with the running app
/// version (main-module FileVersion — robust for unpackaged builds where the
/// MSIX manifest does not apply). Manual checks only: no auto-update, no
/// silent startup check. Implementations must never throw across this
/// boundary — every failure is an <see cref="UpdateCheckStatus.Failed"/> result.
/// </summary>
public interface IGitHubUpdateService
{
    /// <summary>Fetch the latest release of <c>Mr-Second/QNote</c> and compare versions.</summary>
    Task<UpdateCheckResult> CheckLatestAsync(CancellationToken ct = default);
}
