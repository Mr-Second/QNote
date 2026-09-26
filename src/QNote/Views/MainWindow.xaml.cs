using System.Runtime.InteropServices;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QNote.Controls;
using QNote.Memory;
using QNote.Models;
using QNote.Services;
using Windows.Graphics;

namespace QNote.Views;

/// <summary>
/// The application window: frameless (content extended into the title bar) with a
/// custom <c>TitleBar</c> and a Frame that hosts the notes screen. Flushes the
/// currently edited note on close (save-on-close). Applies the window-scoped
/// settings live: theme (RequestedTheme on the root), always-on-top
/// (OverlappedPresenter), and remember-geometry (debounced persistence of
/// AppWindow position/size, restored on launch).
/// </summary>
public sealed partial class MainWindow : Window
{
    private ElementTheme _currentTheme = ElementTheme.Default;

    private readonly ISettingsService _settings;
    private readonly DispatcherQueueTimer _geometrySaveTimer;
    private readonly EdgeHideController _edgeHide;
    private readonly WorkingSetTrimController _workingSetTrim;
    private readonly IGlobalHotkey _hotkey;
    private AppSettings _snapshot = new();

    /// <summary>Tray double-click command (bound from XAML).</summary>
    public ICommand ShowWindowCommand { get; }

    public MainWindow()
    {
        InitializeComponent();

        ShowWindowCommand = new RelayCommand(ShowFromTray);

        _settings = App.Services.GetRequiredService<ISettingsService>();
        _settings.Changed += OnSettingsChanged;

        // Edge-hide: view-side controller owns polling/animation; judgment is in Core.
        _edgeHide = new EdgeHideController(this,
            App.Services.GetRequiredService<ILogger<EdgeHideController>>());

        // Tray-hide working-set trim (perf R2): timing/judgment in Core, only the
        // hide/show signals are fed from here. Engaged by the tray-hide and
        // StartMinimized paths ONLY — never by edge-hide (instant reveal there).
        _workingSetTrim = new WorkingSetTrimController(
            new WorkingSetInterop(),
            new ThreadingOneShotTimer(),
            App.Services.GetRequiredService<ILogger<WorkingSetTrimController>>());

        // Startup milestone (perf R1): one-shot window-activated timestamp.
        Activated += OnFirstActivated;

        // Global hotkey (ADR D6): subclass the HWND for WM_HOTKEY; registration
        // happens once settings load (below) and on every change from the panel.
        _hotkey = App.Services.GetRequiredService<IGlobalHotkey>();
        _hotkey.Attach(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _hotkey.Pressed += OnHotkeyPressed;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ResizeToDefault();

        // Geometry persistence is debounced: dragging/resizing floods Changed.
        _geometrySaveTimer = DispatcherQueue.CreateTimer();
        _geometrySaveTimer.Interval = TimeSpan.FromMilliseconds(800);
        _geometrySaveTimer.Tick += OnGeometrySaveTick;
        AppWindow.Changed += OnAppWindowChanged;

        RootFrame.Navigate(typeof(NotesPage));
        AppWindow.Closing += OnAppWindowClosing;

        _ = ApplyInitialSettingsAsync();
    }

    private void ResizeToDefault()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi <= 0 ? 1.0 : dpi / 96.0;
        AppWindow.Resize(new SizeInt32((int)(940 * scale), (int)(620 * scale)));
    }

    private async Task ApplyInitialSettingsAsync()
    {
        var s = await _settings.LoadAsync();
        Apply(s, restoreGeometry: true);

        // Register the persisted hotkey once at startup; later changes re-register
        // from the settings panel. Failure (combo owned by another app) is logged
        // by the service and surfaced in the panel — never fatal.
        if (s.EdgeHideHotkeyKey != 0)
            _hotkey.TryRegister(s.EdgeHideHotkeyModifiers, s.EdgeHideHotkeyKey);
    }

    /// <summary>
    /// Hotkey pressed (WM_HOTKEY, UI thread): toggle edge-hide from any position.
    /// A reveal also foregrounds the window — same Win32 path as the tray show.
    /// </summary>
    private void OnHotkeyPressed()
    {
        var wasHidden = _edgeHide.IsHidden;
        _edgeHide.ToggleHide();
        if (!wasHidden)
            return;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
    }

    private void OnSettingsChanged(AppSettings s) => Apply(s, restoreGeometry: false);

    private void Apply(AppSettings s, bool restoreGeometry)
    {
        _snapshot = s;

        // Theme: Mica + ThemeResources follow the root element's RequestedTheme.
        _currentTheme = s.ThemeMode switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        ((FrameworkElement)Content).RequestedTheme = _currentTheme;

        // An edge-hidden window must KEEP its forced topmost (PRD: hot-zone reveal
        // reliability) even when a settings re-apply lands while it is off-screen.
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = s.AlwaysOnTop || _edgeHide.IsHidden;

        _edgeHide.ApplySettings(s);

        // Stored values are physical pixels (AppWindow space) — no DPI rescaling.
        if (restoreGeometry && s.RememberWindowGeometry && s.WindowX != -1 && s.WindowWidth > 0 && s.WindowHeight > 0)
            AppWindow.MoveAndResize(new RectInt32(s.WindowX, s.WindowY, s.WindowWidth, s.WindowHeight));
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!_snapshot.RememberWindowGeometry)
            return;
        if (!args.DidPositionChange && !args.DidSizeChange)
            return;
        // ADR D3: edge-hidden / animating positions are never persisted as user geometry.
        if (_edgeHide.SuppressGeometryPersistence)
            return;

        _geometrySaveTimer.Stop();
        _geometrySaveTimer.Start();
    }

    private void OnGeometrySaveTick(DispatcherQueueTimer sender, object args)
    {
        _geometrySaveTimer.Stop();

        // The debounce may fire after a hide started (armed before the slide) — re-check.
        if (_edgeHide.SuppressGeometryPersistence)
            return;

        // A minimized window reports (-32000,-32000) — never persist that as the
        // user's geometry, or the next launch restores the window off-screen.
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            return;

        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        _snapshot = _snapshot with
        {
            WindowX = pos.X,
            WindowY = pos.Y,
            WindowWidth = size.Width,
            WindowHeight = size.Height,
        };
        // ponytail: fire-and-forget; the settings row set is tiny and a lost
        // geometry write only means the next launch restores an older position.
        _ = _settings.SaveAsync(_snapshot);
    }

    /// <summary>
    /// Close requests (X button, Alt+F4) never exit the app: cancel, flush the
    /// dirty note if needed, and hide to the tray. The only real exit is the
    /// tray quit path (<see cref="TrayQuit_Click"/> → Application.Exit).
    /// </summary>
    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;

        if (RootFrame.Content is NotesPage { ViewModel.IsDirty: true } page)
            await page.ViewModel.FlushAsync();

        AppWindow.Hide();
        _workingSetTrim.OnHidden();
    }

    /// <summary>
    /// StartMinimized launch: the window is never activated, so the tray control
    /// never Loads — register the icon explicitly. Efficiency mode is left off:
    /// EcoQoS throttles the process, which risks a sluggish UI when the window
    /// is later shown. (ponytail: revisit per-hide EcoQoS if idle memory matters.)
    /// </summary>
    public void ForceCreateTrayIcon() => TrayIcon.ForceCreate(enablesEfficiencyMode: false);

    /// <summary>
    /// One-shot startup milestone (perf R1): window-activated time is the externally
    /// visible cold-start number. StartMinimized launches never activate, so they
    /// log the tray-icon milestone in <see cref="App.OnLaunched"/> instead.
    /// </summary>
    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
            return;
        Activated -= OnFirstActivated;
        App.Services.GetRequiredService<ILogger<MainWindow>>()
            .LogInformation("Startup milestone: window activated at {ElapsedMs:0} ms since process start.",
                StartupClock.ElapsedMs);
    }

    /// <summary>
    /// StartMinimized launch: the window starts hidden to tray, so the tiered
    /// working-set trim engages exactly like a tray hide (perf R2). Also drop the
    /// window-activated startup milestone — it must measure COLD START only, not
    /// the first tray show hours later.
    /// </summary>
    public void NotifyHiddenSinceLaunch()
    {
        Activated -= OnFirstActivated;
        _workingSetTrim.OnHidden();
    }

    private void TrayShow_Click(object sender, RoutedEventArgs e) => ShowFromTray();

    private void TrayMenu_Opened(object sender, object e)
    {
        // Flyout popups do not inherit the window root's RequestedTheme (same
        // gotcha as ContentDialog) — pin the presenter so the tray menu follows
        // the app theme. Themed on open because the presenter is created lazily.
        if (TrayMenuFlyout.Items.Count == 0)
            return;

        DependencyObject node = TrayMenuFlyout.Items[0];
        while (VisualTreeHelper.GetParent(node) is { } parent)
        {
            if (parent is MenuFlyoutPresenter presenter)
            {
                presenter.RequestedTheme = _currentTheme;
                return;
            }
            node = parent;
        }
    }

    private async void TrayQuit_Click(object sender, RoutedEventArgs e)
    {
        // Destroying only the main AppWindow does NOT exit the process:
        // H.NotifyIcon keeps a hidden menu-host window alive, so the app lingered
        // (and a second quit click then NRE'd on the destroyed AppWindow).
        // Application.Exit() is the only path that tears down every window.
        if (RootFrame.Content is NotesPage { ViewModel.IsDirty: true } page)
            await page.ViewModel.FlushAsync();

        Application.Current.Exit();
    }

    /// <summary>
    /// Reveal and foreground the window from any state (hidden / minimized /
    /// edge-hidden). Used by the tray icon, the tray menu, and the single-instance
    /// redirected-activation path in <see cref="App"/>.
    /// </summary>
    public void ShowFromTray()
    {
        // Cancel any pending deep working-set trim — the window is coming back.
        _workingSetTrim.OnShown();

        // An edge-hidden window slides back first (ADR D5); SW_RESTORE alone would
        // foreground it at its off-screen position.
        _edgeHide.RevealFromTray();

        // Win32 path (same as H.NotifyIcon's own WindowUtilities): AppWindow.Show
        // + Window.Activate silently no-op on a hidden window. SW_RESTORE covers
        // all three states: hidden, minimized, visible-but-background.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
