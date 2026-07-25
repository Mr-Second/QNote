namespace QNote.Text;

/// <summary>
/// Splits display text into (text, isHit) runs for search-result highlighting —
/// parity with the Qt build's client-side literal highlight: the WHOLE keyword,
/// case-insensitive substring, never tokenized. A bigram/AND match whose keyword is
/// not contiguous in the text simply shows no highlight (accepted Qt behaviour).
/// Pure function; lives in Core for headless tests (same rule as RtfPictStripper).
/// </summary>
public static class HighlightSplitter
{
    /// <summary>Split <paramref name="text"/> into runs; empty/blank keyword yields a single non-hit run.</summary>
    public static IReadOnlyList<(string Text, bool IsHit)> Split(string? text, string? keyword)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        if (string.IsNullOrWhiteSpace(keyword))
            return [(text, false)];

        var segments = new List<(string, bool)>();
        var pos = 0;
        while (pos < text.Length)
        {
            var hit = text.IndexOf(keyword, pos, StringComparison.OrdinalIgnoreCase);
            if (hit < 0)
            {
                segments.Add((text[pos..], false));
                break;
            }

            if (hit > pos)
                segments.Add((text[pos..hit], false));
            segments.Add((text.Substring(hit, keyword.Length), true));
            pos = hit + keyword.Length;
        }

        return segments;
    }
}
