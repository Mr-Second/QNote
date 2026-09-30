using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Formatters;

/// <summary>Reads and writes the <c>.flow</c> package format: a ZIP container with <c>meta.json</c>
/// (container marker <c>{"format":"flow","version":"1.0"}</c>), <c>document.json</c> (the library's JSON
/// schema, its image pool referencing zip entries instead of embedding base64) and
/// <c>images/&lt;sha256&gt;</c> entries holding the original encoded bytes stored uncompressed. The JSON
/// string contract (<see cref="DocumentSerializer"/>) is unchanged; this is an additional layer for
/// file-based interchange.</summary>
public static class DocumentPackage
{
    /// <summary>Writes <paramref name="document"/> to <paramref name="destination"/> as a .flow
    /// package. The stream is left open.</summary>
    public static void Save(FlowDocument document, Stream destination)
    {
        var (dto, images) = DocumentSerializer.BuildDto(document);
        WriteDto(dto, images, destination);
    }

    // Pure-data half of Save: strips bytes from the DTO pool and writes the zip.
    internal static void WriteDto(FlowDocumentDto dto, Dictionary<string, (byte[] Bytes, string Mime)> images, Stream destination)
    {
        if (images.Count > 0)
        {
            dto.Images = new Dictionary<string, ImagePoolDto>();
            foreach (var (key, img) in images)
                dto.Images[key] = new ImagePoolDto { MimeType = img.Mime };
        }

        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        var metaEntry = zip.CreateEntry("meta.json", CompressionLevel.Optimal);
        using (var s = metaEntry.Open())
        {
            var meta = System.Text.Encoding.UTF8.GetBytes(
                $"{{\"format\":\"flow\",\"version\":\"{DocumentSerializer.CurrentSchemaVersion}\"}}");
            s.Write(meta, 0, meta.Length);
        }

        var docEntry = zip.CreateEntry("document.json", CompressionLevel.Optimal);
        using (var s = docEntry.Open())
            JsonSerializer.Serialize(s, dto, DocumentJsonContext.Default.FlowDocumentDto);
        foreach (var (key, img) in images)
        {
            // Already-compressed image bytes: store as-is (deflate would cost CPU for ~0% gain).
            var entry = zip.CreateEntry("images/" + key, CompressionLevel.NoCompression);
            using var s = entry.Open();
            s.Write(img.Bytes, 0, img.Bytes.Length);
        }
    }

    /// <summary>Reads a .flow package from <paramref name="source"/>. Image decoding is deferred to
    /// first render. The stream is left open.</summary>
    /// <exception cref="InvalidDataException">The stream is not a readable <c>.flow</c> package: not a zip,
    /// damaged, or a zip with no <c>document.json</c> (a .docx, say — every package this library writes
    /// has one).</exception>
    /// <exception cref="JsonException">The package's <c>document.json</c> is not valid JSON.</exception>
    public static FlowDocument Load(Stream source)
    {
        var (dto, pool) = ReadPackage(source);
        return DocumentSerializer.FromDto(dto, pool);
    }

    // Thread-free half of Load: zip + JSON parsing and byte extraction only, no model objects.
    internal static (FlowDocumentDto? Dto, Dictionary<string, (byte[] Bytes, string Mime)> Pool) ReadPackage(Stream source)
    {
        var pool = new Dictionary<string, (byte[] Bytes, string Mime)>();
        try
        {
            using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            var docEntry = zip.GetEntry("document.json");
            // A zip without document.json is some other zip — a .docx is one. It used to read as an EMPTY
            // document, which replaced whatever was open and marked it saved, so the next save wrote the
            // blank over the original (decided 2026-09-12). Reported like any other unreadable package.
            if (docEntry == null) throw new InvalidDataException("The zip has no document.json.");
            FlowDocumentDto? dto;
            using (var s = docEntry.Open())
                dto = JsonSerializer.Deserialize(s, DocumentJsonContext.Default.FlowDocumentDto);

            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.StartsWith("images/", StringComparison.Ordinal)) continue;
                string key = entry.FullName.Substring("images/".Length);
                if (key.Length == 0) continue;
                using var es = entry.Open();
                using var ms = new MemoryStream();
                es.CopyTo(ms);
                var bytes = ms.ToArray();
                // A null pool entry ("k": null) threw NullReferenceException out of the load.
                string mime = dto?.Images != null && dto.Images.TryGetValue(key, out var meta) && meta?.MimeType is { } m
                    ? m
                    : ImageMime.Detect(bytes);
                pool[key] = (bytes, mime);
            }
            return (dto, pool);
        }
        // A corrupt package used to come back as an empty document. That is the worst outcome available:
        // the host has no way to tell "this file was empty" from "this file is damaged", shows the user a
        // blank editor, and a save then overwrites a recoverable file with nothing. Malformed input is
        // reported; the caller decides what to tell the user. JsonException propagates as-is.
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("The stream is not a readable .flow package.", ex);
        }
    }
}
