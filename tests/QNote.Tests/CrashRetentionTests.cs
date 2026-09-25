using QNote.Infrastructure;

namespace QNote.Tests;

/// <summary>
/// Crash-dump retention: .txt/.dmp files pair by shared base name, groups order
/// newest-first by name (timestamp-sortable), only the newest 3 groups survive.
/// </summary>
public sealed class CrashRetentionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qnote-crash-test-{Guid.NewGuid():N}");

    public CrashRetentionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Sweep_KeepsNewestThreePairs()
    {
        // 5 crash pairs, timestamps sort lexicographically == chronologically.
        for (var i = 1; i <= 5; i++)
        {
            var stamp = $"crash-2026092{i}-120000-000";
            File.WriteAllText(Path.Combine(_dir, stamp + ".txt"), "triage");
            File.WriteAllText(Path.Combine(_dir, stamp + ".dmp"), "dump");
        }

        CrashRetention.Sweep(_dir);

        var remaining = Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(6, remaining.Length); // 3 pairs × 2 files
        Assert.DoesNotContain(remaining, n => n!.Contains("20260921"));
        Assert.DoesNotContain(remaining, n => n!.Contains("20260922"));
        Assert.Contains(remaining, n => n == "crash-20260923-120000-000.txt");
        Assert.Contains(remaining, n => n == "crash-20260925-120000-000.dmp");
    }

    [Fact]
    public void Sweep_PairsTxtAndDmpUnderOneGroup()
    {
        // 4 groups where the .txt and .dmp must be deleted TOGETHER: if pairing were
        // wrong (kept newest 3 FILES), the newest pair would lose one half.
        for (var i = 1; i <= 4; i++)
        {
            var stamp = $"crash-2026092{i}-120000-000";
            File.WriteAllText(Path.Combine(_dir, stamp + ".txt"), "triage");
            File.WriteAllText(Path.Combine(_dir, stamp + ".dmp"), "dump");
        }

        CrashRetention.Sweep(_dir);

        foreach (var i in new[] { 2, 3, 4 })
        {
            Assert.True(File.Exists(Path.Combine(_dir, $"crash-2026092{i}-120000-000.txt")));
            Assert.True(File.Exists(Path.Combine(_dir, $"crash-2026092{i}-120000-000.dmp")));
        }
        Assert.False(File.Exists(Path.Combine(_dir, "crash-20260921-120000-000.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "crash-20260921-120000-000.dmp")));
    }

    [Fact]
    public void Sweep_FewerThanKeepCount_DeletesNothing()
    {
        File.WriteAllText(Path.Combine(_dir, "crash-20260925-120000-000.txt"), "triage");
        File.WriteAllText(Path.Combine(_dir, "crash-20260925-120000-000.dmp"), "dump");

        CrashRetention.Sweep(_dir);

        Assert.Equal(2, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public void Sweep_IncludesEnvBackstopDumps_AsSingletonGroups()
    {
        // A partner-less dump (e.g. a runtime/WER backstop dump placed in this
        // folder, no .txt partner) still counts as a group, so huge singleton
        // dumps age out too.
        File.WriteAllText(Path.Combine(_dir, "crash-20260921-120000-000.txt"), "triage");
        File.WriteAllText(Path.Combine(_dir, "crash-20260921-120000-000.dmp"), "dump");
        File.WriteAllText(Path.Combine(_dir, "crash-20260922-120000-000.txt"), "triage");
        File.WriteAllText(Path.Combine(_dir, "crash-env-12345.dmp"), "dump");
        File.WriteAllText(Path.Combine(_dir, "crash-env-99999.dmp"), "dump");

        CrashRetention.Sweep(_dir, keepCount: 2);

        var remaining = Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray();
        Assert.Equal(2, remaining.Length);
        Assert.Contains("crash-env-99999.dmp", remaining);
        Assert.Contains("crash-env-12345.dmp", remaining); // env- > 2026 lexicographically
    }

    [Fact]
    public void Sweep_LeavesNonCrashFilesAlone()
    {
        File.WriteAllText(Path.Combine(_dir, "crash-20260921-120000-000.txt"), "triage");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "user file");
        File.WriteAllText(Path.Combine(_dir, "crash-20260921-120000-000.json"), "not an artifact");

        CrashRetention.Sweep(_dir);

        Assert.True(File.Exists(Path.Combine(_dir, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(_dir, "crash-20260921-120000-000.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "crash-20260921-120000-000.txt"))); // only group, kept
    }
}
