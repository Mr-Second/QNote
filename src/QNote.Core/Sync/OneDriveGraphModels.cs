using System.Text.Json.Serialization;

namespace QNote.Sync;

/// <summary>A Microsoft Graph driveItem, restricted to the fields the driver uses.</summary>
public sealed record OneDriveDriveItem
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>OneDrive's concurrency token (arrives with embedded quotes, e.g. <c>"abc,1"</c>) — passed back verbatim in <c>If-Match</c>.</summary>
    [JsonPropertyName("eTag")]
    public string? ETag { get; init; }

    [JsonPropertyName("size")]
    public long? Size { get; init; }
}

/// <summary>The body of <c>GET .../children</c>: one page plus the pagination cursor.</summary>
public sealed record OneDriveChildrenResponse
{
    [JsonPropertyName("value")]
    public List<OneDriveDriveItem>? Value { get; init; }

    /// <summary>Absolute URL of the next page; null after the last page.</summary>
    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; init; }
}

/// <summary>Folder creation via <c>POST .../children</c>; an existing folder is the expected <c>409 nameAlreadyExists</c>.</summary>
public sealed record OneDriveFolderCreateRequest
{
    public sealed record FolderMarker;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("folder")]
    public FolderMarker Folder { get; init; } = new();

    [JsonPropertyName("@microsoft.graph.conflictBehavior")]
    public string ConflictBehavior { get; init; } = "fail";
}

/// <summary>The body OneDrive attaches to error responses.</summary>
public sealed record OneDriveErrorResponse
{
    [JsonPropertyName("error")]
    public OneDriveErrorBody? Error { get; init; }
}

public sealed record OneDriveErrorBody
{
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>Trim-safe JSON for the OneDrive wire format (reflection-based serialization
/// is banned project-wide — see csharp-conventions).</summary>
[JsonSerializable(typeof(OneDriveDriveItem))]
[JsonSerializable(typeof(OneDriveChildrenResponse))]
[JsonSerializable(typeof(OneDriveFolderCreateRequest))]
[JsonSerializable(typeof(OneDriveErrorResponse))]
public sealed partial class OneDriveGraphJsonContext : JsonSerializerContext;
