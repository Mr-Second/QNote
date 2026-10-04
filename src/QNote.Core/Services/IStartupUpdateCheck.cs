namespace QNote.Services;

/// <summary>
/// One "show the user an update dialog" decision from the startup check:
/// the display version, the (optional) changelog, and the confirm action's
/// target — a GitHub release page (portable) or the Store product page
/// (packaged, chosen by the orchestrator so the UI stays channel-agnostic).
/// </summary>
public sealed record StartupUpdateInfo(string Version, string? Notes, Uri Target);

/// <summary>
/// Startup update-check orchestrator (1.5.1). Channel split — the zero-drift
/// contract (user ruling 2026-10-04):
///
/// - <b>Portable (unpackaged)</b>: <see cref="IGitHubUpdateService"/> is the
///   whole story — newer tag → dialog with the GitHub release URL.
/// - <b>Store (packaged)</b>: the <b>Store backend decides</b>
///   (<see cref="IStoreUpdateChecker"/>); while it reports nothing, no dialog,
///   no matter what GitHub says — a GitHub tag ahead of a pending Store
///   certification can never produce a dead-end dialog. When the Store does
///   report an update, the changelog is fetched from GitHub and shown only if
///   its tag matches the Store version (mismatch → version-only dialog).
///
/// Implementations must never throw across the boundary; every failure path
/// degrades to null ("stay silent this session").
/// </summary>
public interface IStartupUpdateCheck
{
    /// <summary>null = stay silent this session (no update, muted, or check failure).</summary>
    Task<StartupUpdateInfo?> CheckAsync();
}
