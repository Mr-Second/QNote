namespace QNote.Models;

/// <summary>
/// Restore strategy for a backup archive (PRD D2, Qt parity: 覆盖 / 合并 / 仅导入新增).
/// Conflicts are detected by note <see cref="Note.Uuid"/>.
/// </summary>
public enum RestoreMode
{
    /// <summary>Replace the whole data set: DB file + images are swapped out wholesale.</summary>
    Overwrite = 0,

    /// <summary>Per-note import: a conflicting uuid keeps the newer <see cref="Note.UpdatedAt"/>; new notes are added; categories merge by name.</summary>
    Merge = 1,

    /// <summary>Per-note import that skips every uuid already present locally.</summary>
    ImportNewOnly = 2,
}

/// <summary>
/// Result of comparing a backup archive against the current database, shown in the
/// restore dialog before the user picks a <see cref="RestoreMode"/>.
/// </summary>
/// <param name="BackupNoteCount">Notes contained in the backup.</param>
/// <param name="ConflictCount">Uuids present in BOTH the backup and the current DB.</param>
/// <param name="NewCount">Uuids only in the backup (would be imported).</param>
/// <param name="CurrentOnlyCount">Uuids only in the current DB (untouched by merge / lost on overwrite).</param>
/// <param name="BackupCreatedAt">Creation time from the manifest, when present.</param>
public sealed record RestoreAnalysis(
    int BackupNoteCount,
    int ConflictCount,
    int NewCount,
    int CurrentOnlyCount,
    DateTimeOffset? BackupCreatedAt);
