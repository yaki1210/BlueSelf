package com.example.data.files

import android.content.ContentValues
import android.content.Context
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import com.example.data.model.FileEntity
import java.io.File

/**
 * Owns the receive staging directory and the "move, don't copy" save-to-Downloads logic.
 *
 * Downloading is a *move*: the fully-received staging file is moved to the public Downloads
 * location and the staging copy is deleted, so only one persisted copy ever exists on disk.
 */
object ReceivedFileManager {

    /** App-private staging directory for inbound file chunks. */
    fun stagingDir(context: Context): File =
        File(context.getExternalFilesDir(null), "received").apply { mkdirs() }

    /** The staging file for a transfer, if present. */
    fun stagingFile(context: Context, fileId: String): File = File(stagingDir(context), fileId)

    /**
     * Moves the staged file into the fixed (public Downloads) location.
     * Returns the saved absolute path on success, or null on failure.
     * The caller updates the entity status.
     */
    fun saveToDownloads(context: Context, file: FileEntity): String? {
        val staging = File(file.stagingPath)
        if (!staging.exists()) return null
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            saveViaMediaStore(context, file, staging)
        } else {
            saveLegacyMove(context, file, staging)
        }
    }

    private fun saveViaMediaStore(context: Context, file: FileEntity, staging: File): String? {
        return try {
            val resolver = context.contentResolver
            val values = ContentValues().apply {
                put(MediaStore.Downloads.DISPLAY_NAME, file.fileName)
                put(MediaStore.Downloads.MIME_TYPE, file.mimeType)
                put(MediaStore.Downloads.IS_PENDING, 1)
                put(MediaStore.Downloads.SIZE, file.fileSize)
            }
            val uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: return null
            resolver.openOutputStream(uri)?.use { out ->
                staging.inputStream().use { it.copyTo(out) }
            } ?: return null
            values.clear()
            values.put(MediaStore.Downloads.IS_PENDING, 0)
            resolver.update(uri, values, null, null)
            staging.delete()
            publicDownloadsPath(file.fileName)
        } catch (_: Exception) {
            null
        }
    }

    private fun saveLegacyMove(context: Context, file: FileEntity, staging: File): String? {
        // Public Downloads first (same volume → true move). If that fails, fall back to an
        // app-private fixed location (still a move) so we never keep two copies on disk.
        val public = File(
            Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS),
            file.fileName
        )
        if (public.exists()) public.delete()
        if (staging.renameTo(public)) return public.absolutePath

        val fallback = File(File(context.getExternalFilesDir(null), "saved"), file.fileName)
        fallback.parentFile?.mkdirs()
        return try {
            staging.copyTo(fallback, overwrite = true)
            staging.delete()
            fallback.absolutePath
        } catch (_: Exception) {
            null
        }
    }

    /** The conventional public Downloads path (used for user-facing save location). */
    private fun publicDownloadsPath(fileName: String): String =
        File(
            Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS),
            fileName
        ).absolutePath

    /** Deletes a staging file (e.g. on failure or message removal). */
    fun deleteStaging(context: Context, fileId: String) {
        runCatching { File(stagingDir(context), fileId).delete() }
    }
}
