using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using QNote.Sync;

namespace QNote.Tests;

/// <summary>
/// .qnote container codec battery (task 10-06, multi-device sync L1). Pins the
/// PRD decision-10 format byte-for-byte where it is plaintext (the 48-byte
/// header), the entry-table layout, every CRC gate, the LWW header-only read,
/// and the AES-256-GCM AAD binding — including a container assembled BY HAND
/// from the PRD tables, so encoder and decoder cannot silently agree on a
/// wrong format.
/// </summary>
public sealed class QnoteContainerCodecTests
{
    private const int HeaderBytes = QnoteContainerCodec.HeaderByteCount;

    // ---------- 1. round-trips ----------

    [Fact]
    public void RoundTrip_WithoutEncryption_PreservesEverything()
    {
        var header = TestHeader();
        const string body = "# 标题\n\n正文 with 中文 and `code` — keep it non-trivial.";
        var image = Image([0x00, 0xFF, 0x10, 0x80, 0x42]);
        var attachment = Attachment([1, 2, 3, 4, 5, 6, 7, 8], "清单.txt");

        var container = QnoteContainerCodec.Encode(header, body, [image, attachment]);

        Assert.Equal(1, container[0x2C]);
        Assert.Equal(0, container[0x2D]);

        AssertHeaderEqual(header, QnoteContainerCodec.ReadHeader(container));

        var decoded = QnoteContainerCodec.Decode(container);
        AssertHeaderEqual(header, decoded.Header);
        Assert.Equal(body, decoded.Body);
        Assert.Equal(2, decoded.Entries.Count);
        AssertEntryEqual(image, decoded.Entries[0]);
        AssertEntryEqual(attachment, decoded.Entries[1]);
    }

    [Fact]
    public void RoundTrip_Encrypted_PreservesEverything_AndRandomizesTheNonce()
    {
        var header = TestHeader();
        const string body = "机密正文";
        var attachment = Attachment([9, 8, 7, 6], "机密.bin");
        var key = TestKey();

        var first = QnoteContainerCodec.Encode(header, body, [attachment], key);
        var second = QnoteContainerCodec.Encode(header, body, [attachment], key);

        // Encoding byte, encryption flag, and the 12-byte nonce slot.
        Assert.Equal(2, first[0x2C]);
        Assert.Equal(1, first[0x2D] & 1);
        Assert.True(first.Length >= HeaderBytes + 12 + 16);
        // A fresh random nonce per write: identical input never re-encrypts identically.
        Assert.NotEqual(first, second);
        Assert.NotEqual(
            first.AsSpan(HeaderBytes, 12).ToArray(),
            second.AsSpan(HeaderBytes, 12).ToArray());

        var decoded = QnoteContainerCodec.Decode(first, key);
        AssertHeaderEqual(header, decoded.Header);
        Assert.Equal(body, decoded.Body);
        Assert.Single(decoded.Entries);
        AssertEntryEqual(attachment, decoded.Entries[0]);
    }

    [Fact]
    public void RoundTrip_TombstonedHeader_PreservesTheDeletedInstant()
    {
        var header = TestHeader(deletedMs: 1_700_000_200_789);

        var container = QnoteContainerCodec.Encode(header, "正文", []);
        var decoded = QnoteContainerCodec.Decode(container);

        Assert.Equal(header.DeletedAtMs, decoded.Header.DeletedAtMs);
        Assert.Equal(header, decoded.Header);
        Assert.Empty(decoded.Entries);
    }

    [Fact]
    public void RoundTrip_EmptyBodyAndEmptyEntries()
    {
        var header = TestHeader();

        var container = QnoteContainerCodec.Encode(header, "", []);
        var decoded = QnoteContainerCodec.Decode(container);

        Assert.Equal("", decoded.Body);
        Assert.Empty(decoded.Entries);
    }

    [Fact]
    public void RoundTrip_ZeroByteAttachment_Survives()
    {
        var header = TestHeader();
        var empty = Attachment([], "empty.bin");

        var decoded = QnoteContainerCodec.Decode(
            QnoteContainerCodec.Encode(header, "正文", [empty]));

        Assert.Single(decoded.Entries);
        AssertEntryEqual(empty, decoded.Entries[0]);
    }

    // ---------- 2. PRD conformance ----------

    /// <summary>
    /// The 48 plaintext header bytes must match the PRD decision-10 table exactly:
    /// magic, u16 version, raw uuid, three i64 epoch-ms stamps, encoding, flags,
    /// two reserved zero bytes. Pinned byte-for-byte so the format cannot drift.
    /// </summary>
    [Fact]
    public void Encode_WritesThePrdHeaderLayoutExactly()
    {
        var uuid = Guid.Parse("3f2b1a0c9d8e7f6a5b4c3d2e1f0a9b8c");
        var header = new QnoteHeader
        {
            Uuid = uuid,
            CreatedAtMs = 1_700_000_000_123,
            UpdatedAtMs = 1_700_000_100_456,
            DeletedAtMs = 0,
        };

        var container = QnoteContainerCodec.Encode(header, "正文", []);

        Assert.Equal((byte)'Q', container[0]);
        Assert.Equal((byte)'N', container[1]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(container.AsSpan(2)));
        Assert.Equal(uuid.ToByteArray(), container.Skip(4).Take(16).ToArray());
        Assert.Equal(1_700_000_000_123L, BinaryPrimitives.ReadInt64LittleEndian(container.AsSpan(0x14)));
        Assert.Equal(1_700_000_100_456L, BinaryPrimitives.ReadInt64LittleEndian(container.AsSpan(0x1C)));
        Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(container.AsSpan(0x24)));
        Assert.Equal(0, container[0x2E]);
        Assert.Equal(0, container[0x2F]);
    }

    /// <summary>
    /// A container assembled BY HAND from the PRD decision-10 tables (encoding 0 =
    /// raw, which this build never writes but must read) decodes to exactly what
    /// was put in. This is the spec-conformance cross-check: encoder and decoder
    /// cannot both be wrong in the same way against it.
    /// </summary>
    [Fact]
    public void Decode_ReadsAHandBuiltRawContainer()
    {
        var uuid = Guid.Parse("0f1e2d3c4b5a69788796a5b4c3d2e1f0");
        var data = new byte[] { 0xAB, 0xCD, 0xEF, 0x01 };
        var sha = ShaHex(data);
        const string name = "附件.bin";
        const string body = "手写正文";
        var table = EntryBytes((byte)QnoteEntryKind.Attachment, sha, offset: 0, length: data.Length, name);

        var container = BuildRawContainer(
            uuid, 1_700_000_000_123, 1_700_000_100_456, 0, body, table, data);

        Assert.Equal(0, container[0x2C]);
        Assert.Equal(0, container[0x2D]);

        var decoded = QnoteContainerCodec.Decode(container);

        Assert.Equal(uuid, decoded.Header.Uuid);
        Assert.Equal(1_700_000_000_123, decoded.Header.CreatedAtMs);
        Assert.Equal(1_700_000_100_456, decoded.Header.UpdatedAtMs);
        Assert.Equal(0, decoded.Header.DeletedAtMs);
        Assert.Equal(body, decoded.Body);
        Assert.Single(decoded.Entries);
        Assert.Equal(QnoteEntryKind.Attachment, decoded.Entries[0].Kind);
        Assert.Equal(sha, decoded.Entries[0].Sha256);
        Assert.Equal(name, decoded.Entries[0].FileName);
        Assert.Equal(data, decoded.Entries[0].Data);
    }

    /// <summary>
    /// The entry-table item layout is pinned by PRD: 1B kind, 32B raw sha256,
    /// 8B offset, 8B length, 4B name-byte-count, UTF-8 name. Built here by hand
    /// and read back through the codec.
    /// </summary>
    [Fact]
    public void EntryTable_LayoutMatchesThePrdFieldOrder()
    {
        var data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x42 };
        var sha = ShaHex(data);
        var table = EntryBytes((byte)QnoteEntryKind.Image, sha, 0, data.Length, "");
        var container = BuildRawContainer(TestHeader().Uuid, 1, 2, 0, "b", table, data);

        var decoded = QnoteContainerCodec.Decode(container);

        var entry = Assert.Single(decoded.Entries);
        Assert.Equal(QnoteEntryKind.Image, entry.Kind);
        Assert.Equal(sha, entry.Sha256);
        Assert.Equal("", entry.FileName);
        Assert.Equal(data, entry.Data);
    }

    // ---------- 3. header-only read ----------

    /// <summary>
    /// The LWW probe: a driver fetches bytes 0-47 with a Range request and must be
    /// able to parse the header WITHOUT the payload and WITHOUT the E2EE key.
    /// </summary>
    [Fact]
    public void ReadHeader_WorksOnThe48BytePrefix_OfAnEncryptedContainer()
    {
        var header = TestHeader();
        var container = QnoteContainerCodec.Encode(header, "机密", [], TestKey());

        var headerOnly = QnoteContainerCodec.ReadHeader(container.AsSpan(0, HeaderBytes));

        AssertHeaderEqual(header, headerOnly);
    }

    [Fact]
    public void ReadHeader_RejectsBadHeaderDomains()
    {
        var container = QnoteContainerCodec.Encode(TestHeader(), "正文", []);

        AssertKind(QnoteContainerErrorKind.BadHeader, () =>
            QnoteContainerCodec.ReadHeader(container.AsSpan(0, HeaderBytes - 1)));
        AssertKind(QnoteContainerErrorKind.BadMagic, () =>
            QnoteContainerCodec.ReadHeader(WithByte(container, 1, (byte)'X')));
        AssertKind(QnoteContainerErrorKind.UnsupportedVersion, () =>
            QnoteContainerCodec.ReadHeader(WithUInt16(container, 2, 2)));
        AssertKind(QnoteContainerErrorKind.BadHeader, () =>
            QnoteContainerCodec.ReadHeader(WithByte(container, 0x2C, 5)));
        AssertKind(QnoteContainerErrorKind.BadHeader, () =>
            QnoteContainerCodec.ReadHeader(WithByte(container, 0x2D, 0b10)));
        AssertKind(QnoteContainerErrorKind.BadHeader, () =>
            QnoteContainerCodec.ReadHeader(WithByte(container, 0x2D, 0b01))); // flag without encoding 2
    }

    // ---------- 4. CRC gates ----------

    /// <summary>
    /// Every integrity layer fires on its own layer: body/table/binary segment
    /// CRCs name their segment, the overall CRC names itself, framing lies are
    /// BadPayload, and appended garbage is rejected.
    /// </summary>
    [Fact]
    public void Corruption_IsCaughtByTheRightCrcGate()
    {
        var image = Image([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]);
        const string body = " crc 目标正文";
        var table = EntryBytes((byte)QnoteEntryKind.Image, image.Sha256, 0, image.Data.Length, "");
        var container = BuildRawContainer(
            TestHeader().Uuid, 1, 2, 0, body, table, image.Data);

        // Flip a byte INSIDE each segment's data (start = segment header + len prefix).
        int bodyDataStart = HeaderBytes + 4;
        int tableDataStart = HeaderBytes + 4 + Encoding.UTF8.GetByteCount(body) + 4 + 4;
        int binaryDataStart = tableDataStart + 4 + table.Length + 4;

        var bodyFlip = AssertKind(QnoteContainerErrorKind.CrcMismatch, () =>
            QnoteContainerCodec.Decode(Flipped(container, bodyDataStart)));
        Assert.Contains("body", bodyFlip.Message);

        var tableFlip = AssertKind(QnoteContainerErrorKind.CrcMismatch, () =>
            QnoteContainerCodec.Decode(Flipped(container, tableDataStart)));
        Assert.Contains("entry table", tableFlip.Message);

        var binaryFlip = AssertKind(QnoteContainerErrorKind.CrcMismatch, () =>
            QnoteContainerCodec.Decode(Flipped(container, binaryDataStart)));
        Assert.Contains("binary region", binaryFlip.Message);

        var overallFlip = AssertKind(QnoteContainerErrorKind.CrcMismatch, () =>
            QnoteContainerCodec.Decode(Flipped(container, container.Length - 1)));
        Assert.Contains("overall", overallFlip.Message);

        AssertKind(QnoteContainerErrorKind.BadPayload, () =>
            QnoteContainerCodec.Decode(WithUInt32(container, HeaderBytes, 0xFFFFFFF0)));

        var withTrailingGarbage = new byte[container.Length + 1];
        container.CopyTo(withTrailingGarbage, 0);
        AssertKind(QnoteContainerErrorKind.BadPayload, () =>
            QnoteContainerCodec.Decode(withTrailingGarbage));
    }

    // ---------- 5. encryption / AAD ----------

    /// <summary>
    /// The AAD binds the FULL 48-byte header (user ruling 2026-10-21, extending
    /// PRD decision 10's uuid+updated): tampering with any header field —
    /// created/deleted included, which would otherwise fake a tombstone or steer
    /// LWW silently — or any payload byte, nonce, or tag must fail AES-GCM
    /// authentication, never decrypt to plausible-looking data.
    /// </summary>
    [Fact]
    public void EncryptedContainer_TamperingWithHeaderOrPayload_FailsAuthentication()
    {
        var header = TestHeader();
        var key = TestKey();
        var container = QnoteContainerCodec.Encode(
            header, "被保护的正文", [Image([1, 2, 3])], key);

        // Control: the untouched container authenticates.
        var decoded = QnoteContainerCodec.Decode(container, key);
        Assert.Equal("被保护的正文", decoded.Body);

        AssertKind(QnoteContainerErrorKind.DecryptionFailed, () =>
            QnoteContainerCodec.Decode(Flipped(container, 0x1C), key)); // updated (LWW steer)
        AssertKind(QnoteContainerErrorKind.DecryptionFailed, () =>
            QnoteContainerCodec.Decode(Flipped(container, 0x04), key)); // uuid (identity steer)
        AssertKind(QnoteContainerErrorKind.DecryptionFailed, () =>
            QnoteContainerCodec.Decode(Flipped(container, 0x14), key)); // created (was uncovered by the PRD wording)
        AssertKind(QnoteContainerErrorKind.DecryptionFailed, () =>
            QnoteContainerCodec.Decode(Flipped(container, 0x24), key)); // deleted (fake-tombstone hole, closed by the ruling)
        AssertKind(QnoteContainerErrorKind.DecryptionFailed, () =>
            QnoteContainerCodec.Decode(Flipped(container, 0x2E), key)); // reserved
        AssertKind(QnoteContainerErrorKind.DecryptionFailed, () =>
            QnoteContainerCodec.Decode(Flipped(container, HeaderBytes + 12 + 3), key)); // ciphertext
        AssertKind(QnoteContainerErrorKind.DecryptionFailed, () =>
            QnoteContainerCodec.Decode(Flipped(container, HeaderBytes + 1), key)); // nonce
        AssertKind(QnoteContainerErrorKind.DecryptionFailed, () =>
            QnoteContainerCodec.Decode(Flipped(container, container.Length - 2), key)); // tag
        AssertKind(QnoteContainerErrorKind.DecryptionFailed, () =>
            QnoteContainerCodec.Decode(container, TestKey(0x5A))); // wrong key
    }

    [Fact]
    public void KeyPresence_MustMatchTheContainerEncoding()
    {
        var plain = QnoteContainerCodec.Encode(TestHeader(), "正文", []);
        var encrypted = QnoteContainerCodec.Encode(TestHeader(), "正文", [], TestKey());

        AssertKind(QnoteContainerErrorKind.KeyRequired, () =>
            QnoteContainerCodec.Decode(encrypted));
        Assert.Throws<ArgumentException>(() =>
            QnoteContainerCodec.Decode(plain, TestKey()));
        Assert.Throws<ArgumentException>(() =>
            QnoteContainerCodec.Encode(TestHeader(), "正文", [], new byte[16]));
        Assert.Throws<ArgumentException>(() =>
            QnoteContainerCodec.Decode(encrypted, new byte[31]));
    }

    // ---------- 6. entry-table validation ----------

    [Fact]
    public void Decode_RejectsMalformedEntryTables()
    {
        var data = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var sha = ShaHex(data);

        // Unknown kind byte.
        AssertKind(QnoteContainerErrorKind.BadEntryTable, () =>
            QnoteContainerCodec.Decode(BuildRawContainer(
                TestHeader().Uuid, 1, 2, 0, "b",
                EntryBytes(3, sha, 0, data.Length, ""), data)));
        // Non-contiguous offset (entries must tile the binary region from 0).
        AssertKind(QnoteContainerErrorKind.BadEntryTable, () =>
            QnoteContainerCodec.Decode(BuildRawContainer(
                TestHeader().Uuid, 1, 2, 0, "b",
                EntryBytes((byte)QnoteEntryKind.Image, sha, offset: 2, length: 2, ""), data)));
        // Length reaches past the region.
        AssertKind(QnoteContainerErrorKind.BadEntryTable, () =>
            QnoteContainerCodec.Decode(BuildRawContainer(
                TestHeader().Uuid, 1, 2, 0, "b",
                EntryBytes((byte)QnoteEntryKind.Image, sha, 0, length: data.Length + 1, ""), data)));
        // Region not fully covered.
        AssertKind(QnoteContainerErrorKind.BadEntryTable, () =>
            QnoteContainerCodec.Decode(BuildRawContainer(
                TestHeader().Uuid, 1, 2, 0, "b",
                EntryBytes((byte)QnoteEntryKind.Image, sha, 0, length: 2, ""), data)));
        // Name length runs past the table.
        var badNameLength = WithUInt32(
            EntryBytes((byte)QnoteEntryKind.Attachment, sha, 0, data.Length, "占位"), 49, 1000);
        AssertKind(QnoteContainerErrorKind.BadEntryTable, () =>
            QnoteContainerCodec.Decode(BuildRawContainer(
                TestHeader().Uuid, 1, 2, 0, "b", badNameLength, data)));
        // Table ends inside an entry.
        var shortTable = EntryBytes((byte)QnoteEntryKind.Image, sha, 0, data.Length, "");
        AssertKind(QnoteContainerErrorKind.BadEntryTable, () =>
            QnoteContainerCodec.Decode(BuildRawContainer(
                TestHeader().Uuid, 1, 2, 0, "b",
                shortTable.AsSpan(0, shortTable.Length - 1).ToArray(), data)));
        // The stored sha does not match the entry data's digest.
        AssertKind(QnoteContainerErrorKind.BadEntryTable, () =>
            QnoteContainerCodec.Decode(BuildRawContainer(
                TestHeader().Uuid, 1, 2, 0, "b",
                EntryBytes((byte)QnoteEntryKind.Image, ShaHex([0xFF]), 0, data.Length, ""), data)));
    }

    // ---------- 7. encode-side validation ----------

    [Fact]
    public void Encode_RejectsInvalidEntriesAndKeys()
    {
        var data = new byte[] { 0x0A, 0x0B };
        var header = TestHeader();

        Assert.Throws<ArgumentException>(() => QnoteContainerCodec.Encode(
            header, "b", [new QnoteEntry { Kind = QnoteEntryKind.Image, Sha256 = "abc", FileName = "", Data = data }]));
        Assert.Throws<ArgumentException>(() => QnoteContainerCodec.Encode(
            header, "b", [new QnoteEntry { Kind = QnoteEntryKind.Image, Sha256 = new string('g', 64), FileName = "", Data = data }]));
        Assert.Throws<ArgumentException>(() => QnoteContainerCodec.Encode(
            header, "b", [new QnoteEntry { Kind = (QnoteEntryKind)7, Sha256 = ShaHex(data), FileName = "", Data = data }]));
        Assert.Throws<ArgumentException>(() => QnoteContainerCodec.Encode(
            header, "b", [new QnoteEntry { Kind = QnoteEntryKind.Image, Sha256 = ShaHex([0xFF]), FileName = "", Data = data }]));
        Assert.Throws<ArgumentException>(() =>
            QnoteContainerCodec.Encode(header, "b", [], new byte[16]));

        Assert.Throws<ArgumentNullException>(() =>
            QnoteContainerCodec.Encode(null!, "b", []));
        Assert.Throws<ArgumentNullException>(() =>
            QnoteContainerCodec.Encode(header, null!, []));
        Assert.Throws<ArgumentNullException>(() =>
            QnoteContainerCodec.Encode(header, "b", null!));
    }

    /// <summary>Digest strings may arrive uppercase (hand-editing, older tools);
    /// the wire format carries raw bytes, so reads always normalize to lowercase.</summary>
    [Fact]
    public void Decode_NormalizesSha256ToLowercase()
    {
        var data = new byte[] { 0xCA, 0xFE };
        var upperSha = ShaHex(data).ToUpperInvariant();
        var header = TestHeader();

        var container = QnoteContainerCodec.Encode(
            header, "b", [new QnoteEntry { Kind = QnoteEntryKind.Attachment, Sha256 = upperSha, FileName = "a", Data = data }]);
        var decoded = QnoteContainerCodec.Decode(container);

        Assert.Equal(ShaHex(data), decoded.Entries[0].Sha256);
    }

    // ---------- fixtures ----------

    private static QnoteHeader TestHeader(long deletedMs = 0) => new()
    {
        Uuid = Guid.Parse("3f2b1a0c9d8e7f6a5b4c3d2e1f0a9b8c"),
        CreatedAtMs = 1_700_000_000_123,
        UpdatedAtMs = 1_700_000_100_456,
        DeletedAtMs = deletedMs,
    };

    private static byte[] TestKey(byte fill = 0x2A) =>
        Enumerable.Repeat(fill, 32).ToArray();

    private static string ShaHex(byte[] data) =>
        Convert.ToHexStringLower(SHA256.HashData(data));

    private static QnoteEntry Image(byte[] data) => new()
    {
        Kind = QnoteEntryKind.Image,
        Sha256 = ShaHex(data),
        FileName = "",
        Data = data,
    };

    private static QnoteEntry Attachment(byte[] data, string name) => new()
    {
        Kind = QnoteEntryKind.Attachment,
        Sha256 = ShaHex(data),
        FileName = name,
        Data = data,
    };

    private static void AssertHeaderEqual(QnoteHeader expected, QnoteHeader actual)
    {
        Assert.Equal(expected.Uuid, actual.Uuid);
        Assert.Equal(expected.CreatedAtMs, actual.CreatedAtMs);
        Assert.Equal(expected.UpdatedAtMs, actual.UpdatedAtMs);
        Assert.Equal(expected.DeletedAtMs, actual.DeletedAtMs);
    }

    private static void AssertEntryEqual(QnoteEntry expected, QnoteEntry actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Sha256, actual.Sha256);
        Assert.Equal(expected.FileName, actual.FileName);
        Assert.Equal(expected.Data, actual.Data);
    }

    private static QnoteContainerException AssertKind(
        QnoteContainerErrorKind kind, Action action)
    {
        var ex = Assert.Throws<QnoteContainerException>(action);
        Assert.Equal(kind, ex.Kind);
        return ex;
    }

    private static byte[] WithByte(byte[] container, int index, byte value)
    {
        var copy = (byte[])container.Clone();
        copy[index] = value;
        return copy;
    }

    private static byte[] WithUInt16(byte[] container, int index, ushort value)
    {
        var copy = (byte[])container.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(index), value);
        return copy;
    }

    private static byte[] WithUInt32(byte[] container, int index, uint value)
    {
        var copy = (byte[])container.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(index), value);
        return copy;
    }

    private static byte[] Flipped(byte[] container, int index) =>
        WithByte(container, index, (byte)(container[index] ^ 0xFF));

    /// <summary>
    /// Builds an encoding-0 (raw payload) container straight from the PRD
    /// decision-10 tables: 48-byte header, three [len][data][crc32] segments,
    /// then the overall CRC32 over everything before it.
    /// </summary>
    private static byte[] BuildRawContainer(
        Guid uuid, long created, long updated, long deleted,
        string body, byte[] table, byte[] binary)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = new byte[HeaderBytes];
        header[0] = (byte)'Q';
        header[1] = (byte)'N';
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 1);
        uuid.ToByteArray().CopyTo(header, 4);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x14), created);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x1C), updated);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x24), deleted);

        var payload = new byte[4 + bodyBytes.Length + 4
                             + 4 + table.Length + 4
                             + 4 + binary.Length + 4
                             + 4];
        int pos = 0;
        Seg(bodyBytes);
        Seg(table);
        Seg(binary);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(pos), Crc(payload.AsSpan(0, pos)));

        var container = new byte[HeaderBytes + payload.Length];
        header.CopyTo(container, 0);
        payload.CopyTo(container, HeaderBytes);
        return container;

        void Seg(byte[] data)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(pos), (uint)data.Length);
            pos += 4;
            data.CopyTo(payload, pos);
            pos += data.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(pos), Crc(data));
            pos += 4;
        }
    }

    /// <summary>One PRD-shaped entry-table item: 1B kind, 32B sha, 8B offset,
    /// 8B length, 4B name-byte-count, UTF-8 name.</summary>
    private static byte[] EntryBytes(byte kind, string shaHex, long offset, long length, string name)
    {
        var sha = Convert.FromHexString(shaHex);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var bytes = new byte[1 + 32 + 8 + 8 + 4 + nameBytes.Length];
        bytes[0] = kind;
        sha.CopyTo(bytes, 1);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(33), offset);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(41), length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(49), (uint)nameBytes.Length);
        nameBytes.CopyTo(bytes, 53);
        return bytes;
    }

    private static uint Crc(ReadOnlySpan<byte> data) => Crc32.HashToUInt32(data);
}
