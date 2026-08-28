# BlueSelf

跨设备蓝牙文件与文本极简传输工具。Android 手机与 Windows 电脑通过 Bluetooth Classic / RFCOMM 点对点互传文本与文件,协议 v2 两端逐字节对齐。

## 功能

- 通过 Bluetooth Classic / RFCOMM 点对点传输（双向）
- 文本消息收发，收件箱按“文本 + 附件”合并为一条记录
- 文件收发（照片、PDF 等），支持实时进度、MD5 校验、保存到系统下载目录
- 中 / 英文界面（可热切换），支持跟随系统 / 亮色 / 暗色主题，设置持久化
- Windows 端内置“添加新设备”页：DeviceWatcher 流式扫描、应用内配对、一键连接
- Windows 端编辑器支持文件拖拽（窗口任意位置，文件夹自动展开）；单连接模型下设备栏状态圆点即可判断状态，点击行即重试
- Android 端收到消息/文件的通知可点击直达该消息详情；设备类型四信号分类（名字/CoD/服务/链路自报姓名）+ 收到消息后图标自愈
- 协议 v2：长度前缀二进制帧（CRC32），二进制与文件安全

## 目录结构

```
├── app/                 # Android 端(Kotlin + Jetpack Compose + Room)
├── windows/             # Windows 端(WPF + WinRT Bluetooth)
│   └── FileTransferApp.WinUI/
├── docs/
│   ├── android-architecture.md    # Android 端架构
│   ├── windows-architecture.md    # Windows 端架构
│   ├── windows-ui-changelog-2026-08-28.md  # 本轮 UI 优化实施记录
│   └── windows-ui-redesign-plan.md 等      # 方案与计划文档
└── README.md
```

## 架构文档

- [Android 端架构](docs/android-architecture.md)
- [Windows 端架构](docs/windows-architecture.md)

## 构建

### Android

```bash
# 使用 Android Studio 自带 JDK 17+ 构建 Debug
set JAVA_HOME=E:\programs\Android\Android Studio\jbr
./gradlew :app:assembleDebug
# USB 真机安装
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

### Windows

```bash
cd windows/FileTransferApp.WinUI
dotnet build -c Debug
dotnet run -c Debug
```

依赖 .NET 10 SDK,系统需开启蓝牙(Classic / RFCOMM)。

## 版本要求

- Android 7.0(API 24)及以上,支持 Bluetooth Classic,需开启蓝牙并授予运行时权限
- Windows 需支持蓝牙 RFCOMM,并在系统中开启蓝牙