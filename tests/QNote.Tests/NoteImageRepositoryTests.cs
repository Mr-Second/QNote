using QNote.Markdown;
using QNote.Models;
using QNote.Services;

namespace QNote.Tests;

/// <summary>
/// <c>note_images</c> link table: upsert dedup, save-time sync, cascade on note
/// delete, and orphaned-original pruning across notes sharing a content address (D2).
/// </summary>
public sealed class NoteImageRepositoryTests
{
    private const string ShaA = "aa11bb22cc33dd44ee55ff6600778899aabbccddeeff00112233445566778899";
    private const string ShaB = "bb11bb22cc33dd44ee55ff6600778899aabbccddeeff00112233445566778899";

    [Fact]
    public async Task AddNoteImagesAsync_PersistsLink_AndDedupsRepeat()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var note = await repo.CreateAsync(NewNote());

        await repo.AddNoteImagesAsync(note.Id, [Image(note.Id, ShaA)]);
        await repo.AddNoteImagesAsync(note.Id, [Image(note.Id, ShaA)]); // duplicate ignored

        var links = await repo.GetNoteImagesAsync(note.Id);
        var link = Assert.Single(links);
        Assert.Equal(ShaA, link.Sha256);
        Assert.Equal(640, link.ByteSize);
        Assert.Equal(100, link.Width);
        Assert.Equal(50, link.Height);
    }

    [Fact]
    public async Task DisplayBytes_RoundTrips_AndMetadataQueryStaysBlobFree()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var note = await repo.CreateAsync(NewNote());
        await repo.AddNoteImagesAsync(note.Id,
            [Image(note.Id, ShaA) with { DisplayBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D] }]);

        // Blob-free query (sync paths) must not materialize the display copy.
        Assert.Null(Assert.Single(await repo.GetNoteImagesAsync(note.Id)).DisplayBytes);

        // Editor-load query carries it.
        var withDisplay = Assert.Single(await repo.GetNoteImagesWithDisplayAsync(note.Id));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D], withDisplay.DisplayBytes);
    }

    [Fact]
    public async Task AddNoteImagesAsync_ReUpsert_BackfillsBlobOntoLegacyRow()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var note = await repo.CreateAsync(NewNote());
        await repo.AddNoteImagesAsync(note.Id, [Image(note.Id, ShaA)]); // legacy, no blob

        await repo.AddNoteImagesAsync(note.Id,
            [Image(note.Id, ShaA) with { DisplayBytes = [0xFF, 0xD8] }]);

        var row = Assert.Single(await repo.GetNoteImagesWithDisplayAsync(note.Id));
        Assert.Equal([0xFF, 0xD8], row.DisplayBytes);
    }

    [Fact]
    public async Task AddNoteImagesAsync_ReUpsert_NullBlob_DoesNotClobberExistingBytes()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var note = await repo.CreateAsync(NewNote());
        await repo.AddNoteImagesAsync(note.Id,
            [Image(note.Id, ShaA) with { DisplayBytes = [0xFF, 0xD8] }]);

        // A later metadata-only upsert (e.g. adoption copy without a copy) must
        // not wipe the display bytes the row already has.
        await repo.AddNoteImagesAsync(note.Id, [Image(note.Id, ShaA)]);

        var row = Assert.Single(await repo.GetNoteImagesWithDisplayAsync(note.Id));
        Assert.Equal([0xFF, 0xD8], row.DisplayBytes);
    }

    [Fact]
    public async Task SyncNoteImagesAsync_RemovesUnreferencedRows_AndReportsOrphans()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var note = await repo.CreateAsync(NewNote());
        await repo.AddNoteImagesAsync(note.Id, [Image(note.Id, ShaA), Image(note.Id, ShaB)]);

        var orphans = await repo.SyncNoteImagesAsync(note.Id, [ShaA]);

        Assert.Equal(new[] { ShaB }, orphans.ToArray());
        var links = await repo.GetNoteImagesAsync(note.Id);
        Assert.Equal(new[] { ShaA }, links.Select(l => l.Sha256).ToArray());
    }

    [Fact]
    public async Task SyncNoteImagesAsync_DoesNotOrphan_WhenAnotherNoteStillReferencesIt()
    {
        using var db = new TestDatabase();
        var repo = db.NewRepository();
        var first = await repo.CreateAsync(NewNote());
        var second = await repo.CreateAsync(NewNote());
        await repo.AddNoteImagesAsync(first.Id, [Image(first.Id, ShaA)]);
        await repo.AddNoteImagesAsync(second.Id, [Image(second.Id, ShaA)]);

        var orphans = await repo.SyncNoteImagesAsync(first.Id, []);

        Assert.Empty(orphans); // second note still holds the same content address
        Assert.Empty(await repo.GetNoteImagesAsync(first.Id));
        Assert.Single(await repo.GetNoteImagesAsync(second.Id));
    }

    [Fact]
    public async Task DeleteAsync_CascadesNoteImages_AndPrunesOrphanedFile()
    {
        using var db = new TestDatabase();
        var noteService = db.NewNoteService();
        var note = await noteService.CreateAsync();

        var imported = await db.NewImageService()
            .ImportAsync([1, 2, 3], "png", [9], "pngblip", 10, 10);
        await noteService.AddNoteImagesAsync(note.Id, [new NoteImage
        {
            NoteId = note.Id,
            Sha256 = imported.Sha256,
            Ext = "png",
            ByteSize = imported.ByteSize,
            Width = 10,
            Height = 10,
            CreatedAt = DateTimeOffset.UtcNow,
        }]);

        await noteService.DeleteAsync(note.Id);

        Assert.Empty(await db.NewRepository().GetNoteImagesAsync(note.Id));
        Assert.False(File.Exists(imported.OriginalPath), "orphaned original should be pruned");
    }

    [Fact]
    public async Task DeleteAsync_KeepsOriginal_WhenSharedWithSurvivingNote()
    {
        using var db = new TestDatabase();
        var noteService = db.NewNoteService();
        var imageService = db.NewImageService();
        var doomed = await noteService.CreateAsync();
        var surviving = await noteService.CreateAsync();

        var imported = await imageService.ImportAsync([4, 5, 6], "png", [9], "pngblip", 8, 8);
        var image = new NoteImage
        {
            NoteId = doomed.Id,
            Sha256 = imported.Sha256,
            Ext = "png",
            ByteSize = imported.ByteSize,
            Width = 8,
            Height = 8,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await noteService.AddNoteImagesAsync(doomed.Id, [image]);
        await noteService.AddNoteImagesAsync(surviving.Id, [image with { NoteId = surviving.Id }]);

        await noteService.DeleteAsync(doomed.Id);

        Assert.True(File.Exists(imported.OriginalPath), "shared original must survive");
        Assert.Single(await db.NewRepository().GetNoteImagesAsync(surviving.Id));
    }

    [Fact]
    public async Task SyncNoteImagesAsync_AdoptsPastedImage_FromAnotherNotesMetadata()
    {
        using var db = new TestDatabase();
        var noteService = db.NewNoteService();
        var source = await noteService.CreateAsync();
        var target = await noteService.CreateAsync();
        await noteService.AddNoteImagesAsync(source.Id, [Image(source.Id, ShaA)]);

        // Target note's Markdown now references the copied image.
        var md = $"![图]({MarkdownParser.ImageSchemePrefix}{ShaA})";
        await noteService.SyncNoteImagesAsync(target.Id, md);

        var adopted = Assert.Single(await db.NewRepository().GetNoteImagesAsync(target.Id));
        Assert.Equal(ShaA, adopted.Sha256);
        Assert.Equal(640, adopted.ByteSize); // metadata inherited from the source note
        Assert.Equal(100, adopted.Width);
    }

    [Fact]
    public async Task SyncNoteImagesAsync_Adoption_CarriesDisplayBytes()
    {
        using var db = new TestDatabase();
        var noteService = db.NewNoteService();
        var source = await noteService.CreateAsync();
        var target = await noteService.CreateAsync();
        await noteService.AddNoteImagesAsync(source.Id,
            [Image(source.Id, ShaA) with { DisplayBytes = [0x89, 0x50, 0x4E, 0x47] }]);

        var md = $"![]({MarkdownParser.ImageSchemePrefix}{ShaA})";
        await noteService.SyncNoteImagesAsync(target.Id, md);

        var adopted = Assert.Single(await db.NewRepository().GetNoteImagesWithDisplayAsync(target.Id));
        Assert.NotNull(adopted.DisplayBytes);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], adopted.DisplayBytes);
    }

    [Fact]
    public async Task SyncNoteImagesAsync_DoesNotAdopt_WhenNoMetadataAndNoFile()
    {
        using var db = new TestDatabase();
        var noteService = db.NewNoteService();
        var note = await noteService.CreateAsync();

        var md = $"![]({MarkdownParser.ImageSchemePrefix}{ShaA})";
        await noteService.SyncNoteImagesAsync(note.Id, md);

        Assert.Empty(await db.NewRepository().GetNoteImagesAsync(note.Id));
    }

    private static NoteImage Image(long noteId, string sha) => new()
    {
        NoteId = noteId,
        Sha256 = sha,
        Ext = "png",
        ByteSize = 640,
        Width = 100,
        Height = 50,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static Note NewNote() => new()
    {
        Uuid = Guid.NewGuid().ToString("N"),
        Title = "",
        Category = "",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
