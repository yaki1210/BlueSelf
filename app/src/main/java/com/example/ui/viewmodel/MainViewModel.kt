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
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import java.util.UUID

class MainViewModel(application: Application) : AndroidViewModel(application) {

    private val db = AppDatabase.getDatabase(application)
    private val deviceRepository = DeviceRepository(db.deviceDao())
    private val messageRepository = MessageRepository(db.messageDao())

    val bluetoothManager = BluetoothManager(application)

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
        initializeDefaultDataIfNeeded()
        observeIncomingMessages()
    }

    private fun initializeDefaultDataIfNeeded() {
        viewModelScope.launch {
            val count = deviceRepository.getDeviceCount()
            if (count == 0) {
                val defaultDevices = listOf(
                    DeviceEntity(
                        id = "dev_win_01",
                        name = "我的 Windows 电脑",
                        macAddress = "00:1A:7D:DA:71:13",
                        deviceType = "PC",
                        lastKnownState = "ONLINE",
                        isCurrent = true,
                        isPinned = true
                    ),
                    DeviceEntity(
                        id = "dev_s24_02",
                        name = "Galaxy S24",
                        macAddress = "44:6D:57:C2:A8:90",
                        deviceType = "PHONE",
                        lastKnownState = "ONLINE",
                        isCurrent = false
                    ),
                    DeviceEntity(
                        id = "dev_p8_03",
                        name = "Pixel 8",
                        macAddress = "3C:28:6D:E1:92:04",
                        deviceType = "PHONE",
                        lastKnownState = "OFFLINE",
                        isCurrent = false
                    ),
                    DeviceEntity(
                        id = "dev_mi_04",
                        name = "小米手机",
                        macAddress = "E4:5F:01:88:AA:BC",
                        deviceType = "PHONE",
                        lastKnownState = "OFFLINE",
                        isCurrent = false
                    )
                )
                deviceRepository.saveDevices(defaultDevices)

                // Add sample initial received message for testing inbox
                val initialMsg = MessageEntity(
                    id = UUID.randomUUID().toString(),
                    senderDeviceId = "dev_win_01",
                    senderDeviceName = "我的 Windows 电脑",
                    receiverDeviceId = "local_android",
                    receiverDeviceName = "Android 本机",
                    content = "你好，测试一下蓝牙传输",
                    createdAt = System.currentTimeMillis() - 1000 * 60 * 15,
                    receivedAt = System.currentTimeMillis() - 1000 * 60 * 15,
                    readAt = null,
                    status = "RECEIVED",
                    isOutgoing = false
                )
                messageRepository.saveMessage(initialMsg)

                // Connect to the default selected device
                bluetoothManager.connectToDevice(defaultDevices[0])
            } else {
                // Auto connect to current selected device
                val current = deviceRepository.getDeviceById("dev_win_01")
                if (current != null) {
                    bluetoothManager.connectToDevice(current)
                }
            }
        }
    }

    private fun observeIncomingMessages() {
        viewModelScope.launch {
            bluetoothManager.incomingMessages.collect { packet ->
                val entity = MessageProtocol.packetToEntity(packet, isOutgoing = false)
                messageRepository.saveMessage(entity)
                _snackbarEvent.emit("收到来自 ${packet.senderName} 的新消息")
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
                _snackbarEvent.emit("已粘贴剪贴板内容")
            }
        } else {
            viewModelScope.launch {
                _snackbarEvent.emit("剪贴板为空")
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
                _snackbarEvent.emit("请输入要发送的文本")
            }
            return
        }

        val target = currentDevice.value
        if (target == null) {
            viewModelScope.launch {
                _snackbarEvent.emit("请先选择目标设备")
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
                _snackbarEvent.emit("发送成功")
            }.onFailure { err ->
                _snackbarEvent.emit("发送失败: ${err.message ?: "连接未就绪"}")
            }
        }
    }

    fun selectDevice(device: DeviceEntity) {
        viewModelScope.launch {
            deviceRepository.setCurrentDevice(device.id)
            bluetoothManager.connectToDevice(device)
            _snackbarEvent.emit("正在连接 ${device.name}...")
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
            _snackbarEvent.emit("已添加并连接 ${entity.name}")
        }
    }

    fun deleteDevice(device: DeviceEntity) {
        viewModelScope.launch {
            deviceRepository.deleteDevice(device.id)
            if (currentDevice.value?.id == device.id) {
                bluetoothManager.disconnect()
            }
            _snackbarEvent.emit("已删除设备 ${device.name}")
        }
    }

    fun startDeviceScan() {
        bluetoothManager.startScan()
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
            _snackbarEvent.emit("已全部标记为已读")
        }
    }

    fun deleteMessage(id: String) {
        viewModelScope.launch {
            messageRepository.deleteMessage(id)
            if (_selectedMessage.value?.id == id) {
                _selectedMessage.value = null
            }
            _snackbarEvent.emit("已删除消息")
        }
    }

    fun clearAllMessages() {
        viewModelScope.launch {
            messageRepository.clearAll()
            _selectedMessage.value = null
            _snackbarEvent.emit("已清空收件箱")
        }
    }

    fun simulateIncoming(senderName: String? = null, content: String? = null) {
        val activeName = senderName ?: currentDevice.value?.name ?: "我的 Windows 电脑"
        val text = content ?: "你好，测试一下蓝牙传输。时间：${java.text.SimpleDateFormat("HH:mm:ss", java.util.Locale.getDefault()).format(java.util.Date())}"
        bluetoothManager.simulateIncomingMessage(activeName, text)
    }

    override fun onCleared() {
        super.onCleared()
        bluetoothManager.cleanUp()
    }
}
