# BlueSelf Windows UI 优化 · 实施记录（2026-08-28）

> 本轮全部改动已实施并编译通过。对应计划文档：`windows-ui-redesign-plan.md`（方案 A）、`windows-add-device-redesign-plan.md`（AddDevice 重做）、`windows-merged-implementation-plan.md`（合并计划）。

## 已落地的改动

### Windows 端（全部实测验证）

| 类别 | 改动 | 文件 |
|------|------|------|
| 窗口 | Win11 DWM 圆角 + 去系统描边；最大化自动切直角；Win10 静默降级 | `UI/MainWindow.xaml.cs` |
| 工作区 | 方案 A：删编辑器顶部目标区（含空态提示条）；目标信息由左侧设备栏选中高亮表达 | `UI/Views/WorkspaceView.xaml` |
| 工作区 | 附件托盘彻底去容器：chip 裸排；显隐走 `HasPendingAttachments` 显式通知（修集合实例绑定不重求值 bug） | `WorkspaceView.xaml` + `MainViewModel.cs` |
| 工作区 | placeholder 动态引导：未选目标显示"请在左侧选择要发送的目标设备" | `WorkspaceView.xaml` + `MainViewModel.cs` |
| 工作区 | 设备行平面化：删"连接/设为目标/重试"按钮与状态文字（单连接模型，状态圆点足够）；悬停高亮改为主题化 SurfaceBrush（系统 chrome 高亮已根除）；整行点击按状态分发（在线→设目标 / 离线失败→连接重试 / 连接中→忽略） | `App.xaml` + `DeviceListItem.xaml` + `WorkspaceView.xaml` + `MainViewModel.cs` |
| 拖拽 | 窗口级文件拖拽加附件：隧道事件（PreviewDragOver/PreviewDrop + handledEventsToo）截获，绕开 TextBox 原生拖放类处理器对发送区的封锁；文件夹拖入自动展开；拖文本进编辑器的原生行为保留 | `UI/MainWindow.xaml.cs` + `MainViewModel.cs`（AddAttachmentFiles） |
| 扫描 | AddDevice 页重写：修横幅图标 Text 误绑 BoolToVis 的实锤 bug；扫描旋转动画；流式列表（800ms debounce）；已配对徽标；行尾按钮三态（配对并添加/连接/已连接）；空态三条排查建议；顶栏按钮两态（扫描设备⇄停止扫描）；15s 自动停 | `UI/Views/AddDeviceView.xaml(.cs)` |
| 扫描 | DeviceWatcher 流式扫描器（新）：修 AQS ProtocolId GUID 错误导致枚举恒空的深层根因（旧 GUID 不存在，实测 added=0；修正后 added=9） | `Bluetooth/Core/DeviceWatcherScanner.cs`（新） |
| 分类 | DeviceKind 四信号分类器（新）：名字关键词（与 Android 词表对齐）/ CoD / BlueSelf 服务命中 / sName；IsLikelyPeerDevice 放宽（只滤明确配件类） | `Bluetooth/Core/DeviceKind.cs`（新）+ `Discovery.cs` + `MainViewModel.cs` |
| 文案 | 删 wsSendTo（To: 尾随空格问题随之消失）；新增 adScan/adStopScan/adPairAndAdd/adConnect/adConnected/adEmptyHint1~3 | `Resources/Strings/Zh.xaml` / `En.xaml` |
| 清理 | 旧 DeviceScanner.cs 存根化（类型移除，物理删除留待手动） | `Bluetooth/Core/DeviceScanner.cs` |

### Android 端（编译通过，APK 已产出，待真机验证）

| 类别 | 改动 | 文件 |
|------|------|------|
| 通知 | 点击通知跳转该消息详情页：通知携带 EXTRA_MESSAGE_ID 的 PendingIntent；MainActivity 冷启动（onCreate）/热启动（onNewIntent）双路读取 → Compose LaunchedEffect 消费导航 | `notifications/MessageNotifier.kt` + `MainActivity.kt` |
| 分类 | determineDeviceType 词表与 Windows DeviceKind.cs 完全对齐（补中文词） | `bluetooth/BluetoothManager.kt` |
| 自愈 | sName 事后校准：收到 TXT 后按链路自报姓名重判已存设备 deviceType/name 并回写 Room——存错图标的设备收到第一条消息后自动纠正 | `ui/viewmodel/MainViewModel.kt` |

## 关键踩坑记录

1. **AQS GUID 错误**：原方案里的经典蓝牙 ProtocolId `{e0cbf06c-cd8b-4647-bb8b-7f2ef1233d1f}` 不存在（真值尾部 `bb8a-263b43f0f974`），导致 DeviceWatcher 枚举恒空。独立探针程序实测定位。
2. **WinRT 属性键**：`System.Devices.AepBluetooth.Address/MajorClass` 不是合法属性键（COMException）；CoD 属性在多数驱动下不可用，分类退化为名字 + 服务双信号。
3. **UIPI 拖放拦截**：管理员权限（High IL）启动的进程收不到普通权限资源管理器的拖放。调试验证一律用 `explorer.exe <exe>` 代理以 Medium IL 启动。
4. **TextBox 抢拖放事件**：WPF TextBox 原生拖放类处理器把冒泡事件标为已处理，发送区拖放失效；用隧道事件 + handledEventsToo 在窗口层截获，且只对 FileDrop 标记 Handled（保留拖文本进编辑器）。
5. **集合实例绑定陷阱**：`Visibility` 绑 `ObservableCollection` 实例只在初始化求值一次，增删不触发；需显式 bool 属性 + 通知。

## 验证状态

- Windows 端：编译 0 警告 0 错误；工作区/添加设备页/圆角/拖拽均有运行时截图验证
- Android 端：`gradlew assembleDebug` 通过；真机行为（通知跳转、图标自愈、扫描配对）待用户验证
- 遗留：`DeviceScanner.cs` 物理删除（存根已不参与编译）；扫描列表中名字为空的已配对设备显示"未知设备"（连接后可用真实名回写，待用户反馈）

## 构建方式备忘

- Windows：`C:\Program Files\dotnet\dotnet.exe build windows/FileTransferApp.WinUI/FileTransferApp.WinUI.csproj`（注意 PATH 上的 AutoClaw 自带 dotnet 是纯运行时，会遮蔽系统 SDK；启动验证用 `explorer.exe <exe>` 代理）
- Android：`JAVA_HOME 指向 Android Studio jbr` 后 `gradlew assembleDebug`
