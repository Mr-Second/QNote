using Microsoft.Extensions.Logging;
using QNote.Memory;

namespace QNote.Sync;

/// <summary>
/// The tiered polling loop (PRD decision 7): visible ≈ 2 min / hidden (tray,
/// minimized, edge-hidden) ≈ 10 min, both with random jitter so peers do not
/// synchronize their polls. An activation or a tray reveal fires an
/// OPPORTUNISTIC cycle (rate-limited by <see cref="DefaultOpportunisticGap"/> so
/// focus flicker cannot hammer the cloud); the manual Settings button and the
/// <see cref="PushQueue"/> debounce bypass that throttle (explicit user intent /
/// the instant-upload trigger). The engine cycle itself is delegated — this class
/// owns only WHEN, never WHAT (the same judgment/stimulus split as
/// <c>WorkingSetTrimController</c>).
///
/// Single-flight: a cycle is never started while one runs; a trigger landing
/// mid-cycle sets a pending flag honored once when the flight lands. Headless-
/// testable: the one-shot timer, the clock and the jitter source are injectable;
/// <see cref="TriggerNow"/> returns the in-flight task so a caller (Settings)
/// can await the very cycle it requested.
/// </summary>
public sealed class SyncPollingScheduler : IDisposable
{
    /// <summary>Poll cadence while the window is visible (decision 7).</summary>
    public static readonly TimeSpan DefaultVisibleInterval = TimeSpan.FromMinutes(2);

    /// <summary>Poll cadence while the window is invisible (decision 7).</summary>
    public static readonly TimeSpan DefaultHiddenInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Minimum age of the last finished cycle before an OPPORTUNISTIC trigger
    /// (activation / reveal) may start another one. The manual button and the
    /// push queue bypass this — they are explicit intents, not side effects.
    /// </summary>
    public static readonly TimeSpan DefaultOpportunisticGap = TimeSpan.FromSeconds(30);

    /// <summary>Relative jitter half-width (±15 %) applied to every poll delay.</summary>
    public const double JitterFraction = 0.15;

    private readonly Func<CancellationToken, Task<SyncCycleResult?>> _cycle;
    private readonly IOneShotTimer _timer;
    private readonly TimeProvider _clock;
    private readonly ILogger<SyncPollingScheduler>? _log;
    private readonly Random _jitter;
    private readonly object _gate = new();
    private readonly TimeSpan _visibleInterval;
    private readonly TimeSpan _hiddenInterval;
    private readonly TimeSpan _opportunisticGap;

    private bool _started;
    private bool _hidden;
    private bool _disposed;
    private bool _rerunPending;
    private DateTimeOffset? _lastFinishedAt;
    private Task<SyncCycleResult?>? _inFlight;
    private CancellationTokenSource? _cycleCancellation;

    /// <summary>
    /// The run delegate returns <c>null</c> for "skipped" (e.g. nobody is signed
    /// in — the composition root owns that gate, the scheduler stays auth-blind).
    /// </summary>
    public SyncPollingScheduler(
        Func<CancellationToken, Task<SyncCycleResult?>> cycle,
        IOneShotTimer? timer = null,
        TimeProvider? clock = null,
        TimeSpan? visibleInterval = null,
        TimeSpan? hiddenInterval = null,
        TimeSpan? opportunisticGap = null,
        Random? jitterRandom = null,
        ILogger<SyncPollingScheduler>? log = null)
    {
        _cycle = cycle ?? throw new ArgumentNullException(nameof(cycle));
        if (visibleInterval is { } v && v <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(visibleInterval), v, "The visible interval must be positive.");
        if (hiddenInterval is { } h && h <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(hiddenInterval), h, "The hidden interval must be positive.");
        if (opportunisticGap is { } g && g < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(opportunisticGap), g, "The opportunistic gap must not be negative.");

        _timer = timer ?? new ThreadingOneShotTimer();
        _clock = clock ?? TimeProvider.System;
        _visibleInterval = visibleInterval ?? DefaultVisibleInterval;
        _hiddenInterval = hiddenInterval ?? DefaultHiddenInterval;
        _opportunisticGap = opportunisticGap ?? DefaultOpportunisticGap;
        _jitter = jitterRandom ?? Random.Shared;
        _log = log;
        _timer.Elapsed += OnTimerElapsed;
    }

    /// <summary>
    /// Raised when a cycle finishes, on whatever thread the await continuation
    /// landed on. The result is <c>null</c> when the run delegate skipped. Purely
    /// informational — callers that need a specific cycle's result await the task
    /// <see cref="TriggerNow"/> returned.
    /// </summary>
    public event Action<SyncCycleResult?>? CycleCompleted;

    /// <summary>The cycle currently running, if any (also the awaitable the Settings button uses).</summary>
    public Task<SyncCycleResult?>? CurrentCycle
    {
        get
        {
            lock (_gate)
                return _inFlight;
        }
    }

    /// <summary>True between <see cref="Start"/> and <see cref="Dispose"/>.</summary>
    public bool IsStarted
    {
        get
        {
            lock (_gate)
                return _started && !_disposed;
        }
    }

    /// <summary>
    /// Arms the first poll. <paramref name="initiallyHidden"/> seeds the tier for
    /// a StartMinimized launch (the window signals correct it afterwards anyway).
    /// </summary>
    public void Start(bool initiallyHidden = false)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                throw new InvalidOperationException("The sync scheduler was already started.");
            _started = true;
            _hidden = initiallyHidden;
            ArmPollTimer_Locked();
        }
        _log?.LogInformation(
            "Sync polling started (tier: {Tier}, interval: ~{Interval:0}s).",
            initiallyHidden ? "hidden" : "visible",
            (initiallyHidden ? _hiddenInterval : _visibleInterval).TotalSeconds);
    }

    /// <summary>
    /// Window became visible (show / restore / reveal). Switches to the visible
    /// tier and fires a throttled opportunistic cycle (decision 7: 唤出补一次).
    /// Safe before <see cref="Start"/> — the tier is remembered for it.
    /// </summary>
    public void OnShown()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            var switched = _hidden; // was hidden → this is a tier switch
            _hidden = false;
            if (_started && switched && _inFlight is null)
                ArmPollTimer_Locked();
        }
        _ = TriggerNow();
    }

    /// <summary>
    /// Window became invisible (tray hide / minimize / edge-hide / StartMinimized
    /// launch). Switches to the hidden tier. Safe before <see cref="Start"/>.
    /// </summary>
    public void OnHidden()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            var switched = !_hidden; // was visible → this is a tier switch
            _hidden = true;
            if (_started && switched && _inFlight is null)
                ArmPollTimer_Locked();
        }
    }

    /// <summary>
    /// Requests a cycle now. Returns the in-flight or freshly started cycle task
    /// so the caller can await its result, or <c>null</c> when nothing was
    /// started (not started yet, disposed, or — with the throttle in effect — a
    /// cycle finished too recently). <paramref name="bypassThrottle"/> is for the
    /// manual button and the push queue; window activations stay throttled.
    /// </summary>
    public Task<SyncCycleResult?>? TriggerNow(bool bypassThrottle = false)
    {
        lock (_gate)
        {
            if (_disposed || !_started)
                return null;
            if (_inFlight is not null)
            {
                _rerunPending = true;
                return _inFlight;
            }
            if (!bypassThrottle
                && _lastFinishedAt is { } finished
                && _clock.GetUtcNow() - finished < _opportunisticGap)
                return null;
            return StartCycle_Locked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _cycleCancellation?.Cancel();
        }
        _timer.Dispose();
    }

    // ---------- internals ----------

    private void OnTimerElapsed()
    {
        Task? started = null;
        lock (_gate)
        {
            // A spurious or post-dispose fire must not start a cycle.
            if (_disposed || !_started || _inFlight is not null)
                return;
            started = StartCycle_Locked();
        }
        _ = started;
    }

    /// <summary>Starts the cycle. Caller holds <see cref="_gate"/> and checked idle.</summary>
    private Task<SyncCycleResult?> StartCycle_Locked()
    {
        _cycleCancellation = new CancellationTokenSource();
        var task = RunCycleAsync(_cycleCancellation.Token);
        // A signed-out probe (or a synchronous failure) can finish inline and
        // release the flight before RunCycleAsync returns. Do not resurrect
        // that completed task: later sign-ins must be able to start a cycle.
        // Async completion takes _gate, so it cannot race this assignment.
        if (!task.IsCompleted)
            _inFlight = task;
        return task;
    }

    /// <summary>
    /// The WRAPPED cycle: awaits the run delegate, then performs the completion
    /// bookkeeping (flight release, re-run or re-arm, the informational event)
    /// BEFORE the returned task completes. Consequence: a caller that awaits the
    /// task TriggerNow handed it observes a fully settled state — the poll timer
    /// is re-armed and any pending re-run has already started — which is also
    /// what makes the scheduling tests deterministic (no cross-thread settle
    /// race between the completion continuation and the test thread).
    /// </summary>
    private async Task<SyncCycleResult?> RunCycleAsync(CancellationToken token)
    {
        SyncCycleResult? result = null;
        var cancelled = false;
        try
        {
            result = await _cycle(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Dispose cancelled the cycle — normal control flow, not an error.
            cancelled = true;
        }
        catch (Exception ex)
        {
            // The engine maps its own failures into the result; anything that
            // escapes here (including a synchronously-throwing delegate) is a
            // contract violation — log, keep the cadence alive, report no result.
            _log?.LogError(ex, "The sync cycle delegate faulted.");
        }

        Task? rerun = null;
        var completed = false;
        lock (_gate)
        {
            _inFlight = null;
            _lastFinishedAt = _clock.GetUtcNow();
            if (!_disposed)
            {
                if (_rerunPending)
                {
                    _rerunPending = false;
                    rerun = StartCycle_Locked();
                }
                else
                {
                    ArmPollTimer_Locked();
                }
                completed = true;
            }
        }
        if (completed)
        {
            if (!cancelled)
                CycleCompleted?.Invoke(result);
            _ = rerun; // started under the lock; nobody awaits the rerun
        }
        return result;
    }

    /// <summary>Arms the poll timer with the current tier's jittered interval. Caller holds <see cref="_gate"/>.</summary>
    private void ArmPollTimer_Locked()
    {
        var baseInterval = _hidden ? _hiddenInterval : _visibleInterval;
        var jitter = 1.0 + (_jitter.NextDouble() * 2.0 - 1.0) * JitterFraction;
        var delay = TimeSpan.FromMilliseconds(baseInterval.TotalMilliseconds * jitter);
        _timer.Arm(delay);
    }
}
