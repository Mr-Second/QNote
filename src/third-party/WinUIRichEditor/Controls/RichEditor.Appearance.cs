using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinUIRichEditor.Controls;

// Caret + selection appearance. The original exposes IBrush SelectionBrush/CaretBrush; here they are
// WinUI Brushes (defaulting to SolidColorBrush) and the renderer pulls a Windows.UI.Color out of them,
// since Win2D's DrawLine/FillRectangle take a Color. A non-solid brush falls back to the default color.
//
// The defaults are made PER EDITOR (PropertyMetadata.Create with a factory). A brush is a DependencyObject and
// belongs to the thread that made it; the defaults used to be one SolidColorBrush each, made in the static
// constructor, so an editor in a window on its own UI thread read another thread's brush on its first paint —
// RPC_E_WRONG_THREAD (measured 2026-09-14). Upstream fixed the same shape with immutable brushes (a66b472);
// WinUI has none, so each editor gets its own.
public partial class RichEditor
{
    /// <summary>Fill brush for the text selection highlight. Defaults to a translucent blue.</summary>
    public static readonly DependencyProperty SelectionBrushProperty = DependencyProperty.Register(
        nameof(SelectionBrush), typeof(Brush), typeof(RichEditor),
        PropertyMetadata.Create(() => new SolidColorBrush(Color.FromArgb(80, 0, 120, 215)), OnAppearanceBrushChanged));

    /// <summary>Fill brush for the text selection highlight.</summary>
    public Brush SelectionBrush
    {
        get => (Brush)GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    /// <summary>Brush for the blinking caret. Defaults to black.</summary>
    public static readonly DependencyProperty CaretBrushProperty = DependencyProperty.Register(
        nameof(CaretBrush), typeof(Brush), typeof(RichEditor),
        PropertyMetadata.Create(() => new SolidColorBrush(Colors.Black), OnAppearanceBrushChanged));

    /// <summary>Brush for the blinking caret.</summary>
    public Brush CaretBrush
    {
        get => (Brush)GetValue(CaretBrushProperty);
        set => SetValue(CaretBrushProperty, value);
    }

    /// <summary>Whether a read-only editor still draws the caret. Default <see langword="false"/>.
    /// <para>A viewer has no caret by default because a caret is a "you can type here" affordance —
    /// browsers and PDF readers show none, and this control already follows browser convention for
    /// read-only links (a plain click opens them). Turn this on for a document reader whose users
    /// navigate by keyboard: arrows, Shift+arrows, Home/End, PageUp/Down and Ctrl+F all work read-only,
    /// and without a caret there is no way to see where they land.</para>
    /// <para>The read-only caret does NOT blink (see <c>RestartBlink</c>): a static line reads as a
    /// position marker, a blinking one reads as an editable field.</para></summary>
    public static readonly DependencyProperty ShowCaretWhenReadOnlyProperty = DependencyProperty.Register(
        nameof(ShowCaretWhenReadOnly), typeof(bool), typeof(RichEditor),
        new PropertyMetadata(false, OnShowCaretWhenReadOnlyChanged));

    /// <summary>Whether a read-only editor still draws the (non-blinking) caret. Default false.</summary>
    public bool ShowCaretWhenReadOnly
    {
        get => (bool)GetValue(ShowCaretWhenReadOnlyProperty);
        set => SetValue(ShowCaretWhenReadOnlyProperty, value);
    }

    private static void OnShowCaretWhenReadOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ed = (RichEditor)d;
        // Re-arm the caret's on-phase: losing focus (or an earlier read-only switch) may have cleared it,
        // and in read-only nothing restarts it because the blink timer never runs.
        ed.RestartBlink();
        ed.InvalidateCanvas();
    }

    /// <summary>Default text color for runs without an explicit foreground (and list markers). Defaults
    /// to black; a dark-theme host assigns a light brush. Runs with their own Foreground are unaffected.</summary>
    public static readonly DependencyProperty TextForegroundProperty = DependencyProperty.Register(
        nameof(TextForeground), typeof(Brush), typeof(RichEditor),
        PropertyMetadata.Create(() => new SolidColorBrush(Colors.Black), OnAppearanceBrushChanged));

    /// <summary>Default text color for runs without an explicit foreground.</summary>
    public Brush TextForeground
    {
        get => (Brush)GetValue(TextForegroundProperty);
        set => SetValue(TextForegroundProperty, value);
    }

    /// <summary>Background of the editing canvas in continuous mode. Default null = transparent (the
    /// host's background shows through). Paged mode keeps its white paper / grey desk regardless.</summary>
    public static readonly DependencyProperty CanvasBackgroundProperty = DependencyProperty.Register(
        nameof(CanvasBackground), typeof(Brush), typeof(RichEditor),
        new PropertyMetadata(null, OnCanvasBackgroundChanged));

    /// <summary>Background of the editing canvas in continuous mode (null = transparent).</summary>
    public Brush? CanvasBackground
    {
        get => (Brush?)GetValue(CanvasBackgroundProperty);
        set => SetValue(CanvasBackgroundProperty, value);
    }

    private static void OnCanvasBackgroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ed = (RichEditor)d;
        ed._canvas.ClearColor = ed.CanvasBackground is SolidColorBrush scb ? scb.Color : Colors.Transparent;
        ed.InvalidateCanvas();
    }

    private static void OnAppearanceBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RichEditor)d).InvalidateCanvas();

    // The effective default text color (Win2D draws take a Color, not a Brush).
    internal Color EffectiveTextColor => BrushColor(TextForeground, Colors.Black);

    // The selection-highlight color, resolved from SelectionBrush (Win2D fills take a Color).
    private Color SelectionFill => BrushColor(SelectionBrush, Color.FromArgb(80, 0, 120, 215));

    // The caret color, resolved from CaretBrush.
    private Color CaretColor => BrushColor(CaretBrush, Colors.Black);

    private static Color BrushColor(Brush? brush, Color fallback)
        => brush is SolidColorBrush scb ? scb.Color : fallback;
}
