namespace QNote.Services;

/// <summary>Classification of backup/restore failures, for localized UI messages.</summary>
public enum BackupErrorKind
{
    /// <summary>The archive file does not exist.</summary>
    ArchiveNotFound,

    /// <summary>The file is not a readable archive at all.</summary>
    NotAnArchive,

    /// <summary>The archive is encrypted but no password was supplied.</summary>
    PasswordRequired,

    /// <summary>Decryption failed — wrong password (or the archive is damaged).</summary>
    WrongPassword,

    /// <summary>The archive opened but is missing required content or the content is invalid.</summary>
    CorruptArchive,

    /// <summary>The archive does not contain the qnote.db entry (distinct from a damaged db).</summary>
    MissingDatabase,

    /// <summary>The backup was written by a newer app/schema version than this build understands.</summary>
    UnsupportedVersion,
}

/// <summary>
/// An expected, user-facing backup/restore failure (error-handling spec: expected
/// outcomes are modeled, not buried in generic exceptions). <see cref="Kind"/>
/// tells Presentation which localized message to show; the technical detail is
/// logged via <see cref="Exception.Message"/>/<see cref="Exception.InnerException"/>.
/// Messages are English diagnostics — Core never owns user-visible wording.
/// </summary>
public sealed class BackupException : Exception
{
    public BackupException(
        BackupErrorKind kind,
        string message,
        Exception? inner = null,
        int? schemaFound = null,
        int? schemaSupported = null)
        : base(message, inner)
    {
        Kind = kind;
        SchemaFound = schemaFound;
        SchemaSupported = schemaSupported;
    }

    public BackupErrorKind Kind { get; }

    /// <summary>For <see cref="BackupErrorKind.UnsupportedVersion"/>: the backup's effective schema version.</summary>
    public int? SchemaFound { get; }

    /// <summary>For <see cref="BackupErrorKind.UnsupportedVersion"/>: the newest schema this build supports.</summary>
    public int? SchemaSupported { get; }
}
