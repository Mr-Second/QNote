using QNote.Text;

namespace QNote.Tests;

/// <summary>Whole-keyword case-insensitive splitting for result highlighting (Qt parity).</summary>
public sealed class HighlightSplitterTests
{
    [Fact]
    public void Split_MatchInMiddle_MarksHitRun()
    {
        var segments = HighlightSplitter.Split("今天要买牛奶", "买牛奶");

        Assert.Equal([("今天要", false), ("买牛奶", true)], segments);
    }

    [Fact]
    public void Split_CaseInsensitive_MatchesLatin()
    {
        var segments = HighlightSplitter.Split("Buy MILK today", "milk");

        Assert.Equal([("Buy ", false), ("MILK", true), (" today", false)], segments);
    }

    [Fact]
    public void Split_RepeatedHits_MarksAll()
    {
        var segments = HighlightSplitter.Split("ab ab", "ab");

        Assert.Equal([("ab", true), (" ", false), ("ab", true)], segments);
    }

    [Fact]
    public void Split_NoMatch_SingleNonHitRun() =>
        Assert.Equal([("你好", false)], HighlightSplitter.Split("你好", "机器学习"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Split_BlankKeyword_SingleNonHitRun(string? keyword) =>
        Assert.Equal([("文本", false)], HighlightSplitter.Split("文本", keyword));

    [Fact]
    public void Split_EmptyText_NoRuns() =>
        Assert.Empty(HighlightSplitter.Split("", "key"));
}
