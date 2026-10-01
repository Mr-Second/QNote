using QNote.Update;

namespace QNote.Tests;

/// <summary>
/// GitHub release-tag comparison (portable channel R3): v-prefix tolerance,
/// zero-padding, prerelease suffixes (IGNORED by design — QNote ships stable
/// tags only), and malformed inputs (Invalid, never "older" — an unparseable
/// tag must never read as "no update"). Pure helpers — no I/O involved.
/// </summary>
public class VersionTagTests
{
    [Theory]
    [InlineData("v1.2.0", "1.1.0.0", VersionTagComparison.Newer)]
    [InlineData("1.2.0", "1.1.0.0", VersionTagComparison.Newer)] // bare tag, no v prefix
    [InlineData("V1.2.0", "1.1.0.0", VersionTagComparison.Newer)] // uppercase prefix tolerated
    [InlineData("v1.2", "1.1.0.0", VersionTagComparison.Newer)] // short core zero-pads
    [InlineData("v1.1.0.1", "1.1.0.0", VersionTagComparison.Newer)] // 4th part decides
    [InlineData("v2.0.0", "1.9.9.9", VersionTagComparison.Newer)]
    [InlineData("v1.2.0-beta.1", "1.1.0.0", VersionTagComparison.Newer)] // suffix ignored
    [InlineData("v1.2.0+build.42", "1.1.0.0", VersionTagComparison.Newer)]
    [InlineData(" v1.2.0 ", "1.1.0.0", VersionTagComparison.Newer)] // surrounding whitespace trimmed
    [InlineData("v1.1.0", "1.0.99", VersionTagComparison.Newer)] // running version may be 3-part
    public void NewerReleases_AreDetected(string tag, string current, VersionTagComparison expected)
        => Assert.Equal(expected, VersionTag.Compare(tag, current));

    [Theory]
    [InlineData("v1.0.1", "1.1.0.0", VersionTagComparison.Older)]
    [InlineData("v1.0.0", "1.1.0.0", VersionTagComparison.Older)]
    [InlineData("v1.1", "1.1.0.1", VersionTagComparison.Older)] // 1.1 == 1.1.0.0 < 1.1.0.1
    [InlineData("1.2.0", "1.2.0.1", VersionTagComparison.Older)]
    public void OlderReleases_AreDetected(string tag, string current, VersionTagComparison expected)
        => Assert.Equal(expected, VersionTag.Compare(tag, current));

    [Theory]
    [InlineData("v1.1.0", "1.1.0.0", VersionTagComparison.Same)]
    [InlineData("1.1.0.0", "1.1.0.0", VersionTagComparison.Same)]
    [InlineData("v1.1", "1.1.0.0", VersionTagComparison.Same)]
    [InlineData("v1.1.0.0", "v1.1", VersionTagComparison.Same)] // both sides go through the same grammar
    [InlineData("v1.1.0-rc.1", "1.1.0.0", VersionTagComparison.Same)] // suffix ignored → same core
    [InlineData("1.1.0", "1.1.0", VersionTagComparison.Same)]
    public void SameVersions_CompareEqual(string tag, string current, VersionTagComparison expected)
        => Assert.Equal(expected, VersionTag.Compare(tag, current));

    [Theory]
    [InlineData(null, "1.1.0.0")] // null tag
    [InlineData("", "1.1.0.0")] // empty tag
    [InlineData("   ", "1.1.0.0")] // whitespace-only tag
    [InlineData("v", "1.1.0.0")] // bare prefix
    [InlineData("vlatest", "1.1.0.0")] // non-numeric core
    [InlineData("release-2026", "1.1.0.0")] // word + dash suffix → empty core
    [InlineData("v1.2.0.0.1", "1.1.0.0")] // 5 parts
    [InlineData("v1.a.0", "1.1.0.0")] // non-numeric component
    [InlineData("1 .2.0", "1.1.0.0")] // inner whitespace rejected
    [InlineData("1.-2.0", "1.1.0.0")] // signed component rejected
    [InlineData("v1.2.0", "")] // malformed CURRENT version
    [InlineData("v1.2.0", "abc")]
    [InlineData("v1.2.0", null)]
    public void MalformedInput_IsInvalid_NeverOlder(string? tag, string? current)
        => Assert.Equal(VersionTagComparison.Invalid, VersionTag.Compare(tag, current));

    [Fact]
    public void MajorWin_BeatsLongerLowerCore()
    {
        // Component-wise, not string-wise: "v2.0" must beat "1.9.9.9".
        Assert.Equal(VersionTagComparison.Newer, VersionTag.Compare("v2.0", "1.9.9.9"));
        Assert.Equal(VersionTagComparison.Older, VersionTag.Compare("v1.99.99", "2.0.0.0"));
    }
}
