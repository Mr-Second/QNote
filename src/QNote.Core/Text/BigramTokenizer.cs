using System.Text;

namespace QNote.Text;

/// <summary>
/// App-side overlapping-bigram tokenizer for the FTS5 shadow (parity-map decision ③).
/// CJK runs become space-separated overlapping bigrams (a lone CJK char emits itself);
/// non-CJK runs pass through untouched for unicode61 to word-split/lowercase. The SAME
/// transform feeds indexing and querying, reproducing the Qt build's Xapian
/// <c>FLAG_CJK_NGRAM</c> recall. Pure function; lives in Core for headless tests.
/// </summary>
public static class BigramTokenizer
{
    /// <summary>Transform raw text into the space-separated token string stored in / matched against FTS5.</summary>
    public static string Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length + 16);
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            if (IsCjk(text[i]))
            {
                while (i < text.Length && IsCjk(text[i]))
                    i++;
                if (i - start == 1)
                {
                    AppendToken(sb, text.AsSpan(start, 1));
                }
                else
                {
                    for (var k = start; k < i - 1; k++)
                        AppendToken(sb, text.AsSpan(k, 2));
                }
            }
            else
            {
                while (i < text.Length && !IsCjk(text[i]) && !char.IsWhiteSpace(text[i]))
                    i++;
                AppendToken(sb, text.AsSpan(start, i - start));
            }
        }

        return sb.ToString();
    }

    private static void AppendToken(StringBuilder sb, ReadOnlySpan<char> token)
    {
        if (sb.Length > 0)
            sb.Append(' ');
        sb.Append(token);
    }

    private static bool IsCjk(char c) =>
        c is >= '一' and <= '鿿' // CJK Unified Ideographs
            or >= '㐀' and <= '䶿' // Extension A
            or >= '豈' and <= '﫿'; // Compatibility Ideographs

}
