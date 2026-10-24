using System.Text.Json;
using System.Text.Json.Serialization;
using QNote.Models;

namespace QNote.Sync;

/// <summary>Local ids and device reminder bookkeeping are normalized before transport.</summary>
public sealed record SyncTodoTaskBody
{
    public required TodoTask Task { get; init; }
    public TodoStep[] Steps { get; init; } = [];
    public TodoAttachment[] Attachments { get; init; } = [];

    public static SyncTodoTaskBody FromDetail(TodoTaskDetail detail) => new()
    {
        Task = detail.Task with { Id = 0, CatchUpFiredAt = null },
        Steps = detail.Steps.OrderBy(s => s.SortOrder).ThenBy(s => s.Uuid, StringComparer.Ordinal)
            .Select(s => s with { Id = 0, TaskId = 0, TaskUuid = detail.Task.Uuid }).ToArray(),
        Attachments = detail.Attachments.OrderBy(a => a.Uuid, StringComparer.Ordinal)
            .Select(a => a with { Id = 0, TaskId = 0, TaskUuid = detail.Task.Uuid }).ToArray(),
    };

    public string Encode() => JsonSerializer.Serialize(this, SyncTodoJsonContext.Default.SyncTodoTaskBody);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(TodoList))]
[JsonSerializable(typeof(SyncTodoTaskBody))]
public sealed partial class SyncTodoJsonContext : JsonSerializerContext;
