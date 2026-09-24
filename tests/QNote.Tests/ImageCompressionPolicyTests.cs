using QNote.Text;

namespace QNote.Tests;

/// <summary>D4 encode-plan decisions: alpha → PNG, otherwise JPEG; reuse in-cap JPEGs.</summary>
public sealed class ImageCompressionPolicyTests
{
    [Fact]
    public void Decide_AlphaSource_EncodesPng()
    {
        var plan = ImageCompressionPolicy.Decide(hasAlpha: true, sourceIsJpeg: false, 100, 100, 1000);
        Assert.Equal(DisplayBlipKind.Png, plan.Kind);
        Assert.False(plan.ReuseSourceBytes);
    }

    [Fact]
    public void Decide_OpaquePngSource_EncodesJpeg()
    {
        // No alpha → JPEG keeps the note (and DB) small even from a PNG source.
        var plan = ImageCompressionPolicy.Decide(hasAlpha: false, sourceIsJpeg: false, 100, 100, 1000);
        Assert.Equal(DisplayBlipKind.Jpeg, plan.Kind);
        Assert.False(plan.ReuseSourceBytes);
    }

    [Fact]
    public void Decide_InCapSmallJpeg_ReusesSourceBytes()
    {
        var plan = ImageCompressionPolicy.Decide(
            hasAlpha: false, sourceIsJpeg: true, 800, 600, sourceByteSize: 200_000);
        Assert.Equal(DisplayBlipKind.Jpeg, plan.Kind);
        Assert.True(plan.ReuseSourceBytes);
    }

    [Fact]
    public void Decide_OversizedJpeg_DoesNotReuse()
    {
        // Over the byte cap → re-encode (downscale/quality) rather than embed as-is.
        var plan = ImageCompressionPolicy.Decide(
            hasAlpha: false, sourceIsJpeg: true, 800, 600, sourceByteSize: 5 * 1024 * 1024);
        Assert.False(plan.ReuseSourceBytes);
    }

    [Fact]
    public void Decide_JpegOverDimensionCap_DoesNotReuse()
    {
        var plan = ImageCompressionPolicy.Decide(
            hasAlpha: false, sourceIsJpeg: true, 4000, 3000, sourceByteSize: 100_000);
        Assert.False(plan.ReuseSourceBytes);
        Assert.Equal(ImageCompressionPolicy.MaxDisplayDimension, plan.MaxDimension);
    }
}
