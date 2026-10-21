using QNote.Memory;
using QNote.Models;
using QNote.Sync;

namespace QNote.Tests;

/// <summary>
/// The scheduling/limit layer battery (L1 step 4): <see cref="PushQueue"/>'s
/// debounce-with-max-latency coalescing and <see cref="ThrottleGate"/>'s
/// sliding-window budget + server-driven pause. Both are driven by injectable
/// test doubles (a manual one-shot timer and a manual clock) — no real waiting.
/// </summary>
public sealed class SyncSchedulingTests
{
    // ---------- PushQueue ----------

    [Fact]
    public void FirstEnqueue_ArmsTheDebounce()
    {
        var timer = new FakeOneShotTimer();
        using var queue = new PushQueue(timer, new FakeClock(T(0)), debounce: TimeSpan.FromSeconds(4));

        queue.Enqueue(SyncEntryKind.Note, "a");

        Assert.Equal(TimeSpan.FromSeconds(4), timer.ArmedDelay);
        Assert.Equal(1, queue.PendingCount);
    }

    [Fact]
    public void ReEnqueue_ResetsTheDebounce_ButCapsAtMaxLatency()
    {
        var clock = new FakeClock(T(0));
        var timer = new FakeOneShotTimer();
        using var queue = new PushQueue(timer, clock,
            debounce: TimeSpan.FromSeconds(4), maxLatency: TimeSpan.FromSeconds(30));

        queue.Enqueue(SyncEntryKind.Note, "a");          // due t0+4
        clock.AdvanceSeconds(3);
        queue.Enqueue(SyncEntryKind.Note, "a");          // debounce reset: due t0+7 (cap t0+30 not hit)
        Assert.Equal(TimeSpan.FromSeconds(4), timer.ArmedDelay);

        clock.AdvanceSeconds(25);                        // t0+28
        queue.Enqueue(SyncEntryKind.Note, "a");          // min(t0+32, t0+30) → 2 s left
        Assert.Equal(TimeSpan.FromSeconds(2), timer.ArmedDelay);

        clock.AdvanceSeconds(27);                        // t0+55, past the cap
        queue.Enqueue(SyncEntryKind.Note, "b");          // overdue → fire immediately
        Assert.Equal(TimeSpan.Zero, timer.ArmedDelay);
    }

    [Fact]
    public void Elapsed_FiresSyncDue_WithTheDedupedBatch_AndClearsPending()
    {
        var timer = new FakeOneShotTimer();
        using var queue = new PushQueue(timer, new FakeClock(T(0)));
        IReadOnlyList<(SyncEntryKind, string)>? batch = null;
        queue.SyncDue += b => batch = b;

        queue.Enqueue(SyncEntryKind.Note, "a");
        queue.Enqueue(SyncEntryKind.Note, "a");          // same entry coalesces
        queue.Enqueue(SyncEntryKind.Category, "c");
        timer.Fire();

        Assert.NotNull(batch);
        Assert.Equal(2, batch!.Count);
        Assert.Contains((SyncEntryKind.Note, "a"), batch);
        Assert.Contains((SyncEntryKind.Category, "c"), batch);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void Elapsed_WithEmptyPending_DoesNotFire()
    {
        var timer = new FakeOneShotTimer();
        using var queue = new PushQueue(timer, new FakeClock(T(0)));
        var fired = false;
        queue.SyncDue += _ => fired = true;

        timer.Fire();

        Assert.False(fired);
    }

    [Fact]
    public void Dispose_StopsFiring()
    {
        var timer = new FakeOneShotTimer();
        var queue = new PushQueue(timer, new FakeClock(T(0)));
        var fired = false;
        queue.SyncDue += _ => fired = true;
        queue.Dispose();

        queue.Enqueue(SyncEntryKind.Note, "a");
        timer.Fire();

        Assert.False(fired);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void Enqueue_RejectsEmptyUuid()
    {
        using var queue = new PushQueue(new FakeOneShotTimer(), new FakeClock(T(0)));

        Assert.Throws<ArgumentException>(() => queue.Enqueue(SyncEntryKind.Note, ""));
    }

    // ---------- ThrottleGate ----------

    [Fact]
    public void Admits_UpToLimit_ThenDenies_WithTheOldestExit()
    {
        var clock = new FakeClock(T(0));
        var gate = new ThrottleGate(3, TimeSpan.FromMinutes(10), clock);

        Assert.True(gate.TryAcquire(out _));
        Assert.True(gate.TryAcquire(out _));
        Assert.True(gate.TryAcquire(out _));
        Assert.Equal(3, gate.UsedInWindow);

        Assert.False(gate.TryAcquire(out var retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(10), retryAfter); // the oldest slot leaves at t0+10min
    }

    [Fact]
    public void SlidingWindow_FreesSlots_AsTimePasses()
    {
        var clock = new FakeClock(T(0));
        var gate = new ThrottleGate(2, TimeSpan.FromMinutes(10), clock);

        Assert.True(gate.TryAcquire(out _));
        Assert.True(gate.TryAcquire(out _));
        Assert.False(gate.TryAcquire(out _));

        clock.AdvanceMinutes(10);
        // Both admissions were stamped t0, so both leave the 10-min window at t0+10min.
        Assert.True(gate.TryAcquire(out _));
        Assert.Equal(1, gate.UsedInWindow);
    }

    [Fact]
    public void Defer_PausesGlobally_EvenWithFreeSlots()
    {
        var clock = new FakeClock(T(0));
        var gate = new ThrottleGate(5, TimeSpan.FromMinutes(10), clock);
        Assert.True(gate.TryAcquire(out _));

        gate.Defer(TimeSpan.FromMinutes(5));
        Assert.True(gate.IsPaused);
        Assert.False(gate.TryAcquire(out var retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(5), retryAfter);

        clock.AdvanceMinutes(5);
        Assert.False(gate.IsPaused);
        Assert.True(gate.TryAcquire(out _));
    }

    [Fact]
    public void PausedAndWindowBlocked_TakesTheLaterExit()
    {
        var clock = new FakeClock(T(0));
        var gate = new ThrottleGate(1, TimeSpan.FromMinutes(10), clock);
        Assert.True(gate.TryAcquire(out _)); // window full until t0+10min

        gate.Defer(TimeSpan.FromMinutes(2)); // pause ends earlier — both must clear

        Assert.False(gate.TryAcquire(out var retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(10), retryAfter);
    }

    [Fact]
    public void RepeatedDefer_ExtendsThePause()
    {
        var clock = new FakeClock(T(0));
        var gate = new ThrottleGate(5, TimeSpan.FromMinutes(10), clock);

        gate.Defer(TimeSpan.FromMinutes(1));
        clock.AdvanceMinutes(0.5);
        gate.Defer(TimeSpan.FromMinutes(1)); // extends to t0+1.5min

        clock.AdvanceMinutes(0.75); // t0+1.25min
        Assert.True(gate.IsPaused);
        clock.AdvanceMinutes(0.3);  // t0+1.55min
        Assert.False(gate.IsPaused);
    }

    [Fact]
    public void InvalidConstruction_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrottleGate(0, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrottleGate(5, TimeSpan.Zero));
    }

    // ---------- fixtures ----------

    private static DateTimeOffset T(double minutes) =>
        DateTimeOffset.UnixEpoch + TimeSpan.FromMinutes(minutes);

    /// <summary>Manual clock so no test ever waits on a real timer.</summary>
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public void AdvanceSeconds(double seconds) => Advance(TimeSpan.FromSeconds(seconds));

        public void AdvanceMinutes(double minutes) => Advance(TimeSpan.FromMinutes(minutes));

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>Manual <see cref="IOneShotTimer"/>: records the armed delay, fires on demand.</summary>
    private sealed class FakeOneShotTimer : IOneShotTimer
    {
        public TimeSpan? ArmedDelay { get; private set; }

        public event Action? Elapsed;

        public void Arm(TimeSpan delay)
        {
            ArmedDelay = delay;
        }

        public void Cancel() => ArmedDelay = null;

        public void Fire() => Elapsed?.Invoke();

        public void Dispose()
        {
        }
    }
}
