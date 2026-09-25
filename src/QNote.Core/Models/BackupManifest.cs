using System.Text.Json.Serialization;

namespace QNote.Models;

/// <summary>
/// The <c>manifest.json</c> entry inside a <c>.qns</c> backup archive. Written at
/// backup time; read back on restore to validate schema compatibility (the Qt
/// build wrote a manifest but never read it — this one is actually checked).
/// </summary>
public sealed record BackupManifest
{
    /// <summary>Version of the app that produced the backup (informational).</summary>
    public string AppVersion { get; init; } = string.Empty;

    /// <summary>UTC creation timestamp (ISO-8601 "O").</summary>
    public string CreatedAt { get; init; } = string.Empty;

    /// <summary>True when the archive entries are AES-256 encrypted.</summary>
    public bool Encrypted { get; init; }

    /// <summary><c>PRAGMA user_version</c> of the backed-up database.</summary>
    public int SchemaVersion { get; init; }
}

/// <summary>
/// Trim-safe JSON for the backup manifest (reflection-based serialization is
/// banned project-wide — see csharp-conventions).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BackupManifest))]
internal partial class BackupJsonContext : JsonSerializerContext;
