package com.example.data.repository

import com.example.data.db.MessageDao
import com.example.data.model.MessageEntity
import kotlinx.coroutines.flow.Flow

class MessageRepository(private val messageDao: MessageDao) {
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

    suspend fun markAllAsRead() {
        messageDao.markAllAsRead()
    }

    suspend fun deleteMessage(id: String) {
        messageDao.deleteMessageById(id)
    }

    suspend fun clearAll() {
        messageDao.clearAllMessages()
    }
}
