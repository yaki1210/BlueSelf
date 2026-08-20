package com.example.bluetooth

import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.File
import java.io.FileOutputStream
import java.io.InputStream
import java.security.MessageDigest
import kotlin.math.ceil
import kotlin.math.roundToLong

/** Transfer direction. */
enum class TransferDirection { SEND, RECEIVE }

/**
 * Progress for one active file transfer.
 */
data class TransferProgress(
    val fileId: String,
    val direction: TransferDirection,
    val bytesDone: Long,
    val totalBytes: Long,
    val speedBps: Long
) {
    val fraction: Float get() = if (totalBytes <= 0) 0f else (bytesDone.toFloat() / totalBytes).coerceIn(0f, 1f)
}

/** Result of a finished transfer. */
data class TransferResult(
    val fileId: String,
    val success: Boolean,
    val md5Match: Boolean,
    val totalBytes: Long,
    val durationMs: Long,
    val error: String? = null
) {
    /** Mbps measured over the whole transfer. */
    val mbps: Double
        get() = if (durationMs <= 0) 0.0 else (totalBytes * 8.0 / 1_000_000.0) / (durationMs / 1000.0)
}

/**
 * FileTransferManager drives the chunked, windowed file transfer over a byte stream.
 *
 * Send: streams the source, sends FILE_START, then up to [MessageProtocol.SEND_WINDOW]
 * FILE_CHUNK frames in flight, then FILE_END. The receiver replies FILE_ACK periodically.
 *
 * Receive: at most ONE active incoming file at a time. FILE_START opens a staging file,
 * ordered chunks are appended, an ACK is sent every 8 chunks / 1s, and FILE_END triggers an
 * MD5 verification before the result is reported.
 *
 * RFCOMM/L2CAP is a reliable ordered stream, so chunks arrive in order. A failed MD5 check
 * reports a result with success=false for the caller to handle (full resend).
 */
class FileTransferManager(
    private val stagingDir: File,
    private val writeFrame: (MessageProtocol.Frame) -> Unit
) {
    private val _progress = MutableStateFlow<Map<String, TransferProgress>>(emptyMap())
    val progress: StateFlow<Map<String, TransferProgress>> = _progress.asStateFlow()

    private val _results = MutableSharedFlow<TransferResult>(extraBufferCapacity = 16)
    val results: SharedFlow<TransferResult> = _results.asSharedFlow()

    private val _ackEvents = MutableSharedFlow<FileAck>(extraBufferCapacity = 16)
    val ackEvents: SharedFlow<FileAck> = _ackEvents.asSharedFlow()

    /** Emitted when an inbound FILE_START is received (receiver inserts the file row). */
    private val _fileStarts = MutableSharedFlow<FileStart>(extraBufferCapacity = 16)
    val fileStarts: SharedFlow<FileStart> = _fileStarts.asSharedFlow()

    private var seqCounter = 0L
    private var activeReceive: ReceiveSession? = null

    @Synchronized
    private fun nextSeq(): Long = ++seqCounter

    // ---------------- SEND ----------------

    /**
     * Sends [input] as a file transfer. Returns the [TransferResult] once the pipeline finishes.
     */
    fun sendFile(
        input: InputStream,
        fileId: String,
        msgId: String,
        name: String,
        mime: String,
        size: Long,
        md5: String
    ): TransferResult {
        val chunkSize = MessageProtocol.DEFAULT_CHUNK_SIZE
        val totalChunks = ceil(size.toDouble() / chunkSize).toLong()
        val startTime = System.currentTimeMillis()

        writeFrame(
            MessageProtocol.Frame(
                type = MessageProtocol.FT_FILE_START,
                seq = nextSeq(),
                payload = FileMetaJson.encodeStart(fileId, msgId, name, mime, size, md5, chunkSize, totalChunks)
            )
        )

        var sentBytes = 0L
        var sentChunks = 0L
        val buffer = ByteArray(chunkSize)
        try {
            while (true) {
                // Fill the in-flight window.
                val window = mutableListOf<ByteArray>()
                while (window.size < MessageProtocol.SEND_WINDOW) {
                    val readSize = input.read(buffer)
                    if (readSize < 0) break
                    window.add(buffer.copyOf(readSize))
                }
                if (window.isEmpty()) break

                window.forEach { bytes ->
                    val payload = ByteArray(4 + bytes.size)
                    writeUInt32BE(payload, 0, sentChunks)
                    bytes.copyInto(payload, 4)
                    writeFrame(
                        MessageProtocol.Frame(
                            type = MessageProtocol.FT_FILE_CHUNK,
                            seq = nextSeq(),
                            payload = payload
                        )
                    )
                    sentChunks++
                    sentBytes += bytes.size
                }
                updateProgress(fileId, TransferDirection.SEND, sentBytes, size)
            }
        } catch (e: Exception) {
            return TransferResult(fileId, false, false, sentBytes, System.currentTimeMillis() - startTime, e.message)
        }

        writeFrame(
            MessageProtocol.Frame(
                type = MessageProtocol.FT_FILE_END,
                seq = nextSeq(),
                payload = FileMetaJson.encodeEnd(fileId, totalChunks, md5)
            )
        )
        updateProgress(fileId, TransferDirection.SEND, size, size)
        return TransferResult(fileId, true, true, size, System.currentTimeMillis() - startTime)
    }

    // ---------------- RECEIVE ----------------

    /** Handles an inbound frame (called from the BluetoothManager read loop). */
    fun onFrame(frame: MessageProtocol.Frame) {
        when (frame.type) {
            MessageProtocol.FT_FILE_START -> onFileStart(frame)
            MessageProtocol.FT_FILE_CHUNK -> onFileChunk(frame)
            MessageProtocol.FT_FILE_END -> onFileEnd(frame)
            else -> Unit
        }
    }

    private fun onFileStart(frame: MessageProtocol.Frame) {
        // Replace any in-progress receive (one active file at a time).
        closeActiveReceive()
        val meta = FileMetaJson.decodeStart(frame.payload)
        val staging = File(stagingDir, meta.id)
        staging.parentFile?.mkdirs()
        activeReceive = ReceiveSession(meta, staging, FileOutputStream(staging, false))
        updateProgress(meta.id, TransferDirection.RECEIVE, 0, meta.size)
        _fileStarts.emitSafely(meta)
    }

    private fun onFileChunk(frame: MessageProtocol.Frame) {
        val payload = frame.payload
        if (payload.size < 4) return
        val session = activeReceive ?: return
        val bytes = payload.copyOfRange(4, payload.size)
        try {
            session.out.write(bytes)
        } catch (e: Exception) {
            sendErr(session.meta.id, 1, "write failed: ${e.message}")
            closeActiveReceive()
            return
        }
        session.receivedBytes += bytes.size
        session.sinceLastAck++
        val now = System.currentTimeMillis()
        if (session.sinceLastAck >= ACK_EVERY_CHUNKS || now - session.lastAckTime >= ACK_INTERVAL_MS) {
            sendAck(session.meta.id, session.receivedBytes, false, false)
            session.lastAckTime = now
            session.sinceLastAck = 0
        }
        updateProgress(session.meta.id, TransferDirection.RECEIVE, session.receivedBytes, session.meta.size)
    }

    private fun onFileEnd(frame: MessageProtocol.Frame) {
        val end = FileMetaJson.decodeEnd(frame.payload)
        val session = activeReceive ?: return
        if (session.meta.id != end.id) return
        activeReceive = null
        session.out.flush()
        session.out.close()

        val md5Ok = verifyMd5(session.file, end.md5)
        sendAck(end.id, session.receivedBytes, true, md5Ok)
        updateProgress(end.id, TransferDirection.RECEIVE, session.receivedBytes, session.meta.size)
        _results.emitSafely(
            TransferResult(
                fileId = end.id,
                success = md5Ok,
                md5Match = md5Ok,
                totalBytes = session.receivedBytes,
                durationMs = System.currentTimeMillis() - session.receivedStartedAt
            )
        )
        if (!md5Ok) {
            session.file.delete()
        }
    }

    /** Called from BluetoothManager when a FILE_ACK frame arrives. */
    fun onAck(frame: MessageProtocol.Frame) {
        val ack = FileMetaJson.decodeAck(frame.payload)
        _ackEvents.emitSafely(ack)
    }

    /** Called from BluetoothManager when an ERR frame arrives. */
    fun onError(frame: MessageProtocol.Frame) {
        val msg = runCatching {
            org.json.JSONObject(frame.payload.toString(Charsets.UTF_8)).optString("msg", "unknown error")
        }.getOrDefault("unknown error")
        closeActiveReceive()
        _results.emitSafely(TransferResult("", false, false, 0, 0, msg))
    }

    private fun sendAck(fileId: String, ackedChunks: Long, ok: Boolean, md5Match: Boolean) {
        writeFrame(
            MessageProtocol.Frame(
                type = MessageProtocol.FT_FILE_ACK,
                seq = nextSeq(),
                payload = FileMetaJson.encodeAck(fileId, ackedChunks, ok, md5Match)
            )
        )
    }

    private fun sendErr(fileId: String, code: Int, msg: String) {
        writeFrame(
            MessageProtocol.Frame(
                type = MessageProtocol.FT_ERR,
                seq = nextSeq(),
                payload = FileMetaJson.encodeError(fileId, code, msg)
            )
        )
    }

    /** Drops the current receive session and removes its partial staging file. */
    private fun closeActiveReceive() {
        val session = activeReceive ?: return
        activeReceive = null
        runCatching { session.out.close() }
        runCatching { session.file.delete() }
        _progress.value = _progress.value - session.meta.id
    }

    /** Aborts any in-progress receive and clears progress (called on disconnect). */
    fun abort() {
        closeActiveReceive()
        _progress.value = emptyMap()
    }

    private fun updateProgress(fileId: String, direction: TransferDirection, bytesDone: Long, totalBytes: Long) {
        val now = System.currentTimeMillis()
        val prev = _progress.value[fileId]
        val speed = if (prev != null && prev.bytesDone < bytesDone) {
            val dt = (now - (prevLastUpdate[fileId] ?: now)).coerceAtLeast(1)
            ((bytesDone - prev.bytesDone).toDouble() / dt * 1000).roundToLong()
        } else {
            0L
        }
        prevLastUpdate[fileId] = now
        _progress.value = _progress.value + (fileId to TransferProgress(fileId, direction, bytesDone, totalBytes, speed))
    }

    private val prevLastUpdate = java.util.concurrent.ConcurrentHashMap<String, Long>()

    private fun verifyMd5(file: File, expectedHex: String): Boolean {
        if (expectedHex.isBlank()) return true
        val md = MessageDigest.getInstance("MD5")
        file.inputStream().use { input ->
            val buf = ByteArray(64 * 1024)
            while (true) {
                val read = input.read(buf)
                if (read < 0) break
                md.update(buf, 0, read)
            }
        }
        val hex = md.digest().joinToString("") { "%02x".format(it) }
        return hex.equals(expectedHex, ignoreCase = true)
    }

    private fun writeUInt32BE(buf: ByteArray, offset: Int, value: Long) {
        buf[offset] = ((value shr 24) and 0xFF).toByte()
        buf[offset + 1] = ((value shr 16) and 0xFF).toByte()
        buf[offset + 2] = ((value shr 8) and 0xFF).toByte()
        buf[offset + 3] = (value and 0xFF).toByte()
    }

    private class ReceiveSession(
        val meta: FileStart,
        val file: File,
        val out: FileOutputStream,
        val receivedStartedAt: Long = System.currentTimeMillis()
    ) {
        var receivedBytes: Long = 0
        var lastAckTime: Long = System.currentTimeMillis()
        var sinceLastAck: Int = 0
    }

    private companion object {
        const val ACK_EVERY_CHUNKS = 8
        const val ACK_INTERVAL_MS = 1000L
    }
}

private fun <T> MutableSharedFlow<T>.emitSafely(value: T) {
    try {
        this.tryEmit(value)
    } catch (_: Exception) {
        // buffer full or closed; ignore
    }
}
