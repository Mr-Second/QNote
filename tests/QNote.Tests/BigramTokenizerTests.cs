using QNote.Text;

namespace QNote.Tests;

/// <summary>The CJK bigram transform that indexing and querying share (parity: Xapian FLAG_CJK_NGRAM).</summary>
public sealed class BigramTokenizerTests
{
    [Fact]
    public void Tokenize_CjkRun_EmitsOverlappingBigrams() =>
        Assert.Equal("机器 器学 学习", BigramTokenizer.Tokenize("机器学习"));

    [Fact]
    public void Tokenize_LoneCjkChar_EmitsItself() =>
        Assert.Equal("学", BigramTokenizer.Tokenize("学"));

    [Fact]
    public void Tokenize_LatinAndDigits_PassThrough() =>
        Assert.Equal("hello world123", BigramTokenizer.Tokenize("hello world123"));

    [Fact]
    public void Tokenize_Mixed_SplitsCjkAndLatinRuns() =>
        Assert.Equal("买牛 牛奶 buy milk", BigramTokenizer.Tokenize("买牛奶buy milk"));

    [Fact]
    public void Tokenize_Punctuation_StaysInNonCjkRun() =>
        // unicode61 will further split punctuation at index/query time; we pass runs through.
        Assert.Equal("e-mail 测试", BigramTokenizer.Tokenize("e-mail测试"));

    [Fact]
    public void Tokenize_Whitespace_Collapses() =>
        Assert.Equal("学习 todo", BigramTokenizer.Tokenize("  学习 \t\n todo "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Tokenize_Blank_ReturnsEmpty(string? input) =>
        Assert.Equal(string.Empty, BigramTokenizer.Tokenize(input));
}
