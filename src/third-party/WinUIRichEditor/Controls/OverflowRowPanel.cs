using System;
using System.Collections.Generic;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinUIRichEditor.Controls;

// QNOTE VENDORED PATCH (P2, editor-toolbar-restyle): a horizontal row panel that JUSTIFIES the visible
// children (first hugs the left edge, last hugs the right edge, gaps spread evenly) AND moves the overflow
// tail into a per-row flyout when the row runs out of width. Used by the fixed two-row toolbar: row 1
// (icon buttons) and row 2 (dropdowns) each own one of these, so each row folds independently and keeps its
// own trailing More button pinned to the right edge.
//
// The re-parenting happens inside MeasureOverride, which mutates the child collection being measured — the
// classic layout-reentrancy hazard. Guards: `_reflowing` (a nested pass never mutates again) + idempotence
// (skip the move when the cut index and width are unchanged). No reflection/templates → AOT/trim-safe.
internal sealed partial class OverflowRowPanel : Panel
{
    /// <summary>Minimum gap between visible children.</summary>
    public double MinSpacing { get; set; } = 2;

    /// <summary>Upper bound on the justified gap.</summary>
    public double MaxSpacing { get; set; } = 20;

    /// <summary>The trailing fixed More button (always right-most when anything overflows).</summary>
    public FrameworkElement? MoreButton { get; set; }

    /// <summary>The wrapping host inside the More flyout; demoted children are re-parented into it.</summary>
    public Panel? OverflowHost { get; set; }

    /// <summary>Skip reflow while the flyout is open (moving a child out from under the popup).</summary>
    public Func<bool>? IsOverflowOpen { get; set; }

    private readonly List<UIElement> _all = new();
    private bool _snapshotted;
    private bool _reflowing;
    private double _lastWidth = double.NaN;
    private int _lastBoundary = -1;

    protected override Size MeasureOverride(Size available)
    {
        if (_reflowing) return MeasureCurrent(available);
        _reflowing = true;
        try
        {
            if (!_snapshotted) { foreach (var c in Children) _all.Add(c); _snapshotted = true; }
            if (IsOverflowOpen?.Invoke() != true) Reflow(available.Width);
            return MeasureCurrent(available);
        }
        finally { _reflowing = false; }
    }

    private Size MeasureCurrent(Size available)
    {
        double maxH = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, available.Height));
            maxH = Math.Max(maxH, child.DesiredSize.Height);
        }
        // Report the FULL given width so a parent never shrink-centres the row.
        double width = double.IsInfinity(available.Width) ? Width : available.Width;
        return new Size(width, maxH);
    }

    protected override Size ArrangeOverride(Size final)
    {
        int n = 0; double content = 0, maxH = 0;
        foreach (var child in Children)
        {
            var d = child.DesiredSize;
            if (d.Width <= 0 && d.Height <= 0) continue;
            content += d.Width; n++;
            maxH = Math.Max(maxH, d.Height);
        }
        double gap = MinSpacing;
        if (n > 1)
        {
            double surplus = final.Width - content - MinSpacing * (n - 1);
            if (surplus > 0) gap = MinSpacing + Math.Min(surplus / (n - 1), MaxSpacing - MinSpacing);
        }
        double x = 0;
        foreach (var child in Children)
        {
            var d = child.DesiredSize;
            if (d.Width <= 0 && d.Height <= 0) continue;
            double y = Math.Max(0, (maxH - d.Height) / 2);
            child.Arrange(new Rect(x, y, d.Width, d.Height));
            x += d.Width + gap;
        }
        return new Size(final.Width, maxH);
    }

    private void Reflow(double availableWidth)
    {
        double prevW = _lastWidth; int prevB = _lastBoundary;
        _lastWidth = availableWidth;
        if (_all.Count == 0) { SetOverflow(false, -1); return; }

        // 1. Natural widths (collapsed items measure to 0 and drop out).
        var widths = new double[_all.Count];
        double total = 0; int visible = 0;
        for (int i = 0; i < _all.Count; i++)
        {
            double w = _all[i].Visibility == Visibility.Collapsed ? 0 : NaturalWidth(_all[i], double.PositiveInfinity);
            widths[i] = w;
            if (w > 0) { total += w + MinSpacing; visible++; }
        }
        if (visible > 0) total -= MinSpacing;

        // 2. Fits (or unbounded): restore all, hide More.
        if (double.IsInfinity(availableWidth) || total <= availableWidth)
        { RestoreAll(); SetOverflow(false, -1); return; }

        // 3. Reserve the More button; walk to the first item that would exceed the budget.
        double moreW = MeasureWidth(MoreButton);
        double usable = availableWidth - moreW - MinSpacing;
        int boundary = -1; double running = 0;
        for (int i = 0; i < _all.Count; i++)
        {
            if (widths[i] <= 0) continue;
            running += widths[i] + MinSpacing;
            if (running > usable) { boundary = i; break; }
        }
        if (boundary < 0) boundary = FirstVisibleIndex();
        if (boundary < 0) { RestoreAll(); SetOverflow(false, -1); return; }

        if (boundary == prevB && !double.IsNaN(prevW) && Math.Abs(availableWidth - prevW) <= 0.5)
        { SetOverflow(true, boundary); return; }

        RestoreAll();
        if (OverflowHost != null)
            for (int i = boundary; i < _all.Count; i++) MoveToOverflow(_all[i]);
        SetOverflow(OverflowHost?.Children.Count > 0, boundary);
    }

    private int FirstVisibleIndex()
    {
        for (int i = 0; i < _all.Count; i++)
            if (_all[i].Visibility != Visibility.Collapsed) return i;
        return -1;
    }

    private void RestoreAll()
    {
        // Only detach demoted items (those in _all); the host may hold host-supplied fixed items.
        if (OverflowHost != null)
            for (int i = OverflowHost.Children.Count - 1; i >= 0; i--)
                if (_all.Contains(OverflowHost.Children[i])) OverflowHost.Children.RemoveAt(i);
        Children.Clear();
        foreach (var e in _all) Children.Add(e);
    }

    private void MoveToOverflow(UIElement e)
    {
        Children.Remove(e);
        OverflowHost?.Children.Add(e);
    }

    private void SetOverflow(bool any, int boundary)
    {
        if (MoreButton != null)
        {
            var want = any ? Visibility.Visible : Visibility.Collapsed;
            if (MoreButton.Visibility != want) { MoreButton.Visibility = want; InvalidateMeasure(); }
        }
        _lastBoundary = boundary;
    }

    private static double NaturalWidth(UIElement child, double height)
    {
        child.Measure(new Size(double.PositiveInfinity, height));
        return child.DesiredSize.Width;
    }

    private static double MeasureWidth(FrameworkElement? element)
    {
        if (element == null) return 0;
        if (!double.IsNaN(element.Width) && element.Width > 0 && !double.IsInfinity(element.Width)) return element.Width;
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize.Width;
    }

    internal void Refresh()
    {
        _all.Clear();
        foreach (var c in Children) _all.Add(c);
        _snapshotted = true; _lastWidth = double.NaN; _lastBoundary = -1;
        InvalidateMeasure();
    }
}
