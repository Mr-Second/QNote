namespace QNote.EdgeHide;

/// <summary>Edge-hide lifecycle. Normal → Docked → Hidden → Revealing → Docked.</summary>
public enum EdgeHideState
{
    /// <summary>Window is away from the monitor top edge.</summary>
    Normal = 0,

    /// <summary>Window top is docked at the monitor top, waiting for cursor-leave + delay.</summary>
    Docked = 1,

    /// <summary>Window is fully off-screen; the top hot zone reveals it.</summary>
    Hidden = 2,

    /// <summary>Reveal animation is running back to the docked position.</summary>
    Revealing = 3,
}

/// <summary>What the view-side controller must do after a machine step.</summary>
[Flags]
public enum EdgeHideEffect
{
    None = 0,

    /// <summary>Magnetically snap the window top to the monitor top (non-animated move).</summary>
    SnapToTop = 1,

    /// <summary>Run the hide animation toward <see cref="EdgeHideStep.TargetRect"/>.</summary>
    Hide = 2,

    /// <summary>Run the reveal animation toward <see cref="EdgeHideStep.TargetRect"/>.</summary>
    Reveal = 4,
}

/// <summary>A machine decision: the effect plus the target rect it applies to.</summary>
public readonly record struct EdgeHideStep(EdgeHideEffect Effect, RectPx TargetRect)
{
    public static EdgeHideStep None => default;
}

/// <summary>
/// Pure edge-hide state machine — no UI/Win32 dependencies, clock and geometry are
/// fed in by the view-side controller, so the whole judgment layer is headless-testable.
/// Port of the Qt <c>EdgeHideController</c> semantics (top edge only, cursor-position
/// trigger, no focus trigger) with the multi-monitor fix: every judgment is anchored
/// to the window's OWN monitor rect, not absolute y=0.
/// </summary>
public sealed class EdgeHideStateMachine
{
    private RectPx _window;
    private RectPx _monitor;
    private RectPx _dockRect;
    private RectPx _hiddenRect;
    private long _hideArmedAtMs = -1;

    public EdgeHideState State { get; private set; } = EdgeHideState.Normal;

    /// <summary>The docked geometry — restore target of a reveal.</summary>
    public RectPx DockRect => _dockRect;

    /// <summary>Expected position while hidden (watchdog reference); null in other states.</summary>
    public RectPx? HiddenRect => State == EdgeHideState.Hidden ? _hiddenRect : null;

    /// <summary>
    /// Feed a window geometry change (AppWindow.Changed). The controller must NOT feed
    /// positions produced by its own animation (IsAnimating shield) — only user/OS moves.
    /// </summary>
    public EdgeHideStep OnGeometryChanged(RectPx window, RectPx monitor)
    {
        _window = window;
        _monitor = monitor;

        switch (State)
        {
            case EdgeHideState.Normal:
                if (!EdgeHideMath.IsTopDocked(window.Y, monitor.Y))
                    return EdgeHideStep.None;
                State = EdgeHideState.Docked;
                _dockRect = EdgeHideMath.MagneticSnap(window, monitor);
                return SnapStep();

            case EdgeHideState.Docked:
                if (!EdgeHideMath.IsTopDocked(window.Y, monitor.Y))
                {
                    Disarm();
                    State = EdgeHideState.Normal;
                    return EdgeHideStep.None;
                }

                _dockRect = EdgeHideMath.MagneticSnap(window, monitor);
                return SnapStep();

            default:
                // Hidden / Revealing: position changes are our own animation frames
                // (suppressed by the controller) or external drift (watchdog's job).
                return EdgeHideStep.None;
        }
    }

    /// <summary>
    /// Feed one cursor poll. <paramref name="nowMs"/> is a monotonic clock
    /// (Environment.TickCount64); <paramref name="allMonitors"/> is only consulted when
    /// computing the off-screen target.
    /// </summary>
    public EdgeHideStep OnCursorTick(
        int cursorX, int cursorY, RectPx window, RectPx monitor,
        IReadOnlyList<RectPx> allMonitors, long nowMs)
    {
        _window = window;
        _monitor = monitor;

        switch (State)
        {
            case EdgeHideState.Docked:
                // Keep the restore target fresh (horizontal drift, resize) — but run it
                // through the magnetic snap so a pre-snap cursor tick can never un-snap
                // the docked Y (the snap Move lands asynchronously).
                _dockRect = EdgeHideMath.MagneticSnap(window, monitor);
                if (window.Contains(cursorX, cursorY))
                {
                    Disarm();
                    return EdgeHideStep.None;
                }

                if (_hideArmedAtMs < 0)
                {
                    _hideArmedAtMs = nowMs;
                    return EdgeHideStep.None;
                }

                if (nowMs - _hideArmedAtMs < EdgeHideMath.HideDelayMs)
                    return EdgeHideStep.None;

                Disarm();
                _hiddenRect = EdgeHideMath.ComputeHiddenRect(window, monitor, allMonitors);
                return new EdgeHideStep(EdgeHideEffect.Hide, _hiddenRect);

            case EdgeHideState.Hidden:
                // Hot zone anchored to the CURRENT monitor top (display changes re-resolve).
                return EdgeHideMath.IsInHotZone(cursorX, cursorY, _dockRect, monitor.Y)
                    ? RequestReveal()
                    : EdgeHideStep.None;

            default:
                return EdgeHideStep.None;
        }
    }

    /// <summary>Tray "show window" path: a hidden window slides back instead of staying off-screen.</summary>
    public EdgeHideStep RequestReveal()
    {
        if (State != EdgeHideState.Hidden)
            return EdgeHideStep.None;
        State = EdgeHideState.Revealing;
        return new EdgeHideStep(EdgeHideEffect.Reveal, _dockRect);
    }

    /// <summary>
    /// Manual (global hotkey) hide from ANY position (ADR D6): no dock requirement,
    /// but the restore target is NOT the floating position verbatim — it keeps the
    /// pre-hide X/size and snaps Y to the monitor top, so a revealed window always
    /// lands flush against the top edge (user acceptance, 2026-09-25: "不管是快捷键
    /// 还是自动隐藏，都应该贴着顶边显示"). The target is refreshed from the CURRENT
    /// window rect on every call, so a drag while the feature switch was off (no
    /// geometry feeds) can never restore to a stale X.
    /// </summary>
    public EdgeHideStep RequestHide(RectPx window, RectPx monitor, IReadOnlyList<RectPx> allMonitors)
    {
        if (State is not (EdgeHideState.Normal or EdgeHideState.Docked))
            return EdgeHideStep.None;

        _window = window;
        _monitor = monitor;
        _dockRect = window with { Y = monitor.Y };

        Disarm();
        _hiddenRect = EdgeHideMath.ComputeHiddenRect(window, monitor, allMonitors);
        return new EdgeHideStep(EdgeHideEffect.Hide, _hiddenRect);
    }

    /// <summary>Hide animation finished — the window is now fully off-screen.</summary>
    public void OnHideAnimationCompleted(RectPx hiddenRect)
    {
        if (State is EdgeHideState.Hidden or EdgeHideState.Revealing)
            return; // stale completion after a reset — ignore
        _hiddenRect = hiddenRect;
        _window = hiddenRect;
        State = EdgeHideState.Hidden;
    }

    /// <summary>
    /// Reveal animation finished — back at the restore position. Every reveal target
    /// is top-snapped (auto-hide: magnetically docked; manual hide: <see cref="RequestHide"/>
    /// snaps Y to the monitor top), so the window always lands Docked: the next cursor
    /// tick re-arms the hide delay if the cursor is outside (Qt parity). With the
    /// master switch off the controller simply stops polling — Docked is inert then,
    /// and no auto-hide can fire.
    /// </summary>
    public void OnRevealAnimationCompleted()
    {
        if (State != EdgeHideState.Revealing)
            return; // stale completion after a reset — ignore
        _window = _dockRect;
        State = EdgeHideState.Docked;
    }

    /// <summary>Feature toggled off (or fatal desync): drop all state, back to Normal.</summary>
    public void Reset()
    {
        Disarm();
        State = EdgeHideState.Normal;
    }

    private EdgeHideStep SnapStep() =>
        _dockRect.Y != _window.Y
            ? new EdgeHideStep(EdgeHideEffect.SnapToTop, _dockRect)
            : EdgeHideStep.None;

    private void Disarm() => _hideArmedAtMs = -1;
}
