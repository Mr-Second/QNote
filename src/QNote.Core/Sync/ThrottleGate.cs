namespace QNote.Sync;

/// <summary>
/// The sync target's request budget (framework survey + PRD: 坚果云 free tier =
/// 600 requests / 30 min, paid = 1500): a sliding-window admission counter plus
/// a server-driven global pause. Polly's breaker was rejected because it keys on
/// FAILURE RATE over ~100 throughputs — a sync cycle makes a handful of requests;
/// this gate counts admissions instead.
///
/// The L2 driver acquires one slot per HTTP request (before it is sent) and maps
/// a provider's 429/503 <c>Retry-After</c> into <see cref="Defer"/> — a hard
/// global pause that overrides the window. Headless-testable via
/// <see cref="TimeProvider"/>.
/// </summary>
public sealed class ThrottleGate
{
    private readonly int _requestLimit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _admissions = new();
    private DateTimeOffset? _pausedUntil;

    public ThrottleGate(int requestLimit, TimeSpan window, TimeProvider? clock = null)
    {
        if (requestLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestLimit), requestLimit, "The limit must be positive.");
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), window, "The window must be positive.");

        _requestLimit = requestLimit;
        _window = window;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Requests admitted inside the current window (diagnostics).</summary>
    public int UsedInWindow
    {
        get
        {
            lock (_gate)
            {
                PruneWindow(_clock.GetUtcNow());
                return _admissions.Count;
            }
        }
    }

    /// <summary>True while a <see cref="Defer"/> pause is in effect.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_gate)
                return _pausedUntil is { } until && until > _clock.GetUtcNow();
        }
    }

    /// <summary>
    /// Attempts to consume one request slot. When denied,
    /// <paramref name="retryAfter"/> is how long until acquisition can succeed:
    /// the later of the pause exit and the oldest admission leaving the window
    /// (both constraints must clear).
    /// </summary>
    public bool TryAcquire(out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            PruneWindow(now);

            TimeSpan? wait = null;
            if (_pausedUntil is { } until && until > now)
                wait = until - now;
            if (_admissions.Count >= _requestLimit)
            {
                var oldestExit = _window - (now - _admissions.Peek());
                wait = wait is { } w && w > oldestExit ? w : oldestExit;
            }

            if (wait is { } w2)
            {
                retryAfter = w2;
                return false;
            }

            _admissions.Enqueue(now);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    /// <summary>Hard global pause (a provider's Retry-After); repeated calls extend it.</summary>
    public void Defer(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return;
        lock (_gate)
        {
            var until = _clock.GetUtcNow() + duration;
            _pausedUntil = _pausedUntil is { } existing && existing > until ? existing : until;
        }
    }

    private void PruneWindow(DateTimeOffset now)
    {
        while (_admissions.Count > 0 && now - _admissions.Peek() >= _window)
            _admissions.Dequeue();
    }
}
