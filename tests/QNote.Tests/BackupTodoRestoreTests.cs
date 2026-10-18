using LibArchive.Net;
using QNote.Data.Schema;
using QNote.Models;
using QNote.Services;

namespace QNote.Tests;

/// <summary>
/// Todo-domain coverage of the <c>.qns</c> backup/restore pipeline (task 10-04
/// stage ④, PRD Requirement 8): round-trips per restore mode, uuid→UpdatedAt LWW
/// conflict matrix on BOTH todo tables (older / newer / same; todo timestamps are
/// epoch-ms integers in storage, unlike notes' TEXT ISO — comparisons happen as
/// UTC instants), cross-version archives (v6 → graceful empty todo domain),
/// cascade integrity, the catch-up reminder marker, and the post-restore
/// reminder reconcile. Shares <see cref="BackupServiceTests.BackupHost"/>.
/// </summary>
public sealed class BackupTodoRestoreTests
{
    // ---------- Overwrite: wholesale swap, todo rides inside qnote.db ----------

    [Fact]
    public async Task OverwriteRestore_RestoresTodoListsTasksAndCompletionState()
    {
        using var source = new BackupHost();
        // Distinctive data: a custom list, a completed task, a due+reminder task,
        // and the seeded 生活 list deleted — proving the swap is wholesale.
        await source.CreateTodoListAsync("list-shop", "购物", "#EF4444");
        var life = (await source.TodoRepo.GetListsAsync()).Single(l => l.Name == "生活");
        await source.TodoRepo.DeleteListAsync(life.Id);
        await source.CreateTodoTaskAsync("task-done", "list-shop", "已完成任务",
            completedAt: DateTimeOffset.UtcNow.AddHours(-1));
        await source.CreateTodoTaskAsync("task-due", "list-shop", "带提醒任务",
            dueAt: DateTimeOffset.UtcNow.AddHours(2), reminderOn: true, isImportant: true);
        await source.CreateNoteAsync("uuid-note", "笔记", "内容");

        var dest = source.TempFile();
        await source.Backup.BackupAsync(dest);

        using var target = new BackupHost();
        await target.CreateTodoListAsync("list-target-only", "干扰清单");
        await target.CreateTodoTaskAsync("task-target-only", "list-target-only", "干扰任务");

        await target.Backup.RestoreAsync(dest, RestoreMode.Overwrite);

        // Lists: exactly the source's set — the interference list is gone, the
        // deleted 生活 stays deleted (wholesale, not merge).
        var lists = await target.TodoRepo.GetListsAsync();
        Assert.Equal(2, lists.Count);
        Assert.Contains(lists, l => l.Uuid == "list-shop" && l.Name == "购物" && l.Color == "#EF4444");
        Assert.Contains(lists, l => l.Name == "工作");
        Assert.DoesNotContain(lists, l => l.Name == "生活");

        // Tasks: completion state, due/reminder/important flags all intact.
        var done = await target.TodoRepo.GetTaskByUuidAsync("task-done");
        Assert.NotNull(done);
        Assert.NotNull(done!.CompletedAt);
        Assert.Equal("已完成任务", done.Title);

        var due = await target.TodoRepo.GetTaskByUuidAsync("task-due");
        Assert.NotNull(due);
        Assert.Null(due!.CompletedAt);
        Assert.True(due.ReminderOn);
        Assert.True(due.IsImportant);
        Assert.NotNull(due.DueAt);

        Assert.Null(await target.TodoRepo.GetTaskByUuidAsync("task-target-only"));

        var all = await target.TodoRepo.GetAllAsync(includeCompleted: true);
        Assert.Equal(2, all.Count);

        // The notes domain still restores alongside (whole-set swap).
        var note = Assert.Single(await target.Repo.GetAllAsync());
        Assert.Equal("uuid-note", note.Uuid);
    }

    // ---------- Merge: per-uuid LWW import ----------

    [Fact]
    public async Task MergeRestore_ImportsTodoListsAndTasks()
    {
        using var source = new BackupHost();
        await source.CreateTodoListAsync("list-shop", "购物", "#EF4444");
        await source.CreateTodoTaskAsync("task-a", "list-shop", "牛奶",
            dueAt: DateTimeOffset.UtcNow.AddDays(1));

        var dest = source.TempFile();
        await source.Backup.BackupAsync(dest);

        using var target = new BackupHost(); // fresh data root (seeded lists only)
        await target.Backup.RestoreAsync(dest, RestoreMode.Merge);

        var lists = await target.TodoRepo.GetListsAsync();
        var imported = lists.Single(l => l.Uuid == "list-shop");
        Assert.Equal("购物", imported.Name);
        Assert.Equal("#EF4444", imported.Color);
        // Seeded lists stay untouched by the merge.
        Assert.Equal(2, lists.Count(l => l.Name is "工作" or "生活"));

        // Tasks link to the imported list by uuid and show up in the per-list view.
        var byList = await target.TodoRepo.GetByListAsync("list-shop", includeCompleted: true);
        var task = byList.Single();
        Assert.Equal("task-a", task.Uuid);
        Assert.Equal("牛奶", task.Title);
        Assert.NotNull(task.DueAt);
    }

    [Fact]
    public async Task MergeRestore_DoesNotDuplicateBuiltinLists()
    {
        // The built-in lists carry stable well-known identity across installs
        // (SchemaInitializer heal, stage ④): restoring a backup from ANOTHER
        // install merges them by uuid instead of importing duplicates. This is
        // the primary "restore my backup onto a fresh install" flow.
        using var source = new BackupHost();
        var dest = source.TempFile();
        await source.Backup.BackupAsync(dest);

        using var target = new BackupHost(); // fresh install: seeded lists, same identity
        await target.Backup.RestoreAsync(dest, RestoreMode.Merge);

        var lists = await target.TodoRepo.GetListsAsync();
        Assert.Equal(2, lists.Count);
        Assert.Equal(
            [SchemaInitializer.WorkListUuid, SchemaInitializer.LifeListUuid],
            lists.Select(l => l.Uuid).OrderBy(u => u, StringComparer.Ordinal));
    }

    [Fact]
    public async Task MergeRestore_TodoLWW_KeepsNewerPerDirection_BothTables()
    {
        using var host = new BackupHost();
        var t0 = DateTimeOffset.UtcNow.AddHours(-2);
        await host.CreateTodoListAsync("list-a", "备份的名字", updatedAt: t0, createdAt: t0);
        var task = await host.CreateTodoTaskAsync("task-a", "list-a", "备份标题",
            updatedAt: t0, createdAt: t0);

        var dest = host.TempFile();
        await host.Backup.BackupAsync(dest);

        // Direction 1 — current is NEWER (real mutation path stamps now > t0):
        // merge must keep the local rows.
        await host.TodoRepo.UpdateListAsync((await host.TodoRepo.GetListsAsync())
            .Single(l => l.Uuid == "list-a") with { Name = "本地新名字" });
        await host.TodoRepo.UpdateTaskAsync(task.Id, "本地新标题", "本地备注");
        await host.Backup.RestoreAsync(dest, RestoreMode.Merge);
        Assert.Equal("本地新名字", (await host.TodoRepo.GetListsAsync()).Single(l => l.Uuid == "list-a").Name);
        Assert.Equal("本地新标题", (await host.TodoRepo.GetTaskByUuidAsync("task-a"))!.Title);

        // Direction 2 — current is OLDER (fixture surgery moves it below t0):
        // merge takes the archive's whole rows, restoring its UpdatedAt too.
        await host.BumpUpdatedAtAsync("todo_lists", "list-a", t0.AddHours(-1));
        await host.BumpUpdatedAtAsync("todo_tasks", "task-a", t0.AddHours(-1));
        await host.Backup.RestoreAsync(dest, RestoreMode.Merge);
        var archiveList = (await host.TodoRepo.GetListsAsync()).Single(l => l.Uuid == "list-a");
        Assert.Equal("备份的名字", archiveList.Name);
        Assert.Equal(t0.ToUnixTimeMilliseconds(), archiveList.UpdatedAt.ToUnixTimeMilliseconds());
        var archiveTask = (await host.TodoRepo.GetTaskByUuidAsync("task-a"))!;
        Assert.Equal("备份标题", archiveTask.Title);
        Assert.Empty(archiveTask.Notes);
        Assert.Equal(t0.ToUnixTimeMilliseconds(), archiveTask.UpdatedAt.ToUnixTimeMilliseconds());

        // Direction 3 — SAME timestamps: strictly-newer wins, so the local rows stay.
        await host.TodoRepo.UpdateListAsync(archiveList with { Name = "相同时间戳的本地名字" });
        await host.TodoRepo.UpdateTaskAsync(archiveTask.Id, "相同时间戳的本地标题", "");
        await host.BumpUpdatedAtAsync("todo_lists", "list-a", t0);
        await host.BumpUpdatedAtAsync("todo_tasks", "task-a", t0);
        await host.Backup.RestoreAsync(dest, RestoreMode.Merge);
        Assert.Equal("相同时间戳的本地名字", (await host.TodoRepo.GetListsAsync()).Single(l => l.Uuid == "list-a").Name);
        Assert.Equal("相同时间戳的本地标题", (await host.TodoRepo.GetTaskByUuidAsync("task-a"))!.Title);
    }

    [Fact]
    public async Task ImportNewOnly_SkipsTodoConflicts_ButImportsMissing()
    {
        using var source = new BackupHost();
        var tOld = DateTimeOffset.UtcNow.AddHours(-2);
        await source.CreateTodoListAsync("list-a", "较旧的清单", updatedAt: tOld);
        await source.CreateTodoListAsync("list-b", "缺失的清单");
        await source.CreateTodoTaskAsync("task-a", "list-a", "较旧的任务", updatedAt: tOld);
        await source.CreateTodoTaskAsync("task-b", "list-b", "缺失的任务");

        var dest = source.TempFile();
        await source.Backup.BackupAsync(dest);

        using var target = new BackupHost();
        await target.CreateTodoListAsync("list-a", "本地较新的清单");
        await target.CreateTodoTaskAsync("task-a", "list-a", "本地较新的任务");

        await target.Backup.RestoreAsync(dest, RestoreMode.ImportNewOnly);

        // Conflicts skipped regardless of timestamps — local rows untouched.
        Assert.Equal("本地较新的清单",
            (await target.TodoRepo.GetListsAsync()).Single(l => l.Uuid == "list-a").Name);
        Assert.Equal("本地较新的任务", (await target.TodoRepo.GetTaskByUuidAsync("task-a"))!.Title);

        // Absent uuids imported.
        Assert.Equal("缺失的清单",
            (await target.TodoRepo.GetListsAsync()).Single(l => l.Uuid == "list-b").Name);
        Assert.Equal("缺失的任务", (await target.TodoRepo.GetTaskByUuidAsync("task-b"))!.Title);
    }

    // ---------- Cross-version archives ----------

    [Fact]
    public async Task MergeRestore_V6Archive_LeavesTodoDomainUntouched()
    {
        using var source = new BackupHost();
        await source.CreateNoteAsync("uuid-note", "标题", "内容");
        // Fixture surgery: strip the todo tables and rewind the schema marker —
        // the staged DB now looks exactly like a pre-v7 (v6) archive.
        await source.DowngradeToV6Async();

        var dest = source.TempFile();
        await source.Backup.BackupAsync(dest);

        using var target = new BackupHost();
        await target.CreateTodoListAsync("list-local", "本地清单");
        await target.CreateTodoTaskAsync("task-local", "list-local", "本地任务");

        // Graceful: no exception, notes merge, the local todo domain is untouched
        // (the archive simply has no todo domain).
        await target.Backup.RestoreAsync(dest, RestoreMode.Merge);

        var note = Assert.Single(await target.Repo.GetAllAsync());
        Assert.Equal("uuid-note", note.Uuid);
        var list = (await target.TodoRepo.GetListsAsync()).Single(l => l.Uuid == "list-local");
        Assert.Equal("本地清单", list.Name);
        Assert.Equal("本地任务", (await target.TodoRepo.GetTaskByUuidAsync("task-local"))!.Title);
    }

    [Fact]
    public async Task OverwriteRestore_V6Archive_MigratesForwardIntoEmptyTodoDomain()
    {
        using var source = new BackupHost();
        await source.CreateNoteAsync("uuid-note", "标题", "内容");
        await source.DowngradeToV6Async();

        var dest = source.TempFile();
        await source.Backup.BackupAsync(dest);

        using var target = new BackupHost();
        await target.CreateTodoListAsync("list-local", "本地清单");
        await target.CreateTodoTaskAsync("task-local", "list-local", "本地任务");

        await target.Backup.RestoreAsync(dest, RestoreMode.Overwrite);

        // The post-swap migration brings the v6 file forward to the current
        // schema: todo tables exist again, seeded lists restored, no user tasks —
        // the pre-v7 local todo domain was swapped out with the file.
        Assert.Equal(SchemaInitializer.CurrentVersion, target.UserVersion());
        var lists = await target.TodoRepo.GetListsAsync();
        Assert.Equal(2, lists.Count);
        Assert.Equal(["工作", "生活"], lists.Select(l => l.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Empty(await target.TodoRepo.GetAllAsync(includeCompleted: true));

        var note = Assert.Single(await target.Repo.GetAllAsync());
        Assert.Equal("uuid-note", note.Uuid);
    }

    [Fact]
    public async Task Restore_NewerSchemaArchive_IsStillRejected()
    {
        using var host = new BackupHost();
        await host.CreateTodoListAsync("list-a", "清单A");

        // Hand-build an archive whose manifest claims a schema newer than this build.
        var snapshot = host.TempFile();
        await using (var conn = host.Factory.OpenRead())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "VACUUM INTO $path;";
            cmd.Parameters.AddWithValue("$path", snapshot);
            await cmd.ExecuteNonQueryAsync();
        }
        var tampered = host.TempFile();
        using (var writer = new LibArchiveWriter(tampered, ArchiveFormat.Zip))
        {
            writer.AddEntry("manifest.json",
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                    new { appVersion = "99.0", createdAt = "2030-01-01", encrypted = false, schemaVersion = 999 }));
            writer.AddFile(snapshot, "qnote.db");
        }

        var ex = await Assert.ThrowsAsync<BackupException>(
            () => host.Backup.RestoreAsync(tampered, RestoreMode.Overwrite));
        Assert.Equal(BackupErrorKind.UnsupportedVersion, ex.Kind);
        // The local todo data survived the rejected restore untouched.
        Assert.Equal("清单A", (await host.TodoRepo.GetListsAsync()).Single(l => l.Uuid == "list-a").Name);
    }

    // ---------- Reminder bookkeeping across the round trip ----------

    [Fact]
    public async Task MergeRestore_CatchUpFiredAt_TravelsWithTaskRow()
    {
        using var source = new BackupHost();
        await source.CreateTodoListAsync("list-a", "清单A");
        var task = await source.CreateTodoTaskAsync("task-a", "list-a", "错过提醒的任务",
            dueAt: DateTimeOffset.UtcNow.AddHours(-3), reminderOn: true);
        var fired = DateTimeOffset.UtcNow.AddHours(-2.5);
        await source.TodoRepo.MarkCatchUpFiredAsync(task.Id, fired);
        // The marker write deliberately does NOT stamp UpdatedAt — pin it too.
        var updatedAtBefore = (await source.TodoRepo.GetTaskByUuidAsync("task-a"))!.UpdatedAt;

        var dest = source.TempFile();
        await source.Backup.BackupAsync(dest);

        using var target = new BackupHost();
        await target.Backup.RestoreAsync(dest, RestoreMode.Merge);

        var restored = await target.TodoRepo.GetTaskByUuidAsync("task-a");
        Assert.NotNull(restored);
        Assert.NotNull(restored!.CatchUpFiredAt);
        Assert.Equal(fired.ToUnixTimeMilliseconds(), restored.CatchUpFiredAt!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(updatedAtBefore.ToUnixTimeMilliseconds(), restored.UpdatedAt.ToUnixTimeMilliseconds());
        Assert.True(restored.ReminderOn);
        Assert.NotNull(restored.DueAt);
    }

    // ---------- Cascade integrity across the round trip ----------

    [Fact]
    public async Task MergeRestore_ListDeleteCascadeSurvivesRestore()
    {
        using var source = new BackupHost();
        await source.CreateTodoListAsync("list-a", "清单A");
        await source.CreateTodoTaskAsync("task-1", "list-a", "任务一");
        await source.CreateTodoTaskAsync("task-2", "list-a", "任务二");

        var dest = source.TempFile();
        await source.Backup.BackupAsync(dest);

        using var target = new BackupHost();
        await target.Backup.RestoreAsync(dest, RestoreMode.Merge);

        // The restored ListUuid link is live: deleting the list cascades the
        // restored tasks in one transaction.
        var list = (await target.TodoRepo.GetListsAsync()).Single(l => l.Uuid == "list-a");
        await target.TodoRepo.DeleteListAsync(list.Id);

        Assert.Null(await target.TodoRepo.GetTaskByUuidAsync("task-1"));
        Assert.Null(await target.TodoRepo.GetTaskByUuidAsync("task-2"));
        Assert.Empty(await target.TodoRepo.GetAllAsync(includeCompleted: true));
    }

    // ---------- Post-restore reminder reconcile ----------

    [Fact]
    public async Task MergeRestore_SchedulesToastsForImportedFutureReminders()
    {
        using var source = new BackupHost();
        await source.CreateTodoListAsync("list-a", "清单A");
        await source.CreateTodoTaskAsync("task-rem", "list-a", "未来提醒任务",
            dueAt: DateTimeOffset.UtcNow.AddHours(2), reminderOn: true);

        var dest = source.TempFile();
        await source.Backup.BackupAsync(dest);

        using var target = new BackupHost(withReminders: true);
        await target.Backup.RestoreAsync(dest, RestoreMode.Merge);

        // The 14-day fill picked the imported task up: its toast is scheduled
        // in the SAME session, without waiting for the next launch.
        Assert.True(target.Scheduler!.Scheduled.ContainsKey("task-rem"));
    }

    [Fact]
    public async Task OverwriteRestore_CancelsToastsOfDisappearedTasks()
    {
        using var source = new BackupHost();
        await source.CreateTodoListAsync("list-a", "清单A");

        var dest = source.TempFile();
        await source.Backup.BackupAsync(dest);

        using var target = new BackupHost(withReminders: true);
        await target.CreateTodoListAsync("list-local", "本地清单");
        await target.CreateTodoTaskAsync("task-gone", "list-local", "将被覆盖的任务",
            dueAt: DateTimeOffset.UtcNow.AddHours(3), reminderOn: true);
        // Simulate the OS holding its toast (as the launch reconcile would leave it).
        await target.Scheduler!.ScheduleAsync(new TodoReminder
        {
            TaskUuid = "task-gone",
            Title = "将被覆盖的任务",
            DueAt = DateTimeOffset.UtcNow.AddHours(3),
        });

        await target.Backup.RestoreAsync(dest, RestoreMode.Overwrite);

        // The stale sweep cancelled the toast of the task the archive didn't have.
        Assert.Empty(target.Scheduler.Scheduled);
    }

    // ---------- Stage ⑤ children: steps + attachments (rows AND bytes) ----------

    [Fact]
    public async Task OverwriteRestore_RestoresStepsAttachmentsAndBytes_Wholesale()
    {
        using var source = new BackupHost();
        await source.CreateTodoListAsync("list-a", "清单A");
        var task = await source.CreateTodoTaskAsync("task-a", "list-a", "带步骤和附件");
        await source.CreateTodoStepAsync("step-1", task.Id, "未完成的步骤");
        await source.CreateTodoStepAsync("step-2", task.Id, "已完成的步骤",
            completedAt: DateTimeOffset.UtcNow.AddHours(-1));
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];
        var attachment = await source.CreateTodoAttachmentAsync("att-1", task.Id, "资料.zip", payload);

        var dest = source.TempFile();
        await source.Backup.BackupAsync(dest);

        using var target = new BackupHost();
        // Interference on the target: an attachment row + its store file that the
        // archive does NOT have — a wholesale overwrite must wipe both halves.
        await target.CreateTodoListAsync("list-local", "本地清单");
        var localTask = await target.CreateTodoTaskAsync("task-local", "list-local", "本地任务");
        var interference = await target.CreateTodoAttachmentAsync("att-local", localTask.Id, "干扰.bin", [9]);
        var interferenceFile = Path.Combine(target.Paths.AttachmentsDir, interference.Sha);

        await target.Backup.RestoreAsync(dest, RestoreMode.Overwrite);

        var restoredTask = await target.TodoRepo.GetTaskByUuidAsync("task-a");
        Assert.NotNull(restoredTask);
        var steps = await target.TodoRepo.GetStepsAsync(restoredTask!.Id);
        Assert.Equal(["未完成的步骤", "已完成的步骤"], steps.Select(s => s.Title).ToArray());
        Assert.Null(steps[0].CompletedAt);
        Assert.NotNull(steps[1].CompletedAt);

        var attachments = await target.TodoRepo.GetAttachmentsAsync(restoredTask.Id);
        var restoredAttachment = Assert.Single(attachments);
        Assert.Equal("att-1", restoredAttachment.Uuid);
        Assert.Equal("资料.zip", restoredAttachment.FileName);
        Assert.Equal(payload.Length, restoredAttachment.ByteSize);
        // The BYTES came back with the row.
        Assert.Equal(payload, await File.ReadAllBytesAsync(
            Path.Combine(target.Paths.AttachmentsDir, restoredAttachment.Sha)));
        // Wholesale: the interference row AND its file are gone.
        Assert.Empty(await target.TodoRepo.GetAttachmentsAsync(
            (await target.TodoRepo.GetTaskByUuidAsync("task-local"))?.Id ?? -1));
        Assert.False(File.Exists(interferenceFile));
    }

    [Fact]
    public async Task MergeRestore_ImportsStepsAndAttachments_RelinkedToLiveTaskIds()
    {
        using var source = new BackupHost();
        await source.CreateTodoListAsync("list-a", "清单A");
        var sourceTask = await source.CreateTodoTaskAsync("task-a", "list-a", "任务A");
        await source.CreateTodoStepAsync("step-1", sourceTask.Id, "第一步");
        await source.CreateTodoStepAsync("step-2", sourceTask.Id, "第二步");
        byte[] payload = [42, 43, 44];
        var sourceAttachment = await source.CreateTodoAttachmentAsync("att-1", sourceTask.Id, "附件.bin", payload);

        var dest = source.TempFile();
        await source.Backup.BackupAsync(dest);

        using var target = new BackupHost();
        // A pre-existing local task shifts the live id space, so the imported
        // task's live Id differs from its STAGED id — the children's TaskId must
        // be remapped, not copied (they reference todo_tasks.Id, not the uuid).
        await target.CreateTodoListAsync("list-local", "本地清单");
        await target.CreateTodoTaskAsync("task-local", "list-local", "占位任务");

        await target.Backup.RestoreAsync(dest, RestoreMode.Merge);

        var restoredTask = await target.TodoRepo.GetTaskByUuidAsync("task-a");
        Assert.NotNull(restoredTask);
        Assert.NotEqual(sourceTask.Id, restoredTask!.Id); // relink is observable

        var steps = await target.TodoRepo.GetStepsAsync(restoredTask.Id);
        Assert.Equal(["第一步", "第二步"], steps.Select(s => s.Title).ToArray());
        Assert.All(steps, s => Assert.Equal(restoredTask.Id, s.TaskId));
        Assert.Equal(["step-1", "step-2"], steps.Select(s => s.Uuid).ToArray()); // identity preserved

        var restoredAttachment = Assert.Single(await target.TodoRepo.GetAttachmentsAsync(restoredTask.Id));
        Assert.Equal("att-1", restoredAttachment.Uuid);
        Assert.Equal(restoredTask.Id, restoredAttachment.TaskId);
        Assert.Equal(sourceAttachment.Sha, restoredAttachment.Sha);
        // The bytes traveled as an attachments/ archive entry into the live store.
        Assert.Equal(payload, await File.ReadAllBytesAsync(
            Path.Combine(target.Paths.AttachmentsDir, restoredAttachment.Sha)));
    }

    [Fact]
    public async Task MergeRestore_StepLWW_KeepsNewerPerDirection()
    {
        using var host = new BackupHost();
        var t0 = DateTimeOffset.UtcNow.AddHours(-2);
        await host.CreateTodoListAsync("list-a", "清单A");
        var task = await host.CreateTodoTaskAsync("task-a", "list-a", "任务A");
        var step = await host.CreateTodoStepAsync("step-a", task.Id, "备份时的步骤", updatedAt: t0);

        var dest = host.TempFile();
        await host.Backup.BackupAsync(dest);

        // Direction 1 — local step is NEWER (checking it off stamps now > t0):
        // the merge keeps the local row.
        await host.TodoRepo.SetStepCompletedAsync(step.Id, DateTimeOffset.UtcNow);
        await host.Backup.RestoreAsync(dest, RestoreMode.Merge);
        var kept = (await host.TodoRepo.GetStepsAsync(task.Id)).Single(s => s.Uuid == "step-a");
        Assert.NotNull(kept.CompletedAt);

        // Direction 2 — local step is OLDER (fixture surgery below t0): the
        // archive's row wins whole (unchecked again, archive UpdatedAt restored).
        await host.BumpUpdatedAtAsync("todo_steps", "step-a", t0.AddHours(-1));
        await host.Backup.RestoreAsync(dest, RestoreMode.Merge);
        var reverted = (await host.TodoRepo.GetStepsAsync(task.Id)).Single(s => s.Uuid == "step-a");
        Assert.Null(reverted.CompletedAt);
        Assert.Equal(t0.ToUnixTimeMilliseconds(), reverted.UpdatedAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task MergeRestore_AttachmentLWW_SwapsShaAndPrunesTheLosingBytes()
    {
        using var host = new BackupHost();
        var t0 = DateTimeOffset.UtcNow.AddHours(-2);
        await host.CreateTodoListAsync("list-a", "清单A");
        var task = await host.CreateTodoTaskAsync("task-a", "list-a", "任务A");
        byte[] archiveBytes = [1, 2, 3];
        var attachment = await host.CreateTodoAttachmentAsync("att-a", task.Id, "附件.bin", archiveBytes, updatedAt: t0);

        var dest = host.TempFile();
        await host.Backup.BackupAsync(dest);

        // Fixture surgery: the local row points at DIFFERENT bytes (a re-attach of
        // the same logical slot) and is OLDER — the merge must take the archive's
        // sha, restore its bytes, and prune the losing file once it is unreferenced.
        byte[] losingBytes = [9, 9, 9, 9];
        var losingSha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(losingBytes)).ToLowerInvariant();
        var losingFile = Path.Combine(host.Paths.AttachmentsDir, losingSha);
        await File.WriteAllBytesAsync(losingFile, losingBytes);
        await using (var conn = host.Factory.OpenWrite())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "UPDATE todo_attachments SET Sha = $sha, ByteSize = $size, UpdatedAt = $ms WHERE Uuid = 'att-a';";
            cmd.Parameters.AddWithValue("$sha", losingSha);
            cmd.Parameters.AddWithValue("$size", losingBytes.Length);
            cmd.Parameters.AddWithValue("$ms", t0.AddHours(-1).ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync();
        }

        await host.Backup.RestoreAsync(dest, RestoreMode.Merge);

        var restored = Assert.Single(await host.TodoRepo.GetAttachmentsAsync(task.Id));
        Assert.Equal(attachment.Sha, restored.Sha); // archive's sha won
        Assert.Equal(archiveBytes.Length, restored.ByteSize);
        Assert.Equal(archiveBytes, await File.ReadAllBytesAsync(
            Path.Combine(host.Paths.AttachmentsDir, attachment.Sha)));
        Assert.False(File.Exists(losingFile)); // losing bytes pruned (last reference lost)
    }
}
