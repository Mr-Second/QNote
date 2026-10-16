using System.IO;
using System.Security.Cryptography;
using QNote.Infrastructure;

namespace QNote.Services;

/// <summary>
/// Content-addressed store for todo attachment bytes (PRD Decision 11): files
/// live under <c>attachments/</c> keyed by their SHA-256 hex, raw and
/// un-normalized (unlike note images). Deliberately separate from the notes
/// image store so the notes orphan-GC can never touch attachment files; the
/// <c>todo_attachments</c> rows are the only thing that keeps a file alive
/// (same content attached twice = one file, two rows).
/// </summary>
public interface ITodoAttachmentStore
{
    /// <summary>The store directory (backup writer zips it; restore extracts it).</summary>
    string Directory { get; }

    /// <summary>
    /// Writes <paramref name="content"/> to the store (idempotent — same content
    /// overwrites the same file) and returns the (sha, byteSize) pair for the
    /// reference row.
    /// </summary>
    Task<(string Sha, long ByteSize)> SaveAsync(Stream content, CancellationToken ct = default);

    /// <summary>
    /// Materializes the content under a user-facing file name for shell opening:
    /// a temp copy at <c>%TEMP%\QNote\viewer\attachments\&lt;sha&gt;_&lt;name&gt;</c>
    /// (overwrite-on-copy, at most once per content — same pattern as the note
    /// image viewer: packaged AppData virtualization hides the alias path from
    /// external processes).
    /// </summary>
    Task<string> MaterializeForOpenAsync(string sha, string fileName, CancellationToken ct = default);

    /// <summary>Deletes the file for <paramref name="sha"/>; true when it existed.</summary>
    Task<bool> TryDeleteAsync(string sha, CancellationToken ct = default);

    /// <summary>Opens the stored file for reading (restore / verification). Null when missing.</summary>
    Task<Stream?> OpenReadAsync(string sha, CancellationToken ct = default);
}

public sealed class TodoAttachmentStore : ITodoAttachmentStore
{
    public TodoAttachmentStore(AppPaths paths)
    {
        Directory = paths.AttachmentsDir;
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    public async Task<(string Sha, long ByteSize)> SaveAsync(Stream content, CancellationToken ct = default)
    {
        // Buffer to a memory stream: hashing needs the full content anyway and
        // the 10 MB cap (service-enforced) makes buffering cheap.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var path = Path.Combine(Directory, sha);
        if (!File.Exists(path))
            await File.WriteAllBytesAsync(path, bytes, ct);
        return (sha, bytes.LongLength);
    }

    public async Task<string> MaterializeForOpenAsync(string sha, string fileName, CancellationToken ct = default)
    {
        var safeName = MakeSafeFileName(fileName);
        var viewerDir = Path.Combine(Path.GetTempPath(), "QNote", "viewer", "attachments");
        System.IO.Directory.CreateDirectory(viewerDir);
        var target = Path.Combine(viewerDir, $"{sha}_{safeName}");
        File.Copy(Path.Combine(Directory, sha), target, overwrite: true);
        return await Task.FromResult(target);
    }

    public Task<bool> TryDeleteAsync(string sha, CancellationToken ct = default)
    {
        var path = Path.Combine(Directory, sha);
        if (!File.Exists(path))
            return Task.FromResult(false);
        File.Delete(path);
        return Task.FromResult(true);
    }

    public Task<Stream?> OpenReadAsync(string sha, CancellationToken ct = default)
    {
        var path = Path.Combine(Directory, sha);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }

    private static string MakeSafeFileName(string fileName)
    {
        // Strip path separators/dots so the temp name can never escape the viewer dir.
        var invalid = Path.GetInvalidFileNameChars();
        var chars = fileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var safe = new string(chars).Trim();
        return safe.Length == 0 ? "attachment" : safe;
    }
}
