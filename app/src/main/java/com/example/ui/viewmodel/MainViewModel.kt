package com.example.ui.viewmodel

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.example.bluetooth.BluetoothConnectionState
import com.example.bluetooth.BluetoothManager
import com.example.bluetooth.MessageProtocol
import com.example.bluetooth.ScannedBluetoothDevice
import com.example.data.db.AppDatabase
import com.example.data.model.DeviceEntity
import com.example.data.model.MessageEntity
import com.example.data.repository.DeviceRepository
import com.example.data.repository.MessageRepository
import com.example.data.settings.AppLanguage
import com.example.data.settings.AppThemeMode
import com.example.data.settings.SettingsRepository
import com.example.R
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch

class MainViewModel(application: Application) : AndroidViewModel(application) {

    private val db = AppDatabase.getDatabase(application)
    private val deviceRepository = DeviceRepository(db.deviceDao())
    private val messageRepository = MessageRepository(db.messageDao())
    private val settingsRepository = SettingsRepository(application)

    val bluetoothManager = BluetoothManager(application)

    val language: StateFlow<AppLanguage> = settingsRepository.language
    val themeMode: StateFlow<AppThemeMode> = settingsRepository.themeMode

    private val appContext: Application
        get() = getApplication()

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

    private val _isSending = MutableStateFlow(false)
    val isSending: StateFlow<Boolean> = _isSending.asStateFlow()

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

    private val _selectedMessage = MutableStateFlow<MessageEntity?>(null)
    val selectedMessage: StateFlow<MessageEntity?> = _selectedMessage.asStateFlow()

    val scannedDevices: StateFlow<List<ScannedBluetoothDevice>> = bluetoothManager.scannedDevices
    val isScanning: StateFlow<Boolean> = bluetoothManager.isScanning

    private val _snackbarEvent = MutableSharedFlow<String>(extraBufferCapacity = 1)
    val snackbarEvent: SharedFlow<String> = _snackbarEvent.asSharedFlow()

    init {
        observeIncomingMessages()
        observeManagerErrors()
    }

    private fun observeIncomingMessages() {
        viewModelScope.launch {
            bluetoothManager.incomingMessages.collect { packet ->
                val entity = MessageProtocol.packetToEntity(packet, isOutgoing = false)
                messageRepository.saveMessage(entity)
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

    fun sendText() {
        val text = _textInput.value.trim()
        if (text.isEmpty()) {
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
            val result = bluetoothManager.sendTextMessage(text, target)
            _isSending.value = false

            result.onSuccess { packet ->
                val entity = MessageProtocol.packetToEntity(packet, isOutgoing = true)
                messageRepository.saveMessage(entity)
                _textInput.value = "" // Clear on success as specified
                _snackbarEvent.emit(appContext.getString(R.string.snackbar_send_success))
            }.onFailure { err ->
                _snackbarEvent.emit(appContext.getString(R.string.snackbar_send_failed, err.message ?: "connection"))
            }
        }
    }

    fun selectDevice(device: DeviceEntity) {
        viewModelScope.launch {
            deviceRepository.setCurrentDevice(device.id)
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
            messageRepository.deleteMessage(id)
            if (_selectedMessage.value?.id == id) {
                _selectedMessage.value = null
            }
            _snackbarEvent.emit(appContext.getString(R.string.snackbar_message_deleted))
        }
    }

    fun clearAllMessages() {
        viewModelScope.launch {
            messageRepository.clearAll()
            _selectedMessage.value = null
            _snackbarEvent.emit(appContext.getString(R.string.snackbar_inbox_cleared))
        }
    }

    override fun onCleared() {
        super.onCleared()
        bluetoothManager.cleanUp()
    }
}
