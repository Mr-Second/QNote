using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;

namespace QNote.Infrastructure.Logging;

/// <summary>
/// Minimal daily-rolling file logger provider — no external dependency, honoring
/// the memory north-star. Writes <c>{logsDir}\qnote-yyyyMMdd.log</c> and retains
/// 7 days, mirroring the Qt build's rolling logs. The 7-day sweep runs at startup
/// AND whenever the date rolls over between writes (a tray app can run for weeks,
/// so a startup-only sweep would never fire). <see cref="CurrentLogFilePath"/>
/// mirrors Qt's <c>Logger::currentLogFilePath</c> so the crash triage can link the
/// active log.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int RetentionDays = 7;

    private readonly string _logsDir;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    private string _currentLogFilePath;

    public FileLoggerProvider(string logsDir, Func<DateTime>? clock = null)
    {
        _logsDir = logsDir;
        _clock = clock ?? (static () => DateTime.Now);
        Directory.CreateDirectory(_logsDir);
        _currentLogFilePath = LogFilePathFor(_clock());
        SweepOldLogs();
    }

    /// <summary>Full path of the log file the next write lands in (may not exist yet).</summary>
    public string CurrentLogFilePath
    {
        get
        {
            lock (_gate)
                return _currentLogFilePath;
        }
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    internal void Write(string categoryName, LogLevel level, string message)
    {
        lock (_gate)
        {
            var now = _clock();
            var file = LogFilePathFor(now);
            if (!string.Equals(file, _currentLogFilePath, StringComparison.OrdinalIgnoreCase))
            {
                // The date rolled over while running — run the retention sweep once
                // alongside the roll, so week-long sessions don't accumulate logs.
                SweepOldLogs();
                _currentLogFilePath = file;
            }

            var line =
                $"{new DateTimeOffset(now, DateTimeOffset.Now.Offset):yyyy-MM-dd HH:mm:ss.fff zzz} [{level,-11}] {categoryName}: {message}{Environment.NewLine}";
            File.AppendAllText(file, line);
        }
    }

    private string LogFilePathFor(DateTime date) =>
        Path.Combine(_logsDir, $"qnote-{date:yyyyMMdd}.log");

    private void SweepOldLogs()
    {
        try
        {
            var cutoff = _clock().AddDays(-RetentionDays);
            foreach (var f in Directory.EnumerateFiles(_logsDir, "qnote-*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff)
                    File.Delete(f);
            }
        }
        catch
        {
            // Retention is best-effort; never fail logging on cleanup.
        }
    }

    public void Dispose() => _loggers.Clear();
}
