using System.Text.Json.Serialization;

namespace QNote.Sync;

/// <summary>
/// The wire form of one category row (<c>categories/{uuid}.json</c>). Plain LWW
/// on <see cref="UpdatedAtMs"/>; the engine downloads the whole file to read it
/// (a KB-size JSON — no range probe is worth a second code path). DeletedAtMs
/// mirrors the schema-v8 tombstone column (null = live).
/// </summary>
public sealed record SyncCategoryFile
{
    public required string Uuid { get; init; }

    public required string Name { get; init; }

    public string IconKey { get; init; } = string.Empty;

    /// <summary>Accent color as <c>#RRGGBB</c>; empty = theme accent.</summary>
    public string Color { get; init; } = string.Empty;

    /// <summary>Synced sidebar order (reorders bump UpdatedAt — it IS data).</summary>
    public int SortOrder { get; init; }

    public required long CreatedAtMs { get; init; }

    public required long UpdatedAtMs { get; init; }

    public long? DeletedAtMs { get; init; }
}

/// <summary>Trim-safe JSON for the category cloud files (reflection-based
/// serialization is banned project-wide — see csharp-conventions).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SyncCategoryFile))]
public sealed partial class SyncCategoryFileJsonContext : JsonSerializerContext;
