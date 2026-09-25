using QNote.EdgeHide;

namespace QNote.Tests;

/// <summary>
/// Pure geometry helpers: dock threshold, hot zone, off-screen target computation
/// (multi-monitor safe, above the -32000 clamp), easing, watchdog drift, poll backoff.
/// </summary>
public class EdgeHideMathTests
{
    private static readonly RectPx Primary = new(0, 0, 1920, 1080);
    private static readonly RectPx Above = new(0, -1080, 1920, 1080);
    private static readonly RectPx LeftOf = new(-1920, 0, 1920, 1080);

    // ---------- IsTopDocked ----------

    [Theory]
    [InlineData(0, 0, true)]     // exactly at the top
    [InlineData(10, 0, true)]    // at the Qt threshold (<= 10 px)
    [InlineData(11, 0, false)]   // past the threshold
    [InlineData(-5, 0, true)]    // slightly above the top still counts (Qt parity)
    [InlineData(-10, 0, true)]   // overshoot at the threshold
    [InlineData(-11, 0, false)]  // too far above the top — not a dock
    [InlineData(-32000, 0, false)] // the minimized/restored-off-screen position
    [InlineData(300, 0, false)]
    public void IsTopDocked_Threshold(int windowTop, int monitorTop, bool expected) =>
        Assert.Equal(expected, EdgeHideMath.IsTopDocked(windowTop, monitorTop));

    [Fact]
    public void IsTopDocked_AnchorsToOwnMonitor_NotAbsoluteZero()
    {
        // Secondary monitor stacked above the primary: its top is y=-1080.
        Assert.True(EdgeHideMath.IsTopDocked(-1075, Above.Y));
        Assert.False(EdgeHideMath.IsTopDocked(0, Above.Y)); // primary top is NOT the secondary's top
    }

    // ---------- IsInHotZone ----------

    [Fact]
    public void IsInHotZone_TopStripClippedToWindowXRange()
    {
        var window = new RectPx(400, 0, 500, 620);

        Assert.True(EdgeHideMath.IsInHotZone(500, 0, window, 0));
        Assert.True(EdgeHideMath.IsInHotZone(899, 5, window, 0));
        Assert.False(EdgeHideMath.IsInHotZone(500, 6, window, 0));   // below the strip
        Assert.False(EdgeHideMath.IsInHotZone(399, 3, window, 0));   // left of the window
        Assert.False(EdgeHideMath.IsInHotZone(900, 3, window, 0));   // right of the window
        Assert.False(EdgeHideMath.IsInHotZone(500, -1, window, 0));  // above the monitor top
    }

    [Fact]
    public void IsInHotZone_AnchoredToMonitorTop_MultiMonitor()
    {
        var window = new RectPx(400, -1080, 500, 620); // docked on the monitor above

        Assert.True(EdgeHideMath.IsInHotZone(500, -1080, window, Above.Y));
        Assert.False(EdgeHideMath.IsInHotZone(500, 0, window, Above.Y)); // primary top must not reveal it
    }

    // ---------- ComputeHiddenRect ----------

    [Fact]
    public void ComputeHiddenRect_SlidesStraightUp_AboveOwnMonitor_WithMargin()
    {
        var window = new RectPx(400, 0, 500, 620);

        var hidden = EdgeHideMath.ComputeHiddenRect(window, Primary, [Primary]);

        Assert.Equal(window.X, hidden.X);
        Assert.Equal(window.Width, hidden.Width);
        Assert.Equal(window.Height, hidden.Height);
        Assert.Equal(Primary.Y - 620 - EdgeHideMath.OffScreenMarginPx, hidden.Y);
        Assert.False(hidden.IntersectsWith(Primary));
    }

    [Fact]
    public void ComputeHiddenRect_PushesFurther_WhenAnotherMonitorIsAbove()
    {
        var window = new RectPx(400, 0, 500, 620);
        var monitors = new[] { Primary, Above };

        var hidden = EdgeHideMath.ComputeHiddenRect(window, Primary, monitors);

        foreach (var m in monitors)
            Assert.False(hidden.IntersectsWith(m), $"hidden rect {hidden} must not intersect {m}");
    }

    [Fact]
    public void ComputeHiddenRect_SideMonitor_DoesNotAffectVerticalSlide()
    {
        var window = new RectPx(400, 0, 500, 620);

        var hidden = EdgeHideMath.ComputeHiddenRect(window, Primary, [Primary, LeftOf]);

        Assert.Equal(Primary.Y - 620 - EdgeHideMath.OffScreenMarginPx, hidden.Y);
    }

    [Fact]
    public void ComputeHiddenRect_NeverBelowClamp()
    {
        // Pathological stack: many monitors above → the loop would run away without the clamp.
        var window = new RectPx(400, 0, 500, 620);
        var monitors = Enumerable.Range(1, 100)
            .Select(i => new RectPx(0, -1080 * i, 1920, 1080))
            .Append(Primary)
            .ToArray();

        var hidden = EdgeHideMath.ComputeHiddenRect(window, Primary, monitors);

        Assert.True(hidden.Y >= EdgeHideMath.MinOffScreenY);
        Assert.True(hidden.Y > -32768, "must stay above the OS minimized-coordinate clamp");
    }

    [Fact]
    public void ComputeHiddenRect_AnchoredToOwnMonitor_MultiMonitor()
    {
        var window = new RectPx(400, -1080, 500, 620); // docked on the monitor above

        var hidden = EdgeHideMath.ComputeHiddenRect(window, Above, [Primary, Above]);

        Assert.Equal(Above.Y - 620 - EdgeHideMath.OffScreenMarginPx, hidden.Y);
        Assert.False(hidden.IntersectsWith(Above));
        Assert.False(hidden.IntersectsWith(Primary));
    }

    // ---------- easing / lerp ----------

    [Fact]
    public void EaseInOutQuad_EndpointsAndMidpoint()
    {
        Assert.Equal(0.0, EdgeHideMath.EaseInOutQuad(0.0), precision: 6);
        Assert.Equal(1.0, EdgeHideMath.EaseInOutQuad(1.0), precision: 6);
        Assert.Equal(0.5, EdgeHideMath.EaseInOutQuad(0.5), precision: 6);
    }

    [Theory]
    [InlineData(0, 100, 0.0, 0)]
    [InlineData(0, 100, 1.0, 100)]
    [InlineData(-720, 0, 0.5, -360)]
    public void Lerp_Interpolates(int from, int to, double t, int expected) =>
        Assert.Equal(expected, EdgeHideMath.Lerp(from, to, t));

    // ---------- watchdog ----------

    [Theory]
    [InlineData(0, 0, 5, 0, false)]   // within tolerance
    [InlineData(0, 0, 11, 0, true)]   // x drift beyond 10 px
    [InlineData(0, 0, 0, -11, true)]  // y drift beyond 10 px
    public void HasDrifted_Tolerance(int ex, int ey, int ax, int ay, bool expected) =>
        Assert.Equal(expected, EdgeHideMath.HasDrifted(
            new RectPx(ex, ey, 500, 620), new RectPx(ax, ay, 500, 620)));

    // ---------- poll backoff ----------

    [Theory]
    [InlineData(EdgeHideState.Docked, 900, 0, EdgeHideMath.PollNearMs)]   // engaged → fast
    [InlineData(EdgeHideState.Hidden, 900, 0, EdgeHideMath.PollNearMs)]   // engaged → fast
    [InlineData(EdgeHideState.Normal, 50, 0, EdgeHideMath.PollNearMs)]    // cursor near the top
    [InlineData(EdgeHideState.Normal, -50, 0, EdgeHideMath.PollNearMs)]   // cursor just above (screen on top)
    [InlineData(EdgeHideState.Normal, 900, 0, EdgeHideMath.PollFarMs)]    // idle, far away
    public void PollIntervalMs_AdaptiveBackoff(EdgeHideState state, int cursorY, int monitorTop, int expected) =>
        Assert.Equal(expected, EdgeHideMath.PollIntervalMs(state, cursorY, monitorTop));
}
