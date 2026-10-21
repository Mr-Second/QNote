namespace QNote.Sync;

/// <summary>Classification of .qnote container failures, for localized UI messages.</summary>
public enum QnoteContainerErrorKind
{
    /// <summary>The leading bytes are not the QN magic.</summary>
    BadMagic,

    /// <summary>The container was written by a newer format version than this build understands.</summary>
    UnsupportedVersion,

    /// <summary>The header is truncated, or one of its fields is outside its allowed domain.</summary>
    BadHeader,

    /// <summary>The payload framing is broken (a segment is truncated or trailing bytes exist).</summary>
    BadPayload,

    /// <summary>A segment CRC32, or the overall CRC32, does not match.</summary>
    CrcMismatch,

    /// <summary>The entry table is malformed, or an entry does not match its content address.</summary>
    BadEntryTable,

    /// <summary>The container is end-to-end encrypted but no key was supplied.</summary>
    KeyRequired,

    /// <summary>AES-GCM authentication failed: wrong key, or a tampered container/header.</summary>
    DecryptionFailed,
}

/// <summary>
/// An expected .qnote container failure (error-handling spec: expected outcomes
/// are modeled, not buried in generic exceptions). <see cref="Kind"/> tells the
/// sync engine / Presentation which localized message applies; the technical
/// detail travels via <see cref="Exception.Message"/>/<see
/// cref="Exception.InnerException"/>. Messages are English diagnostics — Core
/// never owns user-visible wording.
/// </summary>
public sealed class QnoteContainerException : Exception
{
    public QnoteContainerException(QnoteContainerErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public QnoteContainerErrorKind Kind { get; }
}
