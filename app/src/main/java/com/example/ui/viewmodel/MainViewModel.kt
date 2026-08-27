package com.example.ui.viewmodel

import android.app.Application
import android.net.Uri
import android.util.Log
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.example.R
import com.example.bluetooth.BluetoothConnectionState
import com.example.bluetooth.BluetoothManager
import com.example.bluetooth.FileStart
import com.example.bluetooth.MessageProtocol
import com.example.bluetooth.ScannedBluetoothDevice
import com.example.bluetooth.TransferDirection
import com.example.bluetooth.TransferProgress
import com.example.bluetooth.TransferResult
import com.example.data.db.AppDatabase
import com.example.data.files.ReceivedFileManager
import com.example.data.model.DeviceEntity
import com.example.data.model.FileEntity
import com.example.data.model.MessageEntity
import com.example.data.repository.DeviceRepository
import com.example.data.repository.MessageRepository
import com.example.data.settings.AppLanguage
import com.example.data.settings.AppThemeMode
import com.example.data.settings.SettingsRepository
import com.example.notifications.MessageNotifier
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.security.MessageDigest
import java.util.UUID

/** A file picked by the user that is queued to be sent with the next message. */
data class PendingAttachment(
    val uri: Uri,
    val name: String,
    val mime: String,
    val size: Long
)

class MainViewModel(application: Application) : AndroidViewModel(application) {

    private val db = AppDatabase.getDatabase(application)
    private val deviceRepository = DeviceRepository(db.deviceDao())
    private val messageRepository = MessageRepository(db.messageDao(), db.fileDao())
    private val settingsRepository = SettingsRepository(application)

    val bluetoothManager = BluetoothManager(application)

    val language: StateFlow<AppLanguage> = settingsRepository.language
    val themeMode: StateFlow<AppThemeMode> = settingsRepository.themeMode

    /** A5：新消息通知开关（应用层闸门，与系统权限/系统开关双重控制）。 */
    val notificationsEnabled: StateFlow<Boolean> = settingsRepository.notificationsEnabled

    fun setNotificationsEnabled(enabled: Boolean) = settingsRepository.setNotificationsEnabled(enabled)

    // ---- A4: 前台标志（MainActivity onResume/onPause 维护）----
    var isAppInForeground = false
        private set

    fun setForeground(active: Boolean) { isAppInForeground = active }

    private val appContext: Application
        get() = getApplication()

    /** 供 Compose 层取应用上下文（可发现性请求等系统调用使用）。 */
    fun getApplicationContext(): Application = getApplication()

    /** Serializes all receive-side DB writes so concurrent receive coroutines never
     * clobber each other (a whole-row @Update with a stale snapshot overwrites a fresh
     * COMPLETE status back to RECEIVING). */
    private val receiveDbLock = Mutex()

    val savedDevices: StateFlow<List<DeviceEntity>> = deviceRepository.allDevices
        .stateIn(
            scope = viewModelScope,
            started = SharingStarted.WhileSubscribed(5000),
            initialValue = emptyList()
        )

    val currentDevice: StateFlow<DeviceEntity?> = deviceRepository.currentDevice
        .stateIn(
            scope = viewModelScope,
            started = SharingStarted.WhileSubscribed(5000),
            initialValue = null
        )

    val connectionState: StateFlow<BluetoothConnectionState> = bluetoothManager.connectionState

    private val _textInput = MutableStateFlow("")
    val textInput: StateFlow<String> = _textInput.asStateFlow()

    private val _pendingAttachments = MutableStateFlow<List<PendingAttachment>>(emptyList())
    val pendingAttachments: StateFlow<List<PendingAttachment>> = _pendingAttachments.asStateFlow()

    private val _isSending = MutableStateFlow(false)
    val isSending: StateFlow<Boolean> = _isSending.asStateFlow()

    /** File ids currently being written to Downloads (drive per-file spinners). */
    private val _savingFileIds = MutableStateFlow<Set<String>>(emptySet())
    val savingFileIds: StateFlow<Set<String>> = _savingFileIds.asStateFlow()

    /** Live transfer progress keyed by file id (drives progress bars). */
    val transferProgress: StateFlow<Map<String, TransferProgress>> = bluetoothManager.fileTransfer.progress

    val inboxMessages: StateFlow<List<MessageEntity>> = messageRepository.inboxMessages
        .stateIn(
            scope = viewModelScope,
            started = SharingStarted.WhileSubscribed(5000),
            initialValue = emptyList()
        )

    val allMessages: StateFlow<List<MessageEntity>> = messageRepository.allMessages
        .stateIn(
            scope = viewModelScope,
            started = SharingStarted.WhileSubscribed(5000),
            initialValue = emptyList()
        )

    val unreadCount: StateFlow<Int> = messageRepository.unreadCount
        .stateIn(
            scope = viewModelScope,
            started = SharingStarted.WhileSubscribed(5000),
            initialValue = 0
        )

    /** messageId → attachment count, for inbox preview indicators. */
    val fileCounts: StateFlow<Map<String, Int>> = messageRepository.fileCounts()
        .stateIn(
            scope = viewModelScope,
            started = SharingStarted.WhileSubscribed(5000),
            initialValue = emptyMap()
        )

    private val _selectedMessage = MutableStateFlow<MessageEntity?>(null)
    val selectedMessage: StateFlow<MessageEntity?> = _selectedMessage.asStateFlow()

    val scannedDevices: StateFlow<List<ScannedBluetoothDevice>> = bluetoothManager.scannedDevices
    val isScanning: StateFlow<Boolean> = bluetoothManager.isScanning

    private val _snackbarEvent = MutableSharedFlow<String>(extraBufferCapacity = 1)
    val snackbarEvent: SharedFlow<String> = _snackbarEvent.asSharedFlow()

    init {
        observeIncomingMessages()
        observeManagerErrors()
        observeFileStarts()
        observeFileResults()
        observeFileProgress()
    }

    // ---- observation ----

    private fun observeIncomingMessages() {
        viewModelScope.launch {
            bluetoothManager.incomingMessages.collect { packet ->
                val entity = MessageProtocol.packetToEntity(packet, isOutgoing = false)
                withContext(Dispatchers.IO) {
                    receiveDbLock.withLock { messageRepository.saveMessage(entity) }
                }
                // A4：App 在后台且通知开关开启时发状态栏通知；前台走 Snackbar。
                if (!isAppInForeground && settingsRepository.notificationsEnabled.value) {
                    val senderName = packet.senderName.ifBlank {
                        bluetoothManager.activeDevice.value?.name ?: "远端设备"
                    }
                    MessageNotifier.notifyMessage(appContext, senderName, packet.content)
                }
                _snackbarEvent.emit(
                    appContext.getString(R.string.snackbar_received_from, packet.senderName)
                )
            }
        }
    }

    private fun observeManagerErrors() {
        viewModelScope.launch {
            bluetoothManager.connectionErrors.collect { msg ->
                _snackbarEvent.emit(msg)
            }
        }
        viewModelScope.launch {
            bluetoothManager.scanErrors.collect { msg ->
                _snackbarEvent.emit(msg)
            }
        }
    }

    /** Receiver side: FILE_START → insert a RECEIVING file row under the parent message. */
    private fun observeFileStarts() {
        viewModelScope.launch {
            bluetoothManager.fileTransfer.fileStarts.collect { start: FileStart ->
                Log.i(TAG, "recv START msgId=${start.msgId} fileId=${start.id} name=${start.name} size=${start.size}")
                try {
                    withContext(Dispatchers.IO) {
                        receiveDbLock.withLock {
                            // 父消息可能尚未落库（TXT 与 FILE_START 并发到达）。补一个占位父消息，
                            // 避免 FileEntity 的外键约束失败。真实 TXT 到达后会就地覆盖 content。
                            if (messageRepository.getMessageByIdOnce(start.msgId) == null) {
                                val active = bluetoothManager.activeDevice.value
                                messageRepository.saveMessage(
                                    MessageEntity(
                                        id = start.msgId,
                                        senderDeviceId = active?.id ?: "remote",
                                        senderDeviceName = active?.name ?: "远端设备",
                                        receiverDeviceId = "local",
                                        receiverDeviceName = "This Device",
                                        content = "",
                                        createdAt = System.currentTimeMillis(),
                                        messageType = MessageProtocol.TYPE_FILE
                                    )
                                )
                            }
                            val staging = ReceivedFileManager.stagingFile(appContext, start.id)
                            val existing = messageRepository.getFileById(start.id)
                            if (existing == null) {
                                messageRepository.saveFile(
                                    FileEntity(
                                        id = start.id,
                                        messageId = start.msgId,
                                        fileName = start.name,
                                        mimeType = start.mime,
                                        fileSize = start.size,
                                        md5 = start.md5,
                                        totalChunks = start.totalChunks,
                                        chunkSize = start.chunkSize,
                                        receivedBytes = 0,
                                        stagingPath = staging.absolutePath,
                                        status = "RECEIVING",
                                        isOutgoing = false,
                                        sortOrder = (messageRepository.getFilesForMessageOnce(start.msgId).maxOfOrNull { it.sortOrder } ?: -1) + 1,
                                        createdAt = System.currentTimeMillis()
                                    )
                                )
                            }
                        }
                    }
                } catch (e: Exception) {
                    // 打印真实错误，避免静默吞掉导致状态不收敛。
                    Log.e(TAG, "recv START insert failed", e)
                }
            }
        }
    }

    /** Receiver side: FILE_END / ERR → finalize file status (COMPLETE / FAILED). */
    private fun observeFileResults() {
        viewModelScope.launch {
            bluetoothManager.fileTransfer.results.collect { result: TransferResult ->
                Log.i(TAG, "recv RESULT fileId=${result.fileId} success=${result.success} md5Match=${result.md5Match} bytes=${result.totalBytes} err=${result.error}")
                try {
                    withContext(Dispatchers.IO) {
                        receiveDbLock.withLock {
                            val file = messageRepository.getFileById(result.fileId)
                            if (file == null) {
                                Log.w(TAG, "recv RESULT: file not found, skipping; ${result.fileId}")
                                return@withLock
                            }
                            val newStatus = if (result.success && result.md5Match) "COMPLETE" else "FAILED"
                            // 定向更新 status+bytes，绝不使用整行旧快照覆盖新状态。
                            messageRepository.updateFileStatus(result.fileId, newStatus, result.totalBytes)
                            if (!result.success) {
                                ReceivedFileManager.deleteStaging(appContext, result.fileId)
                            }
                        }
                    }
                    if (result.success) {
                        val file = messageRepository.getFileById(result.fileId)
                        if (file != null) {
                            // A4：文件接收完成同样在后台时发通知。
                            if (!isAppInForeground && settingsRepository.notificationsEnabled.value) {
                                val senderName = messageRepository.getMessageByIdOnce(file.messageId)?.senderDeviceName
                                    ?: bluetoothManager.activeDevice.value?.name ?: "远端设备"
                                MessageNotifier.notifyFile(appContext, senderName, file.fileName)
                            }
                            _snackbarEvent.emit(
                                appContext.getString(R.string.snackbar_file_received, file.fileName)
                            )
                        }
                    }
                } catch (e: Exception) {
                    // 打印真实错误，避免状态不收敛被静默吞掉。
                    Log.e(TAG, "recv RESULT finalize failed", e)
                }
            }
        }
    }

    /**
     * Receiver side: persist live inbound progress into the file row (throttled) so the
     * detail screen can always render the progress bar / percentage from the DB instead of
     * relying on an in-memory map that may be missing.
     */
    private fun observeFileProgress() {
        viewModelScope.launch {
            var lastWrite = 0L
            bluetoothManager.fileTransfer.progress.collect { progressMap ->
                val now = System.currentTimeMillis()
                progressMap.values
                    .filter { it.direction == TransferDirection.RECEIVE }
                    .filter { it.totalBytes > 0 }
                    .forEach { p ->
                        try {
                            withContext(Dispatchers.IO) {
                                receiveDbLock.withLock {
                                    val file = messageRepository.getFileById(p.fileId) ?: return@withLock
                                    if (file.status != "RECEIVING") return@withLock
                                    if (p.bytesDone == file.receivedBytes) return@withLock
                                    // Throttle DB writes to ~5/s while still updating on completion chunks.
                                    if (p.fraction >= 1f || now - lastWrite >= 200L) {
                                        lastWrite = now
                                        // 只更新 receivedBytes，绝不触碰 status（避免覆盖 COMPLETE）。
                                        messageRepository.updateFileReceivedBytes(p.fileId, p.bytesDone)
                                    }
                                }
                            }
                        } catch (e: Exception) {
                            Log.e(TAG, "recv PROGRESS update failed", e)
                        }
                    }
            }
        }
    }

    // ---- text input & attachments ----

    fun onTextInputChange(text: String) {
        _textInput.value = text
    }

    fun pasteClipboardText(text: String) {
        if (text.isNotBlank()) {
            _textInput.value = text
            viewModelScope.launch {
                _snackbarEvent.emit(appContext.getString(R.string.snackbar_pasted))
            }
        } else {
            viewModelScope.launch {
                _snackbarEvent.emit(appContext.getString(R.string.snackbar_clipboard_empty))
            }
        }
    }

    fun clearTextInput() {
        _textInput.value = ""
    }

    fun addAttachments(attachments: List<PendingAttachment>) {
        // No hard size limit: accept everything, but warn when a file is unusually large.
        _pendingAttachments.value = (_pendingAttachments.value + attachments).distinctBy { it.uri }
        if (attachments.any { it.size > MAX_FILE_SIZE_BYTES }) {
            viewModelScope.launch {
                _snackbarEvent.emit(appContext.getString(R.string.snackbar_file_large_warning))
            }
        }
    }

    fun removeAttachment(uri: Uri) {
        _pendingAttachments.value = _pendingAttachments.value.filterNot { it.uri == uri }
    }

    fun clearAttachments() {
        _pendingAttachments.value = emptyList()
    }

    // ---- send ----

    fun sendMessage() {
        val text = _textInput.value.trim()
        val attachments = _pendingAttachments.value
        if (text.isEmpty() && attachments.isEmpty()) {
            viewModelScope.launch {
                _snackbarEvent.emit(appContext.getString(R.string.snackbar_enter_text))
            }
            return
        }

        val target = currentDevice.value
        if (target == null) {
            viewModelScope.launch {
                _snackbarEvent.emit(appContext.getString(R.string.snackbar_select_device))
            }
            return
        }

        viewModelScope.launch {
            _isSending.value = true

            // The whole send pipeline (MD5 hashing + blocking socket writes) must run off the
            // main thread, otherwise the UI freezes/ANRs while a file is being sent.
            val (allOk, sentBytes, sentDurationMs) = withContext(Dispatchers.IO) {
                val myName = bluetoothManager.localDeviceName()
                val messageId = UUID.randomUUID().toString()
                val hasFiles = attachments.isNotEmpty()
                val messageEntity = MessageEntity(
                    id = messageId,
                    senderDeviceId = "local_android",
                    senderDeviceName = myName,
                    receiverDeviceId = target.id,
                    receiverDeviceName = target.name,
                    content = text,
                    createdAt = System.currentTimeMillis(),
                    receivedAt = null,
                    readAt = null,
                    status = "SENDING",
                    isOutgoing = true,
                    messageType = if (hasFiles) MessageProtocol.TYPE_FILE else MessageProtocol.TYPE_TEXT
                )
                messageRepository.saveMessage(messageEntity)

                // 1) TXT frame first — it carries the message id / text and creates the message on the receiver.
                val packet = MessageProtocol.Packet(
                    senderId = "local_android",
                    senderName = myName,
                    receiverId = target.id,
                    receiverName = target.name,
                    content = text,
                    id = messageId,
                    type = if (hasFiles) MessageProtocol.TYPE_FILE else MessageProtocol.TYPE_TEXT,
                    timestamp = messageEntity.createdAt
                )
                val sendResult = bluetoothManager.sendTextMessage(packet)

                // 2) Then each file, one by one. Accumulate bytes/duration to report the real rate.
                var ok = sendResult.isSuccess
                var bytes = 0L
                var durationMs = 0L
                if (ok) {
                    for (attachment in attachments) {
                        val result = sendFileAttachment(attachment, messageId, target.id, target.name)
                        if (result != null && result.success) {
                            bytes += result.totalBytes
                            durationMs += result.durationMs
                        } else {
                            ok = false
                        }
                    }
                }

                // 3) Finalize the message row.
                messageRepository.updateMessageRow(
                    messageId,
                    if (ok) "SENT" else "FAILED"
                )
                Triple(ok, bytes, durationMs)
            }

            _isSending.value = false
            if (allOk) {
                _textInput.value = ""
                _pendingAttachments.value = emptyList()
                if (attachments.isNotEmpty()) {
                    val mbps = if (sentDurationMs > 0) {
                        sentBytes * 8.0 / 1_000_000.0 / (sentDurationMs / 1000.0)
                    } else 0.0
                    _snackbarEvent.emit(
                        appContext.getString(R.string.snackbar_files_sent_rate, attachments.size, mbps)
                    )
                } else {
                    _snackbarEvent.emit(appContext.getString(R.string.snackbar_send_success))
                }
            } else {
                _snackbarEvent.emit(
                    appContext.getString(R.string.snackbar_send_failed, "connection or file error")
                )
            }
        }
    }

    private suspend fun sendFileAttachment(
        attachment: PendingAttachment,
        messageId: String,
        targetId: String,
        targetName: String
    ): TransferResult? {
        val fileId = UUID.randomUUID().toString()
        val fileEntity = FileEntity(
            id = fileId,
            messageId = messageId,
            fileName = attachment.name,
            mimeType = attachment.mime,
            fileSize = attachment.size,
            md5 = "",
            totalChunks = 0,
            chunkSize = MessageProtocol.DEFAULT_CHUNK_SIZE,
            receivedBytes = 0,
            stagingPath = "",
            status = "SENDING",
            isOutgoing = true,
            sortOrder = 0,
            createdAt = System.currentTimeMillis()
        )
        messageRepository.saveFile(fileEntity)
        return try {
            val md5 = md5OfUri(attachment.uri)
            appContext.contentResolver.openInputStream(attachment.uri)?.use { input ->
                val result = bluetoothManager.fileTransfer.sendFile(
                    input = input,
                    fileId = fileId,
                    msgId = messageId,
                    name = attachment.name,
                    mime = attachment.mime,
                    size = attachment.size,
                    md5 = md5
                )
                messageRepository.updateFile(
                    fileEntity.copy(
                        md5 = md5,
                        totalChunks = (attachment.size + MessageProtocol.DEFAULT_CHUNK_SIZE - 1) / MessageProtocol.DEFAULT_CHUNK_SIZE,
                        status = if (result.success) "SENT" else "FAILED",
                        receivedBytes = result.totalBytes
                    )
                )
                result
            }
        } catch (e: Exception) {
            messageRepository.updateFile(fileEntity.copy(status = "FAILED"))
            null
        }
    }

    private fun md5OfUri(uri: Uri): String {
        val md = MessageDigest.getInstance("MD5")
        appContext.contentResolver.openInputStream(uri)?.use { input ->
            val buf = ByteArray(64 * 1024)
            while (true) {
                val read = input.read(buf)
                if (read < 0) break
                md.update(buf, 0, read)
            }
        }
        return md.digest().joinToString("") { "%02x".format(it) }
    }

    // ---- file download (move to Downloads) ----

    fun downloadFile(file: FileEntity) {
        if (file.status != "COMPLETE" || file.id in _savingFileIds.value) return
        viewModelScope.launch {
            _savingFileIds.value = _savingFileIds.value + file.id
            val savedPath = withContext(Dispatchers.IO) {
                ReceivedFileManager.saveToDownloads(appContext, file)
            }
            messageRepository.updateFile(file.copy(status = if (savedPath != null) "SAVED" else "FAILED"))
            _savingFileIds.value = _savingFileIds.value - file.id
            _snackbarEvent.emit(
                appContext.getString(
                    if (savedPath != null) R.string.snackbar_file_saved_path else R.string.snackbar_file_save_failed,
                    if (savedPath != null) savedPath else file.fileName
                )
            )
        }
    }

    fun filesFor(messageId: String): kotlinx.coroutines.flow.Flow<List<FileEntity>> =
        messageRepository.filesForMessage(messageId)

    // ---- devices / inbox / settings (unchanged) ----

    fun selectDevice(device: DeviceEntity) {
        viewModelScope.launch {
            deviceRepository.setCurrentDevice(device.id)
            // 方案A（与 Windows W1 同步）：已连接该设备且链路健康 → 仅切换目标，不触碰现有链路。
            val active = bluetoothManager.activeDevice.value
            val state = bluetoothManager.connectionState.value
            if (active?.macAddress == device.macAddress && state == BluetoothConnectionState.ONLINE) {
                return@launch
            }
            bluetoothManager.connectToDevice(device)
            _snackbarEvent.emit(appContext.getString(R.string.snackbar_connecting, device.name))
        }
    }

    fun addDevice(scanned: ScannedBluetoothDevice) {
        viewModelScope.launch {
            val entity = DeviceEntity(
                id = scanned.address,
                name = scanned.name.ifBlank { "未知设备" },
                macAddress = scanned.address,
                deviceType = scanned.deviceType,
                lastKnownState = "ONLINE",
                isCurrent = true
            )
            deviceRepository.saveDevice(entity)
            deviceRepository.setCurrentDevice(entity.id)
            bluetoothManager.connectToDevice(entity)
            _snackbarEvent.emit(appContext.getString(R.string.snackbar_added_connected, entity.name))
        }
    }

    fun deleteDevice(device: DeviceEntity) {
        viewModelScope.launch {
            deviceRepository.deleteDevice(device.id)
            if (currentDevice.value?.id == device.id) {
                bluetoothManager.disconnect()
            }
            _snackbarEvent.emit(appContext.getString(R.string.snackbar_device_deleted, device.name))
        }
    }

    fun startDeviceScan() {
        bluetoothManager.startScan()
    }

    fun setLanguage(language: AppLanguage) {
        settingsRepository.setLanguage(language)
    }

    fun setThemeMode(mode: AppThemeMode) {
        settingsRepository.setThemeMode(mode)
    }

    fun stopDeviceScan() {
        bluetoothManager.stopScan()
    }

    fun openMessage(message: MessageEntity) {
        _selectedMessage.value = message
        viewModelScope.launch {
            messageRepository.markAsRead(message.id)
        }
    }

    fun closeMessageDetail() {
        _selectedMessage.value = null
    }

    fun markAllInboxRead() {
        viewModelScope.launch {
            messageRepository.markAllAsRead()
            _snackbarEvent.emit(appContext.getString(R.string.snackbar_mark_all_read))
        }
    }

    fun deleteMessage(id: String) {
        viewModelScope.launch {
            messageRepository.getFilesForMessageOnce(id).forEach { file ->
                ReceivedFileManager.deleteStaging(appContext, file.id)
            }
            messageRepository.deleteMessage(id)
            if (_selectedMessage.value?.id == id) {
                _selectedMessage.value = null
            }
            _snackbarEvent.emit(appContext.getString(R.string.snackbar_message_deleted))
        }
    }

    fun clearAllMessages() {
        viewModelScope.launch {
            // Delete staging files for every stored message before wiping the DB.
            messageRepository.allMessages.first().forEach { msg ->
                messageRepository.getFilesForMessageOnce(msg.id).forEach { file ->
                    ReceivedFileManager.deleteStaging(appContext, file.id)
                }
            }
            messageRepository.clearAll()
            _selectedMessage.value = null
            _snackbarEvent.emit(appContext.getString(R.string.snackbar_inbox_cleared))
        }
    }

    override fun onCleared() {
        super.onCleared()
        bluetoothManager.cleanUp()
    }

    companion object {
        // Warning threshold: files above this size are allowed but flagged as slow to transfer.
        private const val MAX_FILE_SIZE_BYTES = 20L * 1024 * 1024
        private const val TAG = "BlueSelfRecv"
    }
}
