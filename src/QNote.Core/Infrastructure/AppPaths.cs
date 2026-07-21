using System.IO;

namespace QNote.Infrastructure;

/// <summary>
/// Single source of truth for on-disk data locations. Everything lives under
/// <c>%APPDATA%\Roaming\QNote\QNote\</c> (decision: explicit Roaming path, human
/// navigable and shared with the future portable build). Never build these paths
/// inline anywhere else — depend on this type.
/// </summary>
public sealed class AppPaths
{
    public AppPaths()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Root = Path.Combine(roaming, "QNote", "QNote");
        DatabasePath = Path.Combine(Root, "qnote.db");
        ImagesDir = Path.Combine(Root, "images");
        LogsDir = Path.Combine(Root, "logs");
        CrashDumpsDir = Path.Combine(Root, "CrashDumps");
        BackupsDir = Path.Combine(Root, "backups");
    }

    /// <summary>Data root: <c>%APPDATA%\Roaming\QNote\QNote</c>.</summary>
    public string Root { get; }

    /// <summary>The single SQLite store (notes + categories + settings + FTS5).</summary>
    public string DatabasePath { get; }

    public string ImagesDir { get; }

    public string LogsDir { get; }

    public string CrashDumpsDir { get; }

    public string BackupsDir { get; }

    /// <summary>Create every data directory if missing. Call once at startup.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ImagesDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(CrashDumpsDir);
        Directory.CreateDirectory(BackupsDir);
    }
}
