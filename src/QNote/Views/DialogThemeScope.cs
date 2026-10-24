using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace QNote.Views;

/// <summary>Popup dialogs follow the window root while open, including system theme changes.</summary>
internal sealed class DialogThemeScope : IDisposable
{
    private readonly ContentDialog _dialog;
    private readonly FrameworkElement _root;
    private bool _disposed;

    public DialogThemeScope(ContentDialog dialog, FrameworkElement root)
    {
        _dialog = dialog;
        _root = root;
        _dialog.RequestedTheme = root.ActualTheme;
        _root.ActualThemeChanged += OnThemeChanged;
    }

    private void OnThemeChanged(FrameworkElement sender, object args) =>
        _dialog.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed) _dialog.RequestedTheme = _root.ActualTheme;
        });

    public void Dispose()
    {
        _disposed = true;
        _root.ActualThemeChanged -= OnThemeChanged;
    }
}
