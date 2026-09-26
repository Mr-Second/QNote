using System.Diagnostics;

namespace QNote;

/// <summary>
/// Startup milestone clock (perf-size-optimization R1). Elapsed time is measured
/// from <see cref="Process.StartTime"/> — a few ms BEFORE <c>Program.Main</c>,
/// which is intentional: cold-start cost includes the CLR/WinAppSDK bootstrap,
/// and this needs no wiring in Main. Presentation-only; Core stays headless.
/// Milestone log lines carry the <c>Startup milestone:</c> prefix so the
/// measurement workflow can grep them out of the rolling log file.
/// </summary>
internal static class StartupClock
{
    private static readonly DateTime ProcessStart = Process.GetCurrentProcess().StartTime;

    /// <summary>Milliseconds since process start.</summary>
    public static double ElapsedMs => (DateTime.Now - ProcessStart).TotalMilliseconds;
}
