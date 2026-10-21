using System.Security.Cryptography;
using QNote.Sync;

namespace QNote.Tests;

/// <summary>
/// Unit battery for the sync primitives (task 10-06 L1): the note-body envelope
/// (title + category name framed in front of the markdown — user ruling
/// 2026-10-21 amending PRD decision 10) and the E2EE key deriver.
/// </summary>
public sealed class SyncPrimitivesTests
{
    // ---------- SyncNoteBody envelope ----------

    [Fact]
    public void Envelope_RoundTrips()
    {
        var body = SyncNoteBody.Encode("标题 T", "工作", "# heading\n\nbody – with émoji 🎉");

        var (title, category, content) = SyncNoteBody.Decode(body);

        Assert.Equal("标题 T", title);
        Assert.Equal("工作", category);
        Assert.Equal("# heading\n\nbody – with émoji 🎉", content);
    }

    [Fact]
    public void Envelope_RoundTripsHostileMetadata()
    {
        // Newlines, quotes, backslashes and the frame separator INSIDE the fields —
        // JSON escaping makes any title/category expressible, and JSON strings never
        // contain a raw newline, so the frame stays unambiguous.
        var body = SyncNoteBody.Encode("line1\nline2 \"quoted\" \\ {json}", "cat\tegory\n", "content");

        var (title, category, content) = SyncNoteBody.Decode(body);

        Assert.Equal("line1\nline2 \"quoted\" \\ {json}", title);
        Assert.Equal("cat\tegory\n", category);
        Assert.Equal("content", content);
    }

    [Fact]
    public void Envelope_EmptyFields_RoundTrip()
    {
        var (title, category, content) = SyncNoteBody.Decode(SyncNoteBody.Encode("", "", ""));

        Assert.Equal("", title);
        Assert.Equal("", category);
        Assert.Equal("", content);
    }

    [Fact]
    public void Envelope_ContentMayLookLikeAnything()
    {
        // The markdown may itself start with a JSON-looking line or a newline —
        // only the FIRST raw newline terminates the envelope.
        var content = "{\"title\":\"fake\"}\nmore";
        var (title, category, decoded) = SyncNoteBody.Decode(SyncNoteBody.Encode("real", "real-cat", content));

        Assert.Equal("real", title);
        Assert.Equal("real-cat", category);
        Assert.Equal(content, decoded);
    }

    [Fact]
    public void Envelope_MissingFrame_Throws()
    {
        var ex = Assert.Throws<QnoteContainerException>(() => SyncNoteBody.Decode("no newline here"));
        Assert.Equal(QnoteContainerErrorKind.BadPayload, ex.Kind);
    }

    [Fact]
    public void Envelope_InvalidJson_Throws()
    {
        var ex = Assert.Throws<QnoteContainerException>(() => SyncNoteBody.Decode("not-json\ncontent"));
        Assert.Equal(QnoteContainerErrorKind.BadPayload, ex.Kind);
    }

    // ---------- SyncKeyDeriver ----------

    [Fact]
    public void Deriver_IsDeterministic_AndProduces32Bytes()
    {
        var salt = RandomNumberGenerator.GetBytes(SyncKeyDeriver.SaltByteCount);

        var key1 = SyncKeyDeriver.Derive("password", salt);
        var key2 = SyncKeyDeriver.Derive("password", salt);

        Assert.Equal(SyncKeyDeriver.KeyByteCount, key1.Length);
        Assert.Equal(key1, key2);
        Assert.NotEqual(SyncKeyDeriver.Derive("other", salt), key1);
        Assert.NotEqual(SyncKeyDeriver.Derive("password", RandomNumberGenerator.GetBytes(32)), key1);
    }

    [Fact]
    public void Deriver_RejectsWrongSaltLength()
    {
        Assert.Throws<ArgumentException>(() => SyncKeyDeriver.Derive("password", new byte[16]));
        Assert.Throws<ArgumentException>(() => SyncKeyDeriver.Derive("password", []));
        Assert.Throws<ArgumentException>(() => SyncKeyDeriver.Derive("", new byte[32]));
    }

    // ---------- ConflictResolver (LWW tiebreak) ----------

    [Fact]
    public void Tiebreak_LargerDigestWins()
    {
        Assert.Equal(ConflictWinner.Local, ConflictResolver.Resolve("ff", "aa"));
        Assert.Equal(ConflictWinner.Cloud, ConflictResolver.Resolve("aa", "ff"));
    }

    [Fact]
    public void Tiebreak_IsTotalAndDeterministic()
    {
        // Equal digests mean identical content (the caller handles that first),
        // but the resolution must still be total: Cloud, every time.
        Assert.Equal(ConflictWinner.Cloud, ConflictResolver.Resolve("aa", "aa"));
        Assert.Equal(ConflictWinner.Cloud, ConflictResolver.Resolve("", ""));
    }

    // ---------- SyncManifest round-trip ----------

    [Fact]
    public void Manifest_RoundTrips()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new SyncManifest { E2eeEnabled = true, E2eeSalt = "c2FsdA==" },
            SyncManifestJsonContext.Default.SyncManifest);

        var manifest = System.Text.Json.JsonSerializer.Deserialize(json, SyncManifestJsonContext.Default.SyncManifest)!;

        Assert.Equal(SyncManifest.CurrentFormatVersion, manifest.FormatVersion);
        Assert.True(manifest.E2eeEnabled);
        Assert.Equal("c2FsdA==", manifest.E2eeSalt);
    }
}
