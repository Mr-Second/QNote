using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using QNote.Data;
using QNote.Data.Schema;
using QNote.Models;
using QNote.Services;
using QNote.Sync;
using QNote.ViewModels;

namespace QNote.Tests;

public sealed class TodoSyncEngineTests
{
    [Fact]
    public async Task LegacyChildClock_DoesNotHideSubsequentParentEdits()
    {
        using var source = new Device(); using var peer = new Device(source.Cloud);
        var task = await source.Service.CreateTaskAsync(SchemaInitializer.WorkListUuid, "legacy");
        await source.Service.AddStepAsync(task.Id, "future child");
        var detail = (await source.Repository.ReadSyncSnapshotAsync()).Tasks.Single();
        var future = DateTimeOffset.UtcNow.AddDays(1);
        await source.Repository.ApplySyncTaskAsync(detail with
        { Steps = detail.Steps.Select(s => s with { UpdatedAt = future }).ToArray() }, null);
        await source.RunAsync(); await peer.RunAsync();
        await source.Service.UpdateTaskAsync(task.Id, "new title", "new body");
        Assert.True((await source.Repository.GetTaskByIdAsync(task.Id))!.UpdatedAt > future);
        await source.RunAsync(); await peer.RunAsync();
        Assert.Equal("new title", (await peer.Repository.GetTaskByUuidAsync(task.Uuid))!.Title);
        await SettleAsync(source, peer);
    }

    [Fact]
    public async Task FreshInstall_DoesNotResurrectADeletedBuiltinList()
    {
        using var source = new Device();
        var list = (await source.Repository.GetListsAsync()).Single(l => l.Uuid == SchemaInitializer.WorkListUuid);
        await source.Service.DeleteListAsync(list.Id); await source.RunAsync();
        using var peer = new Device(source.Cloud); await peer.RunAsync();
        Assert.DoesNotContain(await peer.Repository.GetListsAsync(), l => l.Uuid == list.Uuid);
        Assert.DoesNotContain(SyncTodoPaths.FilePath(SyncEntryKind.TodoList, list.Uuid), source.Cloud.Files.Keys);
        await SettleAsync(source, peer);
    }

    [Fact]
    public async Task ImportedChildWithAnotherOwner_RollsBackTaskAndAgreement()
    {
        using var device = new Device();
        var first = await device.Service.CreateTaskAsync(SchemaInitializer.WorkListUuid, "first");
        var second = await device.Service.CreateTaskAsync(SchemaInitializer.WorkListUuid, "second");
        var step = await device.Service.AddStepAsync(first.Id, "owned by first");
        var snapshot = (await device.Repository.ReadSyncSnapshotAsync()).Tasks.Single(t => t.Task.Uuid == second.Uuid);
        var replacement = snapshot with { Task = snapshot.Task with { Title = "must roll back" },
            Steps = [step with { TaskId = second.Id, TaskUuid = second.Uuid }] };
        var agreement = new SyncStateEntry { EntryKind = SyncEntryKind.TodoTask, EntryUuid = second.Uuid,
            RemoteId = "remote", Etag = "tag", LastSyncedAtMs = second.UpdatedAt.ToUnixTimeMilliseconds() };
        await Assert.ThrowsAsync<InvalidDataException>(() => device.Repository.ApplySyncTaskAsync(replacement, agreement));
        Assert.Equal("second", (await device.Repository.GetTaskByIdAsync(second.Id))!.Title);
        Assert.Single(await device.Repository.GetStepsAsync(first.Id));
        Assert.Null(await new SyncStateStore(device.Db.Factory).GetAsync(SyncEntryKind.TodoTask, second.Uuid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletionAdvancesBeyondFutureTaskAndChildClocks(bool listDeletion)
    {
        using var device = new Device();
        var list = await device.Service.CreateListAsync("future", "");
        var task = await device.Service.CreateTaskAsync(list.Uuid, "future task");
        await device.Service.AddStepAsync(task.Id, "future child");
        var detail = (await device.Repository.ReadSyncSnapshotAsync()).Tasks.Single();
        var future = DateTimeOffset.UtcNow.AddDays(2);
        await device.Repository.ApplySyncTaskAsync(detail with { Task = detail.Task with { UpdatedAt = future },
            Steps = detail.Steps.Select(s => s with { UpdatedAt = future }).ToArray() }, null);
        if (listDeletion) await device.Service.DeleteListAsync(list.Id);
        else await device.Service.DeleteTaskAsync(task.Id);
        var deleted = (await device.Repository.ReadSyncSnapshotAsync()).Tasks.Single().Task;
        Assert.True(deleted.DeletedAt > future);
        Assert.Equal(deleted.DeletedAt, deleted.UpdatedAt);
    }

    [Fact]
    public async Task TaskDeletionDoesNotDeleteANoteWithTheSameUuid()
    {
        using var device = new Device();
        var note = await device.Db.NewNoteService().CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var task = await device.Repository.CreateTaskAsync(new TodoTask
        { Uuid = note.Uuid, ListUuid = SchemaInitializer.WorkListUuid, Title = "same identity", CreatedAt = now, UpdatedAt = now });
        await device.RunAsync(); await device.Service.DeleteTaskAsync(task.Id); await device.RunAsync();
        Assert.Contains($"notes/{note.Uuid}.qnote", device.Cloud.Files.Keys);
        Assert.DoesNotContain($"todo_tasks/{note.Uuid}.qnote", device.Cloud.Files.Keys);
        await device.RunAsync();
        Assert.NotNull(await device.Db.NewRepository().GetByIdAsync(note.Id));
    }

    [Fact]
    public async Task LocalEditDuringDownload_IsNotOverwrittenOrMarkedAgreed()
    {
        using var source = new Device(); using var peer = new Device(source.Cloud);
        var task = await source.Service.CreateTaskAsync(SchemaInitializer.WorkListUuid, "initial");
        await source.RunAsync(); await peer.RunAsync();
        await source.Service.UpdateTaskAsync(task.Id, "remote edit", ""); await source.RunAsync();
        var path = SyncTodoPaths.FilePath(SyncEntryKind.TodoTask, task.Uuid);
        var peerTask = (await peer.Repository.GetTaskByUuidAsync(task.Uuid))!;
        var before = await new SyncStateStore(peer.Db.Factory).GetAsync(SyncEntryKind.TodoTask, task.Uuid);
        source.Cloud.BeforeRead = async (reading, ct) =>
        {
            if (reading != path) return;
            source.Cloud.BeforeRead = null;
            await peer.Service.UpdateTaskAsync(peerTask.Id, "typed while downloading", "local draft", ct);
        };
        var result = await peer.Engine.RunAsync();
        Assert.False(result.Ok);
        Assert.Equal("typed while downloading", (await peer.Repository.GetTaskByUuidAsync(task.Uuid))!.Title);
        Assert.Equal(before, await new SyncStateStore(peer.Db.Factory).GetAsync(SyncEntryKind.TodoTask, task.Uuid));
        await SettleAsync(source, peer);
    }

    [Fact]
    public async Task EncryptedTaskContainer_RoundTripsWithTheSameTargetKey()
    {
        using var source = new Device(); using var peer = new Device(source.Cloud);
        var task = await source.Service.CreateTaskAsync(SchemaInitializer.WorkListUuid, "encrypted");
        await source.AttachAsync(task.Id, "one.txt", "encrypted bytes");
        var key = RandomNumberGenerator.GetBytes(32);
        Assert.True((await source.Engine.RunAsync(key)).Ok);
        Assert.True((await peer.Engine.RunAsync(key)).Ok);
        Assert.NotNull(await peer.Repository.GetTaskByUuidAsync(task.Uuid));
        var payload = QnoteContainerCodec.Decode(source.Cloud.Files[$"todo_tasks/{task.Uuid}.qnote"], key);
        Assert.Equal("encrypted bytes", Encoding.UTF8.GetString(Assert.Single(payload.Entries).Data));
    }

    [Fact]
    public async Task TaskAggregate_RoundTripsWithRemappedIds_AndConvergesWithoutWrites()
    {
        using var source = new Device();
        using var peer = new Device(source.Cloud);
        await peer.Service.CreateTaskAsync(SchemaInitializer.WorkListUuid, "peer local row");
        var list = await source.Service.CreateListAsync("shared list", "#112233");
        list = (await source.Service.UpdateListAsync(list with { Theme = 3, HideCompleted = true, SortMode = 2 }))!;
        var task = await source.Service.CreateTaskAsync(list.Uuid, "shared task");
        await source.Service.UpdateTaskAsync(task.Id, "shared task", "task notes");
        await source.Service.SetDueAsync(task.Id, DateTimeOffset.UtcNow.AddDays(10), false, true);
        await source.Service.SetImportantAsync(task.Id, true);
        await source.Service.SetRecurrenceAsync(task.Id, TodoRecurrence.Weekly);
        var step = await source.Service.AddStepAsync(task.Id, "step");
        await source.Service.SetStepCompletedAsync(step.Id, true);
        var attachment = await source.AttachAsync(task.Id, "report.txt", "shared bytes");
        await source.AttachAsync(task.Id, "same bytes.txt", "shared bytes");

        await source.RunAsync();
        await peer.RunAsync();

        var remote = (await peer.Repository.GetTaskByUuidAsync(task.Uuid))!;
        Assert.NotEqual(task.Id, remote.Id);
        Assert.Equal(list.Uuid, remote.ListUuid);
        Assert.Equal("task notes", remote.Notes);
        Assert.True(remote.ReminderOn);
        Assert.True(remote.IsImportant);
        Assert.Equal(TodoRecurrence.Weekly, remote.RecurrenceRule);
        var remoteStep = Assert.Single(await peer.Repository.GetStepsAsync(remote.Id));
        Assert.Equal(task.Uuid, remoteStep.TaskUuid);
        Assert.Equal(remote.Id, remoteStep.TaskId);
        Assert.NotNull(remoteStep.CompletedAt);
        var attachments = await peer.Repository.GetAttachmentsAsync(remote.Id);
        Assert.Equal(2, attachments.Count);
        Assert.All(attachments, a => Assert.Equal(remote.Id, a.TaskId));
        Assert.Equal("shared bytes", await File.ReadAllTextAsync(Path.Combine(peer.Db.Paths.AttachmentsDir, attachment.Sha)));
        var remoteList = (await peer.Repository.GetListsAsync()).Single(l => l.Uuid == list.Uuid);
        Assert.Equal((3, true, 2), (remoteList.Theme, remoteList.HideCompleted, remoteList.SortMode));
        var payload = QnoteContainerCodec.Decode(source.Cloud.Files[$"todo_tasks/{task.Uuid}.qnote"]);
        Assert.Single(payload.Entries); // duplicate hashes share bytes, not metadata identities
        Assert.DoesNotContain("taskId", payload.Body);
        await SettleAsync(source, peer);
    }

    [Fact]
    public async Task ChildEditsAndRemovals_AdvanceParentClock_AndReplaceTheWholeChildSnapshot()
    {
        using var source = new Device(); using var peer = new Device(source.Cloud);
        var task = await source.Service.CreateTaskAsync(SchemaInitializer.WorkListUuid, "task");
        var step = await source.Service.AddStepAsync(task.Id, "check me");
        var attachment = await source.AttachAsync(task.Id, "one.txt", "one");
        await source.RunAsync(); await peer.RunAsync();
        var before = (await source.Repository.GetTaskByIdAsync(task.Id))!.UpdatedAt;
        await source.Service.SetStepCompletedAsync(step.Id, true);
        Assert.True((await source.Repository.GetTaskByIdAsync(task.Id))!.UpdatedAt > before);
        await source.RunAsync(); await peer.RunAsync();
        var other = (await peer.Repository.GetTaskByUuidAsync(task.Uuid))!;
        Assert.NotNull(Assert.Single(await peer.Repository.GetStepsAsync(other.Id)).CompletedAt);

        await source.Service.DeleteStepAsync(step.Id);
        await source.Service.DeleteAttachmentAsync(attachment.Id);
        await source.RunAsync(); await peer.RunAsync();
        Assert.Empty(await peer.Repository.GetStepsAsync(other.Id));
        Assert.Empty(await peer.Repository.GetAttachmentsAsync(other.Id));
        await SettleAsync(source, peer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deletions_PropagateAndNeverResurrect(bool deleteList)
    {
        using var source = new Device(); using var peer = new Device(source.Cloud);
        var list = await source.Service.CreateListAsync("delete list", "");
        var task = await source.Service.CreateTaskAsync(list.Uuid, "delete task");
        await source.Service.AddStepAsync(task.Id, "child");
        await source.AttachAsync(task.Id, "file.txt", "bytes");
        await source.RunAsync(); await peer.RunAsync();
        if (deleteList) await source.Service.DeleteListAsync(list.Id);
        else await source.Service.DeleteTaskAsync(task.Id);
        await source.RunAsync(); await peer.RunAsync();
        Assert.Null(await peer.Repository.GetTaskByUuidAsync(task.Uuid));
        Assert.Contains(SyncTodoPaths.DeletionPath(SyncEntryKind.TodoTask, task.Uuid), source.Cloud.Files.Keys);
        if (deleteList) Assert.DoesNotContain(await peer.Repository.GetListsAsync(), l => l.Uuid == list.Uuid);
        await SettleAsync(source, peer);
    }

    [Fact]
    public async Task EqualClockConflict_PreservesLosingTaskStepsAndAttachments_ThenConverges()
    {
        using var source = new Device(); using var peer = new Device(source.Cloud);
        var task = await source.Service.CreateTaskAsync(SchemaInitializer.WorkListUuid, "base");
        await source.Service.AddStepAsync(task.Id, "original step");
        await source.AttachAsync(task.Id, "file.txt", "attachment bytes");
        await source.RunAsync(); await peer.RunAsync();
        var version = (await source.Repository.ReadSyncSnapshotAsync()).Tasks.Single(t => t.Task.Uuid == task.Uuid);
        var stamp = version.Task.UpdatedAt.AddMinutes(1);
        await source.Repository.ApplySyncTaskAsync(version with { Task = version.Task with { Title = "source edit", UpdatedAt = stamp } }, null);
        var other = (await peer.Repository.ReadSyncSnapshotAsync()).Tasks.Single(t => t.Task.Uuid == task.Uuid);
        await peer.Repository.ApplySyncTaskAsync(other with { Task = other.Task with { Title = "peer edit", UpdatedAt = stamp } }, null);
        await source.RunAsync();
        var result = await peer.RunAsync();
        Assert.Equal(1, result.ConflictCopies);
        await source.RunAsync();
        var tasks = (await source.Repository.ReadSyncSnapshotAsync()).Tasks.Where(t => t.Task.DeletedAt is null).ToArray();
        Assert.Equal(2, tasks.Length);
        Assert.Contains(tasks, t => t.Task.Title.Contains("(conflict)", StringComparison.Ordinal));
        Assert.All(tasks, t => { Assert.Single(t.Steps); Assert.Single(t.Attachments); });
        await SettleAsync(source, peer);
    }

    [Fact]
    public async Task WeakEtags_StillDetectSameClockChangedContent()
    {
        using var source = new Device(); using var peer = new Device(source.Cloud);
        var task = await source.Service.CreateTaskAsync(SchemaInitializer.WorkListUuid, "base");
        await source.RunAsync(); await peer.RunAsync();
        var path = SyncTodoPaths.FilePath(SyncEntryKind.TodoTask, task.Uuid);
        source.Cloud.Etags[path] = "";
        var detail = (await source.Repository.ReadSyncSnapshotAsync()).Tasks.Single();
        await source.Repository.ApplySyncTaskAsync(detail with { Task = detail.Task with { Title = "same clock change" } }, null);
        await source.RunAsync(); await peer.RunAsync();
        await SettleAsync(source, peer);
        Assert.Contains(await peer.Repository.GetAllAsync(), t => t.Title.Contains("same clock change", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NewDevice_DefaultBuiltinListDoesNotOverwriteCloudListOptions()
    {
        using var source = new Device();
        var list = (await source.Repository.GetListsAsync()).Single(l => l.Uuid == SchemaInitializer.WorkListUuid);
        await source.Service.UpdateListAsync(list with { Name = "renamed", Theme = 5, HideCompleted = true });
        await source.RunAsync();
        using var peer = new Device(source.Cloud);
        await peer.RunAsync();
        var restored = (await peer.Repository.GetListsAsync()).Single(l => l.Uuid == list.Uuid);
        Assert.Equal("renamed", restored.Name);
        Assert.Equal(5, restored.Theme);
        Assert.True(restored.HideCompleted);
        await SettleAsync(source, peer);
    }

    [Theory]
    [InlineData(SyncEngine.TodoListsFolder)]
    [InlineData(SyncEngine.TodoTasksFolder)]
    public async Task IncompleteTodoListing_BlocksAllDomainsBeforeManifestOrNoteWrites(string folder)
    {
        using var device = new Device();
        await device.Db.NewNoteService().CreateAsync();
        await device.Service.CreateTaskAsync(SchemaInitializer.WorkListUuid, "local");
        device.Cloud.TruncatedFolders.Add(folder);
        var result = await device.Engine.RunAsync();
        Assert.Equal(SyncCycleAbortKind.ListingIncomplete, result.AbortKind);
        Assert.Empty(device.Cloud.Writes);
        Assert.Empty(device.Cloud.Deletes);
    }

    [Fact]
    public async Task CorruptRemoteAttachment_LeavesExistingTaskAndAgreementUntouched()
    {
        using var source = new Device(); using var peer = new Device(source.Cloud);
        var task = await source.Service.CreateTaskAsync(SchemaInitializer.WorkListUuid, "valid");
        await source.AttachAsync(task.Id, "file.txt", "correct bytes");
        await source.RunAsync(); await peer.RunAsync();
        var path = SyncTodoPaths.FilePath(SyncEntryKind.TodoTask, task.Uuid);
        // Flipping a final DEFLATE padding bit may leave the payload unchanged.
        // Truncation reliably damages the attachment-bearing container.
        var bytes = source.Cloud.Files[path][..^8];
        source.Cloud.Seed(path, bytes);
        var before = await new SyncStateStore(peer.Db.Factory).GetAsync(SyncEntryKind.TodoTask, task.Uuid);
        var result = await peer.Engine.RunAsync();
        Assert.False(result.Ok);
        Assert.Equal("valid", (await peer.Repository.GetTaskByUuidAsync(task.Uuid))!.Title);
        Assert.Equal(before, await new SyncStateStore(peer.Db.Factory).GetAsync(SyncEntryKind.TodoTask, task.Uuid));
    }

    [Fact]
    public async Task MissingLocalAttachment_DoesNotPublishPartialTask()
    {
        using var device = new Device();
        var task = await device.Service.CreateTaskAsync(SchemaInitializer.WorkListUuid, "task");
        var attachment = await device.AttachAsync(task.Id, "file.txt", "missing");
        File.Delete(Path.Combine(device.Db.Paths.AttachmentsDir, attachment.Sha));
        var result = await device.Engine.RunAsync();
        Assert.False(result.Ok);
        Assert.DoesNotContain(SyncTodoPaths.FilePath(SyncEntryKind.TodoTask, task.Uuid), device.Cloud.Files.Keys);
        Assert.Null(await new SyncStateStore(device.Db.Factory).GetAsync(SyncEntryKind.TodoTask, task.Uuid));
    }

    [Fact]
    public async Task ReminderBookkeeping_DoesNotChangeWireDataOrUpload()
    {
        using var device = new Device();
        var task = await device.Service.CreateTaskAsync(SchemaInitializer.WorkListUuid, "task");
        await device.RunAsync(); device.Cloud.Writes.Clear();
        await device.Repository.MarkCatchUpFiredAsync(task.Id, DateTimeOffset.UtcNow);
        await device.RunAsync(); Assert.Empty(device.Cloud.Writes);
    }

    [Fact]
    public async Task UserMutations_EnqueueOwningTaskAndImportsDoNotEnqueue()
    {
        using var device = new Device();
        var task = await device.Service.CreateTaskAsync(SchemaInitializer.WorkListUuid, "task");
        var step = await device.Service.AddStepAsync(task.Id, "step");
        await device.Service.SetImportantAsync(task.Id, true);
        await device.Service.SetRecurrenceAsync(task.Id, TodoRecurrence.Weekly);
        await device.Service.SetStepCompletedAsync(step.Id, true);
        var attachment = await device.AttachAsync(task.Id, "one", "one");
        await device.Service.DeleteStepAsync(step.Id);
        await device.Service.DeleteAttachmentAsync(attachment.Id);
        Assert.Equal(8, device.Changes.Count);
        Assert.All(device.Changes, change => Assert.Equal((SyncEntryKind.TodoTask, task.Uuid), change));
        device.Changes.Clear();
        var detail = (await device.Repository.ReadSyncSnapshotAsync()).Tasks.Single();
        await device.Repository.ApplySyncTaskAsync(detail, null);
        Assert.Empty(device.Changes);
    }

    [Fact]
    public async Task DirtyEditor_PreservesDownloadedTaskAndDraftAsSeparateTasks()
    {
        using var device = new Device();
        var task = await device.Service.CreateTaskAsync(SchemaInitializer.WorkListUuid, "original");
        await device.Service.AddStepAsync(task.Id, "step");
        await device.AttachAsync(task.Id, "file.txt", "bytes");
        var vm = new TodoPageViewModel(device.Service, NullLogger<TodoPageViewModel>.Instance,
            detailSaveDebounce: TimeSpan.FromHours(1));
        await vm.LoadAsync(); await vm.SelectTaskByUuidAsync(task.Uuid);
        vm.DetailTitle = "unsaved local"; vm.DetailNotes = "unsaved body";
        var detail = (await device.Repository.ReadSyncSnapshotAsync()).Tasks.Single();
        await device.Repository.ApplySyncTaskAsync(detail with { Task = detail.Task with
        { Title = "cloud title", Notes = "cloud notes", UpdatedAt = detail.Task.UpdatedAt.AddMinutes(1) } }, null);
        await vm.RefreshAfterSyncAsync();
        Assert.Equal("unsaved local", vm.DetailTitle);
        await vm.FlushAsync();
        Assert.False(vm.HasPendingDetailEdits);
        Assert.Equal("cloud title", (await device.Repository.GetTaskByUuidAsync(task.Uuid))!.Title);
        var copy = (await device.Repository.ReadSyncSnapshotAsync()).Tasks.Single(t => t.Task.Uuid != task.Uuid);
        Assert.Equal("unsaved local (conflict)", copy.Task.Title);
        Assert.Equal("unsaved body", copy.Task.Notes);
        Assert.Single(copy.Steps); Assert.Single(copy.Attachments);
        Assert.Equal(copy.Task.Uuid, vm.SelectedTask?.Uuid);
        vm.Detach();
    }

    private static async Task SettleAsync(Device first, Device second)
    {
        await first.RunAsync(); await second.RunAsync(); await first.RunAsync();
        first.Cloud.Writes.Clear(); first.Cloud.Deletes.Clear();
        await first.RunAsync(); await second.RunAsync();
        Assert.Empty(first.Cloud.Writes); Assert.Empty(first.Cloud.Deletes);
        var a = (await first.Repository.ReadSyncSnapshotAsync()).Tasks.Where(t => t.Task.DeletedAt is null)
            .OrderBy(t => t.Task.Uuid, StringComparer.Ordinal).Select(t => SyncTodoTaskBody.FromDetail(t).Encode());
        var b = (await second.Repository.ReadSyncSnapshotAsync()).Tasks.Where(t => t.Task.DeletedAt is null)
            .OrderBy(t => t.Task.Uuid, StringComparer.Ordinal).Select(t => SyncTodoTaskBody.FromDetail(t).Encode());
        Assert.Equal(a, b);
    }

    private sealed class Device : IDisposable
    {
        public TestDatabase Db { get; } = new();
        public TodoRepository Repository { get; }
        public TodoService Service { get; }
        public FakeSyncDriver Cloud { get; }
        public SyncEngine Engine { get; }
        public List<(SyncEntryKind, string)> Changes { get; } = [];
        public Device(FakeSyncDriver? cloud = null)
        {
            Cloud = cloud ?? new FakeSyncDriver(); Repository = Db.NewTodoRepository();
            Service = new TodoService(Repository, NullLogger<TodoService>.Instance,
                attachments: new TodoAttachmentStore(Db.Paths), onLocalChange: (kind, uuid) => Changes.Add((kind, uuid)));
            Engine = new SyncEngine(Db.NewRepository(), new SyncStateStore(Db.Factory), Cloud, Db.Paths,
                NullLogger<SyncEngine>.Instance, todos: Repository);
        }
        public Task<TodoAttachment> AttachAsync(long taskId, string name, string content) =>
            Service.AddAttachmentAsync(taskId, name, new MemoryStream(Encoding.UTF8.GetBytes(content)));
        public async Task<SyncCycleResult> RunAsync()
        {
            var result = await Engine.RunAsync();
            Assert.True(result.Ok, string.Join(Environment.NewLine, result.Errors));
            return result;
        }
        public void Dispose() => Db.Dispose();
    }
}
