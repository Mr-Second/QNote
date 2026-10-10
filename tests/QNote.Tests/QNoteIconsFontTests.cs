using System.Reflection;
using System.Text;
using System.Text.Json;

namespace QNote.Tests;

/// <summary>
/// QNoteIcons font consistency: the code-point constants in QNoteIconGlyphs (link-compiled
/// here), the generator's scripts/icon-font/codepoints.json, and the shipped
/// QNoteIcons.ttf must agree exactly — a missing cmap entry renders as a silent tofu box in
/// the editor chrome, so any drift fails the build. Parses the TTF binary directly (sfnt
/// directory + head/cmap/name tables) with no font dependency.
/// </summary>
public sealed class QNoteIconsFontTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "QNote.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    private static byte[] FontBytes() =>
        File.ReadAllBytes(Path.Combine(RepoRoot(), "src", "QNote", "Assets", "Fonts", "QNoteIcons.ttf"));

    private static Dictionary<string, int> LoadCodepointsJson()
    {
        var path = Path.Combine(RepoRoot(), "scripts", "icon-font", "codepoints.json");
        Assert.True(File.Exists(path), $"Missing codepoints.json: {path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, int>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var value = prop.Value.GetString()!;
            Assert.True(value.StartsWith("U+", StringComparison.Ordinal), $"bad code point '{value}' for {prop.Name}");
            result[prop.Name] = int.Parse(value[2..], System.Globalization.NumberStyles.HexNumber, null);
        }
        return result;
    }

    private static Dictionary<string, int> LoadGlyphConstants()
    {
        return typeof(Controls.QNoteIconGlyphs).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(int))
            .ToDictionary(f => f.Name, f => (int)f.GetRawConstantValue()!, StringComparer.Ordinal);
    }

    // ---- minimal TrueType reader (big-endian, head/cmap/name walk only) ----

    private static uint ReadU32(byte[] b, int o) => (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);

    private static ushort ReadU16(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);

    private static int TableOffset(byte[] font, string tag)
    {
        var numTables = ReadU16(font, 4);
        for (var i = 0; i < numTables; i++)
        {
            var rec = 12 + i * 16;
            if (Encoding.ASCII.GetString(font, rec, 4) == tag)
                return (int)ReadU32(font, rec + 8);
        }
        throw new InvalidOperationException($"sfnt table '{tag}' not found");
    }

    private static ushort[] ReadCmapFormat4(byte[] font, int offset)
    {
        var segCount = ReadU16(font, offset + 6) / 2;
        var endCode = new ushort[segCount];
        var startCode = new ushort[segCount];
        var idDelta = new short[segCount];
        var idRangeOffset = new ushort[segCount];
        for (var i = 0; i < segCount; i++)
            endCode[i] = ReadU16(font, offset + 14 + i * 2);
        for (var i = 0; i < segCount; i++)
            startCode[i] = ReadU16(font, offset + 16 + segCount * 2 + i * 2);
        for (var i = 0; i < segCount; i++)
            idDelta[i] = (short)ReadU16(font, offset + 16 + segCount * 4 + i * 2);
        for (var i = 0; i < segCount; i++)
            idRangeOffset[i] = ReadU16(font, offset + 16 + segCount * 6 + i * 2);
        var glyphIdArrayOffset = offset + 16 + segCount * 8;

        var mapped = new List<ushort>();
        for (var i = 0; i < segCount; i++)
        {
            for (var c = (int)startCode[i]; c >= 0 && c <= endCode[i]; c++)
            {
                int gid;
                if (idRangeOffset[i] == 0)
                {
                    gid = (c + idDelta[i]) & 0xFFFF;
                }
                else
                {
                    // Index into glyphIdArray measured from the START of the idRangeOffset
                    // array (Apple TrueType spec), hence the - segCount.
                    var idx = idRangeOffset[i] / 2 + (c - startCode[i]) + i - segCount;
                    gid = ReadU16(font, glyphIdArrayOffset + idx * 2);
                    if (gid != 0)
                        gid = (gid + idDelta[i]) & 0xFFFF;
                }
                if (gid != 0)
                    mapped.Add((ushort)c);
            }
        }
        return mapped.ToArray();
    }

    private static SortedSet<ushort> ReadFontCmap(byte[] font)
    {
        var cmap = TableOffset(font, "cmap");
        var numTables = ReadU16(font, cmap + 2);
        int? best = null;
        for (var i = 0; i < numTables; i++)
        {
            var rec = cmap + 4 + i * 8;
            var platform = ReadU16(font, rec);
            var encoding = ReadU16(font, rec + 2);
            var offset = (int)ReadU32(font, rec + 4);
            var format = ReadU16(font, cmap + offset);
            if (format != 4)
                continue;
            if (platform == 3 && encoding == 1) // Windows Unicode BMP — preferred
                return new SortedSet<ushort>(ReadCmapFormat4(font, cmap + offset));
            if (best is null && (platform == 0 || platform == 3))
                best = cmap + offset;
        }
        Assert.NotNull(best);
        return new SortedSet<ushort>(ReadCmapFormat4(font, best!.Value));
    }

    private static List<string> ReadFamilyNames(byte[] font)
    {
        var name = TableOffset(font, "name");
        var count = ReadU16(font, name + 2);
        var stringOffset = name + ReadU16(font, name + 4);
        var families = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var rec = name + 6 + i * 12;
            var platform = ReadU16(font, rec);
            var nameId = ReadU16(font, rec + 6);
            var length = ReadU16(font, rec + 8);
            var offset = ReadU16(font, rec + 10);
            if (nameId != 1)
                continue;
            var bytes = new byte[length];
            Array.Copy(font, stringOffset + offset, bytes, 0, length);
            families.Add(platform == 3
                ? Encoding.BigEndianUnicode.GetString(bytes)
                : Encoding.ASCII.GetString(bytes));
        }
        return families;
    }

    // ---- assertions ----

    private static List<string> SortedKeys(Dictionary<string, int> d) =>
        d.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    [Fact]
    public void Glyphs_MatchCodepointsJson()
    {
        var json = LoadCodepointsJson();
        var constants = LoadGlyphConstants();

        Assert.NotEmpty(json);
        Assert.Equal(SortedKeys(json), SortedKeys(constants));

        foreach (var (slot, codepoint) in json)
            Assert.True(constants.TryGetValue(slot, out var constant) && constant == codepoint,
                $"constant/codepoints.json drift at {slot}: {constants.GetValueOrDefault(slot)} vs {codepoint}");

        var table = Controls.QNoteIconGlyphs.SlotCodepoints;
        Assert.Equal(SortedKeys(json), table.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList());
        foreach (var (slot, codepoint) in json)
            Assert.True(table.TryGetValue(slot, out var value) && value == codepoint,
                $"SlotCodepoints/codepoints.json drift at {slot}: {table.GetValueOrDefault(slot)} vs {codepoint}");
    }

    [Fact]
    public void MappedCodepoints_ExistInFontCmap()
    {
        var font = FontBytes();
        var head = TableOffset(font, "head");
        Assert.Equal(0x5F0F3CF5u, ReadU32(font, head + 12)); // TrueType magic
        Assert.Equal(1024, ReadU16(font, head + 18));         // unitsPerEm

        var fontCmap = ReadFontCmap(font);
        var mapped = Controls.QNoteIconGlyphs.SlotCodepoints.Values.Distinct().Select(v => (ushort)v).ToHashSet();
        Assert.NotEmpty(mapped);

        var missing = mapped.Where(cp => !fontCmap.Contains(cp)).ToList();
        Assert.True(missing.Count == 0,
            $"code points missing from QNoteIcons.ttf cmap (tofu): [{string.Join(", ", missing.Select(c => $"U+{c:X4}"))}]");

        var extra = fontCmap.Where(cp => !mapped.Contains(cp)).ToList();
        Assert.True(extra.Count == 0,
            $"cmap carries unmapped code points: [{string.Join(", ", extra.Select(c => $"U+{c:X4}"))}]");
    }

    [Fact]
    public void Font_FamilyName_IsQNoteIcons()
    {
        var families = ReadFamilyNames(FontBytes());
        Assert.NotEmpty(families);
        Assert.All(families, f => Assert.Equal("QNoteIcons", f));
    }
}
