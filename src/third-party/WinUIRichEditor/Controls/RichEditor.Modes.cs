using System;
using WinUIRichEditor.Documents;

using Microsoft.UI.Xaml;

namespace WinUIRichEditor.Controls;

// Read-only behavior and the document image soft-limit. Capability is now expressed directly by the
// IsReadOnly DP (see RichEditor.cs) plus the Allow* feature flags (RichEditor.Features.cs) — there is no
// EditorMode preset. A "viewer" is simply IsReadOnly=true with no (or a minimal) toolbar. ReadOnly disables
// the caret blink and undo history (see OnReadOnlyChanged).
public partial class RichEditor
{
    /// <summary>Identifies the <see cref="MaxRecommendedImages"/> dependency property.</summary>
    public static readonly DependencyProperty MaxRecommendedImagesProperty = DependencyProperty.Register(
        nameof(MaxRecommendedImages), typeof(int), typeof(RichEditor), new PropertyMetadata(50));

    /// <summary>Soft limit on the document's image count. When the count first exceeds this value,
    /// <see cref="RecommendedImageLimitExceeded"/> is raised once; editing is never blocked. Zero or
    /// negative disables the warning. Default 50.</summary>
    public int MaxRecommendedImages
    {
        get => (int)GetValue(MaxRecommendedImagesProperty);
        set => SetValue(MaxRecommendedImagesProperty, value);
    }

    /// <summary>Raised (once per crossing) when the document's image count exceeds
    /// <see cref="MaxRecommendedImages"/>. Re-arms when the count drops back to the limit or below.</summary>
    public event EventHandler? RecommendedImageLimitExceeded;

    private bool _imageLimitNotified;

    /// <summary>Counts the document's images: top-level <see cref="ImageBlock"/>s plus
    /// <see cref="InlineImage"/>s in paragraphs and table cells.</summary>
    public int GetImageCount()
    {
        var doc = Document;
        if (doc == null) return 0;
        return Count(doc.Blocks);

        // Recursive: a cell's blocks can hold a nested table and a paragraph can hold an inline table,
        // both of which carry images of their own. Counting one level deep under-reported them, so the
        // soft-limit warning fired late (or never).
        static int Count(System.Collections.Generic.IEnumerable<Block> blocks)
        {
            int n = 0;
            foreach (var b in blocks)
            {
                switch (b)
                {
                    case ImageBlock: n++; break; // block images in cells count too
                    case Paragraph p:
                        foreach (var i in p.Inlines)
                        {
                            if (i is InlineImage) n++;
                            else if (i is InlineTable it)
                                foreach (var (_, _, cell) in it.Table.LogicalCells()) n += Count(cell.Blocks);
                        }
                        break;
                    case TableBlock t:
                        foreach (var (_, _, cell) in t.LogicalCells()) n += Count(cell.Blocks);
                        break;
                }
            }
            return n;
        }
    }

    // Edge-triggered soft-limit check, run after each content edit (AfterEdit).
    internal void CheckImageLimit()
    {
        int limit = MaxRecommendedImages;
        if (limit <= 0) { _imageLimitNotified = false; return; }
        if (GetImageCount() > limit)
        {
            if (!_imageLimitNotified)
            {
                _imageLimitNotified = true;
                RecommendedImageLimitExceeded?.Invoke(this, EventArgs.Empty);
            }
        }
        else _imageLimitNotified = false;
    }

    // ReadOnly perf: a viewer needs no blinking caret and no undo history. Fires whether ReadOnly
    // arrives via the DP setter or the initial value.
    private void OnReadOnlyChanged(bool readOnly)
    {
        if (readOnly) _undo.Clear();
        // RestartBlink handles both directions: it starts the timer only when editable, and in read-only
        // it just leaves the caret in its "on" phase (static) for ShowCaretWhenReadOnly. The old
        // StopBlink() here cleared _caretOn for good, so a read-only caret could never light up again.
        RestartBlink();
        _automationPeer?.NotifyReadOnlyChanged(!readOnly, readOnly); // let assistive tech know
        InvalidateCanvas();
        // The toolbar re-reads IsReadOnly on StatusChanged (edit toolbar ⇄ view toolbar), and this did not raise
        // it: a host that made the editor read-only at run time kept the whole edit toolbar — Import included,
        // which replaces the viewer's document — until the next caret move (measured 2026-09-19). Same channel
        // as the feature flags (OnToolbarFlagChanged); upstream's toolbar subscribes to the property itself.
        RaiseStatusChanged();
    }
}
