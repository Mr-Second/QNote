using System.Text;

namespace QNote.Text;

/// <summary>
/// Removes <c>{\pict …}</c> groups from an RTF string via brace matching, so pasted
/// images are dropped while text formatting survives (image insertion is a separate
/// task). Escaped braces (<c>\{</c> <c>\}</c> <c>\\</c>) are honoured. Pure function.
/// Lives in Core (not Presentation, as the PRD sketched) so the headless xUnit suite —
/// which references QNote.Core only — can cover it.
/// </summary>
public static class RtfPictStripper
{
    private const string PictMarker = "\\pict";

    /// <summary>Returns <paramref name="rtf"/> with every <c>{\pict …}</c> group removed.</summary>
    public static string StripPictGroups(string rtf)
    {
        if (string.IsNullOrEmpty(rtf) || !rtf.Contains(PictMarker, StringComparison.Ordinal))
            return rtf;

        var sb = new StringBuilder(rtf.Length);
        var i = 0;
        while (i < rtf.Length)
        {
            var c = rtf[i];
            if (IsEscapeAt(rtf, i))
            {
                sb.Append(rtf, i, 2);
                i += 2;
                continue;
            }
            if (c == '{' && IsPictGroupStart(rtf, i))
            {
                i = SkipGroup(rtf, i);
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static bool IsPictGroupStart(string rtf, int braceIndex)
    {
        var j = braceIndex + 1;
        if (j + PictMarker.Length > rtf.Length)
            return false;
        if (string.CompareOrdinal(rtf, j, PictMarker, 0, PictMarker.Length) != 0)
            return false;
        j += PictMarker.Length;
        // The control word must end in a delimiter, otherwise e.g. "\picture" is not "\pict".
        return j >= rtf.Length || rtf[j] is ' ' or '\\' or '{' or '\r' or '\n';
    }

    // Index just past the group's closing brace; end-of-string if unbalanced.
    private static int SkipGroup(string rtf, int braceIndex)
    {
        var depth = 0;
        var i = braceIndex;
        while (i < rtf.Length)
        {
            var c = rtf[i];
            if (IsEscapeAt(rtf, i))
            {
                i += 2;
                continue;
            }
            if (c == '{')
                depth++;
            else if (c == '}' && --depth == 0)
                return i + 1;
            i++;
        }
        return rtf.Length;
    }

    private static bool IsEscapeAt(string rtf, int i) =>
        rtf[i] == '\\' && i + 1 < rtf.Length && rtf[i + 1] is '{' or '}' or '\\';
}
