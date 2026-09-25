using QNote.EdgeHide;

namespace QNote.Tests;

/// <summary>
/// Edge-hide state machine transitions: Normal → Docked (magnetic snap) → Hidden
/// (cursor-out + 800 ms) → Revealing (hot zone / tray) → Docked. Geometry and the
/// clock are fed in, so everything runs headless.
/// </summary>
public class EdgeHideStateMachineTests
{
    private static readonly RectPx Primary = new(0, 0, 1920, 1080);
    private static readonly RectPx Above = new(0, -1080, 1920, 1080);
    private static readonly RectPx[] Monitors = [Primary];

    private static readonly RectPx Floating = new(400, 300, 500, 620);
    private static readonly RectPx DockedAtTop = new(400, 0, 500, 620);

    private const int CursorInsideX = 500;
    private const int CursorInsideY = 300;
    private const int CursorOutsideX = 100;
    private const int CursorOutsideY = 500;

    // ---------- docking ----------

    [Fact]
    public void Normal_WindowMovedToTop_EntersDocked_WithSnapEffect()
    {
        var m = new EdgeHideStateMachine();

        var step = m.OnGeometryChanged(new RectPx(400, 7, 500, 620), Primary);

        Assert.Equal(EdgeHideState.Docked, m.State);
        Assert.Equal(EdgeHideEffect.SnapToTop, step.Effect);
        Assert.Equal(DockedAtTop, step.TargetRect); // snapped to the monitor top
    }

    [Fact]
    public void Normal_WindowExactlyAtTop_DocksWithoutSnap()
    {
        var m = new EdgeHideStateMachine();

        var step = m.OnGeometryChanged(DockedAtTop, Primary);

        Assert.Equal(EdgeHideState.Docked, m.State);
        Assert.Equal(EdgeHideEffect.None, step.Effect); // already at the top, nothing to snap
    }

    [Fact]
    public void Normal_WindowAwayFromTop_StaysNormal()
    {
        var m = new EdgeHideStateMachine();

        var step = m.OnGeometryChanged(Floating, Primary);

        Assert.Equal(EdgeHideState.Normal, m.State);
        Assert.Equal(EdgeHideEffect.None, step.Effect);
    }

    [Fact]
    public void Docked_WindowDraggedAway_ReturnsToNormal()
    {
        var m = new EdgeHideStateMachine();
        m.OnGeometryChanged(DockedAtTop, Primary);

        var step = m.OnGeometryChanged(Floating, Primary);

        Assert.Equal(EdgeHideState.Normal, m.State);
        Assert.Equal(EdgeHideEffect.None, step.Effect);
    }

    [Fact]
    public void Docked_DraggedAway_CancelsArmedHide()
    {
        var m = new EdgeHideStateMachine();
        m.OnGeometryChanged(DockedAtTop, Primary);
        m.OnCursorTick(CursorOutsideX, CursorOutsideY, DockedAtTop, Primary, Monitors, nowMs: 1_000); // armed

        m.OnGeometryChanged(Floating, Primary);
        var step = m.OnCursorTick(CursorOutsideX, CursorOutsideY, Floating, Primary, Monitors, nowMs: 5_000);

        Assert.Equal(EdgeHideState.Normal, m.State);
        Assert.Equal(EdgeHideEffect.None, step.Effect);
    }

    [Fact]
    public void Docked_CursorTickBeforeSnapLands_DoesNotUnsnapRestoreTarget()
    {
        var m = new EdgeHideStateMachine();
        m.OnGeometryChanged(new RectPx(400, 7, 500, 620), Primary); // docks + snap to y=0

        // The snap Move lands asynchronously — a tick can still see the pre-snap y=7.
        m.OnCursorTick(CursorOutsideX, CursorOutsideY, new RectPx(400, 7, 500, 620), Primary, Monitors, 1_000);

        Assert.Equal(0, m.DockRect.Y); // restore target stays magnetically snapped
    }

    // ---------- hide delay ----------

    [Fact]
    public void Docked_CursorOutside_HidesOnlyAfterDelay()
    {
        var m = new EdgeHideStateMachine();
        m.OnGeometryChanged(DockedAtTop, Primary);

        // First tick arms the timer.
        Assert.Equal(EdgeHideEffect.None,
            m.OnCursorTick(CursorOutsideX, CursorOutsideY, DockedAtTop, Primary, Monitors, 1_000).Effect);
        // Too early.
        Assert.Equal(EdgeHideEffect.None,
            m.OnCursorTick(CursorOutsideX, CursorOutsideY, DockedAtTop, Primary, Monitors, 1_000 + EdgeHideMath.HideDelayMs - 1).Effect);

        // Delay elapsed → hide, straight above the monitor.
        var step = m.OnCursorTick(CursorOutsideX, CursorOutsideY, DockedAtTop, Primary, Monitors, 1_000 + EdgeHideMath.HideDelayMs);
        Assert.Equal(EdgeHideEffect.Hide, step.Effect);
        Assert.Equal(DockedAtTop.Y - DockedAtTop.Height - EdgeHideMath.OffScreenMarginPx, step.TargetRect.Y);
        Assert.Equal(EdgeHideState.Docked, m.State); // Hidden only when the animation completes
    }

    [Fact]
    public void Docked_CursorReturnsInside_DisarmsHide()
    {
        var m = new EdgeHideStateMachine();
        m.OnGeometryChanged(DockedAtTop, Primary);
        m.OnCursorTick(CursorOutsideX, CursorOutsideY, DockedAtTop, Primary, Monitors, 1_000);

        m.OnCursorTick(CursorInsideX, CursorInsideY, DockedAtTop, Primary, Monitors, 1_500);
        var step = m.OnCursorTick(CursorOutsideX, CursorOutsideY, DockedAtTop, Primary, Monitors, 1_000 + EdgeHideMath.HideDelayMs);

        Assert.Equal(EdgeHideEffect.None, step.Effect); // re-armed at 1500, not elapsed yet
    }

    [Fact]
    public void Hidden_OnlyAfterAnimationCompletion()
    {
        var m = new EdgeHideStateMachine();
        HideFully(m);

        Assert.Equal(EdgeHideState.Hidden, m.State);
        Assert.NotNull(m.HiddenRect);
    }

    // ---------- reveal ----------

    [Fact]
    public void Hidden_CursorEntersHotZone_Reveals()
    {
        var m = new EdgeHideStateMachine();
        var hidden = HideFully(m);

        var step = m.OnCursorTick(CursorInsideX, 2, hidden, Primary, Monitors, 10_000);

        Assert.Equal(EdgeHideEffect.Reveal, step.Effect);
        Assert.Equal(DockedAtTop, step.TargetRect);
        Assert.Equal(EdgeHideState.Revealing, m.State);
    }

    [Fact]
    public void Hidden_CursorAtTopButOutsideWindowXRange_StaysHidden()
    {
        var m = new EdgeHideStateMachine();
        var hidden = HideFully(m);

        var step = m.OnCursorTick(50, 2, hidden, Primary, Monitors, 10_000);

        Assert.Equal(EdgeHideEffect.None, step.Effect);
        Assert.Equal(EdgeHideState.Hidden, m.State);
    }

    [Fact]
    public void Reveal_Completion_ReturnsToDocked_AndRearmsOnCursorOutside()
    {
        var m = new EdgeHideStateMachine();
        var hidden = HideFully(m);
        m.OnCursorTick(CursorInsideX, 2, hidden, Primary, Monitors, 10_000);

        m.OnRevealAnimationCompleted();

        Assert.Equal(EdgeHideState.Docked, m.State);
        // Cursor outside again → the hide delay re-arms (Qt parity).
        Assert.Equal(EdgeHideEffect.None,
            m.OnCursorTick(CursorOutsideX, CursorOutsideY, DockedAtTop, Primary, Monitors, 11_000).Effect);
        Assert.Equal(EdgeHideEffect.Hide,
            m.OnCursorTick(CursorOutsideX, CursorOutsideY, DockedAtTop, Primary, Monitors, 11_000 + EdgeHideMath.HideDelayMs).Effect);
    }

    [Fact]
    public void RequestReveal_OnlyFromHidden()
    {
        var m = new EdgeHideStateMachine();

        Assert.Equal(EdgeHideEffect.None, m.RequestReveal().Effect); // Normal: no-op

        m.OnGeometryChanged(DockedAtTop, Primary);
        Assert.Equal(EdgeHideEffect.None, m.RequestReveal().Effect); // Docked: no-op

        HideFully(m);
        Assert.Equal(EdgeHideEffect.Reveal, m.RequestReveal().Effect); // tray path
        Assert.Equal(EdgeHideState.Revealing, m.State);
    }

    // ---------- manual (hotkey) hide — ADR D6 ----------

    [Fact]
    public void RequestHide_FromFloatingPosition_RestoreTargetKeepsX_SnapsYToMonitorTop()
    {
        var m = new EdgeHideStateMachine();

        var step = m.RequestHide(Floating, Primary, Monitors);

        Assert.Equal(EdgeHideEffect.Hide, step.Effect);
        // Restore target = pre-hide X/size, Y snapped to the monitor top — a reveal
        // always lands flush at the top edge, never back at the floating gap.
        Assert.Equal(Floating with { Y = Primary.Y }, m.DockRect);
        // Hidden Y anchors to the MONITOR top, not the pre-hide window Y.
        Assert.Equal(Primary.Y - Floating.Height - EdgeHideMath.OffScreenMarginPx, step.TargetRect.Y);
        Assert.Equal(EdgeHideState.Normal, m.State); // Hidden only when the animation completes
    }

    [Fact]
    public void RequestHide_FromDocked_RefreshesRestoreTargetFromCurrentRect()
    {
        var m = new EdgeHideStateMachine();
        m.OnGeometryChanged(DockedAtTop, Primary); // docks at x=400

        // Window drifted horizontally while docked (e.g. moved with the feature
        // switch off, so no geometry feeds refreshed the machine).
        var drifted = DockedAtTop with { X = 700 };
        var hide = m.RequestHide(drifted, Primary, Monitors);

        Assert.Equal(EdgeHideEffect.Hide, hide.Effect);
        Assert.Equal(drifted, m.DockRect); // fresh X, not the stale dock rect
    }

    [Fact]
    public void ManualHide_RevealCompletion_LandsDocked_AndRearmsAutoHide()
    {
        var m = new EdgeHideStateMachine();
        var hide = m.RequestHide(Floating, Primary, Monitors);
        m.OnHideAnimationCompleted(hide.TargetRect);

        var reveal = m.RequestReveal();
        Assert.Equal(Floating with { Y = Primary.Y }, reveal.TargetRect);
        m.OnRevealAnimationCompleted();

        // The top-snapped restore target means the window IS docked now — cursor
        // outside re-arms the auto-hide delay like any docked window (Qt parity).
        Assert.Equal(EdgeHideState.Docked, m.State);
        var restored = Floating with { Y = Primary.Y };
        Assert.Equal(EdgeHideEffect.None,
            m.OnCursorTick(CursorOutsideX, CursorOutsideY, restored, Primary, Monitors, 20_000).Effect);
        Assert.Equal(EdgeHideEffect.Hide,
            m.OnCursorTick(CursorOutsideX, CursorOutsideY, restored, Primary, Monitors, 20_000 + EdgeHideMath.HideDelayMs).Effect);
    }

    [Fact]
    public void RequestHide_FromDocked_KeepsSnappedDockRect_AndReturnsToDocked()
    {
        var m = new EdgeHideStateMachine();
        m.OnGeometryChanged(DockedAtTop, Primary);

        var hide = m.RequestHide(DockedAtTop, Primary, Monitors);
        Assert.Equal(EdgeHideEffect.Hide, hide.Effect);
        Assert.Equal(DockedAtTop, m.DockRect);

        m.OnHideAnimationCompleted(hide.TargetRect);
        m.RequestReveal();
        m.OnRevealAnimationCompleted();

        Assert.Equal(EdgeHideState.Docked, m.State); // was docked before the manual hide
    }

    [Fact]
    public void RequestHide_WhenHiddenOrRevealing_IsIgnored()
    {
        var m = new EdgeHideStateMachine();
        var hide = m.RequestHide(Floating, Primary, Monitors);
        m.OnHideAnimationCompleted(hide.TargetRect);

        Assert.Equal(EdgeHideEffect.None, m.RequestHide(Floating, Primary, Monitors).Effect);

        m.RequestReveal(); // → Revealing
        Assert.Equal(EdgeHideEffect.None, m.RequestHide(Floating, Primary, Monitors).Effect);
    }

    [Fact]
    public void ManualHide_HotZoneRevealAnchorsToPreHideXRange()
    {
        var m = new EdgeHideStateMachine();
        var hide = m.RequestHide(Floating, Primary, Monitors); // pre-hide x-range 400..900
        var hidden = hide.TargetRect;
        m.OnHideAnimationCompleted(hidden);

        // Cursor at the top edge but outside the pre-hide x-range: stays hidden.
        Assert.Equal(EdgeHideEffect.None,
            m.OnCursorTick(50, 2, hidden, Primary, Monitors, 10_000).Effect);
        // Inside the pre-hide x-range: reveals, top-snapped at the pre-hide X.
        var reveal = m.OnCursorTick(500, 2, hidden, Primary, Monitors, 10_001);
        Assert.Equal(EdgeHideEffect.Reveal, reveal.Effect);
        Assert.Equal(Floating with { Y = Primary.Y }, reveal.TargetRect);
    }

    [Fact]
    public void Reset_AfterManualHide_StaleRevealCompletionIsSafe()
    {
        var m = new EdgeHideStateMachine();
        var hide = m.RequestHide(Floating, Primary, Monitors);
        m.OnHideAnimationCompleted(hide.TargetRect);

        m.Reset();
        m.OnRevealAnimationCompleted(); // stale completion must not misbehave

        Assert.Equal(EdgeHideState.Normal, m.State);
    }

    // ---------- reset / multi-monitor ----------

    [Fact]
    public void Reset_FromAnyState_ReturnsToNormal()
    {
        var m = new EdgeHideStateMachine();
        HideFully(m);

        m.Reset();

        Assert.Equal(EdgeHideState.Normal, m.State);
        Assert.Null(m.HiddenRect);
    }

    [Fact]
    public void Hidden_IgnoresGeometryChanges()
    {
        var m = new EdgeHideStateMachine();
        var hidden = HideFully(m);

        var step = m.OnGeometryChanged(hidden with { X = hidden.X + 50 }, Primary);

        Assert.Equal(EdgeHideEffect.None, step.Effect);
        Assert.Equal(EdgeHideState.Hidden, m.State);
    }

    [Fact]
    public void MultiMonitor_DockAndHideAnchorToWindowsOwnMonitor()
    {
        var m = new EdgeHideStateMachine();
        var monitors = new[] { Primary, Above };
        var dockedOnAbove = new RectPx(400, -1075, 500, 620);

        var dock = m.OnGeometryChanged(dockedOnAbove, Above);
        Assert.Equal(EdgeHideState.Docked, m.State);
        Assert.Equal(Above.Y, dock.TargetRect.Y); // snaps to the UPPER monitor's top

        var docked = dockedOnAbove with { Y = Above.Y };
        m.OnCursorTick(CursorOutsideX, CursorOutsideY, docked, Above, monitors, 1_000);
        var hide = m.OnCursorTick(CursorOutsideX, CursorOutsideY, docked, Above, monitors, 1_000 + EdgeHideMath.HideDelayMs);

        Assert.Equal(EdgeHideEffect.Hide, hide.Effect);
        Assert.True(hide.TargetRect.Bottom <= Above.Y, "slides above the monitor it is docked on");
        Assert.False(hide.TargetRect.IntersectsWith(Above));
        Assert.False(hide.TargetRect.IntersectsWith(Primary));
    }

    // ---------- helpers ----------

    /// <summary>Drive the machine: dock → arm → delay → hide → animation completed.</summary>
    private static RectPx HideFully(EdgeHideStateMachine m)
    {
        m.OnGeometryChanged(DockedAtTop, Primary);
        m.OnCursorTick(CursorOutsideX, CursorOutsideY, DockedAtTop, Primary, Monitors, 1_000);
        var hide = m.OnCursorTick(CursorOutsideX, CursorOutsideY, DockedAtTop, Primary, Monitors, 1_000 + EdgeHideMath.HideDelayMs);
        Assert.Equal(EdgeHideEffect.Hide, hide.Effect);
        m.OnHideAnimationCompleted(hide.TargetRect);
        return hide.TargetRect;
    }
}
