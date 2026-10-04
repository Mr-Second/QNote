namespace QNote.Update;

/// <summary>
/// Pure release-notes extraction for the startup update dialog (1.5.1). The
/// GitHub release body is a long document (download variants, migration
/// guides…, see <c>.github/release-notes-template.md</c>); the dialog only
/// wants the <c>## 更新内容</c> section's bullet lines, lightly de-markdowned:
/// bullet prefixes dropped, <c>**bold**</c> asterisks dropped,
/// <c>[text](url)</c> collapsed to <c>text</c>, backticks dropped.
///
/// The section heading is a FIXED contract with the CI template — if the
/// template ever renames it, extraction returns null and the dialog simply
/// hides the notes block (graceful degradation, user-approved 2026-10-04).
/// Headless-pure (no I/O) so it is directly unit-testable.
/// </summary>
public static class ReleaseNotes
{
    /// <summary>Section heading the extraction anchors on (template contract).</summary>
    private const string SectionHeading = "## 更新内容";

    /// <summary>Upper bound of lines shown in the dialog (scrollable block).</summary>
    public const int DefaultMaxLines = 12;

    /// <summary>
    /// Extract the changelog lines from a GitHub release <paramref name="body"/>.
    /// Returns null when the section is missing or empty (caller hides the
    /// notes block).
    /// </summary>
    public static string? ExtractChangelog(string? body, int maxLines = DefaultMaxLines)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        var collected = new List<string>(maxLines);
        var inSection = false;

        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inSection)
                    break; // next section — the changelog block is done
                inSection = line == SectionHeading;
                continue;
            }

            if (!inSection || line.Length == 0)
                continue;

            if (collected.Count >= maxLines)
                break;

            collected.Add(CleanInlineMarkup(line));
        }

        return collected.Count > 0 ? string.Join(Environment.NewLine, collected) : null;
    }

    /// <summary>
    /// Best-effort inline markdown cleanup: <c>- **修复**：x [y](z)</c> →
    /// <c>修复：x y</c>. Malformed fragments are left as-is — cosmetic only.
    /// </summary>
    private static string CleanInlineMarkup(string s)
    {
        // Bullet prefix: "- ", "* " (numbered lists are not used by the template).
        if (s.StartsWith("- ", StringComparison.Ordinal) || s.StartsWith("* ", StringComparison.Ordinal))
            s = s[2..];

        // [text](url) → text — plain scan, no regex (trim-safe, AOT-friendly).
        int open;
        while ((open = s.IndexOf('[')) >= 0)
        {
            var close = s.IndexOf(']', open + 1);
            if (close < 0 || close + 1 >= s.Length || s[close + 1] != '(')
                break;
            var end = s.IndexOf(')', close + 2);
            if (end < 0)
                break;
            s = s[..open] + s[(open + 1)..close] + s[(end + 1)..];
        }

        return s.Replace("**", string.Empty).Replace("`", string.Empty);
    }
}
