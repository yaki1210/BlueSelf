# BlueSelf

Android ↔ Windows 蓝牙文件与文本极简传输。Bluetooth Classic / RFCOMM 点对点，**无需网络、无需账号**，协议 v2 两端逐字节对齐。

[English](#english) · [下载最新版](https://github.com/yaki1210/BlueSelf/releases/latest)

当前版本：**v0.2.1**

## 功能

- Bluetooth Classic / RFCOMM 点对点双向传输
- 文本消息收发；收件箱按「文本 + 附件」合并为一条记录
- **Windows 收件箱本地持久化**（关闭应用后仍可查看），支持按正文 / 设备名 / 文件名搜索
- 文件收发（照片、PDF 等）：实时进度、MD5 校验、保存到系统下载目录
- 中 / 英文界面可热切换；跟随系统 / 亮色 / 暗色主题，设置持久化
- Windows「添加新设备」：DeviceWatcher 流式扫描、应用内配对、一键连接
- Windows 编辑器支持窗口任意位置拖入文件（文件夹自动展开）
- Android 收到消息 / 文件的通知可点击直达详情；设备类型四信号分类（名字 / CoD / 服务 / 链路自报姓名）
- 协议 v2：长度前缀二进制帧（CRC32）

## 下载

从 [GitHub Releases](https://github.com/yaki1210/BlueSelf/releases/latest) 获取安装包：

- **Windows x64**：自包含 zip，解压即用，无需安装 .NET
- **Android 7.0+**：正式签名 APK，可覆盖安装（与旧版同证书）

Windows 需开启系统蓝牙（Classic / RFCOMM）。Android 需授予蓝牙与通知相关运行时权限。

## 架构文档

- [Android 端架构](docs/android-architecture.md)
- [Windows 端架构](docs/windows-architecture.md)

## 目录结构

```
├── app/                 # Android（Kotlin + Jetpack Compose + Room）
├── windows/             # Windows（WPF + WinRT Bluetooth）
│   └── FileTransferApp.WinUI/
├── docs/
└── README.md
```

## 构建

### Android

需要 JDK 17+（可用 Android Studio 自带 JBR）：

```bash
./gradlew :app:assembleDebug
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

### Windows

需要 .NET 10 SDK，系统蓝牙已开启：

```bash
cd windows/FileTransferApp.WinUI
dotnet build -c Debug
dotnet run -c Debug
```

自包含发布（免装运行时）：

```bash
dotnet publish -c Release -r win-x64 --self-contained
```

## 版本要求

- Android 7.0（API 24）及以上，支持 Bluetooth Classic
- Windows 10/11 x64，支持蓝牙 RFCOMM

---

## English

BlueSelf sends text and files between an Android phone and a Windows PC over **Bluetooth Classic / RFCOMM**. No internet, no account, no cloud.

- Merged inbox rows (text + attachments)
- Windows inbox is persisted locally and searchable
- Live transfer progress, MD5 checks, files saved to Downloads
- Chinese / English UI, light / dark / system theme
- In-app device scan, pairing, and connect on Windows

**Download:** [latest release](https://github.com/yaki1210/BlueSelf/releases/latest)
