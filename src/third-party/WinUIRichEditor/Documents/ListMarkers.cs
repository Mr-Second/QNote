using System.Text;

namespace WinUIRichEditor.Documents;

/// <summary>Pure logic for a list item's marker text (bullet glyph or formatted number). Shared by the
/// control's rendering and the HTML/RTF formatters so they always agree on the marker string.</summary>
internal static class ListMarkers
{
    /// <summary>The marker text for a list item: a bullet glyph or a formatted number, per the
    /// paragraph's <see cref="ListMarkerStyle"/> (Default = • for bullets, "N." for numbers).</summary>
    public static string Text(ListKind kind, ListMarkerStyle style, int num)
    {
        if (kind == ListKind.Bullet)
            return style switch
            {
                ListMarkerStyle.Circle => "◦",
                ListMarkerStyle.Square => "▪",
                ListMarkerStyle.Dash => "–",
                _ => "•",
            };
        return style switch
        {
            ListMarkerStyle.DecimalParen => $"{num})",
            ListMarkerStyle.LowerAlpha => $"{ToAlpha(num, false)})",
            ListMarkerStyle.UpperAlpha => $"{ToAlpha(num, true)})",
            ListMarkerStyle.LowerRoman => $"{ToRoman(num)})",
            _ => $"{num}.",
        };
    }

    /// <summary>1→a, 26→z, 27→aa (bijective base-26), upper- or lower-case.</summary>
    public static string ToAlpha(int n, bool upper)
    {
        if (n < 1) n = 1;
        var sb = new StringBuilder();
        while (n > 0) { n--; sb.Insert(0, (char)((upper ? 'A' : 'a') + n % 26)); n /= 26; }
        return sb.ToString();
    }

    /// <summary>1→i, 4→iv, 9→ix (lowercase Roman numerals).</summary>
    public static string ToRoman(int n)
    {
        if (n < 1) return n.ToString();
        int[] vals = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
        string[] syms = { "m", "cm", "d", "cd", "c", "xc", "l", "xl", "x", "ix", "v", "iv", "i" };
        var sb = new StringBuilder();
        for (int i = 0; i < vals.Length && n > 0; i++)
            while (n >= vals[i]) { sb.Append(syms[i]); n -= vals[i]; }
        return sb.ToString();
    }
}
