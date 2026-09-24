using System.Security.Cryptography;
using QNote.Services;

namespace QNote.Tests;

/// <summary>
/// Content-addressed original storage: sha256 naming, automatic dedup, and pruning
/// of orphaned files (PRD D2).
/// </summary>
public sealed class ImageServiceTests
{
    [Fact]
    public async Task ImportAsync_StoresOriginal_UnderSha256Name()
    {
        using var db = new TestDatabase();
        var service = db.NewImageService();
        var original = new byte[] { 1, 2, 3, 4, 5 };
        var expectedSha = Convert.ToHexStringLower(SHA256.HashData(original));

        var imported = await service.ImportAsync(original, "png", [9, 9], "pngblip", 10, 20);

        Assert.Equal(expectedSha, imported.Sha256);
        Assert.Equal("png", imported.Ext);
        Assert.Equal(original.LongLength, imported.ByteSize);
        Assert.Equal(10, imported.PixelWidth);
        Assert.Equal(20, imported.PixelHeight);
        Assert.True(File.Exists(imported.OriginalPath));
        Assert.Equal(original, await File.ReadAllBytesAsync(imported.OriginalPath));
        Assert.EndsWith(expectedSha + ".png", imported.OriginalPath);
    }

    [Fact]
    public async Task ImportAsync_Dedups_SameContent()
    {
        using var db = new TestDatabase();
        var service = db.NewImageService();
        var original = new byte[] { 7, 7, 7 };

        var first = await service.ImportAsync(original, "png", [1], "pngblip", 1, 1);
        var second = await service.ImportAsync(original, "png", [2], "pngblip", 1, 1);

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.OriginalPath, second.OriginalPath);
        Assert.Single(Directory.GetFiles(db.Paths.ImagesDir));
    }

    [Fact]
    public async Task FindOriginalPath_ScansWhateverExtensionWasStored()
    {
        using var db = new TestDatabase();
        var service = db.NewImageService();
        var imported = await service.ImportAsync(new byte[] { 3, 1, 4 }, "jpg", [1], "jpegblip", 1, 1);

        Assert.Equal(imported.OriginalPath, service.FindOriginalPath(imported.Sha256));
        Assert.Null(service.FindOriginalPath(new string('0', 64)));
    }

    [Fact]
    public async Task DeleteOriginalsAsync_RemovesFiles_AndIgnoresMissing()
    {
        using var db = new TestDatabase();
        var service = db.NewImageService();
        var imported = await service.ImportAsync(new byte[] { 5, 5, 5 }, "png", [1], "pngblip", 1, 1);

        await service.DeleteOriginalsAsync([imported.Sha256, new string('f', 64)]);

        Assert.False(File.Exists(imported.OriginalPath));
    }

    [Fact]
    public async Task NormalizeExt_RejectsPathTraversalCharacters()
    {
        using var db = new TestDatabase();
        var service = db.NewImageService();
        // A hostile "extension" must not escape the images dir.
        var imported = await service.ImportAsync(new byte[] { 1 }, "../evil", [1], "pngblip", 1, 1);

        Assert.Equal("bin", imported.Ext);
        Assert.StartsWith(db.Paths.ImagesDir, imported.OriginalPath);
    }

    [Fact]
    public async Task ImportAsync_LeavesNoTempFile_Behind()
    {
        using var db = new TestDatabase();
        var service = db.NewImageService();

        await service.ImportAsync(new byte[] { 8, 8 }, "png", [1], "pngblip", 1, 1);

        Assert.Single(Directory.GetFiles(db.Paths.ImagesDir)); // only the content address
    }

    [Fact]
    public async Task ImportAsync_Concurrent_SameContent_DoesNotThrow()
    {
        using var db = new TestDatabase();
        var service = db.NewImageService();
        var bytes = new byte[] { 42, 42, 42 };

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => service.ImportAsync(bytes, "png", [1], "pngblip", 1, 1)));

        Assert.All(results, r => Assert.Equal(results[0].OriginalPath, r.OriginalPath));
        Assert.Single(Directory.GetFiles(db.Paths.ImagesDir));
    }

    [Fact]
    public void FindOriginalPath_RejectsNonDigestLookups()
    {
        using var db = new TestDatabase();
        var service = db.NewImageService();

        // A wildcard / short / non-hex "sha" must never be turned into a file glob.
        Assert.Null(service.FindOriginalPath("*"));
        Assert.Null(service.FindOriginalPath(".."));
        Assert.Null(service.FindOriginalPath("zz"));
        Assert.Null(service.FindOriginalPath(new string('a', 63)));
    }
}
