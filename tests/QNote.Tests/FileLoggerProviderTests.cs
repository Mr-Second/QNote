using Microsoft.Extensions.Logging;
using QNote.Infrastructure.Logging;

namespace QNote.Tests;

/// <summary>
/// Daily-rolling file logger: current-log path exposure, startup 7-day sweep, and
/// the cross-day sweep (a long-running tray app must still prune old logs — the
/// clock is injected so the rollover is simulated instead of waiting a day).
/// </summary>
public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qnote-log-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void CurrentLogFilePath_ReflectsToday_AndFileGetsWritten()
    {
        var today = new DateTime(2026, 9, 25, 10, 0, 0);
        using var provider = new FileLoggerProvider(_dir, () => today);

        var logger = provider.CreateLogger("Test");
        logger.LogInformation("hello");

        Assert.EndsWith($"qnote-20260925.log", provider.CurrentLogFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(provider.CurrentLogFilePath));
        Assert.Contains("hello", File.ReadAllText(provider.CurrentLogFilePath));
    }

    [Fact]
    public void Ctor_SweepsLogsOlderThanSevenDays()
    {
        Directory.CreateDirectory(_dir);
        var stale = Path.Combine(_dir, "qnote-20260901.log");
        var fresh = Path.Combine(_dir, "qnote-20260924.log");
        File.WriteAllText(stale, "old");
        File.WriteAllText(fresh, "new");
        File.SetLastWriteTime(stale, new DateTime(2026, 9, 1));
        File.SetLastWriteTime(fresh, new DateTime(2026, 9, 24));

        var now = new DateTime(2026, 9, 25, 10, 0, 0);
        using var provider = new FileLoggerProvider(_dir, () => now);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Write_DateRollover_TriggersSweep()
    {
        // A session that starts on day 1 and keeps running: when the clock rolls to
        // day 1+8, the next write must roll the file AND prune the day-1 log (which
        // is now beyond the 7-day retention window).
        var now = new DateTime(2026, 9, 25, 23, 59, 0);
        using var provider = new FileLoggerProvider(_dir, () => now);
        var logger = provider.CreateLogger("Test");

        logger.LogInformation("day one");
        var dayOneFile = provider.CurrentLogFilePath;
        Assert.True(File.Exists(dayOneFile));
        // Pin the write time to the simulated day so the sweep cutoff is deterministic
        // (the sweep compares File.GetLastWriteTime against the injected clock).
        File.SetLastWriteTime(dayOneFile, now);

        now = now.AddDays(8); // app kept running for over a week
        logger.LogInformation("day nine");

        Assert.EndsWith("qnote-20261003.log", provider.CurrentLogFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(dayOneFile)); // swept on rollover, not just at startup
        Assert.True(File.Exists(provider.CurrentLogFilePath));
        Assert.Contains("day nine", File.ReadAllText(provider.CurrentLogFilePath));
    }

    [Fact]
    public void Write_SameDay_DoesNotSweepOrRoll()
    {
        var now = new DateTime(2026, 9, 25, 10, 0, 0);
        using var provider = new FileLoggerProvider(_dir, () => now);
        var logger = provider.CreateLogger("Test");

        logger.LogInformation("first");
        var file = provider.CurrentLogFilePath;

        now = now.AddHours(3);
        logger.LogInformation("second");

        Assert.Equal(file, provider.CurrentLogFilePath);
        var content = File.ReadAllText(file);
        Assert.Contains("first", content);
        Assert.Contains("second", content);
    }
}
