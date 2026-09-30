using System.Collections.Generic;

namespace WinUIRichEditor.Documents;

/// <summary>The root document model: an ordered list of block-level elements
/// (<see cref="Paragraph"/>, <see cref="TableBlock"/>, <see cref="ImageBlock"/>, <see cref="DividerBlock"/>).</summary>
public class FlowDocument
{
    /// <summary>The ordered list of top-level block elements.</summary>
    public List<Block> Blocks { get; } = new List<Block>();

    /// <summary>Optional page setup (paper size, orientation, header/footer, page numbers). Null = unspecified
    /// (the editor keeps its current/host defaults). Persisted in JSON/.flow and applied to the editor on load.</summary>
    public PageSetup? PageSetup { get; set; }

    // True once headings carry their format on their runs (HeadingStyle.Materialize). A document built
    // before that, or by upstream, has it false and is converted once when the editor receives it.
    internal bool HeadingFormatsApplied { get; set; }

    /// <summary>Creates a deep clone of this document (all blocks and their children are cloned recursively).
    /// Image bytes are reference-shared (not copied) for efficiency.</summary>
    public FlowDocument Clone()
    {
        var doc = new FlowDocument { PageSetup = PageSetup?.Clone(), HeadingFormatsApplied = HeadingFormatsApplied };
        foreach (var block in Blocks)
        {
            var clone = block.Clone() as Block;
            if (clone != null)
            {
                clone.Parent = doc;
                doc.Blocks.Add(clone);
            }
        }
        return doc;
    }
}
