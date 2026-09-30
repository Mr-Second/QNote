using System;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinUIRichEditor.Controls;

// A horizontal wrap panel. WinUI 3 ships no WrapPanel, and the library takes no CommunityToolkit
// dependency, so the toolbar carries its own: items flow left-to-right and wrap to a new row when the
// available width runs out (instead of clipping or horizontal-scrolling). Mirrors the original
// AvaloniaRichEditor toolbar's WrapPanel, which also sidesteps the overflow-dropdown layout-reentrancy
// crash a wrapping toolbar can otherwise hit during an interactive resize.
internal sealed partial class ToolbarWrapPanel : Panel
{
    public double HorizontalSpacing { get; set; }
    public double VerticalSpacing { get; set; }

    protected override Size MeasureOverride(Size available)
    {
        double lineW = 0, lineH = 0, totalW = 0, totalH = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, available.Height));
            var d = child.DesiredSize;
            if (lineW > 0 && lineW + d.Width > available.Width)
            {
                totalW = Math.Max(totalW, lineW);
                totalH += lineH + VerticalSpacing;
                lineW = d.Width + HorizontalSpacing;
                lineH = d.Height;
            }
            else
            {
                lineW += d.Width + HorizontalSpacing;
                lineH = Math.Max(lineH, d.Height);
            }
        }
        totalW = Math.Max(totalW, lineW);
        totalH += lineH;
        double w = double.IsInfinity(available.Width) ? totalW : Math.Min(totalW, available.Width);
        return new Size(w, totalH);
    }

    protected override Size ArrangeOverride(Size final)
    {
        // Two passes per line so items can be CENTERED vertically within the line: with the old
        // top-alignment, shorter controls (buttons ~28px) hugged the top edge next to taller ones
        // (combos ~32px), which is what made the strip look ragged.
        var children = Children;
        void ArrangeLine(int from, int to, double top, double height)
        {
            double cx = 0;
            for (int i = from; i < to; i++)
            {
                var d = children[i].DesiredSize;
                children[i].Arrange(new Rect(cx, top + (height - d.Height) / 2, d.Width, d.Height));
                cx += d.Width + HorizontalSpacing;
            }
        }

        int lineStart = 0;
        double x = 0, y = 0, lineH = 0;
        for (int i = 0; i < children.Count; i++)
        {
            var d = children[i].DesiredSize;
            if (x > 0 && x + d.Width > final.Width)
            {
                ArrangeLine(lineStart, i, y, lineH);
                y += lineH + VerticalSpacing;
                x = 0;
                lineH = 0;
                lineStart = i;
            }
            x += d.Width + HorizontalSpacing;
            lineH = Math.Max(lineH, d.Height);
        }
        ArrangeLine(lineStart, children.Count, y, lineH);
        return final;
    }
}
