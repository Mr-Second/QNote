using System.Text.Json.Serialization;

namespace QNote.Sync;

/// <summary>
/// The <c>manifest.json</c> at the sync root (PRD decision 12): the cloud's
/// format version plus the E2EE mode and its single per-target salt. The engine
/// validates the version on read, creates the file on a fresh cloud, and
/// generates the 32-byte salt exactly once — the moment E2EE is first enabled
/// (which, while E2EE mode is immutable per target, is manifest-creation time).
/// </summary>
public sealed record SyncManifest
{
    /// <summary>The layout/cloud-protocol version this build writes (v1).</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>Version of the cloud layout the writer used; never rewritten forward.</summary>
    public int FormatVersion { get; init; } = CurrentFormatVersion;

    /// <summary>True when every <c>.qnote</c> container on this target is AES-256-GCM.</summary>
    public bool E2eeEnabled { get; init; }

    /// <summary>
    /// The target's single PBKDF2 salt (base64, 32 bytes), present iff
    /// <see cref="E2eeEnabled"/>. One master key per sync target — derived once
    /// per cycle, never per file (PRD decision 12).
    /// </summary>
    public string? E2eeSalt { get; init; }
}

/// <summary>Trim-safe JSON for the sync manifest (reflection-based serialization
/// is banned project-wide — see csharp-conventions).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SyncManifest))]
public sealed partial class SyncManifestJsonContext : JsonSerializerContext;
