using System.Text.Json.Serialization;

namespace QNote.Sync;

/// <summary>
/// The <c>manifest.json</c> at the sync root (PRD decision 12): the cloud's
/// format version, encryption mode, target salt and resumable conversion state.
/// Each enable operation creates a fresh 32-byte salt. Encryption stays enabled
/// throughout either conversion until all managed files have been verified.
/// </summary>
public sealed record SyncManifest
{
    /// <summary>The layout/cloud-protocol version this build writes (v1).</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>Version of the cloud layout the writer used; never rewritten forward.</summary>
    public int FormatVersion { get; init; } = CurrentFormatVersion;

    /// <summary>True for encrypted targets and while either encryption conversion is pending.</summary>
    public bool E2eeEnabled { get; init; }

    /// <summary>
    /// The target's PBKDF2 salt (base64, 32 bytes). Also retained after disabling
    /// encryption to authenticate <see cref="EncryptionDisabledProof"/>.
    /// One master key per sync target, never a separate derivation per file.
    /// </summary>
    public string? E2eeSalt { get; init; }

    /// <summary>Authenticated verifier, including for targets with no containers yet.</summary>
    public string? E2eeKeyCheck { get; init; }
    public int? EncryptionVersion { get; init; }
    public int? E2eeKdfIterations { get; init; }
    /// <summary>Authorizes a completed disable operation to previously unlocked devices.</summary>
    public string? EncryptionDisabledProof { get; init; }

    /// <summary>Non-null while converting. E2eeEnabled remains true as a sync barrier.</summary>
    public bool? EncryptionConversionTo { get; init; }

    public string? EncryptionConversionId { get; init; }
}

/// <summary>Trim-safe JSON for the sync manifest (reflection-based serialization
/// is banned project-wide — see csharp-conventions).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SyncManifest))]
public sealed partial class SyncManifestJsonContext : JsonSerializerContext;
