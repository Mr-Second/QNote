using QNote.Markdown;
using QNote.Text;

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

public class RtfEmitterTests
{
    private static DocumentContent Parse(string md) => MarkdownParser.Parse(md);

    [Fact]
    public void EmitsValidHeaderAndFooter()
    {
        var rtf = RtfEmitter.Emit(Parse("hi"));
        Assert.StartsWith(@"{\rtf1\ansi\deff0{\fonttbl{\f0\fswiss Segoe UI;}}{\colortbl;}", rtf);
        Assert.EndsWith("}", rtf);
    }

    [Fact]
    public void EmitsBoldItalicStrikethroughControls()
    {
        var rtf = RtfEmitter.Emit(Parse("a **b** *c* ~~d~~"));
        Assert.Contains(@"\b ", rtf);
        Assert.Contains(@"\b0", rtf);
        Assert.Contains(@"\i ", rtf);
        Assert.Contains(@"\i0", rtf);
        Assert.Contains(@"\strike ", rtf);
        Assert.Contains(@"\strike0", rtf);
    }

    [Fact]
    public void EmitsHeadingFontSizes()
    {
        Assert.Contains(@"\b\fs40", RtfEmitter.Emit(Parse("# big")));
        Assert.Contains(@"\b\fs32", RtfEmitter.Emit(Parse("## mid")));
        Assert.Contains(@"\b\fs26", RtfEmitter.Emit(Parse("### small")));
        Assert.Contains(@"\fs22", RtfEmitter.Emit(Parse("body")));
    }

    [Fact]
    public void EmitsLegacyListControls()
    {
        var bullet = RtfEmitter.Emit(Parse("- x"));
        Assert.Contains(@"\pntext\f0\'b7\tab", bullet);
        Assert.Contains(@"\li720", bullet);

        var ordered = RtfEmitter.Emit(Parse("1. x"));
        Assert.Contains(@"\pntext\f0 1.\tab", ordered);
        Assert.Contains(@"\pndec", ordered);
    }

    [Fact]
    public void EscapesRtfSignificantCharacters()
    {
        var model = new DocumentContent(
        [
            new DocumentBlock(BlockKind.Paragraph, [new DocumentRun("a{b}c\\d")]),
        ]);
        Assert.Contains(@"a\{b\}c\\d", RtfEmitter.Emit(model));
    }

    [Fact]
    public void EmitsUnicodeEscapeForChinese()
    {
        var rtf = RtfEmitter.Emit(Parse("你好"));
        Assert.Contains(@"\u20320?", rtf);   // U+4F60 你
        Assert.Contains(@"\u22909?", rtf);   // U+597D 好
        Assert.DoesNotContain("你好", rtf);
    }

    [Fact]
    public void EmitsLineBreakForEmbeddedNewline()
    {
        var model = new DocumentContent(
        [
            new DocumentBlock(BlockKind.Paragraph, [new DocumentRun("a\nb")]),
        ]);
        Assert.Contains(@"\line ", RtfEmitter.Emit(model));
    }

    [Fact]
    public void EmitsPictWithTwipsGeometryWhenImageResolves()
    {
        const string sha = "deadbeef";
        var payload = new RtfImagePayload([0x89, 0x50, 0x4E, 0x47], 100, 50, 200.0, 100.0,
            RtfImagePayload.PngBlip);
        var rtf = RtfEmitter.Emit(Parse($"![]({MarkdownParser.ImageSchemePrefix}{sha})"),
            sha256 => sha256 == sha ? payload : null);

        Assert.Contains(@"{\pict\pngblip\picw100\pich50\picwgoal3000\pichgoal1500 ", rtf);
        Assert.Contains("89504e47", rtf);
    }

    [Fact]
    public void EmitsJpegBlipFromPayloadKind()
    {
        const string sha = "cafe";
        var payload = new RtfImagePayload([0xFF, 0xD8], 10, 10, 10.0, 10.0, RtfImagePayload.JpegBlip);
        var rtf = RtfEmitter.Emit(Parse($"![]({MarkdownParser.ImageSchemePrefix}{sha})"),
            _ => payload);

        Assert.Contains(@"\pict\jpegblip", rtf);
        Assert.DoesNotContain(@"\pngblip", rtf);
    }

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

    [Fact]
    public void MissingImageDegradesToReadablePlaceholder()
    {
        var rtf = RtfEmitter.Emit(Parse($"![alt text]({MarkdownParser.ImageSchemePrefix}missing)"),
            _ => null);
        Assert.Contains("alt text", rtf);
        Assert.DoesNotContain(@"\pict", rtf);
    }

    [Fact]
    public void ImageProviderReceivesReferenceSha()
    {
        const string sha = "abc";
        string? seen = null;
        RtfEmitter.Emit(Parse($"![]({MarkdownParser.ImageSchemePrefix}{sha})"),
            s => { seen = s; return null; });
        Assert.Equal(sha, seen);
    }

    [Fact]
    public void PictBytes_RoundTripThroughEmittedRtf_ForIdentityMatching()
    {
        // The byte-identity save path end to end (its Core half): MD reference →
        // emitted RTF hex → (RichEdit passes picts through unmodified) →
        // RtfPictInspector extraction. The recovered bytes must equal the display
        // copy bit for bit, so SHA256(pict.Bytes) matches SHA256(display_bytes) and
        // the controller resolves the reference even after a reload stripped the alt.
        const string sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        byte[] display = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
        var payload = new RtfImagePayload(display, display.Length, 10, 20.0, 20.0, RtfImagePayload.PngBlip);
        var md = $"text ![]({MarkdownParser.ImageSchemePrefix}{sha}) tail";
        var rtf = RtfEmitter.Emit(MarkdownParser.Parse(md), _ => payload);

        var pict = Assert.Single(RtfPictInspector.FindPicts(rtf));
        Assert.Equal(display, pict.Bytes);
        // Null alt is expected (the emitter writes none); the bytes alone identify it.
        Assert.Null(pict.Sha256);

        // Same pict twice → same identity → both references resolve to one sha.
        var twice = RtfEmitter.Emit(
            MarkdownParser.Parse($"![]({MarkdownParser.ImageSchemePrefix}{sha}) mid ![]({MarkdownParser.ImageSchemePrefix}{sha})"),
            _ => payload);
        var picts = RtfPictInspector.FindPicts(twice);
        Assert.Equal(2, picts.Count);
        Assert.All(picts, p => Assert.Equal(display, p.Bytes));
    }

    [Fact]
    public void FormatSubset_RoundTripsThroughEmittedRtf()
    {
        // Step ⑤ matrix, headless half: every locked-subset construct must survive
        // MD → model → RTF emission with its control words intact (what RichEdit will
        // parse); the TOM side of the loop is verified in-app.
        const string sha = "deadbeef";
        var md = """
            # 一级标题
            ## 二级标题
            ### 三级标题
            正文 **粗** *斜* ~~删~~
            - 无序项

            1. 有序项

            ![](qnote-img:deadbeef)
            """;
        var rtf = RtfEmitter.Emit(MarkdownParser.Parse(md.Replace("qnote-img:", MarkdownParser.ImageSchemePrefix)),
            s => s == sha ? new RtfImagePayload([0x89, 0x50], 2, 1, 2.0, 1.0, RtfImagePayload.PngBlip) : null);

        Assert.Contains(@"\b\fs40 ", rtf);           // H1
        Assert.Contains(@"\fs32 ", rtf);             // H2
        Assert.Contains(@"\fs26 ", rtf);             // H3
        Assert.Contains(@"\b ", rtf);                // bold
        Assert.Contains(@"\i ", rtf);                // italic
        Assert.Contains(@"\strike ", rtf);           // strikethrough
        Assert.Contains(@"{\pict\pngblip", rtf);     // image
        // List markers: the emitter writes Word-compatible \ls lists OR \pntext —
        // assert on whichever the emitter produces via round-trip: re-parse the
        // emitted markdown instead of asserting RTF internals for lists.
        var emitted = MarkdownEmitter.Emit(MarkdownParser.Parse(md.Replace("qnote-img:", MarkdownParser.ImageSchemePrefix)));
        Assert.Contains("- 无序项", emitted);
        Assert.Contains("1. 有序项", emitted);
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
