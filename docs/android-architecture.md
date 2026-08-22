# BlueSelf · Android 端架构

Android 端是 BlueSelf 移动端，实现蓝牙发现、配对、点对点 RFCOMM 连接、文本与文件收发，以及本地收件箱（Room 持久化）。

## 技术栈

- 语言：Kotlin
- UI：Jetpack Compose（Material 3）
- 数据层：Room（SQLite）
- 蓝牙：Android Bluetooth Classic（RFCOMM / SPP）+ `bluetooth-adapter` 原生 API
- 异步：Kotlin Coroutines（`viewModelScope`、`Dispatchers.IO`、StateFlow/SharedFlow）

## 模块结构

```
app/src/main/java/com/example/
├── MainActivity.kt          # 应用入口、导航宿主
├── bluetooth/               # 蓝牙连接与传输管线
│   ├── BluetoothManager.kt      # 扫描/配对/连接/读循环/服务端监听
│   ├── FileTransferManager.kt   # 分块窗口发送、接收落盘、进度/结果事件
│   ├── MessageProtocol.kt       # 协议 v2 帧编解码、JSON 元数据
│   └── BluetoothConnectionState.kt
├── data/
│   ├── db/                  # Room：MessageDao / FileDao / DeviceDao / AppDatabase
│   ├── model/               # MessageEntity / FileEntity / DeviceEntity
│   ├── repository/          # MessageRepository / DeviceRepository
│   ├── files/               # ReceivedFileManager（暂存、移入系统 Downloads）
│   └── settings/            # SettingsRepository（语言/主题）
└── ui/
    ├── screens/             # Home / AddDevice / Inbox / MessageDetail / Settings
    ├── viewmodel/MainViewModel.kt  # 统一状态机（设备、消息、文件、连接）
    ├── components/          # DeviceSelectionSheet 等
    ├── theme/  Format.kt    # 主题与格式化工具
```

## 传输协议 v2

与 Windows 端**逐字节对齐**：

- 帧布局（大端）：`Magic(0x42 0x53)` + `Version(0x02)` + `Flags` + `Type` + `Seq(4)` + `Len(4)` + `Payload` + `CRC32(4)`。
- 类型：`FT_TXT(0x10)`、`FT_FILE_START(0x11)`、`FT_FILE_CHUNK(0x12)`、`FT_FILE_END(0x13)`、`FT_FILE_ACK(0x14)`、`FT_ERR(0xF0)`。
- 帧编解码见 [MessageProtocol.kt](app/src/main/java/com/example/bluetooth/MessageProtocol.kt)：`FrameCodec.encodeBatch` 把整个窗口合成一次写；`decode` 按长度读回单帧。
- 文件元数据（`FileMetaJson`）：`id`、`msgId`（父消息）、`name`、`mime`、`size`、`md5`、`chunkSize`、`totalChunks`。
- 分块参数：`DEFAULT_CHUNK_SIZE = 64KB`，`SEND_WINDOW = 16`。

## 连接生命周期（BluetoothManager）

1. **扫描**：`startScan()` 枚举附近经典蓝牙（含已配对），`ensureBonded()` 触发系统配对（`createBond` + 等待 `ACTION_BOND_STATE_CHANGED`）。
2. **连接**：`connectToDevice()` 依次尝试 `secure-appUuid` → `secure-spp` → `insecure-appUuid`，成功后 `startReading(socket)` 进入读循环。
3. **服务端**：`startServerListener()` 用 `listenUsingRfcommWithServiceRecord("BluetoothTransfer", appUuid)` 等待手机对端连入，被连入时同样进入读循环。
4. **读循环**：`FrameCodec.decode` → `handleFrame` 分发到文本流 `incomingMessages` 或文件管线 `fileTransfer.onFrame`。

## 文件接收管线（FileTransferManager）

- 接收端把"读帧"与"写盘"解耦：读循环把 `FILE_CHUNK` 放进有界队列，独立 writer 线程落盘，避免磁盘拖慢蓝牙读。
- 事件流：
  - `fileStarts` → `MainViewModel.observeFileStarts` 建文件行（`status=RECEIVING`）；
  - `progress`（`StateFlow`）→ `observeFileProgress` 只更新 `receivedBytes`（**不写 status**）；
  - `results` → `observeFileResults` 用定向 UPDATE 置 `COMPLETE / FAILED`。
- **状态收敛关键**：三个接收协程通过单一 `Mutex`（`receiveDbLock`）串行化所有 Room 写，并采用**定向列更新**（只写 `status`/`bytes`），避免"整行旧快照覆盖 COMPLETE"。

## 关键文件

- [BluetoothManager.kt](app/src/main/java/com/example/bluetooth/BluetoothManager.kt)：连接/扫描/收发入口。
- [FileTransferManager.kt](app/src/main/java/com/example/bluetooth/FileTransferManager.kt)：发送窗口与接收落盘。
- [MessageProtocol.kt](app/src/main/java/com/example/bluetooth/MessageProtocol.kt)：协议编解码。
- [MainViewModel.kt](app/src/main/java/com/example/ui/viewmodel/MainViewModel.kt)：业务状态机与接收协调。
- [ReceivedFileManager.kt](app/src/main/java/com/example/data/files/ReceivedFileManager.kt)：接收文件保存到系统 Downloads（API 29+ 走 MediaStore，否则重命名移动）。

## 版本要求

- Android 7.0（API 24）+，设备支持 Bluetooth Classic，需开启蓝牙并授予运行时权限（Android 12+ 为 `BLUETOOTH_SCAN / BLUETOOTH_CONNECT / BLUETOOTH_ADVERTISE`）。