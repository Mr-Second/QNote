using System.IO;

namespace QNote.Infrastructure;

/// <summary>
/// Retention for <c>CrashDumps\</c>: heap dumps run to hundreds of MB, so only the
/// newest <see cref="KeepCount"/> crash artifacts survive. Files are grouped by base
/// name — a <c>crash-&lt;stamp&gt;.txt</c>/<c>crash-&lt;stamp&gt;.dmp</c> pair shares one group,
/// while any partner-less dump that lands here (e.g. a runtime/WER backstop dump
/// copied in for triage) forms a singleton group under the same sweep. Base names
/// embed a sortable timestamp (or pid), so ordinal-descending name order == newest-first.
/// </summary>
public static class CrashRetention
{
    public const int KeepCount = 3;

    /// <summary>Delete every crash artifact group older than the newest <paramref name="keepCount"/>.</summary>
    public static void Sweep(string crashDumpsDir, int keepCount = KeepCount)
    {
        var stale = Directory.EnumerateFiles(crashDumpsDir, "crash-*.*")
            .Where(IsCrashArtifact)
            .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Skip(keepCount);

        foreach (var group in stale)
        {
            foreach (var file in group)
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Retention is housekeeping; a locked file must never block the sweep.
                }
            }
        }
    }

    private static bool IsCrashArtifact(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".dmp", StringComparison.OrdinalIgnoreCase);
    }
}
