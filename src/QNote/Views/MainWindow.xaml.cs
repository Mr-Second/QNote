using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
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
    private bool _closingHandled;

    private readonly ISettingsService _settings;
    private readonly DispatcherQueueTimer _geometrySaveTimer;
    private AppSettings _snapshot = new();

    public MainWindow()
    {
        InitializeComponent();

        _settings = App.Services.GetRequiredService<ISettingsService>();
        _settings.Changed += OnSettingsChanged;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
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
    }

    private void OnSettingsChanged(AppSettings s) => Apply(s, restoreGeometry: false);

    private void Apply(AppSettings s, bool restoreGeometry)
    {
        _snapshot = s;

        // Theme: Mica + ThemeResources follow the root element's RequestedTheme.
        ((FrameworkElement)Content).RequestedTheme = s.ThemeMode switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = s.AlwaysOnTop;

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

        _geometrySaveTimer.Stop();
        _geometrySaveTimer.Start();
    }

    private void OnGeometrySaveTick(DispatcherQueueTimer sender, object args)
    {
        _geometrySaveTimer.Stop();

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

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closingHandled)
            return;

        if (RootFrame.Content is NotesPage { ViewModel.IsDirty: true } page)
        {
            // Cancel this close, flush the unsaved note, then close for real.
            args.Cancel = true;
            await page.ViewModel.FlushAsync();
            _closingHandled = true;
            Close();
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
