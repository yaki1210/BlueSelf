# BlueSelf

跨设备蓝牙文件与文本极简传输工具。

## 功能

- 通过 Bluetooth Classic / RFCOMM 点对点传输
- 文本消息收发，收件箱管理
- 文件收发（照片、PDF 等），支持进度显示、MD5 校验、保存到系统下载目录
- 中 / 英文界面，支持明暗主题
- 协议 v2：长度前缀二进制帧（CRC32），二进制与文件安全

## 目录结构

```
app/src/main/java/com/example/
├── bluetooth/       # 蓝牙连接、帧编解码、文件传输管线
├── data/            # Room 数据库（devices / messages / files）、文件存储管理
├── ui/              # Compose 界面（主页、收件箱、消息详情、设置、添加设备）
└── MainActivity.kt  # 应用入口与导航
```

## 构建

```bash
# 编译 Debug
./gradlew :app:assembleDebug
# 运行单元测试
./gradlew :app:testDebugUnitTest
```

## 版本要求

- Android 7.0（API 24）及以上
- 需要设备具备 Bluetooth Classic 能力并开启蓝牙、授以相关运行时权限