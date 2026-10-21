using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using QNote.Infrastructure;

namespace QNote.Sync;

/// <summary>
/// Encoder/decoder for the self-designed .qnote binary container (PRD decision
/// 10). Pure byte-level — no file or network I/O; drivers feed it whole buffers.
/// Layout: 48-byte plaintext header | (encrypted: 12B nonce) DEFLATE(payload)
/// (16B GCM tag). The payload is three CRC32-stamped segments — body (UTF-8
/// markdown), entry table, binary region — plus an overall CRC32 over everything
/// before it, DEFLATEd, then optionally AES-256-GCM'd with the AAD bound to the
/// full 48-byte header (user ruling 2026-10-21, extending PRD decision 10's
/// uuid+updated: ANY header tamper — created/deleted/encoding included — breaks
/// authentication). The whole payload is assembled in memory by design
/// (KB..25MB) and never streamed.
/// </summary>
public static class QnoteContainerCodec
{
    public const int HeaderByteCount = 48;
    public const ushort CurrentVersion = 1;

    private const int UuidOffset = 0x04;
    private const int CreatedOffset = 0x14;
    private const int UpdatedOffset = 0x1C;
    private const int DeletedOffset = 0x24;
    private const int EncodingOffset = 0x2C;
    private const int FlagsOffset = 0x2D;

    private const byte MagicByte0 = (byte)'Q';
    private const byte MagicByte1 = (byte)'N';

    private const byte EncodingNone = 0;
    private const byte EncodingDeflate = 1;
    private const byte EncodingDeflateGcm = 2;

    private const byte FlagsEncrypted = 0b0000_0001;
    private const byte FlagsReservedMask = 0b1111_1110;

    private const int NonceByteCount = 12;
    private const int TagByteCount = 16;
    private const int ShaByteCount = 32;
    private const int KeyByteCount = 32;
    private const int FixedEntryPrefixBytes = 1 + ShaByteCount + 8 + 8 + 4;

    /// <summary>
    /// Encodes a container. The payload always goes through DEFLATE (encoding 1);
    /// a 32-byte key upgrades it to encoding 2 with a fresh random nonce per call.
    /// Validates that every entry's <see cref="QnoteEntry.Sha256"/> really is the
    /// digest of its <see cref="QnoteEntry.Data"/> — content addressing is a
    /// format invariant, not a courtesy.
    /// </summary>
    public static byte[] Encode(
        QnoteHeader header,
        string body,
        IReadOnlyList<QnoteEntry> entries,
        byte[]? key = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(entries);
        ValidateKey(key);

        var headerBytes = WriteHeader(header, key is null ? EncodingDeflate : EncodingDeflateGcm);
        var deflated = Deflate(BuildPayload(body, entries));

        if (key is null)
        {
            var container = new byte[HeaderByteCount + deflated.Length];
            headerBytes.CopyTo(container, 0);
            deflated.CopyTo(container, HeaderByteCount);
            return container;
        }

        var nonce = new byte[NonceByteCount];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[deflated.Length];
        var tag = new byte[TagByteCount];
        using (var gcm = new AesGcm(key, TagByteCount))
            gcm.Encrypt(nonce, deflated, ciphertext, tag, headerBytes.AsSpan(0, HeaderByteCount));

        var encrypted = new byte[HeaderByteCount + NonceByteCount + ciphertext.Length + TagByteCount];
        headerBytes.CopyTo(encrypted, 0);
        nonce.CopyTo(encrypted, HeaderByteCount);
        ciphertext.CopyTo(encrypted, HeaderByteCount + NonceByteCount);
        tag.CopyTo(encrypted, encrypted.Length - TagByteCount);
        return encrypted;
    }

    /// <summary>
    /// Parses only the 48-byte header — the LWW probe a driver serves from a
    /// 48-byte Range fetch. Validates magic, version, and the encoding/flag
    /// domain; never touches (or needs) the payload or a key.
    /// </summary>
    public static QnoteHeader ReadHeader(ReadOnlySpan<byte> container)
    {
        ValidateHeaderBytes(container);
        return ReadHeaderCore(container);
    }

    /// <summary>
    /// Decodes and fully validates a container: header domain, GCM authentication
    /// (when encrypted), DEFLATE, per-segment CRCs, the overall CRC, and every
    /// entry's content address. Encoding 0 (raw payload, no DEFLATE) is part of
    /// the v1 format and is read, though this build never writes it.
    /// </summary>
    public static QnotePayload Decode(ReadOnlySpan<byte> container, byte[]? key = null)
    {
        ValidateKey(key);
        ValidateHeaderBytes(container);
        var header = ReadHeaderCore(container);
        byte encoding = container[EncodingOffset];

        if (encoding == EncodingDeflateGcm && key is null)
            throw new QnoteContainerException(
                QnoteContainerErrorKind.KeyRequired,
                "The container is end-to-end encrypted but no key was supplied.");
        if (encoding != EncodingDeflateGcm && key is not null)
            throw new ArgumentException("A key was supplied but the container is not encrypted.", nameof(key));

        var deflated = encoding switch
        {
            EncodingNone => container.Slice(HeaderByteCount),
            EncodingDeflate => container.Slice(HeaderByteCount),
            _ => Decrypt(container, key!),
        };
        var payload = encoding == EncodingNone ? deflated.ToArray() : Inflate(deflated);
        return ParsePayload(payload, header);
    }

    // ---------- header ----------

    private static QnoteHeader ReadHeaderCore(ReadOnlySpan<byte> container) => new()
    {
        Uuid = new Guid(container.Slice(UuidOffset, ShaByteCount / 2)),
        CreatedAtMs = BinaryPrimitives.ReadInt64LittleEndian(container.Slice(CreatedOffset, 8)),
        UpdatedAtMs = BinaryPrimitives.ReadInt64LittleEndian(container.Slice(UpdatedOffset, 8)),
        DeletedAtMs = BinaryPrimitives.ReadInt64LittleEndian(container.Slice(DeletedOffset, 8)),
    };

    private static byte[] WriteHeader(QnoteHeader header, byte encoding)
    {
        var bytes = new byte[HeaderByteCount];
        bytes[0] = MagicByte0;
        bytes[1] = MagicByte1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), CurrentVersion);
        header.Uuid.TryWriteBytes(bytes.AsSpan(UuidOffset));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(CreatedOffset), header.CreatedAtMs);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(UpdatedOffset), header.UpdatedAtMs);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(DeletedOffset), header.DeletedAtMs);
        bytes[EncodingOffset] = encoding;
        bytes[FlagsOffset] = encoding == EncodingDeflateGcm ? FlagsEncrypted : (byte)0;
        return bytes;
    }

    private static void ValidateHeaderBytes(ReadOnlySpan<byte> container)
    {
        if (container.Length < HeaderByteCount)
            throw new QnoteContainerException(
                QnoteContainerErrorKind.BadHeader,
                $"The container is {container.Length} bytes; the fixed header alone is {HeaderByteCount}.");
        if (container[0] != MagicByte0 || container[1] != MagicByte1)
            throw new QnoteContainerException(
                QnoteContainerErrorKind.BadMagic,
                $"Not a .qnote container: expected the 'QN' magic, got 0x{container[0]:X2} 0x{container[1]:X2}.");

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(container.Slice(2, 2));
        if (version != CurrentVersion)
            throw new QnoteContainerException(
                QnoteContainerErrorKind.UnsupportedVersion,
                $"Container format version {version} is newer than this build understands (v{CurrentVersion}).");

        byte encoding = container[EncodingOffset];
        if (encoding is not (EncodingNone or EncodingDeflate or EncodingDeflateGcm))
            throw new QnoteContainerException(
                QnoteContainerErrorKind.BadHeader,
                $"Unknown content encoding {encoding} (expected 0=raw, 1=deflate, 2=deflate+AES-256-GCM).");

        byte flags = container[FlagsOffset];
        if ((flags & FlagsReservedMask) != 0)
            throw new QnoteContainerException(
                QnoteContainerErrorKind.BadHeader,
                $"Reserved flag bits are set (0x{flags:X2}).");
        if (((flags & FlagsEncrypted) != 0) != (encoding == EncodingDeflateGcm))
            throw new QnoteContainerException(
                QnoteContainerErrorKind.BadHeader,
                "The encryption flag disagrees with the content encoding byte.");
    }

    // ---------- payload assembly ----------

    private static byte[] BuildPayload(string body, IReadOnlyList<QnoteEntry> entries)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var tableBytes = BuildEntryTable(entries);

        long binaryLength = 0;
        foreach (var entry in entries)
            binaryLength += entry.Data.Length;
        if (binaryLength > int.MaxValue)
            throw new ArgumentException(
                $"The binary region would be {binaryLength} bytes; a container holds at most {int.MaxValue}.",
                nameof(entries));

        var payload = new byte[checked(
            4 + bodyBytes.Length + 4
            + 4 + tableBytes.Length + 4
            + 4 + (int)binaryLength + 4
            + 4)];
        int pos = 0;
        WriteSegment(payload, ref pos, bodyBytes);
        WriteSegment(payload, ref pos, tableBytes);

        int binaryStart = pos + 4;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(pos), (uint)(int)binaryLength);
        pos += 4;
        foreach (var entry in entries)
        {
            entry.Data.AsSpan().CopyTo(payload.AsSpan(pos));
            pos += entry.Data.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(pos), Crc32Of(payload.AsSpan(binaryStart, (int)binaryLength)));
        pos += 4;

        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(pos), Crc32Of(payload.AsSpan(0, pos)));
        return payload;
    }

    private static void WriteSegment(byte[] payload, ref int pos, ReadOnlySpan<byte> data)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(pos), (uint)data.Length);
        pos += 4;
        data.CopyTo(payload.AsSpan(pos));
        pos += data.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(pos), Crc32Of(data));
        pos += 4;
    }

    private static byte[] BuildEntryTable(IReadOnlyList<QnoteEntry> entries)
    {
        var nameByteCounts = new int[entries.Count];
        int total = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            ValidateEntry(entries[i], i, out string fileName);
            nameByteCounts[i] = Encoding.UTF8.GetByteCount(fileName);
            total += FixedEntryPrefixBytes + nameByteCounts[i];
        }

        var table = new byte[total];
        int pos = 0;
        long offset = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            table[pos++] = (byte)entry.Kind;
            Convert.FromHexString(entry.Sha256).CopyTo(table, pos);
            pos += ShaByteCount;
            BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(pos), offset);
            pos += 8;
            BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(pos), entry.Data.Length);
            pos += 8;
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(pos), (uint)nameByteCounts[i]);
            pos += 4;
            Encoding.UTF8.GetBytes(entry.FileName ?? string.Empty, table.AsSpan(pos));
            pos += nameByteCounts[i];
            offset += entry.Data.Length;
        }
        return table;
    }

    private static void ValidateEntry(QnoteEntry entry, int index, out string fileName)
    {
        if (entry.Kind is not (QnoteEntryKind.Image or QnoteEntryKind.Attachment))
            throw new ArgumentException($"Entry {index}: unknown kind {entry.Kind}.", nameof(entry));
        if (!HexDigest.IsSha256Hex(entry.Sha256))
            throw new ArgumentException(
                $"Entry {index}: sha256 must be {HexDigest.Sha256CharCount} hex characters (got '{entry.Sha256}').",
                nameof(entry));
        var actualSha = Convert.ToHexStringLower(SHA256.HashData(entry.Data));
        if (!string.Equals(actualSha, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Entry {index}: sha256 '{entry.Sha256}' does not match the data digest '{actualSha}'.",
                nameof(entry));
        fileName = entry.FileName ?? string.Empty;
    }

    // ---------- payload parsing ----------

    private static QnotePayload ParsePayload(byte[] payload, QnoteHeader header)
    {
        int pos = 0;
        var bodyBytes = ReadSegment(payload, ref pos, "body");
        var tableBytes = ReadSegment(payload, ref pos, "entry table");
        var binaryBytes = ReadSegment(payload, ref pos, "binary region");

        if (pos + 4 > payload.Length)
            throw new QnoteContainerException(
                QnoteContainerErrorKind.BadPayload,
                "The payload ends before the overall CRC32.");
        uint storedOverall = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(pos));
        uint actualOverall = Crc32Of(payload.AsSpan(0, pos));
        if (actualOverall != storedOverall)
            throw new QnoteContainerException(
                QnoteContainerErrorKind.CrcMismatch,
                $"The overall CRC32 does not match (stored 0x{storedOverall:X8}, computed 0x{actualOverall:X8}).");
        pos += 4;
        if (pos != payload.Length)
            throw new QnoteContainerException(
                QnoteContainerErrorKind.BadPayload,
                $"{payload.Length - pos} trailing bytes follow the overall CRC32.");

        var entries = ParseEntryTable(tableBytes, binaryBytes);
        return new QnotePayload
        {
            Header = header,
            Body = Encoding.UTF8.GetString(bodyBytes),
            Entries = entries,
        };
    }

    private static byte[] ReadSegment(byte[] payload, ref int pos, string name)
    {
        if (pos + 4 > payload.Length)
            throw new QnoteContainerException(
                QnoteContainerErrorKind.BadPayload,
                $"The payload ends before the {name} segment's length.");
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(pos));
        pos += 4;
        if (length > (uint)(payload.Length - pos))
            throw new QnoteContainerException(
                QnoteContainerErrorKind.BadPayload,
                $"The {name} segment declares {length} bytes but only {payload.Length - pos} remain.");
        var data = payload.AsSpan(pos, (int)length).ToArray();
        pos += (int)length;

        if (pos + 4 > payload.Length)
            throw new QnoteContainerException(
                QnoteContainerErrorKind.BadPayload,
                $"The payload ends before the {name} segment's CRC32.");
        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(pos));
        pos += 4;
        uint actual = Crc32Of(data);
        if (actual != stored)
            throw new QnoteContainerException(
                QnoteContainerErrorKind.CrcMismatch,
                $"The {name} segment failed its CRC32 check (stored 0x{stored:X8}, computed 0x{actual:X8}).");
        return data;
    }

    private static IReadOnlyList<QnoteEntry> ParseEntryTable(byte[] table, byte[] binary)
    {
        var entries = new List<QnoteEntry>();
        int pos = 0;
        long expectedOffset = 0;
        while (pos < table.Length)
        {
            if (table.Length - pos < FixedEntryPrefixBytes)
                throw new QnoteContainerException(
                    QnoteContainerErrorKind.BadEntryTable,
                    $"The entry table ends inside an entry ({table.Length - pos} bytes left, need {FixedEntryPrefixBytes}).");

            QnoteEntryKind kind = table[pos] switch
            {
                (byte)QnoteEntryKind.Image => QnoteEntryKind.Image,
                (byte)QnoteEntryKind.Attachment => QnoteEntryKind.Attachment,
                var raw => throw new QnoteContainerException(
                    QnoteContainerErrorKind.BadEntryTable,
                    $"Unknown entry kind {raw} at byte {pos} of the entry table."),
            };
            pos += 1;

            var sha = Convert.ToHexStringLower(table.AsSpan(pos, ShaByteCount));
            pos += ShaByteCount;
            long offset = BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(pos));
            pos += 8;
            long length = BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(pos));
            pos += 8;
            uint nameLength = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(pos));
            pos += 4;
            if (nameLength > (uint)(table.Length - pos))
                throw new QnoteContainerException(
                    QnoteContainerErrorKind.BadEntryTable,
                    $"Entry '{sha}' declares a {nameLength}-byte name but only {table.Length - pos} bytes remain.");
            var fileName = Encoding.UTF8.GetString(table.AsSpan(pos, (int)nameLength));
            pos += (int)nameLength;

            if (offset < 0 || length < 0 || length > binary.Length - offset)
                throw new QnoteContainerException(
                    QnoteContainerErrorKind.BadEntryTable,
                    $"Entry '{sha}' references binary bytes [{offset}, {offset + length}) outside the {binary.Length}-byte region.");
            if (offset != expectedOffset)
                throw new QnoteContainerException(
                    QnoteContainerErrorKind.BadEntryTable,
                    $"Entry '{sha}' starts at binary offset {offset}; entries must tile the region (expected {expectedOffset}).");
            expectedOffset += length;

            var data = new byte[length];
            binary.AsSpan((int)offset, (int)length).CopyTo(data);
            var actualSha = Convert.ToHexStringLower(SHA256.HashData(data));
            if (!string.Equals(actualSha, sha, StringComparison.Ordinal))
                throw new QnoteContainerException(
                    QnoteContainerErrorKind.BadEntryTable,
                    $"Entry '{sha}' does not match its content digest '{actualSha}'.");

            entries.Add(new QnoteEntry
            {
                Kind = kind,
                Sha256 = sha,
                FileName = fileName,
                Data = data,
            });
        }

        if (expectedOffset != binary.Length)
            throw new QnoteContainerException(
                QnoteContainerErrorKind.BadEntryTable,
                $"The binary region has {binary.Length} bytes but its entries cover only {expectedOffset}.");
        return entries;
    }

    // ---------- transforms ----------

    private static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var target = new MemoryStream();
        using (var deflate = new DeflateStream(target, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data);
        return target.ToArray();
    }

    private static byte[] Inflate(ReadOnlySpan<byte> deflated)
    {
        try
        {
            using var source = new MemoryStream(deflated.ToArray());
            using var target = new MemoryStream();
            using (var deflate = new DeflateStream(source, CompressionMode.Decompress))
                deflate.CopyTo(target);
            return target.ToArray();
        }
        catch (InvalidDataException ex)
        {
            throw new QnoteContainerException(
                QnoteContainerErrorKind.BadPayload,
                "The payload is not a valid DEFLATE stream.",
                ex);
        }
    }

    /// <summary>
    /// Decrypts <c>nonce | ciphertext | tag</c> right after the header. The AAD is
    /// the full 48-byte plaintext header as-read (user ruling 2026-10-21,
    /// extending PRD decision 10's uuid+updated): the header never needs
    /// decryption, and every byte of it — created/deleted/encoding included — is
    /// authenticated, so a tampered header fails loudly instead of steering LWW
    /// or faking a tombstone.
    /// </summary>
    private static byte[] Decrypt(ReadOnlySpan<byte> container, byte[] key)
    {
        if (container.Length < HeaderByteCount + NonceByteCount + TagByteCount)
            throw new QnoteContainerException(
                QnoteContainerErrorKind.BadHeader,
                $"An encrypted container is at least {HeaderByteCount + NonceByteCount + TagByteCount} bytes; got {container.Length}.");

        var nonce = container.Slice(HeaderByteCount, NonceByteCount);
        var tag = container.Slice(container.Length - TagByteCount);
        var ciphertext = container.Slice(
            HeaderByteCount + NonceByteCount,
            container.Length - TagByteCount - HeaderByteCount - NonceByteCount);

        try
        {
            using var gcm = new AesGcm(key, TagByteCount);
            var plaintext = new byte[ciphertext.Length];
            gcm.Decrypt(nonce, ciphertext, tag, plaintext, container.Slice(0, HeaderByteCount));
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            throw new QnoteContainerException(
                QnoteContainerErrorKind.DecryptionFailed,
                "AES-GCM authentication failed: the key is wrong or the container/header was tampered with.",
                ex);
        }
    }

    private static void ValidateKey(byte[]? key)
    {
        if (key is { Length: not KeyByteCount })
            throw new ArgumentException(
                $"The E2EE key must be {KeyByteCount} bytes, got {key.Length}.", nameof(key));
    }

    private static uint Crc32Of(ReadOnlySpan<byte> data) => Crc32.HashToUInt32(data);
}
