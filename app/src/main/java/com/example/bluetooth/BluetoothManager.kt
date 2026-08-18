package com.example.bluetooth

import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothManager as AndroidBluetoothManager
import android.bluetooth.BluetoothServerSocket
import android.bluetooth.BluetoothSocket
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.Build
import android.util.Log
import com.example.data.model.DeviceEntity
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.BufferedReader
import java.io.InputStreamReader
import java.io.OutputStream
import java.util.UUID

data class ScannedBluetoothDevice(
    val name: String,
    val address: String,
    val isBonded: Boolean = false,
    val deviceType: String = "OTHER"
)

/**
 * BluetoothManager 负责蓝牙连接管理：
 *   - scanDevices(): 扫描附近设备（含已配对设备）
 *   - connect(device) / disconnect(device): 建立/断开持久连接（Bluetooth Classic + RFCOMM）
 *   - send(message): 通过已建立的 Socket 发送文本
 *   - observeConnectionState(): 以 StateFlow 暴露 OFFLINE / CONNECTING / ONLINE 状态
 *
 * UI 上的灰 / 黄 / 绿圆点直接由 connectionState 驱动。
 */
class BluetoothManager(private val context: Context) {
    private val tag = "BluetoothManager"
    private val sppUuid: UUID = UUID.fromString("00001101-0000-1000-8000-00805F9B34FB")
    private val appUuid: UUID = UUID.fromString("fa87c0d0-afac-11de-8a39-0800200c9a66")

    private val bluetoothAdapter: BluetoothAdapter? by lazy {
        val manager = context.getSystemService(Context.BLUETOOTH_SERVICE) as? AndroidBluetoothManager
        manager?.adapter ?: BluetoothAdapter.getDefaultAdapter()
    }

    private val scope = CoroutineScope(Dispatchers.IO + Job())

    private val _connectionState = MutableStateFlow(BluetoothConnectionState.OFFLINE)
    val connectionState: StateFlow<BluetoothConnectionState> = _connectionState.asStateFlow()

    private val _activeDevice = MutableStateFlow<DeviceEntity?>(null)
    val activeDevice: StateFlow<DeviceEntity?> = _activeDevice.asStateFlow()

    private val _scannedDevices = MutableStateFlow<List<ScannedBluetoothDevice>>(emptyList())
    val scannedDevices: StateFlow<List<ScannedBluetoothDevice>> = _scannedDevices.asStateFlow()

    private val _isScanning = MutableStateFlow(false)
    val isScanning: StateFlow<Boolean> = _isScanning.asStateFlow()

    private val _incomingMessages = MutableSharedFlow<MessageProtocol.Packet>(extraBufferCapacity = 64)
    val incomingMessages: SharedFlow<MessageProtocol.Packet> = _incomingMessages.asSharedFlow()

    private val _connectionErrors = MutableSharedFlow<String>(extraBufferCapacity = 4)
    val connectionErrors: SharedFlow<String> = _connectionErrors.asSharedFlow()

    private val _scanErrors = MutableSharedFlow<String>(extraBufferCapacity = 4)
    val scanErrors: SharedFlow<String> = _scanErrors.asSharedFlow()

    private var activeSocket: BluetoothSocket? = null
    private var outputStream: OutputStream? = null
    private var readJob: Job? = null
    private var serverJob: Job? = null
    private var serverSocket: BluetoothServerSocket? = null

    private val discoveryReceiver = object : BroadcastReceiver() {
        @SuppressLint("MissingPermission")
        override fun onReceive(context: Context?, intent: Intent?) {
            when (intent?.action) {
                BluetoothDevice.ACTION_FOUND -> {
                    val device: BluetoothDevice? = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                        intent.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE, BluetoothDevice::class.java)
                    } else {
                        @Suppress("DEPRECATION")
                        intent.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE)
                    }
                    device?.let {
                        val name = try { it.name ?: "未知设备" } catch (_: SecurityException) { "未知设备" }
                        val address = it.address ?: return
                        val type = determineDeviceType(name, it.bluetoothClass?.majorDeviceClass)
                        val isBonded = it.bondState == BluetoothDevice.BOND_BONDED
                        val scanned = ScannedBluetoothDevice(
                            name = name,
                            address = address,
                            isBonded = isBonded,
                            deviceType = type
                        )
                        updateScannedList(scanned)
                    }
                }
                BluetoothAdapter.ACTION_DISCOVERY_FINISHED -> {
                    _isScanning.value = false
                }
            }
        }
    }

    private var isReceiverRegistered = false

    init {
        startServerListener()
    }

    fun isBluetoothSupported(): Boolean = bluetoothAdapter != null

    fun isBluetoothEnabled(): Boolean = bluetoothAdapter?.isEnabled == true

    @SuppressLint("MissingPermission")
    fun getPairedDevices(): List<ScannedBluetoothDevice> {
        val adapter = bluetoothAdapter ?: return emptyList()
        return try {
            val bonded = adapter.bondedDevices
            if (bonded.isNullOrEmpty()) {
                emptyList()
            } else {
                bonded.map { device ->
                    val name = device.name ?: "未知设备"
                    val type = determineDeviceType(name, device.bluetoothClass?.majorDeviceClass)
                    ScannedBluetoothDevice(
                        name = name,
                        address = device.address,
                        isBonded = true,
                        deviceType = type
                    )
                }
            }
        } catch (_: SecurityException) {
            emptyList()
        }
    }

    @SuppressLint("MissingPermission")
    fun startScan() {
        _isScanning.value = true
        _scannedDevices.value = emptyList()

        // Include paired devices as initial list
        val paired = getPairedDevices()
        _scannedDevices.value = paired

        val adapter = bluetoothAdapter
        if (adapter == null || !adapter.isEnabled) {
            _isScanning.value = false
            scope.launch {
                _scanErrors.emit("蓝牙未开启或设备不支持，请打开系统蓝牙后重试")
            }
            return
        }

        try {
            if (!isReceiverRegistered) {
                val filter = IntentFilter().apply {
                    addAction(BluetoothDevice.ACTION_FOUND)
                    addAction(BluetoothAdapter.ACTION_DISCOVERY_FINISHED)
                }
                context.registerReceiver(discoveryReceiver, filter)
                isReceiverRegistered = true
            }

            if (adapter.isDiscovering) {
                adapter.cancelDiscovery()
            }
            adapter.startDiscovery()
        } catch (_: SecurityException) {
            _isScanning.value = false
            scope.launch {
                _scanErrors.emit("缺少蓝牙扫描权限，请在系统设置中授权后重试")
            }
        }
    }

    @SuppressLint("MissingPermission")
    fun stopScan() {
        _isScanning.value = false
        try {
            bluetoothAdapter?.cancelDiscovery()
            if (isReceiverRegistered) {
                context.unregisterReceiver(discoveryReceiver)
                isReceiverRegistered = false
            }
        } catch (_: Exception) {
            // Ignored
        }
    }

    private fun updateScannedList(newDevice: ScannedBluetoothDevice) {
        val current = _scannedDevices.value.toMutableList()
        val index = current.indexOfFirst { it.address == newDevice.address }
        if (index >= 0) {
            current[index] = newDevice
        } else {
            current.add(newDevice)
        }
        _scannedDevices.value = current
    }

    @SuppressLint("MissingPermission")
    fun connectToDevice(device: DeviceEntity) {
        scope.launch {
            disconnect()
            _activeDevice.value = device
            _connectionState.value = BluetoothConnectionState.CONNECTING

            val adapter = bluetoothAdapter
            if (adapter == null || !adapter.isEnabled) {
                _connectionState.value = BluetoothConnectionState.OFFLINE
                _connectionErrors.emit("蓝牙未开启或设备不支持，无法连接 ${device.name}")
                return@launch
            }

            try {
                if (adapter.isDiscovering) {
                    adapter.cancelDiscovery()
                }

                val bluetoothDevice = adapter.getRemoteDevice(device.macAddress)
                var socket: BluetoothSocket? = null

                // Try SPP UUID first
                try {
                    socket = bluetoothDevice.createRfcommSocketToServiceRecord(sppUuid)
                    socket.connect()
                } catch (e: Exception) {
                    Log.w(tag, "SPP connection failed, trying fallback custom UUID: ${e.message}")
                    try {
                        socket = bluetoothDevice.createRfcommSocketToServiceRecord(appUuid)
                        socket.connect()
                    } catch (e2: Exception) {
                        Log.w(tag, "Direct socket failed: ${e2.message}")
                    }
                }

                if (socket != null && socket.isConnected) {
                    activeSocket = socket
                    outputStream = socket.outputStream
                    _connectionState.value = BluetoothConnectionState.ONLINE
                    startReading(socket)
                } else {
                    _connectionState.value = BluetoothConnectionState.OFFLINE
                    _connectionErrors.emit(
                        "无法连接到 ${device.name}（${device.macAddress}），请确认目标设备已开启蓝牙并运行 selftrans"
                    )
                }
            } catch (e: Exception) {
                Log.e(tag, "Connection error: ${e.message}")
                _connectionState.value = BluetoothConnectionState.OFFLINE
                _connectionErrors.emit("连接失败：${e.message ?: "未知错误"}")
            }
        }
    }

    fun disconnect() {
        readJob?.cancel()
        readJob = null
        try {
            outputStream?.close()
            activeSocket?.close()
        } catch (_: Exception) {
            // Ignored
        }
        activeSocket = null
        outputStream = null
        _connectionState.value = BluetoothConnectionState.OFFLINE
    }

    suspend fun sendTextMessage(text: String, targetDevice: DeviceEntity): Result<MessageProtocol.Packet> = withContext(Dispatchers.IO) {
        val packet = MessageProtocol.Packet(
            senderId = "local_android",
            senderName = "Android 本机",
            receiverId = targetDevice.id,
            receiverName = targetDevice.name,
            content = text,
            timestamp = System.currentTimeMillis()
        )

        val encoded = MessageProtocol.encode(packet)

        if (_connectionState.value != BluetoothConnectionState.ONLINE) {
            return@withContext Result.failure(IllegalStateException("蓝牙尚未连接目标设备"))
        }

        try {
            val stream = outputStream
            if (stream == null) {
                return@withContext Result.failure(IllegalStateException("蓝牙连接未就绪"))
            }
            stream.write(encoded.toByteArray(Charsets.UTF_8))
            stream.flush()
            Result.success(packet)
        } catch (e: Exception) {
            Log.e(tag, "Send error: ${e.message}")
            Result.failure(e)
        }
    }

    private fun startReading(socket: BluetoothSocket) {
        readJob?.cancel()
        readJob = scope.launch {
            try {
                val reader = BufferedReader(InputStreamReader(socket.inputStream, Charsets.UTF_8))
                while (isActive && socket.isConnected) {
                    val line = reader.readLine() ?: break
                    if (line.isNotBlank()) {
                        val active = _activeDevice.value
                        val packet = MessageProtocol.decode(
                            rawString = line,
                            fallbackSenderId = active?.id ?: "remote",
                            fallbackSenderName = active?.name ?: "远端设备"
                        )
                        _incomingMessages.emit(packet)
                    }
                }
            } catch (e: Exception) {
                Log.w(tag, "Read stream ended or error: ${e.message}")
            } finally {
                _connectionState.value = BluetoothConnectionState.OFFLINE
            }
        }
    }

    @SuppressLint("MissingPermission")
    private fun startServerListener() {
        val adapter = bluetoothAdapter ?: return
        serverJob = scope.launch {
            try {
                serverSocket = adapter.listenUsingRfcommWithServiceRecord("BluetoothTransfer", sppUuid)
                while (isActive) {
                    val socket = serverSocket?.accept() ?: break
                    activeSocket = socket
                    outputStream = socket.outputStream
                    val remoteDevice = socket.remoteDevice
                    val devName = try { remoteDevice.name ?: "配对设备" } catch (_: SecurityException) { "配对设备" }
                    val entity = DeviceEntity(
                        id = remoteDevice.address,
                        name = devName,
                        macAddress = remoteDevice.address,
                        lastKnownState = "ONLINE"
                    )
                    _activeDevice.value = entity
                    _connectionState.value = BluetoothConnectionState.ONLINE
                    startReading(socket)
                }
            } catch (e: Exception) {
                Log.d(tag, "Server socket closed: ${e.message}")
            }
        }
    }

    private fun determineDeviceType(name: String, majorClass: Int?): String {
        val lower = name.lowercase()
        return when {
            lower.contains("pc") || lower.contains("windows") || lower.contains("mac") || lower.contains("laptop") || lower.contains("电脑") -> "PC"
            lower.contains("pad") || lower.contains("tablet") || lower.contains("tab") -> "TABLET"
            lower.contains("phone") || lower.contains("galaxy") || lower.contains("pixel") || lower.contains("iphone") || lower.contains("手机") || lower.contains("xiaomi") -> "PHONE"
            majorClass == 0x0100 -> "PC" // BluetoothClass.Device.Major.COMPUTER
            majorClass == 0x0200 -> "PHONE" // BluetoothClass.Device.Major.PHONE
            else -> "OTHER"
        }
    }

    fun cleanUp() {
        stopScan()
        disconnect()
        serverJob?.cancel()
        try {
            serverSocket?.close()
        } catch (_: Exception) {
            // Ignored
        }
    }
}
