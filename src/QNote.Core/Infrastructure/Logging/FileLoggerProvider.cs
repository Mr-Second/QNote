using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;

namespace QNote.Infrastructure.Logging;

/// <summary>
/// Minimal daily-rolling file logger provider — no external dependency, honoring
/// the memory north-star. Writes <c>{logsDir}\qnote-yyyyMMdd.log</c> and retains
/// 7 days, mirroring the Qt build's rolling logs. Can be hardened later.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logsDir;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string logsDir)
    {
        _logsDir = logsDir;
        Directory.CreateDirectory(_logsDir);
        SweepOldLogs();
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    internal void Write(string categoryName, LogLevel level, string message)
    {
        var file = Path.Combine(_logsDir, $"qnote-{DateTime.Now:yyyyMMdd}.log");
        var line =
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level,-11}] {categoryName}: {message}{Environment.NewLine}";
        lock (_gate)
        {
            File.AppendAllText(file, line);
        }
    }

    private void SweepOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var f in Directory.EnumerateFiles(_logsDir, "qnote-*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff)
                    File.Delete(f);
            }
        }
        catch
        {
            // Retention is best-effort; never fail startup on log cleanup.
        }
    }

    public void Dispose() => _loggers.Clear();
}
