using QNote.Models;

namespace QNote.Sync;

/// <summary>Task and list deletion names partition the shared tombstone folder.</summary>
public static class SyncTodoPaths
{
    public const string ListDeletionPrefix = "todo_list-";
    public const string TaskDeletionPrefix = "todo_task-";
    public static string Folder(SyncEntryKind kind) => kind == SyncEntryKind.TodoList ? SyncEngine.TodoListsFolder : SyncEngine.TodoTasksFolder;
    public static string FilePath(SyncEntryKind kind, string uuid) => Folder(kind) + "/" + uuid + (kind == SyncEntryKind.TodoList ? ".json" : ".qnote");
    public static string DeletionPrefix(SyncEntryKind kind) => kind == SyncEntryKind.TodoList ? ListDeletionPrefix : TaskDeletionPrefix;
    public static string DeletionPath(SyncEntryKind kind, string uuid) => "tombstones/" + DeletionPrefix(kind) + uuid;
    public static bool IsEntryPath(string path, string folder, string extension) =>
        path.StartsWith(folder + "/", StringComparison.Ordinal) && path.EndsWith(extension, StringComparison.Ordinal)
        && Guid.TryParseExact(path[(folder.Length + 1)..^extension.Length], "N", out _);

    public static bool TryDescribe(string path, out SyncEntryKind kind, out string uuid, out bool isDeletion)
    {
        foreach (var candidate in new[] { SyncEntryKind.TodoList, SyncEntryKind.TodoTask })
        {
            var prefix = "tombstones/" + DeletionPrefix(candidate);
            if (path.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(path[prefix.Length..], "N", out _))
            { kind = candidate; uuid = path[prefix.Length..]; isDeletion = true; return true; }
            if (IsEntryPath(path, Folder(candidate), candidate == SyncEntryKind.TodoList ? ".json" : ".qnote"))
            { kind = candidate; uuid = Path.GetFileNameWithoutExtension(path); isDeletion = false; return true; }
        }
        kind = default; uuid = ""; isDeletion = false; return false;
    }
}
