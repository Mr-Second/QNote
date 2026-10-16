using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.UI.Core;

namespace QNote.Views;

/// <summary>
/// The 8px pane splitter (mstodo-ui-extraction §1). A self-contained drag
/// control: WinUI's <see cref="Microsoft.UI.Xaml.Controls.Primitives.Thumb"/>
/// is sealed (no cursor subclass) and <see cref="UIElement.ProtectedCursor"/>
/// is protected, so this implements pointer-capture dragging itself and raises
/// <see cref="DragDelta"/> with the horizontal delta in DIPs.
/// </summary>
/// <remarks>
/// Gesture bookkeeping uses <see cref="Windows.UI.Xaml.Input.Pointer.PointerId"/>,
/// never <c>ReferenceEquals</c> on the projected <c>Pointer</c> wrapper, and the
/// drag also ends from <see cref="OnPointerCaptureLost"/>: capture loss (alt-tab,
/// system steal) never delivers PointerReleased, and a drag that never "completes"
/// silently skips the width-persist hook (2026-10-15 bug: zero todo% rows).
/// </remarks>
public sealed partial class PaneSplitter : Control
{
    private double _lastX;
    private uint _pointerId;
    private bool _dragging;

    /// <summary>Horizontal drag delta in DIPs (positive = pointer moved right).</summary>
    public event EventHandler<double>? DragDelta;

    /// <summary>Raised exactly once per drag gesture when it ends (release OR
    /// capture loss) — the persist hook (saving on every DragDelta would hammer
    /// the settings store).</summary>
    public event EventHandler? DragCompleted;

    public PaneSplitter()
    {
        ProtectedCursor = InputCursor.CreateFromCoreCursor(new CoreCursor(CoreCursorType.SizeWestEast, 1));
        IsTabStop = false;
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;
        _lastX = point.Position.X;
        _pointerId = e.Pointer.PointerId;
        _dragging = true;
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (!_dragging || e.Pointer.PointerId != _pointerId)
            return;
        var x = e.GetCurrentPoint(this).Position.X;
        var delta = x - _lastX;
        _lastX = x;
        if (delta != 0)
            DragDelta?.Invoke(this, delta);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (_dragging && e.Pointer.PointerId == _pointerId)
        {
            _dragging = false;
            try
            {
                ReleasePointerCapture(e.Pointer);
            }
            catch (Exception)
            {
                // capture already lost — nothing to release
            }
            OnDragCompleted();
        }
        e.Handled = true;
    }

    // Alt-tab / system pointer steals end the capture WITHOUT a Released event —
    // complete the drag here so the persist hook still runs. On a normal release
    // this arrives AFTER OnPointerReleased already cleared the flag, so the
    // gesture never completes twice.
    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            OnDragCompleted();
        }
    }

    private void OnDragCompleted()
    {
        App.Services.GetRequiredService<ILogger<PaneSplitter>>()
            .LogInformation("PaneSplitter drag gesture completed.");
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }
}
