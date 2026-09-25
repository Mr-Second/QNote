namespace QNote.EdgeHide;

/// <summary>
/// Pure geometry / easing helpers for edge-hide. Constants mirror the Qt build's
/// <c>EdgeHideController.cpp</c> (800 ms hide delay, 10 px dock threshold, 6 px hot
/// zone); the off-screen rules come from the WTQ analysis (never intersect any
/// monitor, never go below the -32000 coordinate clamp).
/// </summary>
public static class EdgeHideMath
{
    /// <summary>Delay between the cursor leaving a docked window and the hide animation.</summary>
    public const long HideDelayMs = 800;

    /// <summary>Window top within this distance of the monitor top counts as docked.</summary>
    public const int TopDockThresholdPx = 10;

    /// <summary>Height of the reveal hot zone at the monitor top while hidden.</summary>
    public const int HotZoneHeightPx = 6;

    /// <summary>Extra gap above the monitor so no sub-pixel sliver stays visible.</summary>
    public const int OffScreenMarginPx = 100;

    /// <summary>
    /// Lowest allowed off-screen Y. Windows clamps coordinates under -32768 to the
    /// minimized position (-32000), which makes restore logic bounce (WTQ comment).
    /// </summary>
    public const int MinOffScreenY = -30000;

    /// <summary>Watchdog: a hidden window drifting more than this is snapped back.</summary>
    public const int DriftTolerancePx = 10;

    /// <summary>Poll interval while docked/hidden or with the cursor near the top edge.</summary>
    public const int PollNearMs = 125;

    /// <summary>Poll interval while idle with the cursor far from the top edge.</summary>
    public const int PollFarMs = 500;

    /// <summary>Cursor within this vertical band of the monitor top counts as "near".</summary>
    public const int NearEdgeBandPx = 100;

    /// <summary>Qt parity: InQuad/OutQuad share one symmetric ease-in-out curve.</summary>
    public static double EaseInOutQuad(double t) =>
        t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;

    public static int Lerp(int from, int to, double t) =>
        (int)Math.Round(from + (to - from) * t);

    /// <summary>
    /// Top-edge dock judgment anchored to the window's OWN monitor (fixes the Qt
    /// absolute-y=0 bug). This is a DISTANCE check: a window far above the monitor
    /// top (restored off-screen geometry, the minimized -32000 position) must not
    /// read as docked — only a slight overshoot above the top counts (Qt parity).
    /// </summary>
    public static bool IsTopDocked(int windowTop, int monitorTop)
    {
        var delta = windowTop - monitorTop;
        return delta >= -TopDockThresholdPx && delta <= TopDockThresholdPx;
    }

    /// <summary>
    /// Qt parity: while 0 &lt;= y-offset &lt;= threshold the top magnetically snaps to the
    /// monitor top; a window pushed ABOVE the top (negative offset) docks but is not
    /// snapped (the Qt build never snapped negative y either).
    /// </summary>
    public static RectPx MagneticSnap(RectPx window, RectPx monitor) =>
        window.Y > monitor.Y && window.Y - monitor.Y <= TopDockThresholdPx
            ? window with { Y = monitor.Y }
            : window;

    /// <summary>
    /// Hot zone = the top <see cref="HotZoneHeightPx"/> px of the window's monitor,
    /// horizontally clipped to the window's x-range (so a cursor at the top edge but
    /// away from the window does not reveal it).
    /// </summary>
    public static bool IsInHotZone(int cursorX, int cursorY, RectPx window, int monitorTop) =>
        cursorY >= monitorTop && cursorY < monitorTop + HotZoneHeightPx &&
        cursorX >= window.X && cursorX < window.Right;

    /// <summary>
    /// Hidden position: straight above the window's own monitor, far enough that the
    /// rect intersects NO monitor (a screen above would otherwise show it), and never
    /// below <see cref="MinOffScreenY"/>.
    /// </summary>
    public static RectPx ComputeHiddenRect(RectPx window, RectPx monitor, IReadOnlyList<RectPx> allMonitors)
    {
        var y = monitor.Y - window.Height - OffScreenMarginPx;
        var candidate = window with { Y = y };
        while (y > MinOffScreenY && IntersectsAny(candidate, allMonitors))
        {
            y -= window.Height + OffScreenMarginPx;
            candidate = window with { Y = y };
        }

        if (y < MinOffScreenY)
            candidate = window with { Y = MinOffScreenY };
        return candidate;
    }

    /// <summary>Watchdog drift check (external move of a hidden window: Snap, monitor unplug, shell restart).</summary>
    public static bool HasDrifted(RectPx expected, RectPx actual) =>
        Math.Abs(expected.X - actual.X) > DriftTolerancePx ||
        Math.Abs(expected.Y - actual.Y) > DriftTolerancePx;

    /// <summary>Adaptive poll interval: fast while engaged/near the edge, backed off otherwise.</summary>
    public static int PollIntervalMs(EdgeHideState state, int cursorY, int monitorTop)
    {
        if (state != EdgeHideState.Normal)
            return PollNearMs;
        var delta = cursorY - monitorTop;
        return delta >= -NearEdgeBandPx && delta <= NearEdgeBandPx ? PollNearMs : PollFarMs;
    }

    private static bool IntersectsAny(RectPx rect, IReadOnlyList<RectPx> monitors)
    {
        foreach (var m in monitors)
        {
            if (rect.IntersectsWith(m))
                return true;
        }

        return false;
    }
}
