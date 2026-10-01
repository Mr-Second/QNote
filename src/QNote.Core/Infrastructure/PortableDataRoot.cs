using System.IO;

namespace QNote.Infrastructure;

/// <summary>
/// Outcome of the unpackaged data-root probe: <see cref="RootOverride"/> is the
/// portable <c>&lt;exedir&gt;\data</c> root when the exe directory is writable;
/// null means the probe failed and the caller keeps the default Roaming root
/// (<see cref="ProbeError"/> carries the technical cause for logging).
/// </summary>
public sealed record PortableDataRootDecision(
    string ProbedPath,
    string? RootOverride,
    Exception? ProbeError)
{
    /// <summary>Portable mode: the exe-adjacent root won the probe.</summary>
    public bool IsPortable => RootOverride is not null;
}

/// <summary>
/// Unpackaged (portable) data-root resolution — the counterpart to the packaged
/// <see cref="AppPaths"/> default (Roaming). The portable root is
/// <c>&lt;exedir&gt;\data\</c>; when that is not writable (Program Files,
/// corporate lock-down) the caller falls back to the Roaming root and shows a
/// one-time notice. All QNote data (db/images/logs/CrashDumps/backups) flows
/// through <see cref="AppPaths"/>, so rooting <see cref="AppPaths"/> here moves
/// everything. Packaged runs never come here — package identity is the mode
/// switch (see <c>QNote.PackageIdentity</c> in Presentation).
/// </summary>
public static class PortableDataRoot
{
    /// <summary>Portable data directory name, created beside the exe.</summary>
    public const string DataDirName = "data";

    /// <summary>
    /// One-time-notice marker, inside the CHOSEN data root — the notice fires
    /// once per root, not once ever. Deliberately NOT a settings-DB key: the
    /// notice is independent of (and may fire before) the database.
    /// </summary>
    public const string NoticeMarkerFileName = "portable-fallback.marker";

    /// <summary>
    /// The exe's directory. <see cref="Environment.ProcessPath"/> is the real
    /// executable (apphost); <see cref="AppContext.BaseDirectory"/> (the app
    /// layout root) is the fallback when it is unavailable.
    /// </summary>
    public static string ExeDirectory()
    {
        var exePath = Environment.ProcessPath;
        return exePath is not null
            ? Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory
            : AppContext.BaseDirectory;
    }

    /// <summary>
    /// Probe the writability of <c>&lt;exeDirectory&gt;\data</c>: create the
    /// directory, write + delete a temp file. Success → portable mode with
    /// <see cref="PortableDataRootDecision.RootOverride"/> = the data dir. ANY
    /// failure → Roaming fallback; writability is an EXPECTED outcome, so the
    /// exception rides along in <see cref="PortableDataRootDecision.ProbeError"/>
    /// for the composition root to log instead of being thrown here.
    /// </summary>
    public static PortableDataRootDecision Resolve(string exeDirectory)
    {
        var candidate = Path.Combine(exeDirectory, DataDirName);
        try
        {
            Directory.CreateDirectory(candidate);
            var probeFile = Path.Combine(candidate, $".write-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probeFile, "qnote portable writability probe");
            File.Delete(probeFile);
            return new PortableDataRootDecision(candidate, candidate, null);
        }
        catch (Exception ex)
        {
            return new PortableDataRootDecision(candidate, null, ex);
        }
    }

    /// <summary>Has the one-time fallback notice already been shown for this root?</summary>
    public static bool NoticeShown(string dataRoot) =>
        File.Exists(Path.Combine(dataRoot, NoticeMarkerFileName));

    /// <summary>
    /// Record the notice as shown. Call only AFTER the dialog was actually
    /// presented — a first run killed mid-dialog must re-notice next launch.
    /// </summary>
    public static void MarkNoticeShown(string dataRoot) =>
        File.WriteAllText(Path.Combine(dataRoot, NoticeMarkerFileName), $"shown {DateTime.UtcNow:O}");
}
