using QNote.Text;

namespace QNote.Tests;

/// <summary>px ↔ pt conversion for the font-size dropdown (pt = px × 0.75).</summary>
public sealed class RtfFontSizeTests
{
    [Theory]
    [InlineData(12, 9f)]
    [InlineData(14, 10.5f)]
    [InlineData(16, 12f)]
    [InlineData(18, 13.5f)]
    [InlineData(20, 15f)]
    [InlineData(24, 18f)]
    [InlineData(28, 21f)]
    [InlineData(32, 24f)]
    public void PxToPt_UsesThreeQuarterRatio(int px, float expectedPt) =>
        Assert.Equal(expectedPt, RtfFontSize.PxToPt(px));

    [Theory]
    [InlineData(9f, 12)]
    [InlineData(10.5f, 14)]
    [InlineData(12f, 16)]
    [InlineData(24f, 32)]
    public void PtToPx_RoundsBackToPixelStep(float pt, int expectedPx) =>
        Assert.Equal(expectedPx, RtfFontSize.PtToPx(pt));

    [Theory]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(32)]
    public void RoundTrip_IsIdentity_ForToolbarSteps(int px) =>
        Assert.Equal(px, RtfFontSize.PtToPx(RtfFontSize.PxToPt(px)));
}
