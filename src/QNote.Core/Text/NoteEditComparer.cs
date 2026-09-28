namespace QNote.Text;

/// <summary>
/// The save no-op guard: decides whether a flush must write the note. A note is
/// unchanged only when the title, the stored content AND the editor's "document
/// differs from its post-load baseline" signal all say so.
///
/// Why all three: the content compare alone misses format-only edits (bold/list
/// leave the text identical) — the bug this guard fixes. The baseline flag is
/// supplied by the editor instead of comparing raw RTF here, because RichEdit
/// rewrites RTF on every load (normalization) and a raw compare would flag every
/// note dirty on open. Pure function — Core, unit-testable.
/// </summary>
public static class NoteEditComparer
{
    /// <param name="loadedTitle">Title as persisted.</param>
    /// <param name="currentTitle">Title as shown in the editor.</param>
    /// <param name="loadedContent">Stored content (Markdown, schema B2).</param>
    /// <param name="currentContent">Editor-emitted Markdown of what's on screen.</param>
    /// <param name="editorChanged">Editor's document differs from its post-load baseline.</param>
    /// <returns>True when the note has real edits and must be saved.</returns>
    public static bool HasChanges(
        string loadedTitle,
        string currentTitle,
        string loadedContent,
        string currentContent,
        bool editorChanged)
    {
        if (editorChanged)
            return true;
        if (!string.Equals(loadedTitle, currentTitle, StringComparison.Ordinal))
            return true;
        // Trailing '\r' is the RichEditBox paragraph mark, not user content.
        return Normalize(loadedContent) != Normalize(currentContent);
    }

    private static string Normalize(string text) => text.TrimEnd('\r');
}
