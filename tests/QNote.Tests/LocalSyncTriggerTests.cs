using Microsoft.Extensions.Logging.Abstractions;
using QNote.Data;
using QNote.Memory;
using QNote.Models;
using QNote.Services;
using QNote.Sync;

namespace QNote.Tests;

/// <summary>Real service writes through the debounce/scheduler/engine boundary.</summary>
public sealed class LocalSyncTriggerTests
{
    [Fact]
    public async Task CreateAndSave_CoalesceThenUpload_AndPeerConverges()
    {
        using var source = new SyncDevice();
        using var peer = new SyncDevice(source.Cloud);

        var note = await source.Notes.CreateAsync();
        await source.Notes.UpdateAsync(note with { Title = "edited", Content = "# latest" });

        Assert.Equal(1, source.Queue.PendingCount);
        Assert.Equal(PushQueue.DefaultDebounce, source.PushTimer.Delay);
        Assert.Empty(source.Cloud.Files);

        var first = await source.FirePushAsync();
        Assert.True(first!.Ok);
        Assert.True(source.Cloud.Files.ContainsKey($"notes/{note.Uuid}.qnote"));
        Assert.Equal("# latest", Decode(source.Cloud, note.Uuid).Content);

        Assert.True((await peer.Engine.RunAsync()).Ok);
        Assert.Equal("# latest", Assert.Single(await peer.Notes.GetAllAsync()).Content);
        Assert.Equal(0, peer.Queue.PendingCount);

        source.Cloud.Writes.Clear();
        Assert.True((await source.Engine.RunAsync()).Ok);
        Assert.True((await peer.Engine.RunAsync()).Ok);
        Assert.Empty(source.Cloud.Writes);
    }

    [Fact]
    public async Task SavedNote_UploadsAfterAnImmediatelyPrecedingCycle()
    {
        using var device = new SyncDevice();
        var note = await SeedNoteAsync(device, "before");
        await device.Scheduler.TriggerNow(bypassThrottle: true)!;

        await device.Notes.UpdateAsync(note with { Title = "after", Content = "saved" });
        var result = await device.FirePushAsync();

        Assert.True(result!.Ok);
        Assert.Equal(1, result.Uploaded);
        Assert.Equal("after", Decode(device.Cloud, note.Uuid).Title);
    }

    [Fact]
    public async Task DeleteNote_AutomaticallyPublishesTombstone_AndDeletesOnPeer()
    {
        using var device = new SyncDevice();
        using var peer = new SyncDevice(device.Cloud);
        var note = await SeedNoteAsync(device, "delete me");
        await device.Engine.RunAsync();
        await peer.Engine.RunAsync();

        await device.Notes.DeleteAsync(note.Id);
        Assert.Equal(1, device.Queue.PendingCount);
        Assert.True((await device.FirePushAsync())!.Ok);

        Assert.Contains($"tombstones/{note.Uuid}", device.Cloud.Files.Keys);
        Assert.DoesNotContain($"notes/{note.Uuid}.qnote", device.Cloud.Files.Keys);
        Assert.True((await peer.Engine.RunAsync()).Ok);
        Assert.Empty(await peer.Notes.GetAllAsync());
        Assert.Equal(0, peer.Queue.PendingCount);
    }

    [Fact]
    public async Task CategoryCreateAndEdit_UsePersistedIdentity_AndUpload()
    {
        using var device = new SyncDevice();
        var created = await device.Categories.CreateAsync("custom", "#112233", "E8A5");
        // Settings/edit callers reconstruct a category from its visible fields.
        await device.Categories.UpdateAsync(new Category
        {
            Id = created.Id, Name = "renamed", Color = "#334455", IconKey = "E8A5",
        });

        Assert.Equal(1, device.Queue.PendingCount);
        Assert.True((await device.FirePushAsync())!.Ok);
        var bytes = device.Cloud.Files[$"categories/{created.Uuid}.json"];
        Assert.Contains("renamed", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task CategoryRename_AutomaticallyUploadsItsNotes()
    {
        using var device = new SyncDevice();
        var category = await SeedCategoryAsync(device, "old");
        var note = await SeedNoteAsync(device, "body", category.Name);
        await device.Engine.RunAsync();

        await device.Categories.UpdateAsync(category with { Name = "new" });
        Assert.True((await device.FirePushAsync())!.Ok);

        Assert.Equal("new", Decode(device.Cloud, note.Uuid).Category);
        Assert.Contains($"categories/{category.Uuid}.json", device.Cloud.Writes);
    }

    [Fact]
    public async Task CategoryDelete_AutomaticallyPublishesItsNotesTombstones()
    {
        using var device = new SyncDevice();
        var category = await SeedCategoryAsync(device, "custom");
        var note = await SeedNoteAsync(device, "body", category.Name);
        await device.Engine.RunAsync();

        await device.Categories.DeleteAsync(category.Id);
        Assert.True((await device.FirePushAsync())!.Ok);

        Assert.Contains($"tombstones/{category.Uuid}", device.Cloud.Files.Keys);
        Assert.Contains($"tombstones/{note.Uuid}", device.Cloud.Files.Keys);
        Assert.DoesNotContain($"notes/{note.Uuid}.qnote", device.Cloud.Files.Keys);
    }

    [Fact]
    public async Task CategoryReorder_QueuesOnlyExistingRequestedRows()
    {
        using var device = new SyncDevice();
        var categories = await device.Categories.GetAllAsync();
        var order = categories.Reverse().Select(c => c.Id).Append(long.MaxValue).ToArray();

        await device.Categories.ReorderAsync(order);

        Assert.Equal(categories.Count, device.Queue.PendingCount);
        Assert.True((await device.FirePushAsync())!.Ok);
        using var peer = new SyncDevice(device.Cloud);
        Assert.True((await peer.Engine.RunAsync()).Ok);
        Assert.Equal(categories.Reverse().Select(c => c.Name),
            (await peer.Categories.GetAllAsync()).Select(c => c.Name));
        Assert.Equal(0, peer.Queue.PendingCount);
    }

    [Fact]
    public async Task FailedOrMissingMutation_DoesNotQueueAnything()
    {
        using var device = new SyncDevice();
        var builtin = (await device.Categories.GetAllAsync())[0];

        await Assert.ThrowsAsync<CategoryException>(() =>
            device.Categories.CreateAsync(" ", "#112233", ""));
        await Assert.ThrowsAsync<CategoryException>(() => device.Categories.DeleteAsync(builtin.Id));
        await device.Categories.DeleteAsync(long.MaxValue);
        await device.Notes.DeleteAsync(long.MaxValue);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            device.Notes.CreateAsync(ct: cancellation.Token));

        Assert.Equal(0, device.Queue.PendingCount);
        Assert.Null(device.PushTimer.Delay);
    }

    [Fact]
    public async Task SignedOutPush_KeepsDataDirtyUntilNextSignedInCycle()
    {
        using var device = new SyncDevice();
        device.SignedIn = false;
        var note = await device.Notes.CreateAsync();
        Assert.Null(await device.FirePushAsync());
        Assert.Empty(device.Cloud.Files);

        device.SignedIn = true;
        var result = await device.Scheduler.TriggerNow(bypassThrottle: true)!;

        Assert.True(result!.Ok);
        Assert.Contains($"notes/{note.Uuid}.qnote", device.Cloud.Files.Keys);
    }

    [Fact]
    public async Task EditDuringFlight_QueuesOneFollowUpCycle()
    {
        using var device = new SyncDevice();
        var note = await SeedNoteAsync(device, "before");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.BeforeCycle = () => hold.Task;
        var first = device.Scheduler.TriggerNow(bypassThrottle: true)!;

        await device.Notes.UpdateAsync(note with { Content = "during flight" });
        device.PushTimer.Fire();
        Assert.Same(first, device.LastPush);

        var followUp = new TaskCompletionSource<SyncCycleResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Scheduler.CycleCompleted += result =>
        {
            if (device.CycleCount == 2)
                followUp.TrySetResult(result);
        };
        device.BeforeCycle = null;
        hold.SetResult();
        await first;
        var result = await followUp.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result!.Ok);
        Assert.Equal(2, device.CycleCount);
        Assert.Equal("during flight", Decode(device.Cloud, note.Uuid).Content);
        Assert.Equal(0, device.Queue.PendingCount);
    }

    private static (string Title, string Category, string Content) Decode(FakeSyncDriver cloud, string uuid) =>
        SyncNoteBody.Decode(QnoteContainerCodec.Decode(cloud.Files[$"notes/{uuid}.qnote"]).Body);

    private static Task<Note> SeedNoteAsync(SyncDevice device, string content, string category = "") =>
        device.Db.NewRepository().CreateAsync(new Note
        {
            Uuid = Guid.NewGuid().ToString("N"), Title = "seed", Content = content,
            Category = category, CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1),
        });

    private static Task<Category> SeedCategoryAsync(SyncDevice device, string name) =>
        device.Db.NewRepository().ImportCategoryAsync(new Category
        {
            Uuid = Guid.NewGuid().ToString("N"), Name = name, Color = "#112233",
            CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1),
        });

    private sealed class SyncDevice : IDisposable
    {
        public TestDatabase Db { get; } = new();
        public FakeSyncDriver Cloud { get; }
        public ManualTimer PushTimer { get; } = new();
        public PushQueue Queue { get; }
        public SyncPollingScheduler Scheduler { get; }
        public SyncEngine Engine { get; }
        public NoteService Notes { get; }
        public CategoryService Categories { get; }
        public Task<SyncCycleResult?>? LastPush { get; private set; }
        public bool SignedIn { get; set; } = true;
        public Func<Task>? BeforeCycle { get; set; }
        public int CycleCount { get; private set; }

        public SyncDevice(FakeSyncDriver? cloud = null)
        {
            Cloud = cloud ?? new FakeSyncDriver();
            Queue = new PushQueue(PushTimer);
            Engine = new SyncEngine(Db.NewRepository(), new SyncStateStore(Db.Factory),
                Cloud, Db.Paths, NullLogger<SyncEngine>.Instance);
            Notes = new NoteService(Db.NewRepository(), Db.NewImageService(),
                NullLogger<NoteService>.Instance, Queue.Enqueue);
            Categories = new CategoryService(Db.NewRepository(),
                NullLogger<CategoryService>.Instance, Queue.Enqueue);
            Scheduler = new SyncPollingScheduler(async ct =>
            {
                CycleCount++;
                if (BeforeCycle is { } before)
                    await before();
                return SignedIn ? await Engine.RunAsync(ct: ct) : null;
            }, timer: new ManualTimer());
            Queue.SyncDue += _ => LastPush = Scheduler.TriggerNow(bypassThrottle: true);
            Scheduler.Start();
        }

        public async Task<SyncCycleResult?> FirePushAsync()
        {
            PushTimer.Fire();
            Assert.NotNull(LastPush);
            return await LastPush;
        }

        public void Dispose()
        {
            Scheduler.Dispose();
            Queue.Dispose();
            Db.Dispose();
        }
    }

    private sealed class ManualTimer : IOneShotTimer
    {
        public TimeSpan? Delay { get; private set; }
        public event Action? Elapsed;
        public void Arm(TimeSpan delay) => Delay = delay;
        public void Cancel() => Delay = null;
        public void Fire() => Elapsed?.Invoke();
        public void Dispose() { }
    }
}
