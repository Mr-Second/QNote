using QNote.Infrastructure;

namespace QNote.Tests;

/// <summary>
/// Unpackaged data-root probe (portable channel R1): a writable exe dir adopts
/// <c>&lt;exedir&gt;\data</c> (probe file cleaned up), an unwritable exe dir
/// falls back to Roaming with the technical cause captured for logging, an
/// existing <c>data\</c> (relocated portable folder) is adopted in place, and
/// the one-time fallback notice is tracked per root. Temp-dir based — never
/// touches the real %APPDATA%.
/// </summary>
public sealed class PortableDataRootTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qnote-portable-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Resolve_WritableExeDir_AdoptsExeAdjacentDataDir()
    {
        Directory.CreateDirectory(_dir);

        var decision = PortableDataRoot.Resolve(_dir);

        var expected = Path.Combine(_dir, PortableDataRoot.DataDirName);
        Assert.Equal(expected, decision.RootOverride);
        Assert.True(decision.IsPortable);
        Assert.Null(decision.ProbeError);
        Assert.True(Directory.Exists(expected));
        // The writability probe file must not linger in the adopted root.
        Assert.Empty(Directory.GetFiles(expected));
    }

    [Fact]
    public void Resolve_UnwritableExeDir_FallsBackWithCause()
    {
        // A FILE where the data directory should go: CreateDirectory reliably
        // fails without ACL games (the "read-only exe dir, e.g. Program Files"
        // outcome rides its exception into ProbeError instead of throwing).
        var exeDir = Path.Combine(_dir, "blocked");
        Directory.CreateDirectory(exeDir);
        var inTheWay = Path.Combine(exeDir, PortableDataRoot.DataDirName);
        File.WriteAllText(inTheWay, "not a directory");

        var decision = PortableDataRoot.Resolve(exeDir);

        Assert.Null(decision.RootOverride);
        Assert.False(decision.IsPortable);
        Assert.NotNull(decision.ProbeError);
        Assert.Equal(inTheWay, decision.ProbedPath);
    }

    [Fact]
    public void Resolve_ExistingDataDir_IsAdoptedInPlace()
    {
        // A relocated portable folder keeps its data: adopting must not touch
        // what is already in data\.
        var dataDir = Path.Combine(_dir, PortableDataRoot.DataDirName);
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "qnote.db"), "existing data");

        var decision = PortableDataRoot.Resolve(_dir);

        Assert.Equal(dataDir, decision.RootOverride);
        Assert.Null(decision.ProbeError);
        Assert.True(File.Exists(Path.Combine(dataDir, "qnote.db")));
    }

    [Fact]
    public void NoticeMarker_IsPerRoot()
    {
        Directory.CreateDirectory(_dir);
        var otherRoot = $"{_dir}-other"; // never created — still a valid probe target

        Assert.False(PortableDataRoot.NoticeShown(_dir));

        PortableDataRoot.MarkNoticeShown(_dir);

        Assert.True(PortableDataRoot.NoticeShown(_dir));
        // One-time PER ROOT: a different data root has not been noticed.
        Assert.False(PortableDataRoot.NoticeShown(otherRoot));
    }

    [Fact]
    public void ExeDirectory_PointsAtAnExistingDirectory()
    {
        // Environment.ProcessPath with the AppContext.BaseDirectory fallback —
        // whatever it resolves to must be a real directory (testhost exe dir).
        Assert.True(Directory.Exists(PortableDataRoot.ExeDirectory()));
    }
}
