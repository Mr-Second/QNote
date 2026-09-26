using Microsoft.Extensions.Logging.Abstractions;
using QNote.Memory;

namespace QNote.Tests;

/// <summary>
/// Tiered working-set trim (perf-size-optimization R2): immediate compacting GC on
/// hide, EmptyWorkingSet only after the deep-trim delay, show-before-delay cancels,
/// and every interop failure is swallowed. The clock and interop are faked, so the
/// whole controller runs headless (same split as EdgeHideStateMachine).
/// </summary>
public class WorkingSetTrimControllerTests
{
    private sealed class FakeInterop : IWorkingSetInterop
    {
        public int CollectCalls;
        public int EmptyCalls;
        public Exception? CollectThrows;
        public Exception? EmptyThrows;

        public void CompactingCollect()
        {
            CollectCalls++;
            if (CollectThrows is not null)
                throw CollectThrows;
        }

        public void EmptyWorkingSet()
        {
            EmptyCalls++;
            if (EmptyThrows is not null)
                throw EmptyThrows;
        }
    }

    /// <summary>Manual one-shot timer: Fire() only works while armed, then auto-disarms.</summary>
    private sealed class FakeTimer : IOneShotTimer
    {
        private bool _armed;

        public int ArmCalls;
        public int CancelCalls;
        public TimeSpan LastDelay;

        public event Action? Elapsed;

        public void Arm(TimeSpan delay)
        {
            ArmCalls++;
            LastDelay = delay;
            _armed = true;
        }

        public void Cancel()
        {
            CancelCalls++;
            _armed = false;
        }

        public void Fire()
        {
            if (!_armed)
                return;
            _armed = false;
            Elapsed?.Invoke();
        }

        public void Dispose() { }
    }

    private static (WorkingSetTrimController Controller, FakeInterop Interop, FakeTimer Timer) Create()
    {
        var interop = new FakeInterop();
        var timer = new FakeTimer();
        var controller = new WorkingSetTrimController(
            interop, timer, NullLogger<WorkingSetTrimController>.Instance);
        return (controller, interop, timer);
    }

    [Fact]
    public void Hide_RunsImmediateCompactingGc_ArmsDeepTrimTimer()
    {
        var (ctl, interop, timer) = Create();

        ctl.OnHidden();

        Assert.Equal(1, interop.CollectCalls);
        Assert.Equal(1, timer.ArmCalls);
        Assert.Equal(WorkingSetTrimController.DeepTrimDelay, timer.LastDelay);
        Assert.Equal(0, interop.EmptyCalls); // deep trim must NOT run immediately
    }

    [Fact]
    public void DeepTrim_FiresOnlyAfterTheDelay()
    {
        var (ctl, interop, timer) = Create();

        ctl.OnHidden();
        Assert.Equal(0, interop.EmptyCalls); // delay not elapsed yet

        timer.Fire();
        Assert.Equal(1, interop.EmptyCalls);
    }

    [Fact]
    public void ShowBeforeDelay_CancelsDeepTrim()
    {
        var (ctl, interop, timer) = Create();

        ctl.OnHidden();
        ctl.OnShown();
        timer.Fire(); // a late fire must be inert

        Assert.Equal(0, interop.EmptyCalls);
        Assert.Equal(1, timer.CancelCalls);
    }

    [Fact]
    public void DeepTrim_FiresOncePerHide()
    {
        var (ctl, interop, timer) = Create();

        ctl.OnHidden();
        timer.Fire();
        timer.Fire(); // a one-shot timer cannot refire; even if it did, hidden stays true

        Assert.Equal(1, interop.EmptyCalls);
    }

    [Fact]
    public void RepeatedHideShowCycles_ReArmEachCycle()
    {
        var (ctl, interop, timer) = Create();

        ctl.OnHidden();
        ctl.OnShown();
        ctl.OnHidden();
        timer.Fire();

        Assert.Equal(2, interop.CollectCalls);  // every hide runs the immediate GC
        Assert.Equal(2, timer.ArmCalls);        // every hide re-arms
        Assert.Equal(1, timer.CancelCalls);
        Assert.Equal(1, interop.EmptyCalls);    // only the second hide stayed hidden
    }

    [Fact]
    public void ShowWithoutPriorHide_IsHarmlessNoOp()
    {
        var (ctl, interop, timer) = Create();

        ctl.OnShown();
        timer.Fire();

        Assert.Equal(0, interop.CollectCalls);
        Assert.Equal(0, interop.EmptyCalls);
        Assert.Equal(1, timer.CancelCalls);
    }

    [Fact]
    public void CollectThrows_Swallowed_TimerStillArmed()
    {
        var (ctl, interop, timer) = Create();
        interop.CollectThrows = new InvalidOperationException("simulated GC failure");

        var ex = Record.Exception(ctl.OnHidden);

        Assert.Null(ex);
        Assert.Equal(1, timer.ArmCalls); // the deep-trim arm must survive the GC failure

        timer.Fire();
        Assert.Equal(1, interop.EmptyCalls);
    }

    [Fact]
    public void EmptyWorkingSetThrows_Swallowed()
    {
        var (ctl, interop, timer) = Create();
        interop.EmptyThrows = new System.ComponentModel.Win32Exception(5, "simulated access denied");

        ctl.OnHidden();
        var ex = Record.Exception(timer.Fire);

        Assert.Null(ex);
        Assert.Equal(1, interop.EmptyCalls);
    }
}
