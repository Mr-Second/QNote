using System.Security.Cryptography;
using QNote.Markdown;
using QNote.WreMarkdown;
using Windows.UI.Text;
using WinUIRichEditor.Documents;
using Xunit;

namespace QNote.WreMarkdown.Tests;

/// <summary>
/// MD→WRE→MD round-trip matrix for <see cref="MarkdownDocumentFormatter"/> — the
/// headless half of the WRE adoption acceptance (PRD step ②). The md subset: flat
/// bullet/ordered lists, headings, bold/italic/strikethrough, inline links and
/// <c>qnote-img:</c> image references; the case battery mirrors
/// <c>tests/QNote.Tests/Markdown/MarkdownConversionTests.cs</c> (the B2 matrix)
/// plus the new link axis.
/// </summary>
public class WreMarkdownRoundTripTests
{
    /// <summary>A real 2×2 PNG (upstream WRE test corpus) whose sha is resolvable.</summary>
    internal static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGP8//8/AzJgYkAD5AsAAP//A+8DTgn2rL0AAAAASUVORK5CYII=");

    internal static readonly byte[] Junk = [1, 2, 3, 4, 5, 6, 7, 8];

    internal static readonly string PngSha = Convert.ToHexStringLower(SHA256.HashData(Png));
    internal static readonly string JunkSha = Convert.ToHexStringLower(SHA256.HashData(Junk));

    /// <summary>The image wiring the save/load paths will have: sha → original bytes.</summary>
    internal static readonly MarkdownDocumentFormatter.ImageBytesProvider Provider = sha =>
        sha == PngSha ? Png : sha == JunkSha ? Junk : null;

    internal static DocumentContent Parse(string md) => MarkdownParser.Parse(md);

    /// <summary>md → QNote model → WRE document → QNote model → md.</summary>
    internal static string RoundTrip(string md) =>
        MarkdownEmitter.Emit(MarkdownDocumentFormatter.ToDocumentContent(
            MarkdownDocumentFormatter.ToFlowDocument(Parse(md), Provider)));

    // ---- zero-loss battery -------------------------------------------------------------------

    public static TheoryData<string> CanonicalSamples() => new()
    {
        // Canonical samples only: what Emit(Parse(x)) already returns for x (the
        // blank-line-free, marker-order-normal form the pipeline uses). The parser
        // collapses blank lines, so none appear here.
        "plain",
        "中英混排 mixed content 2026",
        "# 标题\n正文",
        "## 二级\n### 三级",
        "**粗** *斜* ~~删~~ 普通",
        "**~~both~~**",
        "***bi***",
        "*i ****bi**** i*",
        "line one\nline two",
        "- 项目一\n- 项目二\n- 项目三",
        "1. 第一\n2. 第二\n3. 第三",
        "1. a\n- b",
        "1. 第一\n2. 第二\n- 无序一\n- 无序二",
        "[链接文字](https://example.com/path?x=1)",
        "**[粗体链接](https://example.com)** and [b](https://y.io)",
        "# [标题链接](https://example.com)",
        $"![截图](qnote-img:{PngSha})",
        $"前 ![行内图](qnote-img:{PngSha}) 后",
        $"![乱字节](qnote-img:{JunkSha})",
        "# 一级\n正文 **粗** 和 [链接](https://example.com/a?b=1)\n- 无序 **粗项**\n- 第二项\n1. 第一项\n2. 第二项\n结尾",
    };

    [Theory]
    [MemberData(nameof(CanonicalSamples))]
    public void MdRoundTripsThroughWreWithoutLoss(string sample)
    {
        Assert.Equal(sample + "\n", RoundTrip(sample));
    }

    [Theory]
    [MemberData(nameof(CanonicalSamples))]
    public void RoundTripIsIdempotent(string sample)
    {
        var once = RoundTrip(sample);
        Assert.Equal(once, RoundTrip(once));
    }

    [Theory]
    [MemberData(nameof(CanonicalSamples))]
    public void WreDocumentModelSurvivesTheHop(string sample)
    {
        // The formatter must not collapse two blocks into one (or vice versa) even
        // when the emitted md is identical: re-parse of the emitted text goes through
        // the same parser, so block-by-block shape is pinned here, not just strings.
        var expected = Parse(sample).Blocks;
        var actual = MarkdownDocumentFormatter.ToDocumentContent(
            MarkdownDocumentFormatter.ToFlowDocument(Parse(sample), Provider)).Blocks;

        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Kind, actual[i].Kind);
            Assert.Equal(expected[i].Level, actual[i].Level);
            Assert.Equal(expected[i].Ordered, actual[i].Ordered);
            Assert.Equal(expected[i].ListNumber, actual[i].ListNumber);
            Assert.Equal(expected[i].Inlines.Count, actual[i].Inlines.Count);
            for (var j = 0; j < expected[i].Inlines.Count; j++)
            {
                Assert.Equal(expected[i].Inlines[j], actual[i].Inlines[j]); // records: value equality
            }
        }
    }

    [Fact]
    public void KitchenSinkDocumentRoundTrips()
    {
        var md = "# 一级标题\n## 二级标题\n"
            + "正文 **粗体** *斜体* ~~删除~~ 与 [行内链接](https://example.com/a?b=1) 混排\n"
            + "软换行一行\n软换行两行\n"
            + "- 无序项 **粗体**\n- 无序项二 ~~删除~~\n"
            + "1. 第一项\n2. 第二项 [带链接](https://example.com)\n3. 第三项\n"
            + $"![便签截图](qnote-img:{PngSha})\n结尾文字 前 ![小图](qnote-img:{PngSha}) 后";
        Assert.Equal(md + "\n", RoundTrip(md));
    }

    // ---- load direction: model shape behind the WRE document --------------------------------

    [Fact]
    public void HeadingsAndListsTakeLevelsAndKinds()
    {
        var document = MarkdownDocumentFormatter.ToFlowDocument(Parse("# head\n- a\n1. b"));

        Assert.Equal(3, document.Blocks.Count);
        var heading = Assert.IsType<Paragraph>(document.Blocks[0]);
        Assert.Equal(1, heading.HeadingLevel);
        Assert.Equal(ListKind.None, heading.ListType);
        var bullet = Assert.IsType<Paragraph>(document.Blocks[1]);
        Assert.Equal(ListKind.Bullet, bullet.ListType);
        Assert.Equal(ListMarkerStyle.Default, bullet.ListMarker);
        var ordered = Assert.IsType<Paragraph>(document.Blocks[2]);
        Assert.Equal(ListKind.Ordered, ordered.ListType);
    }

    [Fact]
    public void RunsCarryCharacterFormats()
    {
        var paragraph = Assert.IsType<Paragraph>(
            MarkdownDocumentFormatter.ToFlowDocument(Parse("**b** *i* ~~s~~ [l](https://x.io)")).Blocks[0]);
        var runs = paragraph.Inlines.Cast<Run>().ToList();

        Assert.Equal(700u, runs.Single(r => r.Text == "b").FontWeight.Weight);
        Assert.Equal(FontStyle.Italic, runs.Single(r => r.Text == "i").FontStyle);
        Assert.Equal(TextDecorationFlags.Strikethrough, runs.Single(r => r.Text == "s").TextDecorations);
        Assert.Equal("https://x.io", runs.Single(r => r.Text == "l").NavigateUri);
    }

    [Fact]
    public void SoftBreaksStayInsideRunText()
    {
        // WRE stores Shift+Enter soft breaks as '\n' inside Run.Text — the same
        // convention as the QNote model, so the break maps without splitting.
        var paragraph = Assert.IsType<Paragraph>(
            MarkdownDocumentFormatter.ToFlowDocument(Parse("line one\nline two")).Blocks[0]);
        var run = Assert.IsType<Run>(Assert.Single(paragraph.Inlines));
        Assert.Equal("line one\nline two", run.Text);
    }

    [Fact]
    public void ImagesResolveBytesGeometryAndMimeThroughProvider()
    {
        var paragraph = Assert.IsType<Paragraph>(MarkdownDocumentFormatter
            .ToFlowDocument(Parse($"![截图](qnote-img:{PngSha})"), Provider).Blocks[0]);
        var image = Assert.IsType<InlineImage>(Assert.Single(paragraph.Inlines));

        Assert.Equal(Png, image.RawBytes);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal("截图", image.AltText);
        // px @ 96 DPI = DIP — the RTF-era display geometry convention.
        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
    }

    [Fact]
    public void UnresolvedImagesKeepAltTextOnly()
    {
        // No provider (or a miss): the reference still becomes an image element, so
        // the editor shows the placeholder and the save path degrades to alt text.
        var paragraph = Assert.IsType<Paragraph>(MarkdownDocumentFormatter
            .ToFlowDocument(Parse($"![alt](qnote-img:{PngSha})"), null).Blocks[0]);
        var image = Assert.IsType<InlineImage>(Assert.Single(paragraph.Inlines));

        Assert.Null(image.RawBytes);
        Assert.Equal("alt", image.AltText);
    }

    [Fact]
    public void UnrecognizedImageBytesFallBackToPngMimeAndDefaultGeometry()
    {
        var paragraph = Assert.IsType<Paragraph>(MarkdownDocumentFormatter
            .ToFlowDocument(Parse($"![bin](qnote-img:{JunkSha})"), Provider).Blocks[0]);
        var image = Assert.IsType<InlineImage>(Assert.Single(paragraph.Inlines));

        Assert.Equal(Junk, image.RawBytes);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(16, image.Width);
        Assert.Equal(16, image.Height);
    }

    // ---- save direction: WRE → model semantics ----------------------------------------------

    private static FlowDocument Document(params Block[] blocks)
    {
        var document = new FlowDocument();
        foreach (var block in blocks)
            document.Blocks.Add(block);
        return document;
    }

    private static Paragraph Para(int heading = 0, ListKind list = ListKind.None, params Run[] runs)
    {
        var p = new Paragraph { HeadingLevel = heading, ListType = list };
        foreach (var run in runs)
            p.Inlines.Add(run);
        return p;
    }

    private static Run Text(string text, ushort weight = 400, FontStyle style = FontStyle.Normal,
        TextDecorationFlags decorations = TextDecorationFlags.None, string? uri = null) =>
        new()
        {
            Text = text,
            FontWeight = new FontWeight { Weight = weight },
            FontStyle = style,
            TextDecorations = decorations,
            NavigateUri = uri,
        };

    private static IReadOnlyList<DocumentBlock> Save(FlowDocument document) =>
        MarkdownDocumentFormatter.ToDocumentContent(document).Blocks;

    [Fact]
    public void OrderedItemsNumberConsecutively()
    {
        var blocks = Save(Document(
            Para(list: ListKind.Ordered, runs: Text("a")),
            Para(list: ListKind.Ordered, runs: Text("b")),
            Para(list: ListKind.Ordered, runs: Text("c"))));

        Assert.All(blocks, b => Assert.Equal(BlockKind.ListItem, b.Kind));
        Assert.Equal([1, 2, 3], blocks.Select(b => b.ListNumber));
    }

    [Fact]
    public void NumberingResetsWhenAnyBlockIntervenes()
    {
        // A paragraph — or a switch of kind — ends the md list, so the count
        // restarts. Bullets get ordinals too: the parser numbers every item of a
        // list even where the emitter ignores the number.
        var blocks = Save(Document(
            Para(list: ListKind.Ordered, runs: Text("a")),
            Para(runs: Text("x")),
            Para(list: ListKind.Ordered, runs: Text("b")),
            Para(list: ListKind.Ordered, runs: Text("c")),
            Para(list: ListKind.Bullet, runs: Text("-")),
            Para(list: ListKind.Bullet, runs: Text("--")),
            Para(list: ListKind.Ordered, runs: Text("d"))));

        Assert.Equal([1, 1, 2, 1, 2, 1],
            blocks.Where(b => b.Kind == BlockKind.ListItem).Select(b => b.ListNumber));
    }

    [Fact]
    public void DroppedBlocksStayTransparentForNumbering()
    {
        // Dividers and empty paragraphs emit nothing, so they must not break the
        // list the way a real block would (else the numbers shift on next save).
        var blocks = Save(Document(
            Para(list: ListKind.Ordered, runs: Text("a")),
            new DividerBlock(),
            new Paragraph(),
            Para(list: ListKind.Ordered, runs: Text("b"))));

        var items = blocks.Where(b => b.Kind == BlockKind.ListItem).ToList();
        Assert.Equal([1, 2], items.Select(b => b.ListNumber));
    }

    [Fact]
    public void NestedOrderedLevelsFlattenIntoOneSequentialList()
    {
        // The md subset has no nesting; flattened ordered items join one list and
        // must come out sequential, or re-parsing would renumber them.
        var nested = Para(list: ListKind.Ordered, runs: Text("nested"));
        nested.ListLevel = 1;
        var blocks = Save(Document(
            Para(list: ListKind.Ordered, runs: Text("top")),
            nested));

        Assert.Equal([1, 2], blocks.Select(b => b.ListNumber));
    }

    [Fact]
    public void HeadingRunBoldIsStrippedButItalicStrikeAndLinksSurvive()
    {
        // md's "# " renders bold+large by itself and the editor re-applies the
        // preset (HeadingStyle.Materialize), so run bold inside a heading is marker
        // noise. Italic/strike/links are independent data and must survive.
        var blocks = Save(Document(
            Para(heading: 1, runs: Text("plain", weight: 700)),
            Para(heading: 2, runs: Text("styled", weight: 700, style: FontStyle.Italic,
                decorations: TextDecorationFlags.Strikethrough, uri: "https://x.io")),
            Para(runs: Text("body bold", weight: 700))));

        var headingPlain = Assert.IsType<DocumentRun>(Assert.Single(blocks[0].Inlines));
        Assert.False(headingPlain.Bold);

        var headingStyled = Assert.IsType<DocumentRun>(Assert.Single(blocks[1].Inlines));
        Assert.False(headingStyled.Bold);
        Assert.True(headingStyled.Italic);
        Assert.True(headingStyled.Strikethrough);
        Assert.Equal("https://x.io", headingStyled.NavigateUri);

        var body = Assert.IsType<DocumentRun>(Assert.Single(blocks[2].Inlines));
        Assert.True(body.Bold);
    }

    [Fact]
    public void BoldHeadingRoundTripsWithoutMarkerNoise()
    {
        var flow = MarkdownDocumentFormatter.ToFlowDocument(Parse("# 标题"));
        Assert.Equal("# 标题\n", MarkdownEmitter.Emit(MarkdownDocumentFormatter.ToDocumentContent(flow)));
    }

    [Fact]
    public void DeepWreHeadingClampsToH3()
    {
        // Mirrors the parser's `##### five` → H3 clamp — level 4+ references would be
        // re-clamped by the storage side anyway.
        var blocks = Save(Document(Para(heading: 5, runs: Text("deep"))));
        Assert.Equal(BlockKind.Heading, Assert.Single(blocks).Kind);
        Assert.Equal(HeadingLevel.H3, blocks[0].Level);
        Assert.Equal("### deep\n", MarkdownEmitter.Emit(new DocumentContent(blocks)));
    }

    [Fact]
    public void HeadingInsideListItemDegradesToListItem()
    {
        // The same loss the Markdown parser applies to a heading inside a list item.
        var blocks = Save(Document(Para(heading: 2, list: ListKind.Bullet, runs: Text("x"))));
        Assert.Equal(BlockKind.ListItem, Assert.Single(blocks).Kind);
    }

    [Fact]
    public void WreRunSplittingMergesBackToParserCanonicalRuns()
    {
        // The editor splits runs arbitrarily (edits, undo); the model must collapse
        // adjacent same-format runs the way the parser would.
        var blocks = Save(Document(Para(runs: [Text("a"), Text("b")])));
        var run = Assert.IsType<DocumentRun>(Assert.Single(Assert.Single(blocks).Inlines));
        Assert.Equal("ab", run.Text);
    }

    [Fact]
    public void UnderlineIsOutOfSubsetAndDegrades()
    {
        var blocks = Save(Document(Para(runs: Text("u", decorations: TextDecorationFlags.Underline))));
        var run = Assert.IsType<DocumentRun>(Assert.Single(Assert.Single(blocks).Inlines));
        Assert.Equal("u", run.Text);
        Assert.False(run.Strikethrough); // underline itself has no md representation
    }

    [Fact]
    public void QuoteParagraphsFlattenLikeTheParserFlattensQuoteBlocks()
    {
        var quote = Para(runs: Text("q"));
        quote.IsQuote = true;

        var blocks = Save(Document(quote));
        var block = Assert.Single(blocks);
        Assert.Equal(BlockKind.Paragraph, block.Kind);
        Assert.Equal("q\n", MarkdownEmitter.Emit(new DocumentContent(blocks)));
    }

    [Fact]
    public void TableFlattensCellTextWithoutTheGrid()
    {
        var table = new TableBlock(2, 1);
        for (var r = 0; r < 2; r++)
        {
            table.Cells[r][0].Blocks.Clear();
            table.Cells[r][0].Blocks.Add(Para(runs: Text($"cell-{r}")));
        }

        var blocks = Save(Document(Para(runs: Text("head")), table));
        Assert.Equal(["head", "cell-0", "cell-1"],
            blocks.SelectMany(b => b.Inlines.OfType<DocumentRun>()).Select(r => r.Text).ToArray());
    }

    [Fact]
    public void InlineTableContentLandsAfterItsHostParagraph()
    {
        var table = new TableBlock(1, 1);
        table.Cells[0][0].Blocks.Clear();
        table.Cells[0][0].Blocks.Add(Para(runs: Text("cell")));

        var host = Para(runs: Text("host"));
        host.Inlines.Add(new InlineTable { Table = table });

        var blocks = Save(Document(host));
        Assert.Equal(["host", "cell"],
            blocks.SelectMany(b => b.Inlines.OfType<DocumentRun>()).Select(r => r.Text).ToArray());
    }

    [Fact]
    public void BlockImageBecomesAReferenceParagraph()
    {
        // md has only the inline reference form, so a block picture and an inline
        // picture are the same storage — the reference survives either way.
        var image = new ImageBlock { AltText = "blocky" };
        image.SetImageData(Png, "image/png");

        var md = MarkdownEmitter.Emit(MarkdownDocumentFormatter.ToDocumentContent(Document(image)));
        Assert.Equal($"![blocky](qnote-img:{PngSha})\n", md);
    }

    [Fact]
    public void BytelessImageDegradesToAltTextOrDrops()
    {
        var alt = new InlineImage { AltText = "备用文字" };
        var none = new InlineImage();

        var host = Para(runs: Text("a"));
        host.Inlines.Add(alt);
        host.Inlines.Add(none);

        var md = MarkdownEmitter.Emit(MarkdownDocumentFormatter.ToDocumentContent(Document(host)));
        Assert.Equal("a备用文字\n", md);
    }
}

/// <summary>
/// Core-side tests for the inline-link axis the bridge needs (added to the locked
/// md subset 2026-09-30). Lives here so tests/QNote.Tests stays byte-identical.
/// </summary>
public class MarkdownLinkTests
{
    private static DocumentContent Parse(string md) => MarkdownParser.Parse(md);

    private static string Emit(string md) => MarkdownEmitter.Emit(MarkdownParser.Parse(md));

    [Fact]
    public void TextLinkParsesToLinkedRun()
    {
        var run = Assert.IsType<DocumentRun>(Assert.Single(Parse("[docs](https://example.com)").Blocks[0].Inlines));
        Assert.Equal("docs", run.Text);
        Assert.Equal("https://example.com", run.NavigateUri);
    }

    [Fact]
    public void BoldInsideLinkCombinesFlags()
    {
        var run = Assert.IsType<DocumentRun>(Assert.Single(Parse("[**b**](https://x.io)").Blocks[0].Inlines));
        Assert.True(run.Bold);
        Assert.Equal("https://x.io", run.NavigateUri);
    }

    [Fact]
    public void AdjacentRunsKeepSeparateTargets()
    {
        // Different link targets must never merge, or one link would swallow the other.
        var inlines = Parse("[a](https://x.io)[b](https://y.io)").Blocks[0].Inlines;
        var runs = inlines.Cast<DocumentRun>().ToList();
        Assert.Equal(2, runs.Count);
        Assert.Equal("https://x.io", runs[0].NavigateUri);
        Assert.Equal("https://y.io", runs[1].NavigateUri);
    }

    [Fact]
    public void EmptyUrlLinkDegradesToPlainText()
    {
        var run = Assert.IsType<DocumentRun>(Assert.Single(Parse("[x]()").Blocks[0].Inlines));
        Assert.Null(run.NavigateUri);
    }

    [Fact]
    public void ForeignImageLinkStillDegradesToText()
    {
        var inlines = Parse("![x](https://example.com/a.png)").Blocks[0].Inlines;
        Assert.DoesNotContain(inlines, i => i is DocumentImage);
        Assert.Contains(inlines, i => i is DocumentRun);
    }

    [Fact]
    public void EmittedLinkRoundTripsByteForByte()
    {
        Assert.Equal("[docs](https://example.com/path)\n", Emit("[docs](https://example.com/path)"));
    }

    [Fact]
    public void BoldLinkedRunEmitsLinkInsideEmphasis()
    {
        var model = new DocumentContent(
        [
            new DocumentBlock(BlockKind.Paragraph,
                [new DocumentRun("b", Bold: true, NavigateUri: "https://x.io")]),
        ]);
        Assert.Equal("**[b](https://x.io)**\n", MarkdownEmitter.Emit(model));
    }

    [Fact]
    public void UrlBreakingCharactersArePercentEncoded()
    {
        var model = new DocumentContent(
        [
            new DocumentBlock(BlockKind.Paragraph,
                [new DocumentRun("t", NavigateUri: "https://x.io/a(b) c\\d")]),
        ]);
        Assert.Equal("[t](https://x.io/a%28b%29%20c%5Cd)\n", MarkdownEmitter.Emit(model));
    }

    [Fact]
    public void AutoLinkNormalizesToTheReferenceFormAndStaysStable()
    {
        var once = Emit("<https://example.com>");
        Assert.Equal("[https://example.com](https://example.com)\n", once);
        Assert.Equal(once, Emit(once.TrimEnd('\n')));
    }
}
