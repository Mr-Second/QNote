using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace QNote.Infrastructure;

/// <summary>
/// Global crash capture. Registers unhandled-exception handlers and writes a paired
/// triage <c>.txt</c> + heap <c>.dmp</c> (<see cref="MinidumpWriter"/>) to
/// <c>CrashDumps\</c>, keeping the newest <see cref="CrashRetention.KeepCount"/>
/// artifacts (swept at startup). Ports the Qt CrashHandler intent to the .NET
/// equivalents. AccessViolation / stack-overflow / pure-native crashes never reach
/// managed handlers — those are covered by the <c>DOTNET_DbgEnableMiniDump</c>
/// backstop configured in <c>Program.Main</c> (see minidump research). Must never throw.
/// </summary>
public sealed class CrashHandler
{
    private readonly AppPaths _paths;
    private readonly ILogger<CrashHandler> _log;
    private readonly Func<string?> _currentLogFilePath;

    /// <summary>Only the first fatal capture writes a dump — a crash cascade must not pile up multi-hundred-MB files.</summary>
    private int _dumpWritten;

    public CrashHandler(AppPaths paths, ILogger<CrashHandler> log, Func<string?>? currentLogFilePath = null)
    {
        _paths = paths;
        _log = log;
        _currentLogFilePath = currentLogFilePath ?? (static () => null);
    }

    /// <summary>Hook the non-UI global handlers. UI (App.UnhandledException) is hooked by App.</summary>
    public void Register()
    {
        SweepRetention();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Capture("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        // Non-fatal by design (SetObserved suppresses the process crash): triage text
        // only, no dump — a suppressed exception means the process keeps running.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Capture("TaskScheduler.UnobservedTaskException", e.Exception, writeDump: false);
            e.SetObserved();
        };
    }

    /// <summary>Sweep old crash artifacts at startup; housekeeping must never block launch.</summary>
    public void SweepRetention()
    {
        try
        {
            Directory.CreateDirectory(_paths.CrashDumpsDir);
            CrashRetention.Sweep(_paths.CrashDumpsDir);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Crash-dump retention sweep failed.");
        }
    }

    /// <summary>Log a fatal exception and write a triage <c>.txt</c> (+ <c>.dmp</c> pair). Never throws.</summary>
    public void Capture(string source, Exception? ex, bool writeDump = true)
    {
        try
        {
            _log.LogCritical(ex, "Unhandled exception from {Source}", source);
            Directory.CreateDirectory(_paths.CrashDumpsDir);

            var baseName = $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}";

            string? dumpPath = null;
            var dumpError = -1; // -1 = not attempted
            if (writeDump && Interlocked.Exchange(ref _dumpWritten, 1) == 0)
            {
                dumpPath = Path.Combine(_paths.CrashDumpsDir, baseName + ".dmp");
                if (!MinidumpWriter.TryWriteHeapDump(dumpPath, out dumpError))
                {
                    TryDelete(dumpPath); // remove a partial file from a failed write
                    dumpPath = null;
                }
            }

            var textPath = Path.Combine(_paths.CrashDumpsDir, baseName + ".txt");
            File.WriteAllText(textPath, BuildTriage(source, ex, dumpPath, dumpError));
        }
        catch
        {
            // A crash handler must never throw.
        }
    }

    private string BuildTriage(string source, Exception? ex, string? dumpPath, int dumpError)
    {
        var sb = new StringBuilder();
        sb.Append("Source: ").AppendLine(source);
        sb.Append("Time (UTC): ").AppendLine(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        sb.Append("Time (Local): ").AppendLine(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        sb.Append("App Version: ").AppendLine(GetAppVersion());
        sb.Append("OS: ").AppendLine(Environment.OSVersion.ToString());
        sb.Append("Runtime: ").AppendLine(Environment.Version.ToString());
        sb.Append("Architecture: ").AppendLine(RuntimeInformation.ProcessArchitecture.ToString());
        sb.Append("Working Set: ").Append(Environment.WorkingSet).AppendLine(" bytes");
        sb.Append("GC Total Memory: ").Append(GC.GetTotalMemory(false)).AppendLine(" bytes");
        sb.Append("Minidump Path: ").AppendLine(dumpPath ?? (dumpError >= 0 ? $"(failed, Win32 error {dumpError})" : "(none)"));
        sb.Append("Log File: ").AppendLine(SafeLogFilePath());
        sb.AppendLine();
        if (ex is not null)
        {
            sb.Append("Exception HResult: 0x").AppendLine(ex.HResult.ToString("X8", CultureInfo.InvariantCulture));
            sb.AppendLine(ex.ToString());
        }
        else
        {
            sb.AppendLine("(no exception object)");
        }

        return sb.ToString();
    }

    private string SafeLogFilePath()
    {
        try
        {
            return _currentLogFilePath() ?? "(unknown)";
        }
        catch
        {
            return "(unknown)";
        }
    }

    private static string GetAppVersion()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly();
            return assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly?.GetName().Version?.ToString()
                ?? "(unknown)";
        }
        catch
        {
            return "(unknown)";
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of a partial dump.
        }
    }
}
