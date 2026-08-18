package com.example.bluetooth

import com.example.data.model.MessageEntity
import org.json.JSONObject
import java.util.UUID

/**
 * MessageProtocol handles serialization and framing for cross-device transfer.
 * Extensible for future binary/file chunks, clipboard events, and cloud sync.
 */
object MessageProtocol {
    private const val PROTOCOL_VERSION = 1
    const val TYPE_TEXT = "TEXT"
    const val TYPE_FILE = "FILE"
    const val TYPE_CLIPBOARD = "CLIPBOARD"

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

    /**
     * Serializes packet to JSON line terminated with newline for streaming.
     */
    fun encode(packet: Packet): String {
        val json = JSONObject().apply {
            put("v", packet.version)
            put("type", packet.type)
            put("id", packet.id)
            put("sId", packet.senderId)
            put("sName", packet.senderName)
            put("rId", packet.receiverId)
            put("rName", packet.receiverName)
            put("content", packet.content)
            put("ts", packet.timestamp)
        }
        return json.toString() + "\n"
    }

    /**
     * Decodes incoming line. Falls back gracefully to plain text if sender sent raw string.
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
