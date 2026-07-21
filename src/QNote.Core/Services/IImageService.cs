namespace QNote.Services;

/// <summary>Image insertion / storage (port of the Qt ImageManager).</summary>
public interface IImageService
{
    /// <summary>Import an image into app-data <c>images\</c>; returns a relative reference for RTF.</summary>
    Task<string> ImportAsync(string sourcePath, CancellationToken ct = default);
}
