using QNote.Text;

namespace QNote.Tests;

/// <summary>
/// The save no-op guard. Reproduces the old bug (format-only edit judged "no change"
/// because title + plain text are identical) and proves the new editor-supplied
/// RTF-baseline signal makes it save, while a clean load still stays a no-op.
/// </summary>
public sealed class NoteEditComparerTests
{
    [Fact]
    public void HasChanges_NoOp_WhenNothingChanged()
    {
        Assert.False(NoteEditComparer.HasChanges("标题", "标题", "正文", "正文", editorChanged: false));
    }

    [Fact]
    public void HasChanges_IgnoresTrailingParagraphMark()
    {
        // GetText appends the RichEdit paragraph mark; it is not user content.
        Assert.False(NoteEditComparer.HasChanges("t", "t", "body", "body\r", editorChanged: false));
    }

    [Fact]
    public void HasChanges_DetectsTitleOnlyEdit()
    {
        Assert.True(NoteEditComparer.HasChanges("旧", "新", "正文", "正文", editorChanged: false));
    }

    [Fact]
    public void HasChanges_DetectsTextEdit()
    {
        Assert.True(NoteEditComparer.HasChanges("t", "t", "旧", "新", editorChanged: false));
    }

    [Fact]
    public void HasChanges_FormatOnlyEdit_WasMissedByOldGuard_NowSaves()
    {
        // Old guard = title == && plain == → no save. The editor RTF differs from its
        // post-load baseline (bold toggled) → the new guard saves.
        Assert.True(NoteEditComparer.HasChanges("t", "t", "正文", "正文", editorChanged: true));
    }

    [Fact]
    public void HasChanges_ImageOnlyEdit_Saves()
    {
        // An inserted image changes both the RTF baseline and the plain text (U+FFFC);
        // either signal is sufficient.
        Assert.True(NoteEditComparer.HasChanges("t", "t", "abc", "abc\uFFFC", editorChanged: true));
    }
}
