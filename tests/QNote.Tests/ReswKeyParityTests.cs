using System.Xml.Linq;

namespace QNote.Tests;

/// <summary>
/// resw key-parity: the zh-Hans and en-US resource files must carry IDENTICAL key
/// sets — a key present in only one file means one language silently falls back
/// (zh) or leaks the raw key / English (en). Parses the .resw XML directly from
/// the repo tree (the test assembly runs from bin under the repo on CI and dev).
/// </summary>
public sealed class ReswKeyParityTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "QNote.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    private static string ReswPath(string language) =>
        Path.Combine(RepoRoot(), "src", "QNote", "Strings", language, "Resources.resw");

    private static IReadOnlyDictionary<string, string> LoadKeys(string language)
    {
        var path = ReswPath(language);
        Assert.True(File.Exists(path), $"Missing resw file: {path}");
        var doc = XDocument.Load(path);
        var keys = doc.Root!
            .Elements("data")
            .Where(d => (string?)d.Attribute("type") is null) // string resources only
            .ToDictionary(d => (string)d.Attribute("name")!, _ => path, StringComparer.Ordinal);
        return keys;
    }

    [Fact]
    public void Resw_KeySets_AreIdentical()
    {
        var en = LoadKeys("en-US");
        var zh = LoadKeys("zh-Hans");

        Assert.NotEmpty(en);
        Assert.Equal(en.Count, zh.Count);

        var onlyEn = en.Keys.Except(zh.Keys).ToList();
        var onlyZh = zh.Keys.Except(en.Keys).ToList();
        Assert.True(onlyEn.Count == 0 && onlyZh.Count == 0,
            $"resw key drift — only in en-US: [{string.Join(", ", onlyEn)}]; only in zh-Hans: [{string.Join(", ", onlyZh)}]");
    }

    [Fact]
    public void Resw_Keys_AreUniquePerFile()
    {
        // XElement would throw on a true duplicate only for identical names in the
        // same document — explicit check keeps the failure message readable.
        foreach (var language in new[] { "en-US", "zh-Hans" })
        {
            var doc = XDocument.Load(ReswPath(language));
            var names = doc.Root!.Elements("data").Select(d => (string)d.Attribute("name")!).ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Theory]
    [InlineData("SettingsLanguageLabel.Text")]
    [InlineData("TrayShowItem.Text")]
    [InlineData("NotesEmpty.Text")]
    [InlineData("CategoryAll")]
    [InlineData("CategoryWorkName")]
    [InlineData("CategoryLifeName")]
    [InlineData("CategoryImportantName")]
    [InlineData("BackupErrorNewerVersion")]
    [InlineData("RestoreAnalysisFormat")]
    [InlineData("CharsFormat")]
    [InlineData("UntitledNote")]
    [InlineData("HotkeyNotSet")]
    [InlineData("PortableFallbackContent")]
    public void Resw_CriticalKeys_ExistInBothFiles(string key)
    {
        Assert.Contains(key, (IEnumerable<string>)LoadKeys("en-US").Keys);
        Assert.Contains(key, (IEnumerable<string>)LoadKeys("zh-Hans").Keys);
    }
}