package com.example.data.repository

import com.example.data.db.FileDao
import com.example.data.db.MessageDao
import com.example.data.model.FileEntity
import com.example.data.model.MessageEntity
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map

class MessageRepository(
    private val messageDao: MessageDao,
    private val fileDao: FileDao
) {
    val allMessages: Flow<List<MessageEntity>> = messageDao.getAllMessages()
    val inboxMessages: Flow<List<MessageEntity>> = messageDao.getInboxMessages()
    val unreadCount: Flow<Int> = messageDao.getUnreadInboxCount()

    fun getMessageById(id: String): Flow<MessageEntity?> = messageDao.getMessageById(id)

    suspend fun saveMessage(message: MessageEntity) {
        messageDao.insertMessage(message)
    }

    suspend fun markAsRead(id: String) {
        messageDao.markAsRead(id)
    }

    suspend fun updateMessageRow(id: String, status: String) {
        messageDao.updateStatus(id, status)
    }

    suspend fun markAllAsRead() {
        messageDao.markAllAsRead()
    }

    suspend fun deleteMessage(id: String) {
        messageDao.deleteMessageById(id)
        fileDao.deleteFilesForMessage(id)
    }

    suspend fun clearAll() {
        messageDao.clearAllMessages()
        fileDao.clearAllFiles()
    }

    // ---- File attachments ----

    fun filesForMessage(messageId: String): Flow<List<FileEntity>> = fileDao.filesForMessage(messageId)

    /** Maps messageId → attachment count, for inbox preview indicators. */
    fun fileCounts(): Flow<Map<String, Int>> =
        fileDao.fileCounts().map { list -> list.associate { it.messageId to it.fileCount } }

    suspend fun getFilesForMessageOnce(messageId: String): List<FileEntity> =
        fileDao.getFilesForMessageOnce(messageId)

    suspend fun saveFile(file: FileEntity) {
        fileDao.insertFile(file)
    }

    suspend fun updateFile(file: FileEntity) {
        fileDao.updateFile(file)
    }

    suspend fun getFileById(id: String): FileEntity? = fileDao.getFileById(id)
}
