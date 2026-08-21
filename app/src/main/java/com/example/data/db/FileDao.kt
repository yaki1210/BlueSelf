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

    /** Targets a single file row's status+bytes without reading/overwriting the whole row.
     * Used by the receive pipeline to avoid concurrent whole-row @Update clobbering the status. */
    @Query("UPDATE files SET status = :status, receivedBytes = :receivedBytes WHERE id = :id")
    suspend fun updateStatusAndBytes(id: String, status: String, receivedBytes: Long)

    /** Targets only receivedBytes (progress) so the final COMPLETE status is never overwritten. */
    @Query("UPDATE files SET receivedBytes = :receivedBytes WHERE id = :id")
    suspend fun updateReceivedBytes(id: String, receivedBytes: Long)

    @Query("DELETE FROM files WHERE id = :id")
    suspend fun deleteFileById(id: String)

    @Query("DELETE FROM files WHERE messageId = :messageId")
    suspend fun deleteFilesForMessage(messageId: String)

    @Query("DELETE FROM files")
    suspend fun clearAllFiles()
}
