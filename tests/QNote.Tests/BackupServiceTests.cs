using LibArchive.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using QNote.Data;
using QNote.Data.Schema;
using QNote.Infrastructure;
using QNote.Models;
using QNote.Services;

namespace QNote.Tests;

/// <summary>
/// Backup/restore over the <c>.qns</c> container (ZIP + AES-256, LibArchive.Net).
/// Each test gets a self-contained <see cref="BackupHost"/> (temp AppPaths + real DB +
/// real services) so overwrite restores can genuinely swap files under the service.
/// </summary>
public sealed class BackupServiceTests
{
    // ---------- Container shape ----------

    [Fact]
    public async Task Backup_WritesManifestDbAndImages()
    {
        using var host = new BackupHost();
        await host.CreateNoteAsync("uuid-a", "购物清单", "牛奶和鸡蛋");
        var sha = new string('a', 64);
        await File.WriteAllBytesAsync(Path.Combine(host.Paths.ImagesDir, $"{sha}.png"), [1, 2, 3]);

        var dest = host.TempFile();
        await host.Backup.BackupAsync(dest);

        Assert.True(File.Exists(dest));
        using var reader = new LibArchiveReader(dest);
        var names = reader.Entries().Select(e => e.Name.Replace('\\', '/')).ToList();
        Assert.Contains("manifest.json", names);
        Assert.Contains("qnote.db", names);
        Assert.Contains($"images/{sha}.png", names);
    }

    [Fact]
    public async Task Backup_IsEncrypted_OnlyWithPassword()
    {
        using var host = new BackupHost();
        await host.CreateNoteAsync("uuid-a", "t", "b");

        var plain = host.TempFile();
        await host.Backup.BackupAsync(plain);
        Assert.False(await host.Backup.IsEncryptedAsync(plain));

        var encrypted = host.TempFile();
        await host.Backup.BackupAsync(encrypted, password: "secret");
        Assert.True(await host.Backup.IsEncryptedAsync(encrypted));
    }

    [Fact]
    public async Task EncryptedBackup_RoundTrips_WithCorrectPassword()
    {
        using var host = new BackupHost();
        var note = await host.CreateNoteAsync("uuid-a", "秘密", "加密内容");

        var dest = host.TempFile();
        await host.Backup.BackupAsync(dest, password: "correct horse");

        await host.Repo.DeleteAsync(note.Id);
        Assert.Empty(await host.Repo.GetAllAsync());

        await host.Backup.RestoreAsync(dest, RestoreMode.Overwrite, password: "correct horse");
        var restored = Assert.Single(await host.Repo.GetAllAsync());
        Assert.Equal("uuid-a", restored.Uuid);
        Assert.Equal("秘密", restored.Title);
    }

    // ---------- Error mapping ----------

    [Fact]
    public async Task Analyze_Encrypted_WithoutPassword_RequiresPassword()
    {
        using var host = new BackupHost();
        await host.CreateNoteAsync("uuid-a", "t", "b");
        var dest = host.TempFile();
        await host.Backup.BackupAsync(dest, password: "pw");

        var ex = await Assert.ThrowsAsync<BackupException>(() => host.Backup.AnalyzeAsync(dest));
        Assert.Equal(BackupErrorKind.PasswordRequired, ex.Kind);
    }

    [Fact]
    public async Task Analyze_Encrypted_WrongPassword_ThrowsWrongPassword()
    {
        using var host = new BackupHost();
        await host.CreateNoteAsync("uuid-a", "t", "b");
        var dest = host.TempFile();
        await host.Backup.BackupAsync(dest, password: "right");

        var ex = await Assert.ThrowsAsync<BackupException>(() => host.Backup.AnalyzeAsync(dest, password: "wrong"));
        Assert.Equal(BackupErrorKind.WrongPassword, ex.Kind);
    }

    [Fact]
    public async Task Analyze_MissingFile_ThrowsArchiveNotFound()
    {
        using var host = new BackupHost();
        var ex = await Assert.ThrowsAsync<BackupException>(
            () => host.Backup.AnalyzeAsync(Path.Combine(host.Root, "no-such-file.qns")));
        Assert.Equal(BackupErrorKind.ArchiveNotFound, ex.Kind);
    }

    [Fact]
    public async Task Analyze_GarbageFile_ThrowsNotAnArchive()
    {
        using var host = new BackupHost();
        var garbage = host.TempFile();
        await File.WriteAllBytesAsync(garbage, "this is not an archive"u8.ToArray());

        await Assert.ThrowsAsync<BackupException>(() => host.Backup.AnalyzeAsync(garbage));
    }

    [Fact]
    public async Task Analyze_NewerSchemaVersion_IsRejected()
    {
        using var host = new BackupHost();
        await host.CreateNoteAsync("uuid-a", "t", "b");

        // Hand-build an archive whose manifest claims a schema newer than this build.
        var snapshot = host.TempFile();
        using (var conn = host.Factory.OpenRead())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "VACUUM INTO $path;";
            cmd.Parameters.AddWithValue("$path", snapshot);
            cmd.ExecuteNonQuery();
        }
        var tampered = host.TempFile();
        using (var writer = new LibArchiveWriter(tampered, ArchiveFormat.Zip))
        {
            writer.AddEntry("manifest.json",
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                    new { appVersion = "99.0", createdAt = "2030-01-01", encrypted = false, schemaVersion = 999 }));
            writer.AddFile(snapshot, "qnote.db");
        }

        var ex = await Assert.ThrowsAsync<BackupException>(() => host.Backup.AnalyzeAsync(tampered));
        Assert.Equal(BackupErrorKind.UnsupportedVersion, ex.Kind);
    }

    // ---------- Conflict analysis ----------

    [Fact]
    public async Task Analyze_CountsConflictNewAndCurrentOnly()
    {
        using var host = new BackupHost();
        var a = await host.CreateNoteAsync("uuid-a", "A", "a");
        await host.CreateNoteAsync("uuid-b", "B", "b");

        var dest = host.TempFile();
        await host.Backup.BackupAsync(dest);

        // After the backup: A leaves the current DB (→ backup-only), C is added (→ current-only).
        await host.Repo.DeleteAsync(a.Id);
        await host.CreateNoteAsync("uuid-c", "C", "c");

        var analysis = await host.Backup.AnalyzeAsync(dest);
        Assert.Equal(2, analysis.BackupNoteCount);
        Assert.Equal(1, analysis.ConflictCount);   // B
        Assert.Equal(1, analysis.NewCount);        // A
        Assert.Equal(1, analysis.CurrentOnlyCount);// C
        Assert.NotNull(analysis.BackupCreatedAt);
    }

    // ---------- Overwrite ----------

    [Fact]
    public async Task OverwriteRestore_RestoresNotesSettingsImages_AndRebuildsFts()
    {
        using var host = new BackupHost();
        var note = await host.CreateNoteAsync("uuid-a", "覆盖测试", "原始内容");
        await host.Settings.SetAsync("themeMode", "dark");
        var sha = new string('b', 64);
        var imagePath = Path.Combine(host.Paths.ImagesDir, $"{sha}.png");
        await File.WriteAllBytesAsync(imagePath, [9, 9, 9]);

        var dest = host.TempFile();
        await host.Backup.BackupAsync(dest);

        // Mutate everything the backup captured.
        await host.Repo.DeleteAsync(note.Id);
        await host.CreateNoteAsync("uuid-x", "干扰", "backup 之后新增");
        await host.Settings.SetAsync("themeMode", "light");
        File.Delete(imagePath);

        await host.Backup.RestoreAsync(dest, RestoreMode.Overwrite);

        var notes = await host.Repo.GetAllAsync();
        var restored = Assert.Single(notes);
        Assert.Equal("uuid-a", restored.Uuid);
        Assert.Equal("原始内容", restored.Content);
        Assert.Equal("dark", await host.Settings.GetAsync("themeMode"));
        Assert.True(File.Exists(imagePath));

        // D3: search works in the SAME session, without a restart.
        var hits = await host.Search.SearchAsync("覆盖测试");
        Assert.Contains(hits, h => h.Uuid == "uuid-a");
    }

    [Fact]
    public async Task OverwriteRestore_WritesAutoBackup_AndPrunesToRetention()
    {
        using var host = new BackupHost();
        await host.CreateNoteAsync("uuid-a", "A", "a");
        var dest = host.TempFile();
        await host.Backup.BackupAsync(dest);

        for (var i = 0; i < 7; i++)
        {
            await host.Backup.RestoreAsync(dest, RestoreMode.Overwrite);
            // Timestamps name the auto-backups to the second — force distinct names.
            await Task.Delay(1100);
        }

        var autoBackups = Directory.GetFiles(host.Paths.BackupsDir, $"auto-backup-*{IBackupService.ArchiveExtension}");
        Assert.Equal(5, autoBackups.Length);
    }

    // ---------- Merge / import-new ----------

    [Fact]
    public async Task MergeRestore_KeepsNewerVersion_PerDirection()
    {
        using var host = new BackupHost();
        var note = await host.CreateNoteAsync("uuid-a", "标题", "旧内容");

        var dest = host.TempFile();
        await host.Backup.BackupAsync(dest);

        // Current becomes NEWER than the backup → merge must keep the current content.
        await host.Repo.UpdateAsync(note with
        {
            Content = "新内容",
            PlainText = "新内容",
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        await host.Backup.RestoreAsync(dest, RestoreMode.Merge);
        Assert.Equal("新内容", (await host.Repo.GetByIdAsync(note.Id))!.Content);

        // Current becomes OLDER than the backup → merge takes the backup's version.
        await host.Repo.UpdateAsync(note with
        {
            Content = "过期内容",
            PlainText = "过期内容",
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        });
        await host.Backup.RestoreAsync(dest, RestoreMode.Merge);
        Assert.Equal("旧内容", (await host.Repo.GetByIdAsync(note.Id))!.Content);
    }

    [Fact]
    public async Task MergeRestore_ImportsNewNotesWithImagesAndCategories()
    {
        using var source = new BackupHost();
        await source.Repo.CreateCategoryAsync(new Category { Name = "旅行", IconKey = "E709", Color = "#22C55E" });
        var note = await source.CreateNoteAsync("uuid-trip", "行程", "带上护照", category: "旅行");
        var sha = new string('c', 64);
        await File.WriteAllBytesAsync(Path.Combine(source.Paths.ImagesDir, $"{sha}.jpg"), [7, 7, 7]);
        await source.Repo.AddNoteImagesAsync(note.Id,
        [
            new NoteImage
            {
                NoteId = note.Id,
                Sha256 = sha,
                Ext = "jpg",
                ByteSize = 3,
                Width = 10,
                Height = 10,
                CreatedAt = DateTimeOffset.UtcNow,
            },
        ]);

        var dest = source.TempFile();
        await source.Backup.BackupAsync(dest);

        using var target = new BackupHost(); // fresh data root (built-in categories only)
        await target.Backup.RestoreAsync(dest, RestoreMode.Merge);

        var imported = Assert.Single(await target.Repo.GetAllAsync());
        Assert.Equal("uuid-trip", imported.Uuid);
        Assert.Equal("旅行", imported.Category);

        var categories = await target.Repo.GetCategoriesAsync();
        Assert.Contains(categories, c => c.Name == "旅行" && c.IconKey == "E709" && c.Color == "#22C55E");

        var imageRows = await target.Repo.GetNoteImagesAsync(imported.Id);
        var row = Assert.Single(imageRows);
        Assert.Equal(sha, row.Sha256);
        Assert.True(File.Exists(Path.Combine(target.Paths.ImagesDir, $"{sha}.jpg")));

        // D3: the imported note is searchable immediately.
        var hits = await target.Search.SearchAsync("护照");
        Assert.Contains(hits, h => h.Uuid == "uuid-trip");
    }

    [Fact]
    public async Task ImportNewOnly_SkipsConflicts_ButImportsMissing()
    {
        using var host = new BackupHost();
        var note = await host.CreateNoteAsync("uuid-a", "标题", "备份时的内容");

        var dest = host.TempFile();
        await host.Backup.BackupAsync(dest);

        // The current DB has a NEWER version of the same uuid — import-new must not touch it.
        await host.Repo.UpdateAsync(note with
        {
            Content = "当前的新内容",
            PlainText = "当前的新内容",
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        await host.Backup.RestoreAsync(dest, RestoreMode.ImportNewOnly);
        Assert.Equal("当前的新内容", (await host.Repo.GetByIdAsync(note.Id))!.Content);

        // Once the note is gone locally, import-new brings the backup's copy back.
        await host.Repo.DeleteAsync(note.Id);
        await host.Backup.RestoreAsync(dest, RestoreMode.ImportNewOnly);
        var resurrected = Assert.Single(await host.Repo.GetAllAsync());
        Assert.Equal("uuid-a", resurrected.Uuid);
        Assert.Equal("备份时的内容", resurrected.Content);
    }

    // ---------- Test host ----------

    private sealed class BackupHost : IDisposable
    {
        public BackupHost()
        {
            Root = Path.Combine(Path.GetTempPath(), $"qnote-backup-test-{Guid.NewGuid():N}");
            Paths = new AppPaths(Root);
            Paths.EnsureCreated();
            Factory = new DbConnectionFactory(Paths.DatabasePath);
            Schema = new SchemaInitializer(Factory);
            Schema.EnsureCreated();
            Repo = new NoteRepository(Factory);
            Search = new SearchService(Factory, Repo, NullLogger<SearchService>.Instance);
            Settings = new SettingsService(Factory, NullLogger<SettingsService>.Instance);
            Images = new ImageService(Paths, NullLogger<ImageService>.Instance);
            Backup = new BackupService(Paths, Factory, Repo, Search, Settings, Images, Schema,
                NullLogger<BackupService>.Instance);
        }

        public string Root { get; }
        public AppPaths Paths { get; }
        public DbConnectionFactory Factory { get; }
        public SchemaInitializer Schema { get; }
        public NoteRepository Repo { get; }
        public SearchService Search { get; }
        public SettingsService Settings { get; }
        public ImageService Images { get; }
        public BackupService Backup { get; }

        public string TempFile() => Path.Combine(Root, $"test-{Guid.NewGuid():N}{IBackupService.ArchiveExtension}");

        public Task<Note> CreateNoteAsync(string uuid, string title, string text, string category = "")
        {
            var now = DateTimeOffset.UtcNow;
            return Repo.CreateAsync(new Note
            {
                Uuid = uuid,
                Title = title,
                Content = text,
                PlainText = text,
                Category = category,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A lingering handle must not fail unrelated tests; temp dirs are GC'd by the OS.
            }
        }
    }
}
