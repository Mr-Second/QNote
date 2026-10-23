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
    private Action? _action;

    public AppToast() => InitializeComponent();

    public void ShowSuccess(string title, string message) =>
        Show(success: true, title, message, TimeSpan.FromSeconds(4), actionLabel: null, action: null);

    /// <summary>Success toast with an explicit duration — auth-style feedback wants a
    /// longer window (the user returns from the system account picker first).</summary>
    public void ShowSuccess(string title, string message, TimeSpan duration) =>
        Show(success: true, title, message, duration, actionLabel: null, action: null);

    public void ShowError(string title, string message) =>
        Show(success: false, title, message, TimeSpan.FromSeconds(6), actionLabel: null, action: null);

    /// <summary>
    /// Success toast with an inline action (e.g. 删除任务的「撤销」). Lives longer than
    /// the plain variant so the user has time to decide.
    /// </summary>
    public void ShowSuccess(string title, string message, string actionLabel, Action action) =>
        Show(success: true, title, message, TimeSpan.FromSeconds(6), actionLabel, action);

    /// <summary>Stop the pending auto-dismiss and hide immediately.</summary>
    public void Dismiss()
    {
        _timer?.Stop();
        _timer = null;
        _action = null;
        Visibility = Visibility.Collapsed;
    }

    private void Show(bool success, string title, string message, TimeSpan duration, string? actionLabel, Action? action)
    {
        Dismiss();

        IconBadge.Background = (Brush)Application.Current.Resources[
            success ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"];
        // Escaped PUA glyphs (\uE8FB Accept / \uE783 Error): a literal private-use
        // char is invisible in editors and easy to lose in a rewrite.
        IconGlyph.Glyph = success ? "\uE8FB" : "\uE783";
        TitleText.Text = title;
        MessageText.Text = message;
        // An empty message must COLLAPSE, not render as a blank line: the
        // StackPanel would still reserve the line box + its 2px spacing, which
        // reads as a stray empty row under a one-line toast (user report
        // 2026-10-23, the sign-out toast has no message text).
        MessageText.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        _action = action;
        ActionButton.Content = actionLabel;
        ActionButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;

        Visibility = Visibility.Visible;
        AnimateIn();

        _timer = new DispatcherTimer { Interval = duration };
        _timer.Tick += (_, _) => FadeOut();
        _timer.Start();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => FadeOut();

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        var action = _action;
        FadeOut();
        action?.Invoke();
    }

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
