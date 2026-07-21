using Microsoft.Extensions.Logging;
using QNote.Infrastructure;

namespace QNote.Services;

/// <summary>
/// Copies images into app-data <c>images\</c> and returns a relative reference
/// (no absolute <c>file:///</c> URLs — parity-map change). Placeholder: the
/// md5-naming + RTF embedding pipeline is the image task.
/// </summary>
public sealed class ImageService : IImageService
{
    private readonly AppPaths _paths;
    private readonly ILogger<ImageService> _log;

    public ImageService(AppPaths paths, ILogger<ImageService> log)
    {
        _paths = paths;
        _log = log;
    }

    public Task<string> ImportAsync(string sourcePath, CancellationToken ct = default) =>
        throw new NotImplementedException("TODO(image-task): copy to images\\<md5>.<ext> and return a relative ref.");
}
