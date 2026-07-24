using QNote.Text;

namespace QNote.Tests;

/// <summary>Brace-matching <c>{\pict …}</c> removal for paste sanitization.</summary>
public sealed class RtfPictStripperTests
{
    [Fact]
    public void NoPictGroup_ReturnsInputUnchanged()
    {
        const string rtf = @"{\rtf1\ansi 普通正文}";
        Assert.Equal(rtf, RtfPictStripper.StripPictGroups(rtf));
    }

    [Fact]
    public void StripsSimplePictGroup()
    {
        const string rtf = @"{\rtf1 前{\pict\pngblip 010203}后}";
        Assert.Equal(@"{\rtf1 前后}", RtfPictStripper.StripPictGroups(rtf));
    }

    [Fact]
    public void StripsPictGroupWithNestedBraces()
    {
        const string rtf = @"{\rtf1 前{\pict{\*\picprop\sp{\sn p}{\sv 1}}\pngblip abc}后}";
        Assert.Equal(@"{\rtf1 前后}", RtfPictStripper.StripPictGroups(rtf));
    }

    [Fact]
    public void StripsMultiplePictGroups()
    {
        const string rtf = @"{\rtf1 A{\pict\pngblip 11}B{\pict\jpegblip 22}C}";
        Assert.Equal(@"{\rtf1 ABC}", RtfPictStripper.StripPictGroups(rtf));
    }

    [Fact]
    public void EscapedBracesInsidePict_DoNotEndGroupEarly()
    {
        // "\{" and "\}" are literal braces, not group delimiters.
        const string rtf = @"{\rtf1 前{\pict\pngblip \{still\} inside}后}";
        Assert.Equal(@"{\rtf1 前后}", RtfPictStripper.StripPictGroups(rtf));
    }

    [Fact]
    public void EscapedBracesOutsidePict_ArePreserved()
    {
        const string rtf = @"{\rtf1 \{not a group\}}";
        Assert.Equal(rtf, RtfPictStripper.StripPictGroups(rtf));
    }

    [Fact]
    public void PictLikeWordWithoutDelimiter_IsNotStripped()
    {
        // "\picture" must not match the "\pict" marker.
        const string rtf = @"{\rtf1 {\picture 保留}}";
        Assert.Equal(rtf, RtfPictStripper.StripPictGroups(rtf));
    }

    [Fact]
    public void UnbalancedPictGroup_StripsToEnd_WithoutThrowing()
    {
        const string rtf = @"{\rtf1 前{\pict\pngblip never-closed";
        Assert.Equal(@"{\rtf1 前", RtfPictStripper.StripPictGroups(rtf));
    }
}
