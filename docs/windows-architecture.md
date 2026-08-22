# BlueSelf · Windows 端架构

Windows 端是 BlueSelf 桌面端，提供发送文本/文件、添加设备（扫描+配对）、收件箱与设置持久化。宿主为 **WPF（.NET 10, net10.0-windows…）**，通过 **WinRT 蓝牙 API** 与 Android 互传，协议 v2 与 Android 逐字节对齐。

## 技术栈

- 语言：C#（nullable enabled）
- 框架：WPF darting 自绘标题栏；MVVM（手写 `ObservableObject` / `RelayCommand`，无外部框架）
- 蓝牙：`Windows.Devices.Bluetooth`、`Windows.Networking.Sockets.StreamSocket`/`DataReader`/`DataWriter`
- 协议：与 Android 相同的 v2 二进制帧（见 [MessageProtocol.cs](FileTransferApp.WinUI/Bluetooth/Core/MessageProtocol.cs)）
- 设置持久化：`System.Text.Json` 写 `%LOCALAPPDATA%\BlueSelf\settings.json`

## 模块结构

```
FileTransferApp.WinUI/
├── App.xaml(.cs)                    # 主题/语言热切换（SwapDictionary）、入口
├── UI/
│   ├── MainWindow.xaml(.cs)         # 自绘标题栏、导航区、可折叠日志、激活自动刷新
│   ├── Views/                       # Workspace / Inbox / Settings / AddDevice
│   ├── Controls/                    # DeviceListItem / TransferStatusPanel / AttachmentChip
│   └── Converters.cs                # 可见性/状态/图标转换器
├── ViewModels/
│   ├── MainViewModel.cs             # 全局状态机（设备/连接/发送/收件箱/设置/添加设备）
│   ├── AppSettingsStore.cs          # 设置 JSON 持久化（语言/主题/保存目录）
│   ├── ObservableObject.cs  RelayCommand.cs
└── Bluetooth/
    ├── TransferService.cs           # StreamSocket 帧读写、收发管线、事件
    └── Core/
        ├── RfcommHost.cs            # RFCOMM 服务端广播 + 客户端连接（Cached/Uncached）
        ├── Discovery.cs             # 已配对设备枚举 + 对端类型过滤
        ├── DeviceScanner.cs         # 添加设备页的附近扫描
        └── MessageProtocol.cs       # 帧编解码（与 Android 对齐）
```

## 传输协议 v2

与 Android 完全一致：`Magic(0x42 0x53) + Version(0x02) + Flags + Type + Seq(4) + Len(4) + Payload + CRC32(4)`。类型、文件元数据字段、分块参数（64KB / 窗口 16）均对齐。`FrameCodec.EncodeBatch` 一次写整窗口；`WriteFramesAsync` 用 `StoreAsync`（不走 `FlushAsync`，避免蓝牙流控挂起）。

## 连接生命周期

- **服务端**：`RfcommHost.StartListeningAsync` 用 `RfcommServiceProvider.CreateAsync` 发布 BlueSelf 服务，`IncomingConnected` 事件在 UI 线程回调 `OnIncomingConnected`：解析对端 MAC → 更新设备栏状态与发送目标。
- **客户端**：`RfcommHost.ConnectAsync` 先 `Cached` 再 `Uncached` 查询服务并 `ConnectAsync`，两次整体尝试。任何一次点击设备都触发重新连接（`ConnectDeviceCommand`）。
- **断开**：`TransferService` `Detach`/`ClearIfCurrent`（只清理自身 socket，避免误杀新连接），`Disconnected` 事件把设备全部置灰，保证可再次点击重连。

## 收发管线（TransferService）

- 发送：`SendTextAsync`（TXT 携带 messageId 建父消息）→ `SendFileAsync`（0% 起始、按窗口发送 `FILE_CHUNK`、末帧 `FILE_END`）。
- 接收：`TextReceived(messageId, content)` 与 `FileReceived(InboundFile{ParentMessageId})` 供收件箱按 messageId 合并"文本+附件"为一条；`ReceiveStarted(name)` 驱动状态面板显示本次接收文件名。
- 事件：`Progress`、`TransferCompleted`（发送/接收结束都收起面板）、`Info`/`LogError`（进日志区）、`Disconnected`。

## MVVM 关键设计（MainViewModel）

- **设备栏**：整行可点击（无持久选中框），点击即 `ConnectDeviceAsync`（黄连接中 → 绿成功 / 灰失败，任意时刻可重连）。
- **目标提示**：`IsTargetRowVisible = SelectedDevice != null && !ShowTargetHint`，与"请选择目标"提示互斥，避免重叠。
- **传输面板**：`TransferDirectionText`（传输中/接收中）随进度方向切换；`FileName` 发送/接收分别设置，`ResetTransferUi` 清残。
- **设置持久化**：语言/主题/保存目录写入 `settings.json`，启动时加载并应用；语言切换触发支持字符串的 UI 刷新（设备状态、计数、待发大小）。

## 关键文件

- [Bluetooth/TransferService.cs](FileTransferApp.WinUI/Bluetooth/TransferService.cs)：连接与收发核心。（当前主干为 WPF 项目，路径 `FileTransferApp.WinUI/`）
- [ViewModels/MainViewModel.cs](FileTransferApp.WinUI/ViewModels/MainViewModel.cs)：应用状态机。
- [Bluetooth/Core/RfcommHost.cs](FileTransferApp.WinUI/Bluetooth/Core/RfcommHost.cs)：RFCOMM 服务端/客户端。
- [Bluetooth/Core/MessageProtocol.cs](FileTransferApp.WinUI/Bluetooth/Core/MessageProtocol.cs)：帧编解码。

## 构建与运行

```bash
cd windows/FileTransferApp.WinUI
dotnet build -c Debug
dotnet run -c Debug
```

依赖 .NET 10 SDK；需要系统开启蓝牙并支持 RFCOMM（Classic）。