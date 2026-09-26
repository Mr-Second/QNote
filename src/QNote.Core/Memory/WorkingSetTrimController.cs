using Microsoft.Extensions.Logging;

namespace QNote.Memory;

/// <summary>
/// Tiered working-set trim for a tray-resident app (perf-size-optimization R2):
/// the moment the window hides to the tray, run an immediate compacting full GC;
/// if the hide persists past <see cref="DeepTrimDelay"/>, follow with
/// EmptyWorkingSet. A re-show before the delay cancels the deep trim.
///
/// Deliberately engaged ONLY by the tray-hide and StartMinimized paths — never by
/// edge-hide, where a hot-zone reveal must stay instant and hard page faults are
/// unacceptable. Every interop call is best-effort: failures are logged and
/// swallowed, never affecting app flow. Follows the EdgeHideController split:
/// all judgment/timing lives here in Core (headless-testable via injected
/// <see cref="IWorkingSetInterop"/> + <see cref="IOneShotTimer"/>); Presentation
/// only feeds the hide/show signals.
/// </summary>
public sealed class WorkingSetTrimController : IDisposable
{
    /// <summary>Hide duration after which the deep trim fires (MVP: hardcoded, no setting).</summary>
    public static readonly TimeSpan DeepTrimDelay = TimeSpan.FromSeconds(10);

    private readonly IWorkingSetInterop _interop;
    private readonly IOneShotTimer _deepTrimTimer;
    private readonly ILogger<WorkingSetTrimController> _log;
    private readonly object _gate = new();
    private bool _hidden;

    public WorkingSetTrimController(
        IWorkingSetInterop interop,
        IOneShotTimer deepTrimTimer,
        ILogger<WorkingSetTrimController> log)
    {
        _interop = interop;
        _deepTrimTimer = deepTrimTimer;
        _log = log;
        _deepTrimTimer.Elapsed += OnDeepTrimElapsed;
    }

    /// <summary>
    /// Window hidden to tray (or StartMinimized launch): immediate compacting GC,
    /// then arm the deep-trim timer. The GC runs synchronously on the caller — the
    /// window is already hidden, so a tens-of-ms pause is invisible, and keeping it
    /// synchronous keeps failure handling trivial.
    /// </summary>
    public void OnHidden()
    {
        lock (_gate)
            _hidden = true;

        TryTrim("compacting GC", _interop.CompactingCollect);
        _deepTrimTimer.Arm(DeepTrimDelay);
    }

    /// <summary>Window shown again: cancel a pending deep trim (harmless when not armed).</summary>
    public void OnShown()
    {
        lock (_gate)
            _hidden = false;

        _deepTrimTimer.Cancel();
    }

    /// <summary>Timer elapsed (thread-pool): deep-trim only if the window is still hidden.</summary>
    private void OnDeepTrimElapsed()
    {
        lock (_gate)
        {
            if (!_hidden)
                return; // raced with OnShown between fire and cancel — treat as cancelled
        }

        TryTrim("EmptyWorkingSet", _interop.EmptyWorkingSet);
    }

    private void TryTrim(string action, Action trim)
    {
        try
        {
            trim();
            _log.LogInformation("Working-set trim: {Action} completed.", action);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Working-set trim: {Action} failed (best-effort, ignored).", action);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _deepTrimTimer.Dispose();
}
