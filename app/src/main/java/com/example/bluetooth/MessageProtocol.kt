package com.example.bluetooth

import com.example.data.model.MessageEntity
import org.json.JSONObject
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.util.UUID
import java.util.zip.CRC32

/**
 * MessageProtocol handles serialization and framing for cross-device transfer.
 *
 * Protocol v2 uses a binary length-prefixed frame (13-byte header + payload + CRC32)
 * so both text and binary file chunks share the same reliable framing. A plain-text
 * fallback path keeps backward compatibility with v1 raw-line messages.
 *
 * Frame layout (multi-byte fields big-endian):
 *   offset size field
 *   0      2    Magic 0x42 0x53 ("BS")
 *   2      1    Version 0x02
 *   3      1    Flags (reserved)
 *   4      1    Type (see TYPE_*)
 *   5      4    Seq  uint32
 *   9      4    Len  payload length uint32
 *   13     n    Payload
 *   end    4    CRC32 (over header + payload, CRC-32/ISO-HDLC)
 */
object MessageProtocol {
    const val PROTOCOL_VERSION = 2
    const val MAGIC0: Byte = 0x42
    const val MAGIC1: Byte = 0x53
    const val HEADER_SIZE = 13
    const val CRC_SIZE = 4

    const val TYPE_TEXT = "TEXT"
    const val TYPE_FILE = "FILE"
    const val TYPE_CLIPBOARD = "CLIPBOARD"

    // Frame types
    const val FT_TXT = 0x10
    const val FT_FILE_START = 0x11
    const val FT_FILE_CHUNK = 0x12
    const val FT_FILE_END = 0x13
    const val FT_FILE_ACK = 0x14
    const val FT_ERR = 0xF0

    // File chunk / pipeline parameters (both ends agree)
    const val DEFAULT_CHUNK_SIZE = 32 * 1024
    const val SEND_WINDOW = 16

    data class Packet(
        val version: Int = PROTOCOL_VERSION,
        val type: String = TYPE_TEXT,
        val id: String = UUID.randomUUID().toString(),
        val senderId: String,
        val senderName: String,
        val receiverId: String,
        val receiverName: String,
        val content: String,
        val timestamp: Long = System.currentTimeMillis()
    )

    data class Frame(
        val type: Int,
        val seq: Long,
        val payload: ByteArray
    )

    /**
     * Serializes packet to a v2 frame payload (TXT JSON) for streaming.
     */
    fun encode(packet: Packet): ByteArray {
        val json = JSONObject().apply {
            put("v", PROTOCOL_VERSION)
            put("type", packet.type)
            put("id", packet.id)
            put("sId", packet.senderId)
            put("sName", packet.senderName)
            put("rId", packet.receiverId)
            put("rName", packet.receiverName)
            put("content", packet.content)
            put("ts", packet.timestamp)
        }
        return json.toString().toByteArray(Charsets.UTF_8)
    }

    /**
     * Decodes incoming TXT payload. Falls back gracefully to plain text if sender sent raw string.
     */
    fun decode(rawString: String, fallbackSenderId: String, fallbackSenderName: String): Packet {
        val trimmed = rawString.trim()
        return try {
            if (trimmed.startsWith("{") && trimmed.endsWith("}")) {
                val json = JSONObject(trimmed)
                Packet(
                    version = json.optInt("v", PROTOCOL_VERSION),
                    type = json.optString("type", TYPE_TEXT),
                    id = json.optString("id", UUID.randomUUID().toString()),
                    senderId = json.optString("sId", fallbackSenderId),
                    senderName = json.optString("sName", fallbackSenderName),
                    receiverId = json.optString("rId", "local"),
                    receiverName = json.optString("rName", "This Device"),
                    content = json.optString("content", ""),
                    timestamp = json.optLong("ts", System.currentTimeMillis())
                )
            } else {
                // Fallback for plain text terminals or raw SPP connections
                Packet(
                    version = PROTOCOL_VERSION,
                    type = TYPE_TEXT,
                    id = UUID.randomUUID().toString(),
                    senderId = fallbackSenderId,
                    senderName = fallbackSenderName,
                    receiverId = "local",
                    receiverName = "This Device",
                    content = trimmed,
                    timestamp = System.currentTimeMillis()
                )
            }
        } catch (_: Exception) {
            Packet(
                version = PROTOCOL_VERSION,
                type = TYPE_TEXT,
                id = UUID.randomUUID().toString(),
                senderId = fallbackSenderId,
                senderName = fallbackSenderName,
                receiverId = "local",
                receiverName = "This Device",
                content = rawString,
                timestamp = System.currentTimeMillis()
            )
        }
    }

    /** Decodes a TXT frame payload into a Packet. */
    fun decodeTextPayload(payload: ByteArray, fallbackSenderId: String, fallbackSenderName: String): Packet {
        return decode(payload.toString(Charsets.UTF_8), fallbackSenderId, fallbackSenderName)
    }

    fun packetToEntity(packet: Packet, isOutgoing: Boolean): MessageEntity {
        return MessageEntity(
            id = packet.id,
            senderDeviceId = packet.senderId,
            senderDeviceName = packet.senderName,
            receiverDeviceId = packet.receiverId,
            receiverDeviceName = packet.receiverName,
            content = packet.content,
            createdAt = packet.timestamp,
            receivedAt = if (!isOutgoing) System.currentTimeMillis() else null,
            readAt = null,
            status = if (isOutgoing) "SENT" else "RECEIVED",
            isOutgoing = isOutgoing,
            messageType = packet.type
        )
    }
}

/**
 * FrameCodec turns [Frame]s into length-prefixed bytes on a stream and back.
 * All multi-byte integers are big-endian to be unambiguous across platforms.
 */
object FrameCodec {

    /** Wraps a [MessageProtocol.Frame] into a single frame byte array (header + payload + CRC32). */
    fun encode(frame: MessageProtocol.Frame): ByteArray {
        val payload = frame.payload
        val total = MessageProtocol.HEADER_SIZE + payload.size + MessageProtocol.CRC_SIZE
        val out = ByteArray(total)
        out[0] = MessageProtocol.MAGIC0
        out[1] = MessageProtocol.MAGIC1
        out[2] = MessageProtocol.PROTOCOL_VERSION.toByte()
        out[3] = 0 // flags reserved
        out[4] = frame.type.toByte()
        writeUInt32BE(out, 5, frame.seq)
        writeUInt32BE(out, 9, payload.size.toLong())
        payload.copyInto(out, MessageProtocol.HEADER_SIZE)
        val crc = CRC32()
        crc.update(out, 0, MessageProtocol.HEADER_SIZE + payload.size)
        writeUInt32BE(out, MessageProtocol.HEADER_SIZE + payload.size, crc.value)
        return out
    }

    /**
     * Reads exactly one frame from [input]. Blocks until the full frame is available.
     * @throws EOFException when the stream ends before a complete frame.
     * @throws IllegalArgumentException when magic/version/CRC mismatch.
     */
    @Throws(EOFException::class, IllegalArgumentException::class)
    fun decode(input: InputStream): MessageProtocol.Frame {
        val header = readFully(input, MessageProtocol.HEADER_SIZE)
        if (header[0] != MessageProtocol.MAGIC0 || header[1] != MessageProtocol.MAGIC1) {
            throw IllegalArgumentException("bad magic: ${header[0]} ${header[1]}")
        }
        val version = header[2].toInt() and 0xFF
        if (version != MessageProtocol.PROTOCOL_VERSION) {
            throw IllegalArgumentException("unsupported version: $version")
        }
        val type = header[4].toInt() and 0xFF
        val seq = readUInt32BE(header, 5)
        val len = readUInt32BE(header, 9).toInt()
        if (len < 0 || len > MAX_FRAME_PAYLOAD) {
            throw IllegalArgumentException("invalid payload length: $len")
        }
        val payload = readFully(input, len)
        val crcBytes = readFully(input, MessageProtocol.CRC_SIZE)
        val expected = readUInt32BE(crcBytes, 0)

        val crc = CRC32()
        crc.update(header, 0, MessageProtocol.HEADER_SIZE)
        crc.update(payload)
        if (crc.value != expected) {
            throw IllegalArgumentException("crc mismatch")
        }
        return MessageProtocol.Frame(type, seq, payload)
    }

    /** Writes a single encoded frame to [output]. */
    fun write(output: OutputStream, frame: MessageProtocol.Frame) {
        output.write(encode(frame))
        output.flush()
    }

    private const val MAX_FRAME_PAYLOAD = 64 * 1024 * 1024

    private fun readFully(input: InputStream, count: Int): ByteArray {
        val buf = ByteArray(count)
        var offset = 0
        while (offset < count) {
            val read = input.read(buf, offset, count - offset)
            if (read < 0) throw EOFException("stream ended before full frame")
            offset += read
        }
        return buf
    }

    private fun writeUInt32BE(buf: ByteArray, offset: Int, value: Long) {
        buf[offset] = ((value shr 24) and 0xFF).toByte()
        buf[offset + 1] = ((value shr 16) and 0xFF).toByte()
        buf[offset + 2] = ((value shr 8) and 0xFF).toByte()
        buf[offset + 3] = (value and 0xFF).toByte()
    }

    private fun readUInt32BE(buf: ByteArray, offset: Int): Long {
        return ((buf[offset].toLong() and 0xFF) shl 24) or
            ((buf[offset + 1].toLong() and 0xFF) shl 16) or
            ((buf[offset + 2].toLong() and 0xFF) shl 8) or
            (buf[offset + 3].toLong() and 0xFF)
    }
}

/** JSON helpers shared by file transfer metadata. */
object FileMetaJson {
    fun encodeStart(
        id: String, msgId: String, name: String, mime: String,
        size: Long, md5: String, chunkSize: Int, totalChunks: Long
    ): ByteArray {
        return JSONObject().apply {
            put("id", id)
            put("msgId", msgId)
            put("name", name)
            put("mime", mime)
            put("size", size)
            put("md5", md5)
            put("chunkSize", chunkSize)
            put("totalChunks", totalChunks)
        }.toString().toByteArray(Charsets.UTF_8)
    }

    fun encodeEnd(id: String, totalChunks: Long, md5: String): ByteArray {
        return JSONObject().apply {
            put("id", id)
            put("totalChunks", totalChunks)
            put("md5", md5)
        }.toString().toByteArray(Charsets.UTF_8)
    }

    fun encodeAck(id: String, ackedChunks: Long, ok: Boolean, md5Match: Boolean): ByteArray {
        return JSONObject().apply {
            put("id", id)
            put("ackedChunks", ackedChunks)
            put("ok", ok)
            put("md5Match", md5Match)
        }.toString().toByteArray(Charsets.UTF_8)
    }

    fun encodeError(id: String, code: Int, msg: String): ByteArray {
        return JSONObject().apply {
            put("id", id)
            put("code", code)
            put("msg", msg)
        }.toString().toByteArray(Charsets.UTF_8)
    }

    fun decodeStart(payload: ByteArray): FileStart {
        val j = JSONObject(payload.toString(Charsets.UTF_8))
        return FileStart(
            id = j.getString("id"),
            msgId = j.optString("msgId", ""),
            name = j.getString("name"),
            mime = j.optString("mime", "application/octet-stream"),
            size = j.optLong("size", 0),
            md5 = j.optString("md5", ""),
            chunkSize = j.optInt("chunkSize", MessageProtocol.DEFAULT_CHUNK_SIZE),
            totalChunks = j.optLong("totalChunks", 0)
        )
    }

    fun decodeEnd(payload: ByteArray): FileEnd {
        val j = JSONObject(payload.toString(Charsets.UTF_8))
        return FileEnd(
            id = j.getString("id"),
            totalChunks = j.optLong("totalChunks", 0),
            md5 = j.optString("md5", "")
        )
    }

    fun decodeAck(payload: ByteArray): FileAck {
        val j = JSONObject(payload.toString(Charsets.UTF_8))
        return FileAck(
            id = j.getString("id"),
            ackedChunks = j.optLong("ackedChunks", 0),
            ok = j.optBoolean("ok", false),
            md5Match = j.optBoolean("md5Match", false)
        )
    }
}

data class FileStart(
    val id: String,
    val msgId: String,
    val name: String,
    val mime: String,
    val size: Long,
    val md5: String,
    val chunkSize: Int,
    val totalChunks: Long
)

data class FileEnd(
    val id: String,
    val totalChunks: Long,
    val md5: String
)

data class FileAck(
    val id: String,
    val ackedChunks: Long,
    val ok: Boolean,
    val md5Match: Boolean
)
