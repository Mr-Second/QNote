using QNote.Memory;
using QNote.Models;

namespace QNote.Sync;

/// <summary>
/// The instant-upload trigger (PRD decision 7): every local mutation is
/// enqueued and a sync cycle fires after the debounce window closes — the
/// 3–5 s quiet period that keeps a burst of edits from producing a burst of
/// cycles. Headless-testable: the one-shot timer (<see cref="IOneShotTimer"/>,
/// reused from the memory-floor work) and the clock (<see cref="TimeProvider"/>)
/// are injectable.
///
/// Debounce + a hard max-latency cap: a pure debounce (reset on every enqueue)
/// starves under continuous typing — the upload would never fire — so the due
/// time is <c>min(last enqueue + debounce, first pending + max latency)</c>.
/// Elapsed drains the pending set and hands the batch to <see cref="SyncDue"/>
/// (on a thread-pool thread); the batch is informational (the engine runs full
/// cycles), useful for logs and future targeted pushes.
/// </summary>
public sealed class PushQueue : IDisposable
{
    /// <summary>PRD decision 7 pins 3–5 s; the middle of the band is the default.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Hard cap on how long a pending mutation can be held by continuous
    /// enqueueing (implementation detail of the debounce: starvation protection,
    /// 30 s keeps a typing session's upload within a human-relevant window).
    /// </summary>
    public static readonly TimeSpan DefaultMaxLatency = TimeSpan.FromSeconds(30);

    private readonly IOneShotTimer _timer;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<(SyncEntryKind Kind, string Uuid), DateTimeOffset> _pending = new();
    private DateTimeOffset? _firstPendingAt;
    private bool _disposed;

    public PushQueue(
        IOneShotTimer? timer = null,
        TimeProvider? clock = null,
        TimeSpan? debounce = null,
        TimeSpan? maxLatency = null)
    {
        if (debounce is { } d && d <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounce), d, "The debounce must be positive.");
        if (maxLatency is { } m && m <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxLatency), m, "The max latency must be positive.");

        _timer = timer ?? new ThreadingOneShotTimer();
        _clock = clock ?? TimeProvider.System;
        Debounce = debounce ?? DefaultDebounce;
        MaxLatency = maxLatency ?? DefaultMaxLatency;
        _timer.Elapsed += OnTimerElapsed;
    }

    public TimeSpan Debounce { get; }

    public TimeSpan MaxLatency { get; }

    /// <summary>Distinct entries currently waiting (diagnostics; the batch itself travels on <see cref="SyncDue"/>).</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
                return _pending.Count;
        }
    }

    /// <summary>
    /// Raised when the debounce window closes, carrying the drained (deduped)
    /// pending set. Fires on a thread-pool thread — the consumer must marshal
    /// itself if it touches UI state.
    /// </summary>
    public event Action<IReadOnlyList<(SyncEntryKind Kind, string Uuid)>>? SyncDue;

    /// <summary>
    /// Records a local mutation and (re)arms the debounce. Multiple enqueues of
    /// the same entry within the window coalesce; the due time never slips past
    /// the first pending entry's arrival + <see cref="MaxLatency"/>.
    /// </summary>
    public void Enqueue(SyncEntryKind kind, string uuid)
    {
        if (string.IsNullOrEmpty(uuid))
            throw new ArgumentException("The entry uuid must not be empty.", nameof(uuid));

        lock (_gate)
        {
            if (_disposed)
                return;
            var now = _clock.GetUtcNow();
            _firstPendingAt ??= now;
            _pending[(kind, uuid)] = now;
            var dueAt = Minimum(now + Debounce, _firstPendingAt.Value + MaxLatency);
            var delay = dueAt - now;
            _timer.Arm(delay <= TimeSpan.Zero ? TimeSpan.Zero : delay);
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
        _timer.Dispose();
    }

    private void OnTimerElapsed()
    {
        (SyncEntryKind Kind, string Uuid)[] batch;
        lock (_gate)
        {
            // A spurious or post-dispose fire (a Cancel race) must not emit an
            // empty batch — the next real Enqueue re-arms anyway.
            if (_disposed || _pending.Count == 0)
                return;
            batch = [.. _pending.Keys];
            _pending.Clear();
            _firstPendingAt = null;
        }
        SyncDue?.Invoke(batch);
    }

    private static DateTimeOffset Minimum(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
}
