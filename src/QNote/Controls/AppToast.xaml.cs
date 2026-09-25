using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace QNote.Controls;

/// <summary>
/// Material-style floating toast: shows a success/error card that fades+slides in,
/// auto-dismisses after a few seconds, and can be closed manually. Theme brushes
/// are resolved per show so runtime theme switches stay correct.
/// </summary>
public sealed partial class AppToast : UserControl
{
    private DispatcherTimer? _timer;

    public AppToast() => InitializeComponent();

    public void ShowSuccess(string title, string message) =>
        Show(success: true, title, message, TimeSpan.FromSeconds(4));

    public void ShowError(string title, string message) =>
        Show(success: false, title, message, TimeSpan.FromSeconds(6));

    /// <summary>Stop the pending auto-dismiss and hide immediately.</summary>
    public void Dismiss()
    {
        _timer?.Stop();
        _timer = null;
        Visibility = Visibility.Collapsed;
    }

    private void Show(bool success, string title, string message, TimeSpan duration)
    {
        Dismiss();

        IconBadge.Background = (Brush)Application.Current.Resources[
            success ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"];
        IconGlyph.Glyph = success ? "" : ""; // Accept / Error
        TitleText.Text = title;
        MessageText.Text = message;

        Visibility = Visibility.Visible;
        AnimateIn();

        _timer = new DispatcherTimer { Interval = duration };
        _timer.Tick += (_, _) => FadeOut();
        _timer.Start();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => FadeOut();

    private void AnimateIn()
    {
        Card.Opacity = 0;
        CardSlide.Y = -10;
        RunAnimation(toOpacity: 1, toY: 0, milliseconds: 180, completed: null);
    }

    private void FadeOut()
    {
        _timer?.Stop();
        _timer = null;
        if (Visibility != Visibility.Visible)
            return;
        RunAnimation(toOpacity: 0, toY: -6, milliseconds: 220, completed: () => Visibility = Visibility.Collapsed);
    }

    private void RunAnimation(double toOpacity, double toY, int milliseconds, Action? completed)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
        var fade = new DoubleAnimation { To = toOpacity, Duration = duration };
        Storyboard.SetTarget(fade, Card);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var slide = new DoubleAnimation { To = toY, Duration = duration };
        Storyboard.SetTarget(slide, CardSlide);
        Storyboard.SetTargetProperty(slide, "Y");

        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Children.Add(slide);
        if (completed is not null)
            sb.Completed += (_, _) => completed();
        sb.Begin();
    }
}
