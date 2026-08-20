package com.example.data.db

import androidx.room.ColumnInfo
import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Update
import com.example.data.model.FileEntity
import kotlinx.coroutines.flow.Flow

data class FileCount(
    @ColumnInfo(name = "messageId") val messageId: String,
    @ColumnInfo(name = "fileCount") val fileCount: Int
)

@Dao
interface FileDao {
    @Query("SELECT * FROM files WHERE messageId = :messageId ORDER BY sortOrder ASC")
    fun filesForMessage(messageId: String): Flow<List<FileEntity>>

    @Query("SELECT messageId, COUNT(*) AS fileCount FROM files GROUP BY messageId")
    fun fileCounts(): Flow<List<FileCount>>

    @Query("SELECT * FROM files WHERE messageId = :messageId ORDER BY sortOrder ASC")
    suspend fun getFilesForMessageOnce(messageId: String): List<FileEntity>

    @Query("SELECT * FROM files WHERE id = :id LIMIT 1")
    suspend fun getFileById(id: String): FileEntity?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun insertFile(file: FileEntity)

    @Update
    suspend fun updateFile(file: FileEntity)

    @Query("DELETE FROM files WHERE id = :id")
    suspend fun deleteFileById(id: String)

    @Query("DELETE FROM files WHERE messageId = :messageId")
    suspend fun deleteFilesForMessage(messageId: String)

    @Query("DELETE FROM files")
    suspend fun clearAllFiles()
}
