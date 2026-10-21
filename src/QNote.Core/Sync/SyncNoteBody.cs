using System.Text.Json;
using System.Text.Json.Serialization;

namespace QNote.Sync;

/// <summary>The per-note fields the engine envelopes in front of the markdown.</summary>
public sealed record SyncNoteEnvelope
{
    public string Title { get; init; } = string.Empty;

    /// <summary>The note's category NAME link (notes ↔ categories name contract).</summary>
    public string Category { get; init; } = string.Empty;
}

/// <summary>Trim-safe JSON for the note envelope (reflection-based serialization
/// is banned project-wide — see csharp-conventions).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SyncNoteEnvelope))]
public sealed partial class SyncNoteEnvelopeJsonContext : JsonSerializerContext;

/// <summary>
/// The engine's note-body envelope (user ruling 2026-10-21, amending PRD decision
/// 10): the container's body segment carries the note's TITLE and CATEGORY NAME
/// in front of the markdown, because both are per-note user data the frozen
/// 48-byte header and the binary entry table have no room for. Framing:
///
/// <code>
/// {"title":…,"category":…}\n<markdown>
/// </code>
///
/// The first line is strict JSON (title/category fully escaped — any content,
/// newlines included, survives), and JSON strings never contain a RAW newline,
/// so the first 0x0A byte is exactly the frame separator and the rest is the
/// markdown verbatim. The framing stays INSIDE the payload (rather than the
/// plaintext header) deliberately: under E2EE the title and category must be
/// encrypted, and the AAD binds only the 48-byte header. The body segment
/// remains UTF-8 text end to end, so <see cref="QnoteContainerCodec"/> — which
/// hands the body over as a string — stays untouched.
/// </summary>
public static class SyncNoteBody
{
    public static string Encode(string title, string category, string content)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(content);

        var envelope = JsonSerializer.Serialize(
            new SyncNoteEnvelope { Title = title, Category = category },
            SyncNoteEnvelopeJsonContext.Default.SyncNoteEnvelope);
        return envelope + "\n" + content;
    }

    /// <exception cref="QnoteContainerException">The envelope line is missing or not valid JSON.</exception>
    public static (string Title, string Category, string Content) Decode(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var separator = body.IndexOf('\n');
        if (separator < 0)
            throw new QnoteContainerException(
                QnoteContainerErrorKind.BadPayload,
                "The note body has no envelope line (expected {\"title\":…,\"category\":…}\\n<markdown>).");

        string envelopeJson = body[..separator];
        string content = body[(separator + 1)..];

        SyncNoteEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(envelopeJson, SyncNoteEnvelopeJsonContext.Default.SyncNoteEnvelope);
        }
        catch (JsonException ex)
        {
            throw new QnoteContainerException(
                QnoteContainerErrorKind.BadPayload,
                "The note body envelope line is not valid JSON.",
                ex);
        }
        if (envelope is null)
            throw new QnoteContainerException(
                QnoteContainerErrorKind.BadPayload,
                "The note body envelope line decoded to null.");

        return (envelope.Title, envelope.Category, content);
    }
}
