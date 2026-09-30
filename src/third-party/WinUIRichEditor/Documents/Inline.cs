namespace WinUIRichEditor.Documents;

/// <summary>Abstract base for inline-level elements that live inside a <see cref="Paragraph"/>:
/// <see cref="Run"/> (text), <see cref="InlineImage"/> (icon) and <see cref="InlineTable"/> (a table
/// treated as a character). Every non-<see cref="Run"/> inline is an atomic object occupying one
/// logical character position (U+FFFC) in the offset model.</summary>
public abstract class Inline : TextElement
{
}
