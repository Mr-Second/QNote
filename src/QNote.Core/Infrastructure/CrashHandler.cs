using System.IO;
using Microsoft.Extensions.Logging;

namespace QNote.Infrastructure;

/// <summary>
/// Global crash capture. Registers unhandled-exception handlers and writes a
/// triage <c>.txt</c> to <c>CrashDumps\</c>. Ports the Qt CrashHandler intent to
/// the .NET equivalents. Minidump capture (WER / MiniDumpWriteDump) is deferred to
/// its own task; this skeleton guarantees no crash is silent.
/// </summary>
public sealed class CrashHandler
{
    private readonly AppPaths _paths;
    private readonly ILogger<CrashHandler> _log;

    public CrashHandler(AppPaths paths, ILogger<CrashHandler> log)
    {
        _paths = paths;
        _log = log;
    }

    /// <summary>Hook the non-UI global handlers. UI (App.UnhandledException) is hooked by App.</summary>
    public void Register()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Capture("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Capture("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Log a fatal exception and write a triage file. Never throws.</summary>
    public void Capture(string source, Exception? ex)
    {
        try
        {
            _log.LogCritical(ex, "Unhandled exception from {Source}", source);
            Directory.CreateDirectory(_paths.CrashDumpsDir);
            var file = Path.Combine(_paths.CrashDumpsDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(file, $"Source: {source}{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch
        {
            // A crash handler must never throw.
        }
    }
}
