using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using QNote.EdgeHide;
using QNote.Models;
using Windows.Graphics;

namespace QNote.Controls;

/// <summary>
/// View-side edge-hide controller (port of the Qt EdgeHideController + Main.qml
/// animation pair). Owns everything Win32/WinUI: the adaptive <c>GetCursorPos</c>
/// poll, the frame-by-frame slide animation (WtqTween pattern: time-driven
/// interpolation + frame-cost-adaptive sleep + forced final placement), topmost /
/// switcher chrome while hidden, and the position watchdog. All judgment lives in
/// the headless <see cref="EdgeHideStateMachine"/> (Core).
///
/// Hard rules honored here:
/// - Animation frames move via SetWindowPos(SWP_NOZORDER|SWP_NOACTIVATE|SWP_NOSIZE)
///   so the slide never steals focus or disturbs the z-order; single non-animated
///   moves use AppWindow.Move (dodges the MoveAndResize mixed-DPI bug).
/// - While animating, self-induced AppWindow.Changed events are shielded and the
///   geometry-persistence path must stay off (<see cref="SuppressGeometryPersistence"/>)
///   so an off-screen/animating position is never persisted as the user's geometry.
/// </summary>
public sealed class EdgeHideController
{
    private const int AnimationDurationMs = 200; // Qt parity (InQuad/OutQuad 200 ms)
    private const double FrameMs = 15.0;

    private const uint SwpNosize = 0x0001;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;

    private readonly AppWindow _appWindow;
    private readonly nint _hwnd;
    private readonly ILogger<EdgeHideController> _log;
    private readonly DispatcherQueueTimer _pollTimer;
    private readonly EdgeHideStateMachine _machine = new();

    private bool _enabled;
    private bool _animating;
    private bool _hideTaskbarIcon;
    private bool _normalAlwaysOnTop;

    public EdgeHideController(Window window, ILogger<EdgeHideController> log)
    {
        _log = log;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _appWindow = window.AppWindow;

        _appWindow.Changed += OnAppWindowChanged;

        _pollTimer = window.DispatcherQueue.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(EdgeHideMath.PollFarMs);
        _pollTimer.Tick += OnPollTick;
    }

    /// <summary>
    /// True while hidden or animating — MainWindow's remember-geometry path must not
    /// persist these positions (ADR D3: only normal-state geometry is stored).
    /// </summary>
    public bool SuppressGeometryPersistence =>
        _animating || _machine.State is EdgeHideState.Hidden or EdgeHideState.Revealing;

    /// <summary>True while fully hidden off-screen — the forced-topmost chrome must survive settings re-applies.</summary>
    public bool IsHidden => _machine.State == EdgeHideState.Hidden;

    /// <summary>Live-applies the edge-hide related settings (called from MainWindow.Apply).</summary>
    public void ApplySettings(AppSettings s)
    {
        _hideTaskbarIcon = s.HideTaskbarIconOnEdgeHide;
        _normalAlwaysOnTop = s.AlwaysOnTop;

        if (s.EdgeHideEnabled == _enabled)
            return;
        _enabled = s.EdgeHideEnabled;

        if (_enabled)
        {
            _pollTimer.Start();
            // The window may already sit at the top edge — evaluate immediately.
            try
            {
                EvaluateGeometry();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Edge-hide initial evaluation failed.");
            }
        }
        else
        {
            _pollTimer.Stop();
            // Toggled off while hidden/animating: slide back immediately (ADR D5).
            _ = DisableAndRevealAsync();
        }
    }

    /// <summary>
    /// Tray "show window" / double-click: an edge-hidden window slides back instead of
    /// being restored at its off-screen position. Fire-and-forget; the caller proceeds
    /// with ShowWindow(SW_RESTORE) + SetForegroundWindow right away.
    /// </summary>
    public void RevealFromTray()
    {
        if (_animating)
            return;
        var step = _machine.RequestReveal();
        if (step.Effect == EdgeHideEffect.Reveal)
            _ = RevealAsync(step.TargetRect);
    }

    /// <summary>
    /// Global hotkey toggle (ADR D6): a visible window at ANY position slides out
    /// (the restore target keeps the pre-hide X and snaps Y to the monitor top, so
    /// the slide-back always lands flush at the top edge); a hidden window slides
    /// back. Works even when the auto edge-hide switch is off — the poll timer then
    /// runs only while manually hidden, so the top hot zone still reveals.
    /// </summary>
    public void ToggleHide()
    {
        if (_animating)
            return;

        if (_machine.State == EdgeHideState.Hidden)
        {
            RevealFromTray(); // same reveal pipeline as the tray path
            return;
        }

        if (_machine.State is not (EdgeHideState.Normal or EdgeHideState.Docked))
            return; // mid-reveal
        if (!IsWindowInteractive() || !TryGetContext(out var window, out var monitor))
            return; // minimized / hidden to tray / maximized: nothing sensible to hide

        var step = _machine.RequestHide(window, monitor, AllMonitors());
        if (step.Effect != EdgeHideEffect.Hide)
            return;

        if (!_enabled)
            _pollTimer.Start(); // hot-zone reveal + watchdog while manually hidden
        Execute(step);
    }

    // ---------- event feed ----------

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!_enabled || _animating || (!args.DidPositionChange && !args.DidSizeChange))
            return;

        try
        {
            EvaluateGeometry();
        }
        catch (Exception ex)
        {
            // An event handler must never crash the app; the next poll tick self-heals.
            _log.LogError(ex, "Edge-hide geometry evaluation failed.");
        }
    }

    private void OnPollTick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            if (_animating)
                return;
            // Master switch off: only a manually hidden window keeps polling
            // (hot-zone reveal + watchdog); everything else idles.
            if (!_enabled && _machine.State != EdgeHideState.Hidden)
                return;
            if (!IsWindowInteractive())
            {
                // Window hidden to tray / minimized: idle at the slow rate until it returns.
                _pollTimer.Interval = TimeSpan.FromMilliseconds(EdgeHideMath.PollFarMs);
                return;
            }

            // Feed geometry every tick, not only on AppWindow.Changed: a window
            // restored at the top edge on launch never fires Changed after it
            // becomes visible, so docking would otherwise never engage.
            EvaluateGeometry();

            if (!GetCursorPos(out POINT pt) || !TryGetContext(out var window, out var monitor))
                return;

            // AllMonitors() is a P/Invoke + list alloc — only the Docked → Hide
            // transition consults it, so skip the enumeration in other states.
            var allMonitors = _machine.State == EdgeHideState.Docked
                ? AllMonitors()
                : (IReadOnlyList<RectPx>)[];

            Execute(_machine.OnCursorTick(pt.X, pt.Y, window, monitor, allMonitors, Environment.TickCount64));

            // Watchdog (WTQ pattern): an external force moved the hidden window → snap back.
            if (_machine.HiddenRect is { } expected && EdgeHideMath.HasDrifted(expected, window))
            {
                _log.LogWarning("Edge-hidden window drifted to ({X},{Y}); snapping back to ({EX},{EY}).",
                    window.X, window.Y, expected.X, expected.Y);
                SetWindowPos(_hwnd, 0, expected.X, expected.Y, 0, 0, SwpNosize | SwpNozorder | SwpNoactivate);
            }

            // Adaptive backoff: engaged/near-edge polls fast, idle polls slow (north star).
            _pollTimer.Interval = TimeSpan.FromMilliseconds(
                EdgeHideMath.PollIntervalMs(_machine.State, pt.Y, monitor.Y));
        }
        catch (Exception ex)
        {
            // A polling tick must never crash the app; the next tick self-heals.
            _log.LogError(ex, "Edge-hide poll tick failed.");
        }
    }

    /// <summary>Feed the current window/monitor geometry into the machine and run its decision.</summary>
    private void EvaluateGeometry()
    {
        if (!IsWindowInteractive() || !TryGetContext(out var window, out var monitor))
            return;

        Execute(_machine.OnGeometryChanged(window, monitor));
    }

    private void Execute(EdgeHideStep step)
    {
        switch (step.Effect)
        {
            case EdgeHideEffect.SnapToTop:
                // Single non-animated move → AppWindow.Move (not MoveAndResize: mixed-DPI bug).
                _appWindow.Move(new PointInt32(step.TargetRect.X, step.TargetRect.Y));
                break;
            case EdgeHideEffect.Hide:
                _ = HideAsync(step.TargetRect);
                break;
            case EdgeHideEffect.Reveal:
                _ = RevealAsync(step.TargetRect);
                break;
        }
    }

    // ---------- hide / reveal ----------

    private async Task HideAsync(RectPx target)
    {
        _animating = true;
        try
        {
            await AnimateYAsync(target.Y);
            _machine.OnHideAnimationCompleted(target);
            // Qt parity: chrome flips AFTER the slide finishes.
            if (_appWindow.Presenter is OverlappedPresenter presenter)
                presenter.IsAlwaysOnTop = true;
            if (_hideTaskbarIcon)
                _appWindow.IsShownInSwitchers = false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Edge-hide animation failed.");
            _machine.Reset();
        }
        finally
        {
            _animating = false;
        }
    }

    private async Task RevealAsync(RectPx target)
    {
        _animating = true;
        try
        {
            // Qt parity: chrome restores BEFORE the slide back.
            RestoreChrome();
            await AnimateYAsync(target.Y);
            _machine.OnRevealAnimationCompleted();
            // A manual hide with the master switch off ran the timer only for the
            // hot zone — the reveal is done, go idle again (north star). The machine
            // now sits in Docked, which is inert without the poll timer, so no
            // auto-hide can fire while the switch is off.
            if (!_enabled)
                _pollTimer.Stop();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Edge-reveal animation failed.");
            _machine.Reset();
        }
        finally
        {
            _animating = false;
        }
    }

    /// <summary>
    /// Feature toggled off: wait out any in-flight animation, slide back if hidden,
    /// then drop to Normal (ADR D5).
    /// </summary>
    private async Task DisableAndRevealAsync()
    {
        try
        {
            while (_animating)
                await Task.Delay(30);

            if (_machine.State == EdgeHideState.Hidden)
            {
                RestoreChrome();
                _animating = true;
                try
                {
                    await AnimateYAsync(_machine.DockRect.Y);
                }
                finally
                {
                    _animating = false;
                }
            }

            _machine.Reset();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Edge-hide disable/force-reveal failed.");
            _machine.Reset();
        }
    }

    /// <summary>
    /// WtqTween pattern: time-driven interpolation (frame-rate independent), sleep
    /// adjusted by the frame's own cost, and a forced exact final placement. Runs on
    /// the UI thread (await Task.Delay resumes on the DispatcherQueue context).
    /// </summary>
    private async Task AnimateYAsync(int toY)
    {
        var from = CurrentRect();
        if (from.Y == toY)
            return;

        var swTotal = Stopwatch.StartNew();
        var swFrame = Stopwatch.StartNew();
        SetY(from.X, from.Y); // first frame lands immediately
        while (swTotal.ElapsedMilliseconds < AnimationDurationMs)
        {
            swFrame.Restart();
            var t = Math.Min(1.0, swTotal.Elapsed.TotalMilliseconds / AnimationDurationMs);
            SetY(from.X, EdgeHideMath.Lerp(from.Y, toY, EdgeHideMath.EaseInOutQuad(t)));
            var waitMs = FrameMs - swFrame.ElapsedMilliseconds;
            if (waitMs > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(waitMs));
        }

        SetY(from.X, toY); // forced final placement — kills float drift
    }

    private void SetY(int x, int y) =>
        SetWindowPos(_hwnd, 0, x, y, 0, 0, SwpNosize | SwpNozorder | SwpNoactivate);

    private void RestoreChrome()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = _normalAlwaysOnTop;
        if (!_appWindow.IsShownInSwitchers)
            _appWindow.IsShownInSwitchers = true;
    }

    // ---------- context gathering ----------

    /// <summary>Only a visible, restored (not minimized/maximized) window participates.</summary>
    private bool IsWindowInteractive() =>
        _appWindow.IsVisible &&
        _appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored };

    private RectPx CurrentRect()
    {
        var pos = _appWindow.Position;
        var size = _appWindow.Size;
        return new RectPx(pos.X, pos.Y, size.Width, size.Height);
    }

    private bool TryGetContext(out RectPx window, out RectPx monitor)
    {
        window = CurrentRect();
        monitor = default;
        // OuterBounds (full monitor, not work area): the Qt build docked at the real
        // screen top even with a top-docked taskbar, and the hot zone says "屏幕顶".
        var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
        if (area is null)
            return false;
        var b = area.OuterBounds;
        monitor = new RectPx(b.X, b.Y, b.Width, b.Height);
        return true;
    }

    private static IReadOnlyList<RectPx> AllMonitors()
    {
        var monitors = new List<RectPx>();
        EnumDisplayMonitors(0, 0, Callback, 0);
        return monitors;

        bool Callback(nint hMonitor, nint hdc, nint lprc, nint data)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                var r = info.Monitor;
                monitors.Add(new RectPx(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
            }

            return true;
        }
    }

    // ---------- P/Invoke (DllImport, not LibraryImport — no unsafe blocks) ----------

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfo lpmi);

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, nint lprcMonitor, nint dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }
}
