using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace QNote.Views;

/// <summary>
/// The application window: frameless (content extended into the title bar) with a
/// custom <c>TitleBar</c> and a Frame that hosts the notes screen. Flushes the
/// currently edited note on close (save-on-close).
/// </summary>
public sealed partial class MainWindow : Window
{
    private bool _closingHandled;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        ResizeToDefault();

        RootFrame.Navigate(typeof(NotesPage));
        AppWindow.Closing += OnAppWindowClosing;
    }

    private void ResizeToDefault()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi <= 0 ? 1.0 : dpi / 96.0;
        AppWindow.Resize(new SizeInt32((int)(940 * scale), (int)(620 * scale)));
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
