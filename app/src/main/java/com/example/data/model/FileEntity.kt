package com.example.data.model

import androidx.room.Entity
import androidx.room.ForeignKey
import androidx.room.Index
import androidx.room.PrimaryKey

/**
 * A file attachment belonging to a message.
 *
 * Status values:
 *  - RECEIVING  inbound, chunks still arriving (staging file exists)
 *  - COMPLETE   inbound, fully received and MD5-verified, staged but not yet saved by the user
 *  - SAVED      inbound, moved/copied to the public Downloads location
 *  - SENT       outbound, transfer finished successfully
 *  - FAILED     transfer failed (send or receive)
 */
@Entity(
    tableName = "files",
    foreignKeys = [
        ForeignKey(
            entity = MessageEntity::class,
            parentColumns = ["id"],
            childColumns = ["messageId"],
            onDelete = ForeignKey.CASCADE
        )
    ],
    indices = [Index("messageId")]
)
data class FileEntity(
    @PrimaryKey val id: String,
    val messageId: String,
    val fileName: String,
    val mimeType: String,
    val fileSize: Long,
    val md5: String = "",
    val totalChunks: Long = 0,
    val chunkSize: Int = 0,
    val receivedBytes: Long = 0,
    val stagingPath: String = "",
    val status: String = "RECEIVING", // RECEIVING / COMPLETE / SAVED / SENT / FAILED
    val isOutgoing: Boolean = false,
    val sortOrder: Int = 0,
    val createdAt: Long = System.currentTimeMillis()
)
