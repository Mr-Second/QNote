using System.Text;

namespace QNote.Text;

/// <summary>One <c>{\pict …}</c> group found in an RTF string.</summary>
/// <param name="Alt">The <c>wzDescription</c> alt text, if any (empty otherwise).</param>
/// <param name="Sha256">Sha256 decoded from a <c>qnote:</c> alt, or <c>null</c>.</param>
/// <param name="Blip">Blip keyword (<c>pngblip</c>/<c>jpegblip</c>/…), or <c>null</c>.</param>
/// <param name="Bytes">
/// The decoded hex payload, or <c>null</c> when the group carries no recognizable
/// hex data (e.g. <c>\bin</c> raw runs). The save path hashes these bytes and
/// matches them against <c>note_images.display_bytes</c> — byte identity survives
/// RichEdit reloads, deletions, reordering and undo, none of which the alt marker
/// survives (msftedit rewrites it to "Image" on RTF parse).
/// </param>
public sealed record RtfPict(string Alt, string? Sha256, string? Blip, byte[]? Bytes);

/// <summary>
/// Extracts the embedded images from an RTF document. The save path hashes each
/// pict's bytes and matches them against <c>note_images.display_bytes</c> (byte
/// identity survives RichEdit reloads, unlike the <c>qnote:</c> alt marker, which
/// msftedit rewrites to "Image" on RTF parse); the alt remains the in-session
/// fallback for freshly inserted images.
///
/// Brace matching mirrors <see cref="RtfPictStripper"/> (escaped braces and
/// <c>\bin</c> raw-byte runs are honoured). Pure function — Core, unit-testable.
/// </summary>
public static class RtfPictInspector
{
    private const string PictMarker = "\\pict";

    /// <summary>Returns every <c>{\pict …}</c> group in document order.</summary>
    public static IReadOnlyList<RtfPict> FindPicts(string? rtf)
    {
        if (string.IsNullOrEmpty(rtf) || !rtf.Contains(PictMarker, StringComparison.Ordinal))
            return [];

        var list = new List<RtfPict>();
        var i = 0;
        while (i < rtf.Length)
        {
            var c = rtf[i];
            if (IsEscapeAt(rtf, i))
            {
                i += 2;
                continue;
            }
            if (c == '{' && IsPictGroupStart(rtf, i))
            {
                var end = SkipGroup(rtf, i);
                list.Add(Parse(rtf, i, end));
                i = end;
                continue;
            }
            i++;
        }
        return list;
    }

    /// <summary>Distinct sha256 digests referenced by a document's embedded images.</summary>
    public static IReadOnlyList<string> ReferencedSha256(string? rtf) =>
        FindPicts(rtf)
            .Where(p => p.Sha256 is not null)
            .Select(p => p.Sha256!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static RtfPict Parse(string rtf, int start, int groupEndExclusive)
    {
        var alt = ExtractAlt(rtf, start, groupEndExclusive);
        var blip = FindBlip(rtf, start, groupEndExclusive);
        return new RtfPict(alt, ImageAltCodec.TryDecode(alt), blip,
            ExtractHexBytes(rtf, start, groupEndExclusive, blip));
    }

    /// <summary>
    /// Decodes the hex image data of a pict group. Only direct children of the pict
    /// group count (depth 1): nested groups like <c>{\*\blipuid 123}</c> and the
    /// <c>{\*\picprop}</c> shape properties must not pollute the bytes. Control
    /// words at depth 1 (e.g. <c>\picw100</c> before the data) are skipped with
    /// their numeric parameter. A <c>\bin</c> raw run bails out with null.
    /// </summary>
    private static byte[]? ExtractHexBytes(string rtf, int start, int end, string? blip)
    {
        if (blip is null)
            return null;

        var idx = rtf.IndexOf('\\' + blip, start, StringComparison.Ordinal);
        if (idx < 0 || idx >= end)
            return null;

        var i = idx + 1 + blip.Length;
        var depth = 1; // inside the pict group
        var nibbles = new List<byte>(256);
        var nibble = (byte)0;
        var hasNibble = false;

        while (i < end)
        {
            var c = rtf[i];
            if (IsEscapeAt(rtf, i))
            {
                i += 2;
                continue;
            }

            if (c == '\\')
            {
                // \binN is followed by raw bytes — not hex-scannable.
                if (IsBinRun(rtf, i, out _))
                    return null;

                // Skip a control word: \ + letters + optional digits + one delimiter.
                i++;
                while (i < end && char.IsAsciiLetter(rtf[i]))
                    i++;
                while (i < end && char.IsAsciiDigit(rtf[i]))
                    i++;
                if (i < end && rtf[i] == ' ')
                    i++;
                continue;
            }

            if (c == '{')
            {
                depth++;
                i++;
                continue;
            }

            if (c == '}')
            {
                depth--;
                if (depth == 0)
                    break;
                i++;
                continue;
            }

            if (depth == 1 && Uri.IsHexDigit(c))
            {
                var value = (byte)Uri.FromHex(c);
                if (hasNibble)
                {
                    nibbles.Add((byte)(nibble << 4 | value));
                    hasNibble = false;
                }
                else
                {
                    nibble = value;
                    hasNibble = true;
                }
            }

            // Whitespace and anything else at depth 1 is skipped (hex payloads are
            // commonly wrapped across lines).
            i++;
        }

        if (hasNibble || nibbles.Count == 0)
            return null; // odd nibble count or no data at all — not a valid payload
        return nibbles.ToArray();
    }

    /// <summary>
    /// Reads <c>{\sp{\sn wzDescription}{\sv &lt;alt&gt;}}</c> inside the group's
    /// <c>picprop</c>. Scans for the <c>wzDescription</c> control word, then takes the
    /// following <c>{...}</c> group whose payload is the alt literal.
    /// </summary>
    private static string ExtractAlt(string rtf, int start, int end)
    {
        const string marker = "wzDescription";
        var idx = rtf.IndexOf(marker, start, StringComparison.Ordinal);
        if (idx < 0 || idx >= end)
            return string.Empty;

        // The alt literal is the next group after the marker: {\sv <text>}.
        var open = rtf.IndexOf('{', idx + marker.Length);
        if (open < 0 || open >= end)
            return string.Empty;

        var groupEnd = SkipGroup(rtf, open);
        var body = rtf[(open + 1)..Math.Min(groupEnd - 1 < 0 ? rtf.Length : groupEnd - 1, rtf.Length)];

        // Drop the leading "\sv " control word (and any trailing '}').
        body = body.TrimEnd('}');
        var sv = body.IndexOf('\\');
        if (sv >= 0)
        {
            var space = body.IndexOf(' ', sv);
            body = space >= 0 ? body[(space + 1)..] : string.Empty;
        }

        return UnescapeRtf(body).Trim();
    }

    private static string? FindBlip(string rtf, int start, int end)
    {
        foreach (var keyword in BlipKeywords)
        {
            var idx = rtf.IndexOf('\\' + keyword, start, StringComparison.Ordinal);
            if (idx < 0 || idx >= end)
                continue;
            var after = idx + 1 + keyword.Length;
            if (after >= rtf.Length || !char.IsAsciiLetter(rtf[after]))
                return keyword;
        }
        return null;
    }

    private static readonly string[] BlipKeywords =
    {
        "pngblip", "jpegblip", "emfblip", "wmetafile", "dibitmap", "wbitmap", "macpict", "pmmetafile",
    };

    /// <summary>Undoes RTF <c>\'hh</c> and <c>\{</c> <c>\}</c> <c>\\</c> escapes (alt text is plain ASCII in practice).</summary>
    private static string UnescapeRtf(string value)
    {
        if (value.IndexOf('\\') < 0)
            return value;

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c != '\\' || i + 1 >= value.Length)
            {
                sb.Append(c);
                continue;
            }
            var next = value[i + 1];
            if (next is '{' or '}' or '\\')
            {
                sb.Append(next);
                i++;
            }
            else if (next == '\'' && i + 3 < value.Length && Uri.IsHexDigit(value[i + 2]) && Uri.IsHexDigit(value[i + 3]))
            {
                sb.Append((char)Convert.ToInt32(value.Substring(i + 2, 2), 16));
                i += 3;
            }
            else
            {
                sb.Append(c);
            }
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
        return j >= rtf.Length || rtf[j] is ' ' or '\\' or '{' or '\r' or '\n';
    }

    /// <summary>Index just past the group's closing brace; end-of-string if unbalanced.</summary>
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
            if (c == '\\' && i + 1 < rtf.Length && rtf[i + 1] == 'b' && IsBinRun(rtf, i, out var skipTo))
            {
                i = skipTo;
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

    /// <summary>Recognizes <c>\binN </c> and returns the index past its N raw bytes.</summary>
    private static bool IsBinRun(string rtf, int backslash, out int skipTo)
    {
        skipTo = backslash;
        var j = backslash + 1;
        if (j + 3 > rtf.Length || rtf[j] != 'b' || rtf[j + 1] != 'i' || rtf[j + 2] != 'n')
            return false;
        j += 3;
        var numStart = j;
        while (j < rtf.Length && char.IsAsciiDigit(rtf[j]))
            j++;
        if (j == numStart || !int.TryParse(rtf[numStart..j], out var count))
            return false;
        if (j < rtf.Length && rtf[j] == ' ')
            j++;
        skipTo = (int)Math.Min(rtf.Length, j + count);
        return true;
    }

    private static bool IsEscapeAt(string rtf, int i) =>
        rtf[i] == '\\' && i + 1 < rtf.Length && rtf[i + 1] is '{' or '}' or '\\';
}
