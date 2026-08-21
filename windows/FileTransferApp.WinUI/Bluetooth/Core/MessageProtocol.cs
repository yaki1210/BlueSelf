using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;

namespace FileTransferApp.WinUI.Bluetooth.Core;

/// <summary>
/// v2 frame constants and helpers, byte-for-byte aligned with the Android MessageProtocol.
/// Frame layout (multi-byte fields big-endian):
///   0       2   Magic 0x42 0x53 ("BS")
///   2       1   Version 0x02
///   3       1   Flags (reserved)
///   4       1   Type (see FT_*)
///   5       4   Seq   uint32
///   9       4   Len   payload length uint32
///   13      n   Payload
///   end     4   CRC32 (over header + payload, CRC-32/ISO-HDLC)
/// </summary>
internal static class MessageProtocol
{
    public const int ProtocolVersion = 2;
    public const byte Magic0 = 0x42;
    public const byte Magic1 = 0x53;
    public const int HeaderSize = 13;
    public const int CrcSize = 4;

    public const byte TypeTxt = 0x10;
    public const byte TypeFileStart = 0x11;
    public const byte TypeFileChunk = 0x12;
    public const byte TypeFileEnd = 0x13;
    public const byte TypeFileAck = 0x14;
    public const byte TypeErr = 0xF0;

    public const int DefaultChunkSize = 64 * 1024;
    public const int SendWindow = 16;
    public const int MaxFramePayload = 64 * 1024 * 1024;

    public static readonly Guid AppServiceId = new("fa87c0d0-afac-11de-8a39-0800200c9a66");
}

/// <summary>A decoded inbound/outbound frame.</summary>
internal sealed record Frame(byte Type, long Seq, byte[] Payload);

/// <summary>Serializes frames to/from wire bytes (13B header + payload + CRC32).</summary>
internal static class FrameCodec
{
    /// <summary>Wraps one frame into a single byte array (header + payload + CRC32).</summary>
    public static byte[] Encode(Frame frame)
    {
        var payload = frame.Payload;
        var out_ = new byte[MessageProtocol.HeaderSize + payload.Length + MessageProtocol.CrcSize];
        FillHeader(out_, frame, payload);
        return out_;
    }

    /// <summary>Encodes multiple frames into a single array for one batched write.</summary>
    public static byte[] EncodeBatch(IReadOnlyList<Frame> frames)
    {
        if (frames.Count == 0) return Array.Empty<byte>();
        var total = 0;
        foreach (var f in frames) total += MessageProtocol.HeaderSize + f.Payload.Length + MessageProtocol.CrcSize;
        var out_ = new byte[total];
        var offset = 0;
        foreach (var f in frames)
        {
            var part = Encode(f);
            Array.Copy(part, 0, out_, offset, part.Length);
            offset += part.Length;
        }
        return out_;
    }

    /// <summary>Reads exactly one frame from the stream. Throws on EOF / magic / version / CRC mismatch.</summary>
    public static async Task<Frame> DecodeAsync(Windows.Storage.Streams.DataReader reader, CancellationToken ct = default)
    {
        var header = await ReadExactlyAsync(reader, MessageProtocol.HeaderSize, ct).ConfigureAwait(false);
        if (header[0] != MessageProtocol.Magic0 || header[1] != MessageProtocol.Magic1)
            throw new InvalidDataException($"bad magic: {header[0]} {header[1]}");
        if (header[2] != MessageProtocol.ProtocolVersion)
            throw new InvalidDataException($"unsupported version: {header[2]}");
        var type = header[4];
        var seq = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5, 4));
        var len = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(9, 4));
        if (len < 0 || len > MessageProtocol.MaxFramePayload)
            throw new InvalidDataException($"invalid payload length: {len}");

        var payload = await ReadExactlyAsync(reader, len, ct).ConfigureAwait(false);
        var crcBytes = await ReadExactlyAsync(reader, MessageProtocol.CrcSize, ct).ConfigureAwait(false);
        var expected = BinaryPrimitives.ReadUInt32BigEndian(crcBytes);

        var crc = new Crc32();
        crc.Append(header);
        crc.Append(payload);
        if (crc.GetCurrentHashAsUInt32() != expected)
            throw new InvalidDataException("crc mismatch");

        return new Frame(type, seq, payload);
    }

    private static void FillHeader(byte[] out_, Frame frame, byte[] payload)
    {
        out_[0] = MessageProtocol.Magic0;
        out_[1] = MessageProtocol.Magic1;
        out_[2] = MessageProtocol.ProtocolVersion;
        out_[3] = 0; // flags reserved
        out_[4] = frame.Type;
        BinaryPrimitives.WriteUInt32BigEndian(out_.AsSpan(5, 4), (uint)frame.Seq);
        BinaryPrimitives.WriteUInt32BigEndian(out_.AsSpan(9, 4), (uint)payload.Length);
        Array.Copy(payload, 0, out_, MessageProtocol.HeaderSize, payload.Length);
        var crc = new Crc32();
        crc.Append(out_.AsSpan(0, MessageProtocol.HeaderSize + payload.Length));
        BinaryPrimitives.WriteUInt32BigEndian(out_.AsSpan(MessageProtocol.HeaderSize + payload.Length, 4), crc.GetCurrentHashAsUInt32());
    }

    private static async Task<byte[]> ReadExactlyAsync(Windows.Storage.Streams.DataReader reader, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var need = (uint)(count - offset);
            var loaded = await reader.LoadAsync(need);
            if (loaded == 0) throw new EndOfStreamException("stream ended before full frame");
            var take = (int)Math.Min(loaded, reader.UnconsumedBufferLength);
            var chunk = new byte[take];
            reader.ReadBytes(chunk);
            Buffer.BlockCopy(chunk, 0, buf, offset, take);
            offset += take;
        }
        return buf;
    }
}

/// <summary>Text message packet aligned with Android MessageProtocol.Packet.</summary>
internal sealed record Packet(
    int Version,
    string Type,
    string Id,
    string SenderId,
    string SenderName,
    string ReceiverId,
    string ReceiverName,
    string Content,
    long Timestamp);

/// <summary>File transfer metadata / control payloads (JSON fields aligned with Android).</summary>
internal static class FileMetaJson
{
    public static byte[] EncodeText(Packet p) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
    {
        ["v"] = p.Version, ["type"] = p.Type, ["id"] = p.Id,
        ["sId"] = p.SenderId, ["sName"] = p.SenderName,
        ["rId"] = p.ReceiverId, ["rName"] = p.ReceiverName,
        ["content"] = p.Content, ["ts"] = p.Timestamp
    });

    public static Packet DecodeText(byte[] payload, string fallbackSenderId, string fallbackSenderName)
    {
        var text = Encoding.UTF8.GetString(payload);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            return new Packet(
                MessageProtocol.ProtocolVersion,
                Get(root, "type", "TEXT"),
                Get(root, "id", Guid.NewGuid().ToString()),
                Get(root, "sId", fallbackSenderId),
                Get(root, "sName", fallbackSenderName),
                Get(root, "rId", "local"),
                Get(root, "rName", "This Device"),
                Get(root, "content", ""),
                root.TryGetProperty("ts", out var ts) ? ts.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch (JsonException)
        {
            return new Packet(MessageProtocol.ProtocolVersion, "TEXT", Guid.NewGuid().ToString(),
                fallbackSenderId, fallbackSenderName, "local", "This Device", text,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    public static byte[] EncodeStart(string id, string msgId, string name, string mime, long size, string md5, int chunkSize, long totalChunks)
        => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["id"] = id, ["msgId"] = msgId, ["name"] = name, ["mime"] = mime,
            ["size"] = size, ["md5"] = md5, ["chunkSize"] = chunkSize, ["totalChunks"] = totalChunks
        });

    public static FileStart DecodeStart(byte[] payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var r = doc.RootElement;
        return new FileStart(
            Id: r.GetProperty("id").GetString()!,
            MsgId: Get(r, "msgId", ""),
            Name: r.GetProperty("name").GetString()!,
            Mime: Get(r, "mime", "application/octet-stream"),
            Size: GetLong(r, "size", 0),
            Md5: Get(r, "md5", ""),
            ChunkSize: (int)GetLong(r, "chunkSize", MessageProtocol.DefaultChunkSize),
            TotalChunks: GetLong(r, "totalChunks", 0));
    }

    public static byte[] EncodeEnd(string id, long totalChunks, string md5)
        => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["id"] = id, ["totalChunks"] = totalChunks, ["md5"] = md5
        });

    public static FileEnd DecodeEnd(byte[] payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var r = doc.RootElement;
        return new FileEnd(r.GetProperty("id").GetString()!, GetLong(r, "totalChunks", 0), Get(r, "md5", ""));
    }

    public static byte[] EncodeAck(string id, long ackedChunks, bool ok, bool md5Match)
        => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["id"] = id, ["ackedChunks"] = ackedChunks, ["ok"] = ok, ["md5Match"] = md5Match
        });

    public static FileAck DecodeAck(byte[] payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var r = doc.RootElement;
        return new FileAck(r.GetProperty("id").GetString()!, GetLong(r, "ackedChunks", 0), GetBool(r, "ok"), GetBool(r, "md5Match"));
    }

    public static byte[] EncodeError(string id, int code, string msg)
        => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["id"] = id, ["code"] = code, ["msg"] = msg });

    public static void DecodeError(byte[] payload, out int code, out string msg)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var r = doc.RootElement;
            code = (int)GetLong(r, "code", 0);
            msg = Get(r, "msg", "unknown error");
        }
        catch (JsonException)
        {
            code = 0;
            msg = "unknown error";
        }
    }

    private static string Get(JsonElement e, string key, string dflt)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? dflt : dflt;

    private static long GetLong(JsonElement e, string key, long dflt)
        => e.TryGetProperty(key, out var v) && v.TryGetInt64(out var x) ? x : dflt;

    private static bool GetBool(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>Decoded FILE_START metadata.</summary>
internal sealed record FileStart(string Id, string MsgId, string Name, string Mime, long Size, string Md5, int ChunkSize, long TotalChunks);

/// <summary>Decoded FILE_END metadata.</summary>
internal sealed record FileEnd(string Id, long TotalChunks, string Md5);

/// <summary>Decoded FILE_ACK metadata.</summary>
internal sealed record FileAck(string Id, long AckedChunks, bool Ok, bool Md5Match);