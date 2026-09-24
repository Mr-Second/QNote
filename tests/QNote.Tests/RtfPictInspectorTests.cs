using QNote.Text;

namespace QNote.Tests;

/// <summary>
/// Pict extraction from RTF (alt + sha + blip) used to keep <c>note_images</c> in sync
/// on save. Fixtures mirror the shape RichEdit emits (spike E2):
/// <c>{\pict{\*\picprop{\sp{\sn wzDescription}{\sv qnote:&lt;sha&gt;}}}\pngblip\picwgoal…}</c>.
/// </summary>
public sealed class RtfPictInspectorTests
{
    private const string Sha = "aa11bb22cc33dd44ee55ff6600778899aabbccddeeff00112233445566778899";

    private static string PictWithAlt(string alt) =>
        @"{\pict{\*\picprop{\sp{\sn wzDescription}{\sv " + alt + @"}}}\pngblip\picw3175\pich2381\picwgoal1800\pichgoal1350"
        + "\r\n89504e470d0a1a0a0000000d49484452}" ;

    [Fact]
    public void FindPicts_DecodesShaFromAltMarker()
    {
        var rtf = @"{\rtf1\ansi abc" + PictWithAlt("qnote:" + Sha) + @"def}";

        var picts = RtfPictInspector.FindPicts(rtf);

        var pict = Assert.Single(picts);
        Assert.Equal("qnote:" + Sha, pict.Alt);
        Assert.Equal(Sha, pict.Sha256);
        Assert.Equal("pngblip", pict.Blip);
    }

    [Fact]
    public void FindPicts_KeepsPictWithForeignAlt_ButNoSha()
    {
        var rtf = @"{\rtf1\ansi " + PictWithAlt("E4-image") + "}";

        var pict = Assert.Single(RtfPictInspector.FindPicts(rtf));
        Assert.Equal("E4-image", pict.Alt);
        Assert.Null(pict.Sha256);
    }

    [Fact]
    public void FindPicts_ReturnsEmpty_WhenNoPictures()
    {
        Assert.Empty(RtfPictInspector.FindPicts(@"{\rtf1\ansi hello}"));
        Assert.Empty(RtfPictInspector.FindPicts(""));
        Assert.Empty(RtfPictInspector.FindPicts(null));
    }

    [Fact]
    public void FindPicts_HandlesMultiplePictures_InOrder()
    {
        var sha2 = new string('b', 64);
        var rtf = @"{\rtf1 " + PictWithAlt("qnote:" + Sha) + " middle " + PictWithAlt("qnote:" + sha2) + "}";

        var picts = RtfPictInspector.FindPicts(rtf);

        Assert.Equal(2, picts.Count);
        Assert.Equal(Sha, picts[0].Sha256);
        Assert.Equal(sha2, picts[1].Sha256);
    }

    [Fact]
    public void ReferencedSha256_Deduplicates()
    {
        var rtf = @"{\rtf1 " + PictWithAlt("qnote:" + Sha) + PictWithAlt("qnote:" + Sha) + "}";

        var refs = RtfPictInspector.ReferencedSha256(rtf);

        Assert.Equal(new[] { Sha }, refs.ToArray());
    }

    [Fact]
    public void FindPicts_IgnoresPictKeywordInsideWord()
    {
        // "\pictured" is not a pict group.
        Assert.Empty(RtfPictInspector.FindPicts(@"{\rtf1 \pictured content}"));
    }

    [Fact]
    public void FindPicts_ToleratesEscapedBracesInPayload()
    {
        var rtf = @"{\rtf1 {\pict\pngblip 4142435c7b7d} tail}";
        var pict = Assert.Single(RtfPictInspector.FindPicts(rtf));
        Assert.Null(pict.Sha256);
        Assert.Equal("pngblip", pict.Blip);
    }

    [Fact]
    public void FindPicts_ParsesSinglePictFragment_FromRangeRtf()
    {
        // The double-click hit-test reads the RTF of a ONE-character range at the click
        // point. RichEdit emits a bare fragment (no \rtf1 wrapper) for that range; this
        // is the exact shape the reverse-lookup depends on (regression for the bug where
        // UseObjectText returned the generic "Image" instead of the qnote: alt).
        var fragment = PictWithAlt("qnote:" + Sha);

        var pict = Assert.Single(RtfPictInspector.FindPicts(fragment));
        Assert.Equal(Sha, pict.Sha256);
    }
}
