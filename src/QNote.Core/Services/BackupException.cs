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

    /// <summary>The archive opened but is missing required content (e.g. qnote.db) or the content is invalid.</summary>
    CorruptArchive,

    /// <summary>The backup was written by a newer app/schema version than this build understands.</summary>
    UnsupportedVersion,
}

/// <summary>
/// An expected, user-facing backup/restore failure (error-handling spec: expected
/// outcomes are modeled, not buried in generic exceptions). <see cref="Kind"/>
/// tells Presentation which localized message to show; the technical detail is
/// logged via <see cref="Exception.Message"/>/<see cref="Exception.InnerException"/>.
/// </summary>
public sealed class BackupException : Exception
{
    public BackupException(BackupErrorKind kind, string message, Exception? inner = null)
        : base(message, inner) => Kind = kind;

    public BackupErrorKind Kind { get; }
}
