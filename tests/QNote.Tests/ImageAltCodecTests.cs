using QNote.Text;

namespace QNote.Tests;

/// <summary>The <c>qnote:&lt;sha256&gt;</c> alt-text marker round-trip used to reverse-look an
/// embedded image to its original on disk (PRD D2 / double-click).</summary>
public sealed class ImageAltCodecTests
{
    private const string Sha = "2fd4e1c67a2d28fced849ee1bb76e7391b93eb12c1a5f6f0a4c1e9b0d1f2a3b4";

    [Fact]
    public void Encode_Decode_RoundTrips()
    {
        Assert.Equal(Sha, ImageAltCodec.TryDecode(ImageAltCodec.Encode(Sha)));
    }

    [Fact]
    public void TryDecode_ReturnsNull_ForForeignOrEmptyAlt()
    {
        Assert.Null(ImageAltCodec.TryDecode(null));
        Assert.Null(ImageAltCodec.TryDecode(""));
        Assert.Null(ImageAltCodec.TryDecode("E4-image"));
        Assert.Null(ImageAltCodec.TryDecode("qnote:"));
    }

    [Fact]
    public void TryDecode_ReturnsNull_ForMalformedDigest()
    {
        Assert.Null(ImageAltCodec.TryDecode("qnote:nothex"));
        Assert.Null(ImageAltCodec.TryDecode("qnote:" + Sha[..63])); // 63 hex chars
        Assert.Null(ImageAltCodec.TryDecode("qnote:" + new string('z', 64))); // non-hex
    }

    [Fact]
    public void TryDecode_NormalizesCase()
    {
        Assert.Equal(Sha, ImageAltCodec.TryDecode("QNOTE:" + Sha.ToUpperInvariant()));
    }
}
