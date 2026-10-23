using QNote.Memory;
using QNote.Sync;

namespace QNote.Tests;

/// <summary>
/// The tiered polling loop battery (L2 composition): tier switching with
/// jittered re-arms, opportunistic triggers with the gap throttle, single-flight
/// with a pending re-run, and the guards (before-start, double-start, dispose).
/// Manual timer + clock + a per-call gated cycle delegate — no real waiting.
/// Settling is deterministic BY CONSTRUCTION: the task the scheduler hands out
/// completes only AFTER the completion bookkeeping (re-arm / re-run), so a plain
/// <c>await</c> of that task lands past every state transition it drives.
/// </summary>
public sealed class SyncPollingSchedulerTests
{
    [Fact]
    public void Start_ArmsTheVisibleTier_WithJitter()
    {
        var timer = new FakeOneShotTimer();
        using var scheduler = Scheduler(timer, jitter: 0.5); // jitter factor = 1.0 exactly

        scheduler.Start();

        Assert.True(scheduler.IsStarted);
        Assert.Equal(TimeSpan.FromMinutes(2), timer.ArmedDelay);
    }

    [Fact]
    public void Start_WithInitialHiddenTier_ArmsTheHiddenInterval()
    {
        var timer = new FakeOneShotTimer();
        using var scheduler = Scheduler(timer, jitter: 0.5);

        scheduler.Start(initiallyHidden: true);

        Assert.Equal(TimeSpan.FromMinutes(10), timer.ArmedDelay);
    }

    [Fact]
    public void Jitter_StaysInsideTheHalfWidth()
    {
        var timer = new FakeOneShotTimer();
        using var scheduler = Scheduler(timer, jitter: null); // real Random

        scheduler.Start();

        var lower = TimeSpan.FromMinutes(2) * (1 - SyncPollingScheduler.JitterFraction);
        var upper = TimeSpan.FromMinutes(2) * (1 + SyncPollingScheduler.JitterFraction);
        Assert.InRange(timer.ArmedDelay!.Value, lower, upper);
    }

    [Fact]
    public void OnHidden_SwitchesTheTierToTheHiddenInterval()
    {
        var timer = new FakeOneShotTimer();
        using var scheduler = Scheduler(timer, jitter: 0.5);
        scheduler.Start();

        scheduler.OnHidden();

        Assert.Equal(TimeSpan.FromMinutes(10), timer.ArmedDelay);
    }

    [Fact]
    public void OnShown_SwitchesBackToTheVisibleInterval()
    {
        var timer = new FakeOneShotTimer();
        using var scheduler = Scheduler(timer, jitter: 0.5);
        scheduler.Start();
        scheduler.OnHidden();

        scheduler.OnShown();

        Assert.Equal(TimeSpan.FromMinutes(2), timer.ArmedDelay);
    }

    [Fact]
    public async Task OnShown_FiresAThrottledOpportunisticCycle()
    {
        var timer = new FakeOneShotTimer();
        var cycles = new ManualCycle();
        using var scheduler = Scheduler(timer, cycle: cycles.Delegate, jitter: 0.5);
        scheduler.Start();

        scheduler.OnShown();

        Assert.Equal(1, cycles.Calls);
        var cycle = scheduler.CurrentCycle;
        cycles.CompleteNext(Result());
        Assert.Equal(3, (await cycle!)!.Uploaded);
    }

    [Fact]
    public async Task TimerElapsed_RunsTheCycle_AndRearmsAfterItFinishes()
    {
        var timer = new FakeOneShotTimer();
        var cycles = new ManualCycle();
        using var scheduler = Scheduler(timer, cycle: cycles.Delegate, jitter: 0.5);
        scheduler.Start();
        timer.Cancel();

        timer.Fire();
        Assert.Equal(1, cycles.Calls);

        var cycle = scheduler.CurrentCycle!;
        cycles.CompleteNext(Result());
        await cycle;
        Assert.Equal(TimeSpan.FromMinutes(2), timer.ArmedDelay); // visible tier re-arm
    }

    [Fact]
    public async Task TriggerNow_RunsTheCycle_AndReturnsItsTask()
    {
        var timer = new FakeOneShotTimer();
        var cycles = new ManualCycle();
        using var scheduler = Scheduler(timer, cycle: cycles.Delegate, jitter: 0.5);
        scheduler.Start();
        timer.Cancel();

        var cycle = scheduler.TriggerNow();
        Assert.NotNull(cycle);
        Assert.Equal(1, cycles.Calls);

        cycles.CompleteNext(Result());
        var result = await cycle!;

        Assert.NotNull(result);
        Assert.Equal(3, result!.Uploaded);
    }

    [Fact]
    public void TriggerNow_BeforeStart_IsANoOp()
    {
        using var scheduler = Scheduler(new FakeOneShotTimer(), jitter: 0.5);

        Assert.Null(scheduler.TriggerNow());
        Assert.False(scheduler.IsStarted);
    }

    [Fact]
    public void DoubleStart_Throws()
    {
        using var scheduler = Scheduler(new FakeOneShotTimer(), jitter: 0.5);
        scheduler.Start();

        Assert.Throws<InvalidOperationException>(() => scheduler.Start());
    }

    [Fact]
    public async Task TriggerNow_WhileACycleRuns_ReusesTheFlight_AndSchedulesOneRerun()
    {
        var timer = new FakeOneShotTimer();
        var cycles = new ManualCycle();
        using var scheduler = Scheduler(timer, cycle: cycles.Delegate, jitter: 0.5);
        scheduler.Start();
        timer.Cancel();

        var first = scheduler.TriggerNow();
        var second = scheduler.TriggerNow();
        Assert.Same(first, second);

        cycles.CompleteNext(Result());
        await first!;

        // The pending trigger became exactly one re-run (never a storm).
        var rerun = scheduler.CurrentCycle;
        Assert.NotNull(rerun);
        Assert.NotSame(first, rerun);
        Assert.Equal(2, cycles.Calls);

        cycles.CompleteNext(null);
        Assert.Null(await rerun!);
        Assert.Null(scheduler.CurrentCycle);
        Assert.Equal(2, cycles.Calls);
    }

    [Fact]
    public async Task OpportunisticTrigger_WithinTheGap_IsDenied_ButBypassRuns()
    {
        var timer = new FakeOneShotTimer();
        var clock = new FakeClock(T(0));
        var cycles = new ManualCycle();
        using var scheduler = Scheduler(timer, cycle: cycles.Delegate, clock: clock, jitter: 0.5);
        scheduler.Start();
        timer.Cancel();

        var manual = scheduler.TriggerNow(bypassThrottle: true);
        cycles.CompleteNext(Result());
        await manual!;
        Assert.Equal(1, cycles.Calls);

        // 10 s after the finish — inside the 30 s gap: denied.
        clock.AdvanceSeconds(10);
        Assert.Null(scheduler.TriggerNow());

        // Past the gap: allowed again.
        clock.AdvanceSeconds(21);
        var later = scheduler.TriggerNow();
        Assert.NotNull(later);
        cycles.CompleteNext(null);
        await later!;
    }

    [Fact]
    public async Task ManualTrigger_BypassesTheGap()
    {
        var timer = new FakeOneShotTimer();
        var clock = new FakeClock(T(0));
        var cycles = new ManualCycle();
        using var scheduler = Scheduler(timer, cycle: cycles.Delegate, clock: clock, jitter: 0.5);
        scheduler.Start();
        timer.Cancel();

        var first = scheduler.TriggerNow(bypassThrottle: true);
        cycles.CompleteNext(Result());
        await first!;

        clock.AdvanceSeconds(1); // well inside the gap
        var second = scheduler.TriggerNow(bypassThrottle: true);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        cycles.CompleteNext(null);
        Assert.Null(await second!);
    }

    [Fact]
    public async Task SkippedCycle_ReturnsNullResult_AndKeepsTheCadence()
    {
        var timer = new FakeOneShotTimer();
        var cycles = new ManualCycle();
        using var scheduler = Scheduler(timer, cycle: cycles.Delegate, jitter: 0.5);
        scheduler.Start();
        timer.Cancel();

        var cycle = scheduler.TriggerNow();
        cycles.CompleteNext(null); // composition gate: nobody signed in
        Assert.Null(await cycle!);
        Assert.Equal(TimeSpan.FromMinutes(2), timer.ArmedDelay); // cadence survives
    }

    [Fact]
    public async Task SignInAfterSynchronousSkip_ManualSyncRunsAFreshCycle()
    {
        var signedIn = false;
        var calls = 0;
        using var scheduler = Scheduler(new FakeOneShotTimer(), cycle: _ =>
        {
            calls++;
            return Task.FromResult(signedIn ? Result() : null);
        }, jitter: 0.5);
        scheduler.Start();

        Assert.Null(await scheduler.TriggerNow(bypassThrottle: true)!);
        Assert.Null(scheduler.CurrentCycle);

        signedIn = true;
        var result = await scheduler.TriggerNow(bypassThrottle: true)!;

        Assert.Equal(2, calls);
        Assert.Equal(3, result!.Uploaded);
        Assert.Null(scheduler.CurrentCycle);
    }

    [Fact]
    public async Task SynchronousFailure_DoesNotBlockTheNextManualSync()
    {
        var calls = 0;
        using var scheduler = Scheduler(new FakeOneShotTimer(), cycle: _ =>
        {
            if (++calls == 1)
                throw new InvalidOperationException("A transient failure.");
            return Task.FromResult<SyncCycleResult?>(Result());
        }, jitter: 0.5);
        scheduler.Start();

        Assert.Null(await scheduler.TriggerNow(bypassThrottle: true)!);
        var result = await scheduler.TriggerNow(bypassThrottle: true)!;

        Assert.Equal(2, calls);
        Assert.Equal(3, result!.Uploaded);
        Assert.Null(scheduler.CurrentCycle);
    }

    [Fact]
    public async Task SynchronousPendingRerun_ReleasesTheFlight()
    {
        var firstCycle = new TaskCompletionSource<SyncCycleResult?>();
        var calls = 0;
        using var scheduler = Scheduler(new FakeOneShotTimer(), cycle: _ =>
            ++calls == 1 ? firstCycle.Task : Task.FromResult<SyncCycleResult?>(Result()), jitter: 0.5);
        scheduler.Start();

        var first = scheduler.TriggerNow(bypassThrottle: true)!;
        Assert.Same(first, scheduler.TriggerNow(bypassThrottle: true));
        firstCycle.SetResult(null);
        await first;

        Assert.Equal(2, calls);
        Assert.Null(scheduler.CurrentCycle);
        Assert.NotNull(await scheduler.TriggerNow(bypassThrottle: true)!);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void SynchronouslyThrowingDelegate_IsContained_AndRearms()
    {
        var timer = new FakeOneShotTimer();
        var calls = 0;
        using var scheduler = new SyncPollingScheduler(
            _ => { calls++; throw new InvalidOperationException("bad composition"); },
            timer,
            jitterRandom: new FixedRandom(0.5));
        scheduler.Start();
        timer.Cancel();

        timer.Fire(); // the throw lands inside the wrapped task, never on the timer thread

        Assert.Equal(1, calls);
        Assert.Equal(TimeSpan.FromMinutes(2), timer.ArmedDelay); // cadence survives
    }

    [Fact]
    public async Task Dispose_CancelsTheInFlightCycle()
    {
        var timer = new FakeOneShotTimer();
        var cycles = new ManualCycle(observesCancellation: true);
        var scheduler = Scheduler(timer, cycle: cycles.Delegate, jitter: 0.5);
        scheduler.Start();
        timer.Cancel();

        var cycle = scheduler.TriggerNow();
        scheduler.Dispose();

        Assert.True(cycles.LastToken.IsCancellationRequested);
        // The wrapped task reports the cancellation as "no result" instead of
        // throwing — an awaiter never sees an exception for a disposed scheduler.
        Assert.Null(await cycle!);
        Assert.False(scheduler.IsStarted);
    }

    [Fact]
    public void Dispose_StopsFurtherCycles()
    {
        var timer = new FakeOneShotTimer();
        var cycles = new ManualCycle();
        var scheduler = Scheduler(timer, cycle: cycles.Delegate, jitter: 0.5);
        scheduler.Start();
        scheduler.Dispose();

        timer.Fire();
        Assert.Equal(0, cycles.Calls);
    }

    [Fact]
    public async Task TierSwitch_WhileInFlight_TakesEffectOnTheRearm()
    {
        var timer = new FakeOneShotTimer();
        var cycles = new ManualCycle();
        using var scheduler = Scheduler(timer, cycle: cycles.Delegate, jitter: 0.5);
        scheduler.Start();
        timer.Cancel();

        var cycle = scheduler.TriggerNow(bypassThrottle: true);
        scheduler.OnHidden(); // tier switch lands while the cycle runs
        cycles.CompleteNext(Result());
        await cycle!;

        Assert.Equal(TimeSpan.FromMinutes(10), timer.ArmedDelay);
    }

    // ---------- helpers ----------

    private static SyncCycleResult Result() => new()
    {
        Uploaded = 3,
        Downloaded = 0,
        DeletedRemote = 0,
        TombstonesApplied = 0,
        ConflictCopies = 0,
        Errors = [],
    };

    private static DateTimeOffset T(double minutes) =>
        DateTimeOffset.UnixEpoch + TimeSpan.FromMinutes(minutes);

    /// <summary>Scheduler factory with every knob pinned to production defaults except the jitter source.</summary>
    private static SyncPollingScheduler Scheduler(
        FakeOneShotTimer timer,
        Func<CancellationToken, Task<SyncCycleResult?>>? cycle = null,
        FakeClock? clock = null,
        double? jitter = null) => new(
            cycle ?? new ManualCycle().Delegate,
            timer,
            clock ?? new FakeClock(T(0)),
            jitterRandom: jitter is { } v ? new FixedRandom(v) : null);

    /// <summary>A Random stub so the jitter factor is exactly <c>2·value − 1</c>.</summary>
    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }

    /// <summary>Manual clock so no test ever waits on a real timer.</summary>
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void AdvanceSeconds(double seconds) => _now += TimeSpan.FromSeconds(seconds);

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>Manual <see cref="IOneShotTimer"/>: records the armed delay, fires on demand.</summary>
    private sealed class FakeOneShotTimer : IOneShotTimer
    {
        public TimeSpan? ArmedDelay { get; private set; }

        public event Action? Elapsed;

        public void Arm(TimeSpan delay) => ArmedDelay = delay;

        public void Cancel() => ArmedDelay = null;

        public void Fire() => Elapsed?.Invoke();

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A gated cycle delegate: each call registers a fresh TCS the test completes
    /// explicitly via <see cref="CompleteNext"/>. Only the Dispose test needs
    /// cancellation observation — nobody ever completes that gate, so without
    /// <c>WaitAsync</c> the wrapped task would await it forever.
    /// </summary>
    private sealed class ManualCycle
    {
        private readonly bool _observesCancellation;
        private readonly Queue<TaskCompletionSource<SyncCycleResult?>> _gates = new();

        public ManualCycle(bool observesCancellation = false) => _observesCancellation = observesCancellation;

        public int Calls { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public Func<CancellationToken, Task<SyncCycleResult?>> Delegate => ct =>
        {
            Calls++;
            LastToken = ct;
            var gate = new TaskCompletionSource<SyncCycleResult?>();
            _gates.Enqueue(gate);
            return _observesCancellation ? gate.Task.WaitAsync(ct) : gate.Task;
        };

        public void CompleteNext(SyncCycleResult? result) => _gates.Dequeue().SetResult(result);
    }
}
