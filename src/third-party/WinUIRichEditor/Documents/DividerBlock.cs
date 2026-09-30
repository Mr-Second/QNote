namespace WinUIRichEditor.Documents;

/// <summary>A horizontal rule (<c>&lt;hr&gt;</c>): a thin full-width divider line occupying its own block.</summary>
public class DividerBlock : Block
{
    /// <summary>Creates a divider. Its fixed height already includes vertical spacing, so the default
    /// bottom margin is 0 (unlike other blocks); the top is <see cref="Block.AutoTopMargin"/>, one line
    /// gap below whatever precedes it, as for the other blocks that are objects on the page.</summary>
    public DividerBlock()
    {
        MarginTop = AutoTopMargin;
        MarginBottom = 0;
    }

    /// <inheritdoc/>
    public override TextElement Clone() => new DividerBlock { Indent = Indent, MarginTop = MarginTop, MarginBottom = MarginBottom };
}
