using QNote.Markdown;

namespace QNote.Tests.Markdown;

public class MarkdownParserTests
{
    private static DocumentContent Parse(string md) => MarkdownParser.Parse(md);

    private static string RunText(DocumentBlock block) =>
        string.Concat(block.Inlines.OfType<DocumentRun>().Select(r => r.Text));

    [Fact]
    public void EmptyMarkdownYieldsNoBlocks()
    {
        Assert.Empty(Parse("").Blocks);
        Assert.Empty(Parse("\n\n\n").Blocks);
    }

    [Fact]
    public void PlainParagraphBecomesOneRun()
    {
        var content = Parse("hello world");
        var block = Assert.Single(content.Blocks);
        Assert.Equal(BlockKind.Paragraph, block.Kind);
        var run = Assert.IsType<DocumentRun>(Assert.Single(block.Inlines));
        Assert.Equal("hello world", run.Text);
        Assert.False(run.Bold);
    }

    [Fact]
    public void BoldItalicStrikethroughAreDetected()
    {
        var run = Assert.IsType<DocumentRun>(Assert.Single(Parse("**b**").Blocks[0].Inlines));
        Assert.True(run.Bold);

        run = Assert.IsType<DocumentRun>(Assert.Single(Parse("*i*").Blocks[0].Inlines));
        Assert.True(run.Italic);

        run = Assert.IsType<DocumentRun>(Assert.Single(Parse("~~s~~").Blocks[0].Inlines));
        Assert.True(run.Strikethrough);
    }

    [Fact]
    public void BoldInsideStrikethroughCombinesFlags()
    {
        var run = Assert.IsType<DocumentRun>(Assert.Single(Parse("~~**x**~~").Blocks[0].Inlines));
        Assert.True(run.Bold);
        Assert.True(run.Strikethrough);
        Assert.Equal("x", run.Text);
    }

    [Fact]
    public void MixedRunsKeepAdjacentOrder()
    {
        var inlines = Parse("a **b** c").Blocks[0].Inlines;
        Assert.Equal(new[] { "a ", "b", " c" },
            inlines.OfType<DocumentRun>().Select(r => r.Text).ToArray());
    }

    [Fact]
    public void HeadingLevelsMapWithClampAboveH3()
    {
        Assert.Equal(HeadingLevel.H1, Parse("# one").Blocks[0].Level);
        Assert.Equal(HeadingLevel.H2, Parse("## two").Blocks[0].Level);
        Assert.Equal(HeadingLevel.H3, Parse("### three").Blocks[0].Level);
        Assert.Equal(HeadingLevel.H3, Parse("##### five").Blocks[0].Level);
        Assert.Equal(BlockKind.Heading, Parse("## two").Blocks[0].Kind);
    }

    [Fact]
    public void BulletListItemsKeepOrderAndMarkerKind()
    {
        var blocks = Parse("- a\n- b\n- c").Blocks;
        Assert.Equal(3, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(BlockKind.ListItem, b.Kind));
        Assert.All(blocks, b => Assert.False(b.Ordered));
        Assert.Equal(new[] { "a", "b", "c" }, blocks.Select(RunText).ToArray());
    }

    [Fact]
    public void OrderedListCarriesNumbers()
    {
        var blocks = Parse("1. one\n2. two").Blocks;
        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, b => Assert.True(b.Ordered));
        Assert.Equal(1, blocks[0].ListNumber);
        Assert.Equal(2, blocks[1].ListNumber);
    }

    [Fact]
    public void QnoteImageReferenceBecomesDocumentImage()
    {
        const string sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var image = Assert.IsType<DocumentImage>(Assert.Single(Parse($"![截图]({MarkdownParser.ImageSchemePrefix}{sha})").Blocks[0].Inlines));
        Assert.Equal(sha, image.Sha256);
        Assert.Equal("截图", image.AltText);
    }

    [Fact]
    public void ForeignImageLinkDegradesToText()
    {
        var inlines = Parse("![x](https://example.com/a.png)").Blocks[0].Inlines;
        Assert.DoesNotContain(inlines, i => i is DocumentImage);
        Assert.Contains(inlines, i => i is DocumentRun);
    }

    [Fact]
    public void SoftBreakBecomesNewlineRun()
    {
        var run = Assert.IsType<DocumentRun>(Assert.Single(Parse("line one\nline two").Blocks[0].Inlines));
        Assert.Equal("line one\nline two", run.Text);
    }

    [Fact]
    public void EscapedMarkersParseAsLiteralText()
    {
        var run = Assert.IsType<DocumentRun>(Assert.Single(Parse(@"\*\*not bold\*\*").Blocks[0].Inlines));
        Assert.Equal("**not bold**", run.Text);
        Assert.False(run.Bold);
    }
}

public class MarkdownEmitterTests
{
    private static string Emit(string md) =>
        MarkdownEmitter.Emit(MarkdownParser.Parse(md));

    [Fact]
    public void EmitRoundTripsPlainParagraph()
    {
        Assert.Equal("hello world\n", Emit("hello world"));
    }

    [Fact]
    public void EmitRoundTripsAllInlineFormats()
    {
        Assert.Equal("**b**\n", Emit("**b**"));
        Assert.Equal("*i*\n", Emit("*i*"));
        Assert.Equal("~~s~~\n", Emit("~~s~~"));
        // Combined flags re-emit in canonical bold-outer order; semantically identical
        // (bold+strike) and stable under further round-trips.
        Assert.Equal("**~~x~~**\n", Emit("~~**x**~~"));
        Assert.Equal("***bi***\n", Emit("***bi***"));
    }

    [Fact]
    public void EmitRoundTripIsIdempotent()
    {
        string[] samples =
        [
            "~~**x**~~",
            "***bi***",
            "*i **bi** i*",
            "**粗** *斜* ~~删~~ 普通",
        ];

        foreach (var sample in samples)
        {
            var once = Emit(sample);
            Assert.Equal(once, Emit(once));
        }
    }

    [Fact]
    public void EmitRoundTripsHeadingsAndLists()
    {
        Assert.Equal("# a\n", Emit("# a"));
        Assert.Equal("### c\n", Emit("### c"));
        Assert.Equal("- x\n- y\n", Emit("- x\n- y"));
        Assert.Equal("1. a\n2. b\n", Emit("1. a\n2. b"));
    }

    [Fact]
    public void EmitRoundTripsImageReference()
    {
        const string sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        Assert.Equal($"![alt]({MarkdownParser.ImageSchemePrefix}{sha})\n", Emit($"![alt]({MarkdownParser.ImageSchemePrefix}{sha})"));
    }

    [Fact]
    public void EmitEscapesMarkerCharactersInText()
    {
        var model = new DocumentContent(
        [
            new DocumentBlock(BlockKind.Paragraph,
            [
                new DocumentRun("a*b_c~d[e]f`g\\h"),
            ]),
        ]);
        Assert.Equal(@"a\*b\_c\~d\[e\]f\`g\\h" + "\n", MarkdownEmitter.Emit(model));
    }

    [Fact]
    public void EmitEscapesLineStartListMarker()
    {
        var model = new DocumentContent(
        [
            new DocumentBlock(BlockKind.Paragraph, [new DocumentRun("- not a list")]),
            new DocumentBlock(BlockKind.Paragraph, [new DocumentRun("1. not ordered")]),
            new DocumentBlock(BlockKind.Paragraph, [new DocumentRun("# not heading")]),
        ]);
        var md = MarkdownEmitter.Emit(model);

        // The escaped forms must parse back as literal text, not structure. The three
        // source paragraphs re-join into ONE CommonMark paragraph (single \n = soft
        // break) — the point is that no list/heading blocks materialize.
        var block = Assert.Single(MarkdownParser.Parse(md).Blocks);
        Assert.Equal(BlockKind.Paragraph, block.Kind);
        Assert.Equal("- not a list\n1. not ordered\n# not heading",
            ((DocumentRun)block.Inlines[0]).Text);
    }

    [Fact]
    public void EmitEscapesInnerDigitsBeforeDot()
    {
        var model = new DocumentContent(
        [
            new DocumentBlock(BlockKind.Paragraph, [new DocumentRun("version 2.5 is fine\n2026. new year")]),
        ]);
        var md = MarkdownEmitter.Emit(model);
        Assert.Contains(@"2026\. new year", md);
        Assert.DoesNotContain(@"2\.5", md);
    }

    [Fact]
    public void EmitKeepsSoftBreakAsNewline()
    {
        Assert.Equal("line one\nline two\n", Emit("line one\nline two"));
    }

    [Fact]
    public void RoundTripBattery()
    {
        string[] samples =
        [
            "plain",
            "# 标题\n正文",
            "**粗** *斜* ~~删~~ 普通",
            "- 项目一\n- 项目二\n- 项目三",
            "1. 第一\n2. 第二\n3. 第三",
            "中英混排 mixed content 2026",
        ];

        foreach (var sample in samples)
            Assert.Equal(sample.Replace("\r", "") + "\n", Emit(sample).Replace("\r", ""));
    }

    [Fact]
    public void QuoteBlockFlattensToParagraphWithoutMarker()
    {
        // Quote blocks are outside the subset: content survives, the "> " marker does not.
        var blocks = MarkdownParser.Parse("> quoted text").Blocks;
        var block = Assert.Single(blocks);
        Assert.Equal(BlockKind.Paragraph, block.Kind);
        Assert.Equal("quoted text", ((DocumentRun)block.Inlines[0]).Text);
    }
}

/// <summary>
/// Coverage for the surviving <see cref="RtfImagePayload"/> header sniffer (the
/// RTF emitter it once fed is gone — the WRE bridge reads pixel size through it).
/// </summary>
public class RtfImagePayloadTests
{
    [Fact]
    public void FromBytes_SniffsPngHeader()
    {
        // Minimal PNG frame: signature + IHDR with 640x480 big-endian.
        byte[] png =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // signature
            0x00, 0x00, 0x00, 0x0D,                         // IHDR length
            0x49, 0x48, 0x44, 0x52,                         // "IHDR"
            0x00, 0x00, 0x02, 0x80,                         // width  = 640
            0x00, 0x00, 0x01, 0xE0,                         // height = 480
        ];
        var payload = RtfImagePayload.FromBytes(png);

        Assert.NotNull(payload);
        Assert.Equal(RtfImagePayload.PngBlip, payload!.Blip);
        Assert.Equal(640, payload.PixelWidth);
        Assert.Equal(480, payload.PixelHeight);
        Assert.Equal(640, payload.DisplayWidthDip); // px @ 96 DPI = DIP default
    }

    [Fact]
    public void FromBytes_SniffsJpegSofMarker()
    {
        // SOI + APP0 (JFIF, length 0x10 incl. itself, excl. marker) + SOF0:
        // length, precision, height, width.
        byte[] jpeg =
        [
            0xFF, 0xD8,                                     // SOI
            0xFF, 0xE0, 0x00, 0x10,                         // APP0, length 16
            0x4A, 0x46, 0x49, 0x46, 0x00,                   // "JFIF\0"
            0x01, 0x02, 0x00, 0x00, 0x01, 0x00, 0x01,       // version/units/densities
            0x00, 0x00,                                     // no thumbnail
            0xFF, 0xC0, 0x00, 0x11, 0x08,                   // SOF0, len 17, precision
            0x01, 0xE0,                                     // height = 480
            0x02, 0x80,                                     // width  = 640
        ];
        var payload = RtfImagePayload.FromBytes(jpeg);

        Assert.NotNull(payload);
        Assert.Equal(RtfImagePayload.JpegBlip, payload!.Blip);
        Assert.Equal(640, payload.PixelWidth);
        Assert.Equal(480, payload.PixelHeight);
    }

    [Fact]
    public void FromBytes_RejectsUnrecognizedBytes()
    {
        Assert.Null(RtfImagePayload.FromBytes([1, 2, 3, 4, 5]));
        Assert.Null(RtfImagePayload.FromBytes([]));
    }
}

public class MarkdownTextTests
{
    [Fact]
    public void PlainTextStripsAllSyntax()
    {
        const string md = "# 标题\n**粗体** *斜* ~~删~~ 正文\n- 列表项";
        var text = MarkdownText.ToPlainText(md);
        Assert.DoesNotContain('#', text);
        Assert.DoesNotContain('*', text);
        Assert.DoesNotContain("~~", text);
        Assert.DoesNotContain('-', text);
        Assert.Contains("标题", text);
        Assert.Contains("粗体", text);
        Assert.Contains("列表项", text);
    }

    [Fact]
    public void ImageReferenceContributesNothing()
    {
        var text = MarkdownText.ToPlainText($"前面 ![图]({MarkdownParser.ImageSchemePrefix}sha123) 后面");
        Assert.Equal("前面  后面", text.Replace("\n", ""));
    }

    [Fact]
    public void PreviewLineUsesFirstNonEmptyLineAndTruncates()
    {
        var md = "# 标题\n\n正文内容";
        Assert.Equal("标题", MarkdownText.ToPreviewLine(md, 50));
        var longMd = new string('字', 100);
        Assert.Equal(10, MarkdownText.ToPreviewLine(longMd, 10).Length);
    }

    [Fact]
    public void PlainTextOfEmptyIsSafe()
    {
        Assert.Equal(string.Empty, MarkdownText.ToPlainText(""));
        Assert.Equal(string.Empty, MarkdownText.ToPreviewLine("", 20));
    }
}
