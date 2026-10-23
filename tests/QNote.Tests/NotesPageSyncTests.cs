using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QNote.Models;
using QNote.Services;
using QNote.ViewModels;

namespace QNote.Tests;

public sealed class NotesPageSyncTests
{
    [Fact]
    public async Task CleanRefresh_UpdatesOpenNoteAndThenReflectsRemoteDeletion()
    {
        using var h = new Harness();
        var original = await h.AddAsync("local", "body");
        await h.LoadAsync(original.Id);
        await h.Db.NewRepository().ImportNoteAsync(original with { Title = "cloud", Content = "remote body" });

        await h.Vm.RefreshAfterSyncAsync();

        Assert.Equal("cloud", h.Vm.EditingTitle);
        Assert.Equal("remote body", h.EditorMarkdown);
        Assert.Equal("cloud", Assert.Single(h.Vm.Notes).Title);
        await h.Db.NewRepository().DeleteAsync(original.Id);
        await h.Vm.RefreshAfterSyncAsync();
        Assert.Empty(h.Vm.Notes);
        Assert.Null(h.Vm.LoadedNoteId);
        Assert.Equal("", h.EditorMarkdown);
    }

    [Fact]
    public async Task DirtyRefresh_DefersUntilSave_ThenSelectsPreservedCopy()
    {
        using var h = new Harness();
        var original = await h.AddAsync("local", "body");
        await h.LoadAsync(original.Id);
        h.Edit("draft", "unsaved body");
        await h.Db.NewRepository().ImportNoteAsync(original with { Title = "cloud", Content = "remote body" });

        await h.Vm.RefreshAfterSyncAsync();
        Assert.True(h.Vm.IsDirty);
        Assert.Equal("draft", h.Vm.EditingTitle);
        Assert.Equal("unsaved body", h.EditorMarkdown);
        await h.Vm.FlushAsync();

        Assert.False(h.Vm.IsDirty);
        Assert.NotEqual(original.Id, h.Vm.LoadedNoteId);
        Assert.Equal(h.Vm.LoadedNoteId, h.Vm.SelectedNote!.Id);
        Assert.Equal(2, h.Vm.Notes.Count);
        Assert.Equal("remote body", (await h.Db.NewRepository().GetByIdAsync(original.Id))!.Content);
        Assert.Equal("unsaved body", (await h.Db.NewRepository().GetByIdAsync(h.Vm.LoadedNoteId!.Value))!.Content);
    }

    [Fact]
    public async Task TypingDuringSnapshot_DoesNotSaveMixedVersionsOrClearDirty()
    {
        using var h = new Harness();
        var original = await h.AddAsync("local", "body");
        await h.LoadAsync(original.Id);
        h.Edit("first", "first body");
        var snapshot = new TaskCompletionSource<NotesPageViewModel.EditorSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Vm.EditorContentProvider = () => snapshot.Task;
        var flush = h.Vm.FlushAsync();
        h.Edit("newer", "newer body");
        snapshot.SetResult(new("first body", true));
        await flush;

        Assert.True(h.Vm.IsDirty);
        Assert.Equal("newer", h.Vm.EditingTitle);
        Assert.Equal("body", (await h.Db.NewRepository().GetByIdAsync(original.Id))!.Content);
        h.UseEditorProvider();
        await h.Vm.FlushAsync();
        Assert.Equal("newer body", (await h.Db.NewRepository().GetByIdAsync(original.Id))!.Content);
    }

    [Fact]
    public async Task TypingDuringSave_KeepsNewerDraftAndAdvancesItsBaseline()
    {
        using var h = new Harness();
        var original = await h.AddAsync("local", "body");
        await h.LoadAsync(original.Id);
        h.Edit("first", "first body");
        var gate = new Gate();
        h.Notes.AfterSave = gate.WaitAsync;
        var flush = h.Vm.FlushAsync();
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Edit("newer", "newer body");
        gate.Release.SetResult();
        await flush;

        Assert.True(h.Vm.IsDirty);
        Assert.Equal("newer", h.Vm.EditingTitle);
        Assert.Equal("first body", (await h.Db.NewRepository().GetByIdAsync(original.Id))!.Content);
        h.Notes.AfterSave = null;
        await h.Vm.FlushAsync();
        Assert.False(h.Vm.IsDirty);
        Assert.Equal("newer body", (await h.Db.NewRepository().GetByIdAsync(original.Id))!.Content);
        Assert.Single(await h.Db.NewRepository().GetAllAsync());
    }

    [Fact]
    public async Task TypingDuringRefreshRead_RejectsStaleUiApplication()
    {
        using var h = new Harness();
        var original = await h.AddAsync("local", "body");
        await h.LoadAsync(original.Id);
        await h.Db.NewRepository().ImportNoteAsync(original with { Title = "cloud", Content = "remote body" });
        var gate = new Gate();
        h.Notes.BeforeRead = _ => gate.WaitAsync();
        var refresh = h.Vm.RefreshAfterSyncAsync();
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Edit("typing", "draft body");
        gate.Release.SetResult();
        await refresh;

        Assert.True(h.Vm.IsDirty);
        Assert.Equal("typing", h.Vm.EditingTitle);
        Assert.Equal("draft body", h.EditorMarkdown);
        h.Notes.BeforeRead = null;
        await h.Vm.FlushAsync();
        Assert.Equal(2, h.Vm.Notes.Count);
        Assert.Equal("remote body", (await h.Db.NewRepository().GetByIdAsync(original.Id))!.Content);
    }

    [Fact]
    public async Task ConcurrentFlushes_CreateOnlyOneCopyForTheSameDirtyDraft()
    {
        using var h = new Harness();
        var original = await h.AddAsync("local", "body");
        await h.LoadAsync(original.Id);
        h.Edit("draft", "draft body");
        await h.Db.NewRepository().ImportNoteAsync(original with { Content = "remote body" });
        var gate = new Gate();
        h.Notes.AfterSave = gate.WaitAsync;
        var first = h.Vm.FlushAsync();
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = h.Vm.FlushAsync();
        gate.Release.SetResult();
        await Task.WhenAll(first, second);

        var notes = await h.Db.NewRepository().GetAllAsync();
        Assert.Equal(2, notes.Count);
        Assert.Single(notes, n => n.Content == "draft body");
        Assert.Single(notes, n => n.Content == "remote body");
        Assert.False(h.Vm.IsDirty);
    }

    [Fact]
    public async Task SelectionDuringRead_LateOldLoadCannotReplaceNewEditor()
    {
        using var h = new Harness();
        var first = await h.AddAsync("first", "first body");
        var second = await h.AddAsync("second", "second body");
        await h.Vm.LoadAsync();
        var gate = new Gate();
        h.Notes.BeforeRead = id => id == first.Id ? gate.WaitAsync() : Task.CompletedTask;
        h.Vm.SelectedNote = h.Vm.Notes.Single(n => n.Id == first.Id);
        var oldLoad = h.Vm.OnSelectionChangedAsync();
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Vm.SelectedNote = h.Vm.Notes.Single(n => n.Id == second.Id);
        await h.Vm.OnSelectionChangedAsync();
        gate.Release.SetResult();
        await oldLoad;

        Assert.Equal(second.Id, h.Vm.LoadedNoteId);
        Assert.Equal("second body", h.EditorMarkdown);
    }

    [Fact]
    public async Task Refresh_RespectsCategorySearchAndDetach()
    {
        using var h = new Harness();
        var category = await h.Db.NewCategoryService().CreateAsync("scope", "#112233", "");
        var match = await h.AddAsync("同步", "同步内容", category.Name);
        await h.AddAsync("同步", "其他分类", "");
        await h.LoadAsync(match.Id);
        h.Vm.SelectedCategory = h.Vm.Categories.Single(c => c.Id == category.Id);
        h.Vm.SearchText = "同步";
        await h.Vm.RefreshAfterSyncAsync();
        Assert.Equal(match.Id, Assert.Single(h.Vm.Notes).Id);
        var gate = new Gate();
        h.Notes.BeforeRead = _ => gate.WaitAsync();
        var refresh = h.Vm.RefreshAfterSyncAsync();
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Vm.Detach();
        gate.Release.SetResult();
        await refresh;
        Assert.Equal(match.Id, h.Vm.LoadedNoteId);
    }

    private sealed class Harness : IDisposable
    {
        public TestDatabase Db { get; } = new();
        public PausedNotes Notes { get; }
        public NotesPageViewModel Vm { get; }
        public string EditorMarkdown { get; private set; } = "";
        public Harness()
        {
            Notes = new PausedNotes(Db.NewNoteService());
            Vm = new NotesPageViewModel(Notes, Db.NewSearchService(), Db.NewCategoryService(),
                Db.NewSettingsService(), NullLogger<NotesPageViewModel>.Instance);
            Vm.ContentReloadRequested += () => EditorMarkdown = Vm.EditingContent;
            UseEditorProvider();
        }
        public void UseEditorProvider() => Vm.EditorContentProvider = () =>
            Task.FromResult(new NotesPageViewModel.EditorSnapshot(EditorMarkdown, true));
        public async Task<Note> AddAsync(string title, string content, string category = "")
        {
            var note = await Db.NewNoteService().CreateAsync(category);
            return await Db.NewNoteService().UpdateAsync(note with { Title = title, Content = content });
        }
        public async Task LoadAsync(long id)
        {
            await Vm.LoadAsync();
            Vm.SelectedNote = Vm.Notes.Single(n => n.Id == id);
            await Vm.OnSelectionChangedAsync();
        }
        public void Edit(string title, string content)
        {
            Vm.EditingTitle = title;
            EditorMarkdown = content;
            Vm.NotifyContentEdited();
        }
        public void Dispose() { Vm.Detach(); Db.Dispose(); }
    }

    private sealed class Gate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitAsync() { Started.TrySetResult(); return Release.Task; }
    }

    private sealed class PausedNotes(INoteService inner) : INoteService
    {
        public Func<long, Task>? BeforeRead { get; set; }
        public Func<Task>? AfterSave { get; set; }
        public Task<IReadOnlyList<Note>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);
        public Task<IReadOnlyList<NoteSummary>> GetSummariesAsync(string? category = null, CancellationToken ct = default) => inner.GetSummariesAsync(category, ct);
        public async Task<Note?> GetByIdAsync(long id, CancellationToken ct = default)
        { if (BeforeRead is { } before) await before(id); return await inner.GetByIdAsync(id, ct); }
        public Task<Note> CreateAsync(string category = "", CancellationToken ct = default) => inner.CreateAsync(category, ct);
        public Task<Note> UpdateAsync(Note note, CancellationToken ct = default) => inner.UpdateAsync(note, ct);
        public async Task<Note> SaveDraftAsync(Note draft, Note baseline, CancellationToken ct = default)
        { var result = await inner.SaveDraftAsync(draft, baseline, ct); if (AfterSave is { } after) await after(); return result; }
        public Task DeleteAsync(long id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);
        public Task AddNoteImagesAsync(long id, IReadOnlyList<NoteImage> images, CancellationToken ct = default) => inner.AddNoteImagesAsync(id, images, ct);
        public Task SyncNoteImagesAsync(long id, string? markdown, CancellationToken ct = default) => inner.SyncNoteImagesAsync(id, markdown, ct);
        public Task<IReadOnlyList<NoteImage>> GetNoteImagesWithDisplayAsync(long id, CancellationToken ct = default) => inner.GetNoteImagesWithDisplayAsync(id, ct);
        public Task<IReadOnlyDictionary<string, NoteImage>> GetImageMetadataByShaAsync(IReadOnlyList<string> sha, CancellationToken ct = default) => inner.GetImageMetadataByShaAsync(sha, ct);
        public Task<IReadOnlyDictionary<string, string>> FindOriginalShaByDisplayHashAsync(IReadOnlyList<string> sha, CancellationToken ct = default) => inner.FindOriginalShaByDisplayHashAsync(sha, ct);
    }
}
