namespace QNote.Text;

/// <summary>
/// The save no-op guard: decides whether a flush must write the note. A note is
/// unchanged only when the title, the plain text AND the editor's "RTF differs from
/// its post-load baseline" signal all say so.
///
/// Why all three: the plain-text compare alone misses format-only edits (bold/colour/
/// alignment leave the text identical) — the bug this guard fixes. The RTF-baseline
/// flag is supplied by the editor instead of comparing raw RTF here, because RichEdit
/// rewrites RTF on every load (normalization) and a raw compare would flag every note
/// dirty on open. Pure function — Core, unit-testable.
/// </summary>
public static class NoteEditComparer
{
    /// <param name="loadedTitle">Title as persisted.</param>
    /// <param name="currentTitle">Title as shown in the editor.</param>
    /// <param name="loadedPlainText">Plain text as persisted.</param>
    /// <param name="currentPlainText">Plain text as shown in the editor.</param>
    /// <param name="editorRtfChanged">Editor's RTF differs from the post-load baseline.</param>
    /// <returns>True when the note has real edits and must be saved.</returns>
    public static bool HasChanges(
        string loadedTitle,
        string currentTitle,
        string loadedPlainText,
        string currentPlainText,
        bool editorRtfChanged)
    {
        if (editorRtfChanged)
            return true;
        if (!string.Equals(loadedTitle, currentTitle, StringComparison.Ordinal))
            return true;
        // Trailing '\r' is the RichEditBox paragraph mark, not user content.
        return Normalize(loadedPlainText) != Normalize(currentPlainText);
    }

    private static string Normalize(string text) => text.TrimEnd('\r');
}
