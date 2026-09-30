using Windows.Foundation;
using Microsoft.UI.Xaml.Input;

namespace WinUIRichEditor.Controls;

// The pointer pipeline: the seam that lets a test drive a whole gesture in the ORDER the framework
// produces it (press → capture → move → release → capture-lost), instead of calling the pieces.
//
// WHY THIS EXISTS. The pointer handlers used to take `PointerRoutedEventArgs` all the way down, and that
// type cannot be constructed — neither can `Pointer`. So the control exposed "the handler minus its
// pointer capture" (TableDrawPressAt, BeginImageResizeAt, ArmObjectDragAt …) and the tests called those.
// What that leaves untested is the capture itself, and capture is where the defects were:
// `ReleasePointerCapture` raises `PointerCaptureLost` SYNCHRONOUSLY, so a release handler that lets go of
// the pointer before it finishes its work is cancelled by its own release. That shipped once — every table
// draw silently inserted nothing (2026-09-19, regression of the fix that added the capture-lost cancel),
// with a full green suite; a person found it. Three other handlers carry the same ordering rule in a
// comment (EndColumnResize, EndRowResize, EndObjectDrag) and nothing enforced any of them.
//
// THE RULE. Everything a pointer handler needs from the framework is exactly this: where the pointer is,
// which button, which modifiers, and the ability to take and release the capture. `PointerStep` carries
// those four, so every handler below the event boundary takes a `PointerStep` and the event handlers
// themselves shrink to adapters that build one. The adapters must stay adapters — logic that lives in
// them is logic no test can reach, which is the hole this file closes.
//
// MODIFIERS. `Ctrl`/`Shift` in the pointer path used to be static reads of the real keyboard
// (InputKeyboardSource), which no test can set. They are read ONCE per event, in the adapter, and travel
// on the step. The keyboard handlers still read them directly — they are driven by key events, not here.
public partial class RichEditor
{
    /// <summary>The pointer capture, as the handlers use it. The real implementation is the canvas;
    /// a test substitutes one that records the order and raises capture-lost the way WinUI does.</summary>
    internal interface IPointerCapture
    {
        void Capture();
        /// <summary>Releases the capture. Like WinUI, this raises capture-lost SYNCHRONOUSLY when the
        /// pointer was held — which is why every release handler finishes its work first.</summary>
        void Release();
    }

    /// <summary>One pointer event, reduced to what the handlers actually read from it:
    /// <c>ViewPos</c> is in canvas (physical) coordinates — the handlers call <c>ViewToDoc</c> themselves,
    /// so the zoom/page mapping stays inside the tested path; <c>RightButton</c>, <c>Ctrl</c> and
    /// <c>Shift</c> are the state at the moment of the event; <c>Capture</c> is the pointer capture.</summary>
    internal readonly record struct PointerStep(
        Point ViewPos,
        bool RightButton,
        bool Ctrl,
        bool Shift,
        IPointerCapture Capture);

    // The real capture: the canvas and the pointer that the event carried.
    private sealed class CanvasCapture(Microsoft.UI.Xaml.UIElement canvas, Pointer pointer) : IPointerCapture
    {
        public void Capture() => canvas.CapturePointer(pointer);
        public void Release() => canvas.ReleasePointerCapture(pointer);
    }

    // The one place `PointerRoutedEventArgs` is unpacked.
    private PointerStep StepFrom(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas);
        return new PointerStep(p.Position, p.Properties.IsRightButtonPressed, Ctrl, Shift,
                               new CanvasCapture(_canvas, e.Pointer));
    }
}
