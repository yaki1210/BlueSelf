package com.example.data.db

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Update
import com.example.data.model.MessageEntity
import kotlinx.coroutines.flow.Flow

@Dao
interface MessageDao {
    @Query("SELECT * FROM messages ORDER BY createdAt DESC")
    fun getAllMessages(): Flow<List<MessageEntity>>

    @Query("SELECT * FROM messages ORDER BY createdAt DESC")
    fun getInboxMessages(): Flow<List<MessageEntity>>

    @Query("SELECT COUNT(*) FROM messages WHERE isOutgoing = 0 AND readAt IS NULL")
    fun getUnreadInboxCount(): Flow<Int>

    @Query("SELECT * FROM messages WHERE id = :id LIMIT 1")
    fun getMessageById(id: String): Flow<MessageEntity?>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun insertMessage(message: MessageEntity)

    @Update
    suspend fun updateMessage(message: MessageEntity)

    @Query("UPDATE messages SET readAt = :readAt WHERE id = :id")
    suspend fun markAsRead(id: String, readAt: Long = System.currentTimeMillis())

    @Query("UPDATE messages SET readAt = :readAt WHERE isOutgoing = 0 AND readAt IS NULL")
    suspend fun markAllAsRead(readAt: Long = System.currentTimeMillis())

    @Query("DELETE FROM messages WHERE id = :id")
    suspend fun deleteMessageById(id: String)

    @Query("DELETE FROM messages")
    suspend fun clearAllMessages()
}
