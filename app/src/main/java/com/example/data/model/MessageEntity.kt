package com.example.data.model

import androidx.room.Entity
import androidx.room.PrimaryKey

/**
 * Represents a text message sent or received via Bluetooth.
 * Pre-configured with extensible properties for future file/clipboard/cloud sync.
 */
@Entity(tableName = "messages")
data class MessageEntity(
    @PrimaryKey val id: String,
    val senderDeviceId: String,
    val senderDeviceName: String,
    val receiverDeviceId: String,
    val receiverDeviceName: String,
    val content: String,
    val createdAt: Long = System.currentTimeMillis(),
    val receivedAt: Long? = null,
    val readAt: Long? = null,
    val status: String = "RECEIVED", // "SENT", "DELIVERED", "RECEIVED", "FAILED"
    val isOutgoing: Boolean = false,
    val messageType: String = "TEXT" // "TEXT", "FILE", "IMAGE", "CLIPBOARD"
)
