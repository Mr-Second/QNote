namespace WinUIRichEditor.Documents;

/// <summary>Abstract base for all document model elements (blocks and inlines).
/// Every element can be cloned and optionally has a parent reference.</summary>
public abstract class TextElement
{
    /// <summary>The parent element in the document tree (e.g. the owning <see cref="Paragraph"/> for an inline).
    /// <see langword="null"/> for top-level blocks. Not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public object? Parent { get; internal set; }

    /// <summary>Creates a deep copy of this element (child collections are cloned recursively).</summary>
    public abstract TextElement Clone();
}
