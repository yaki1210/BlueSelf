# Android 端改进计划 · 交互同步 + 可发现性 + 消息通知

> 依据：[connection-research.md](connection-research.md)（v2）方案 A 的 Android 侧同步 + 用户新增需求（可发现性申请 / 通知权限 / 收到消息通知 / 设置中可关闭通知）。
> 前提：先完成 [windows-connection-plan.md](windows-connection-plan.md) 并通过其稳定性测试，再执行本计划（两端交互语义需同步生效）。
> 执行方式：按 A1→A5 顺序，每步含改动文件、具体代码、验证方法。

---

## 总览

| 步骤 | 内容 | 涉及文件（相对 `app/src/main/`） | 需求来源 |
|------|------|------|------|
| A1 | 选中 ≠ 重连（与 Windows W1 同步） | `java/com/example/ui/viewmodel/MainViewModel.kt`、`java/com/example/bluetooth/BluetoothManager.kt` | 报告 P4 |
| A2 | 蓝牙可发现性申请 | `AndroidManifest.xml`、`java/com/example/ui/screens/AddDeviceScreen.kt`、设置页 | 用户新增 |
| A3 | 通知权限申请（Android 13+） | `AndroidManifest.xml`、`java/com/example/MainActivity.kt`（或 HomeScreen） | 用户新增 |
| A4 | 收到消息/文件时发通知 | 新建 `java/com/example/notifications/MessageNotifier.kt`、改 `MainViewModel.kt`、`BluetoothManager.kt` | 用户新增 |
| A5 | 设置中"通知"开关 | `java/com/example/data/settings/SettingsRepository.kt`、`ui/screens/SettingsScreen.kt`、`res/values/strings.xml`、`res/values-en/strings.xml` | 用户新增 |
| A6 | 测试矩阵 | — | 验收 |

---

## A1 · 选中 ≠ 重连（交互同步）

### 目标

已连接设备被再次点击选中时**不再断开重连**；仅当目标变化或当前离线时才建链。

### 改动（`ui/viewmodel/MainViewModel.kt` 的 `selectDevice`）

```kotlin
fun selectDevice(device: DeviceEntity) {
    viewModelScope.launch {
        deviceRepository.setCurrentDevice(device.id)
        // 方案A：已连接该设备 → 仅切换目标，不触碰现有链路
        val active = bluetoothManager.activeDevice.value
        val state = bluetoothManager.connectionState.value
        if (active?.macAddress == device.macAddress && state == BluetoothConnectionState.ONLINE) {
            return@launch   // 链路健康且即为目标：无事可做
        }
        bluetoothManager.connectToDevice(device)
        val ctx = context   // 按现有实现取 context 的方式
        ...  // 原有 Toast/Snackbar 逻辑保持不变
    }
}
```

> 说明：`connectToDevice` 内部开头已有的 `disconnect()` **保留**——它处理"换目标必须断旧链"的场景；上面新增的短路守卫保证"同一目标已在线"时根本不会走到 disconnect。

### 验证

1. 连接成功后，在设备选择列表反复点击当前设备：状态栏"在线"不闪断（不出现 正在连接… → 在线 的过程）；PC 端无重连日志。
2. 切换到另一台设备：正常断开旧连接并连接新设备（原行为保留）。

---

## A2 · 蓝牙可发现性申请

### 背景

当前 Android 端没有任何可发现性请求，手机只有打开系统蓝牙设置页时才对他人可见，导致 PC 端"添加设备"页扫不到未配对的手机（报告 P5 末项）。

### 改动

**1. `AndroidManifest.xml`**：确认已有以下声明（当前已满足，仅核对；`ACTION_REQUEST_DISCOVERABLE` 本身无需额外运行时权限）：

```xml
<!-- Android 12+ 已有 BLUETOOTH_SCAN（含 neverForLocation）/ BLUETOOTH_CONNECT -->
<!-- Android 11- 已有 ACCESS_FINE_LOCATION android:maxSdkVersion="30" -->
```

**2. 添加设备页打开时请求可发现性（`ui/screens/AddDeviceScreen.kt`）**

```kotlin
private const val DISCOVERABLE_DURATION_SECONDS = 300

@Composable
fun AddDeviceScreen(/* 现有参数 */) {
    val context = LocalContext.current
    val discoverableLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) { }   // 结果无所谓：用户拒绝只是这 5 分钟不可见

    LaunchedEffect(Unit) {
        val adapter = (context.getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager)
            ?.adapter          // 注意：android.bluetooth.BluetoothManager → .adapter
        if (adapter?.isEnabled == true && adapter.scanMode != BluetoothAdapter.SCAN_MODE_CONNECTABLE_DISCOVERABLE) {
            runCatching {
                context.startActivity(
                    Intent(BluetoothAdapter.ACTION_REQUEST_DISCOVERABLE).apply {
                        putExtra(BluetoothAdapter.EXTRA_DISCOVERABLE_DURATION, DISCOVERABLE_DURATION_SECONDS)
                    }
                )
            }
            // 或用 discoverableLauncher.launch(intent)——AddDeviceScreen 若非 Activity 上下文必须用 launcher
        }
    }
    ...  // 原有扫描 UI 不变
}
```

> 实现要点：
> - 在 Compose 中**必须用 `rememberLauncherForActivityResult`**（不在 Activity 上下文里直接 `startActivity` 也可以，但用 launcher 更规范且能拿结果）；导入 `androidx.activity.compose.rememberLauncherForActivityResult` 与 `androidx.activity.result.contract.ActivityResultContracts`。
> - 每次进入添加设备页都请求一次即可（系统弹窗很轻），**不做"不再询问"持久化**——可发现性是安全敏感的临时状态，不应永久化。`EXTRA_DISCOVERABLE_DURATION` 最长 3600s，取 300s 足够完成配对。
> - 若项目里 `AddDeviceScreen` 已有类似 `LaunchedEffect(Unit)` 的扫描启动点，把请求放在其前面。

**3.（可选增强）设置页增加"让本机可被发现"入口**：见 A5 的设置区块，按钮触发同一 Intent。若想保持最小改动，可跳过。

### 验证

1. PC 端打开"添加设备"扫描 → 手机端进入"添加设备"页 → 系统弹"允许被发现"→ 同意 → PC 扫描列表 30~60s 内出现该手机。
2. 用户点"拒绝"→ 无崩溃，仅本轮不可见；下次进入页面再次弹窗。

---

## A3 · 通知权限申请（Android 13+）

### 改动

**1. `AndroidManifest.xml`**（`<manifest>` 下、`<application>` 前）：

```xml
<uses-permission android:name="android.permission.POST_NOTIFICATIONS" />
```

**2. `MainActivity.kt`**：在现有权限请求逻辑（`hasAllPermissions` / `requestMissingPermissions` 一带）中并入运行时请求：

```kotlin
private fun requiredRuntimePermissions(): Array<String> {
    val list = mutableListOf<String>()
    // ……现有 BLUETOOTH_SCAN/CONNECT/位置权限逻辑保持不变……
    // Android 13+ 通知权限
    if (Build.VERSION.SDK_INT >= 33 &&
        ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS)
            != PackageManager.PERMISSION_GRANTED) {
        list.add(Manifest.permission.POST_NOTIFICATIONS)
    }
    return list.toTypedArray()
}
```

> 处理原则：把 `POST_NOTIFICATIONS` 加进**现有**的缺失权限集合一起申请（一次系统弹窗），但**拒绝通知权限不应阻塞蓝牙流程**——即：蓝牙权限被拒仍走现有拒绝分支；通知权限被拒不报错（A4 里会静默跳过通知）。若现有代码把所有权限一视同仁，最简单做法是通知权限单独一次 `requestPermissions` 调用、结果忽略。

**3. 拒绝后的降级**：不做"强制跳系统设置"。A4 通知前用 `NotificationManagerCompat.areNotificationsEnabled()` 兜底；用户想改随时可从设置页开关（A5）引导（开关关闭且系统权限也被拒时，点开关可提示去系统设置开启——`Settings.ACTION_APP_NOTIFICATION_SETTINGS` Intent）。

### 验证

1. Android 13+ 设备首次启动：弹通知权限；同意 → 后续 A4 通知可见。
2. 拒绝 → App 正常使用（蓝牙功能不受影响）；设置页开关打开时提示去系统设置开启。

---

## A4 · 收到消息/文件时发通知

### 改动

**1. 新建 `notifications/MessageNotifier.kt`**

```kotlin
package com.example.notifications

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.content.pm.PackageManager
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import com.example.R

/**
 * 收到消息/文件时的通知器。
 * 前置条件：通知渠道已创建、系统通知开关开启、（API 33+）POST_NOTIFICATIONS 已授权。
 */
object MessageNotifier {

    private const val CHANNEL_ID = "incoming_messages"
    private var channelReady = false

    /** 在 App 启动时调用一次（MainActivity onCreate）。 */
    fun ensureChannel(context: Context) {
        if (channelReady) return
        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(
            NotificationChannel(
                CHANNEL_ID,
                context.getString(R.string.notif_channel_name),
                NotificationManager.IMPORTANCE_DEFAULT
            ).apply {
                description = context.getString(R.string.notif_channel_desc)
            }
        )
        channelReady = true
    }

    fun canNotify(context: Context): Boolean {
        if (!NotificationManagerCompat.from(context).areNotificationsEnabled()) return false
        if (android.os.Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS)
                != PackageManager.PERMISSION_GRANTED) return false
        return true
    }

    /** 文本消息通知。 */
    fun notifyMessage(context: Context, senderName: String, preview: String) {
        if (!canNotify(context)) return
        val text = if (preview.length > 60) preview.take(60) + "…" else preview
        val n = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_notify)   // 见第 3 点说明
            .setContentTitle(context.getString(R.string.notif_message_title, senderName))
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(preview))
            .setAutoCancel(true)
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .build()
        runCatching { NotificationManagerCompat.from(context).notify(text.hashCode(), n) }
    }

    /** 文件接收完成通知。 */
    fun notifyFile(context: Context, senderName: String, fileName: String) {
        if (!canNotify(context)) return
        val n = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_notify)
            .setContentTitle(context.getString(R.string.notif_file_title, senderName))
            .setContentText(fileName)
            .setAutoCancel(true)
            .build()
        runCatching { NotificationManagerCompat.from(context).notify(fileName.hashCode(), n) }
    }
}
```

> 通知图标：若项目没有 `ic_stat_notify`，先用现有的 launcher 图标 `R.mipmap.ic_launcher` 代替（可行但不理想），或放一个简单的白色剪影 drawable 到 `res/drawable/ic_stat_notify.xml`（vector）。

**2. `MainActivity.onCreate`**：调用 `MessageNotifier.ensureChannel(this)`。

**3. `MainViewModel.kt`：在接收回调处发通知**

- 在 `observeIncomingMessages()`（收到文本的 Snackbar 处）增加：

```kotlin
val ctx = context
if (isAppInForeground.not() && settingsRepository.notificationsEnabled.value) {
    MessageNotifier.notifyMessage(ctx, senderName, content)
}
```

- 文件接收成功处（`observeFileResults` 中 `FileResult.Success` 分支）同理调用 `notifyFile`。

**4. 前台判断（避免自己正看着屏幕还弹通知）**

`MainViewModel` 增加轻量标志，`MainActivity` 生命周期维护：

```kotlin
// MainViewModel
var isAppInForeground = false
    private set

fun setForeground(active: Boolean) { isAppInForeground = active }
```

```kotlin
// MainActivity：onResume → viewModel.setForeground(true)；onPause → setForeground(false)
// （若已有 lifecycleScope/生命周期观察，并入现有逻辑）
```

> 备选：接入 `androidx.lifecycle:lifecycle-process` 的 `ProcessLifecycleOwner` 更精确，但需要加依赖；上面 onResume/onPause 方案零依赖、够用。

**5. 通知点击行为（可选）**：v1 不做 PendingIntent 跳转（点通知仅消失）。若后续要做，在 `notifyMessage` 里加 `contentIntent` 指向 MainActivity 的 singleTop PendingIntent。

### 验证

1. 手机退到桌面（App 后台）→ PC 发文本 → 状态栏出现通知，标题"来自 XX 的新消息"，预览正常。
2. App 在前台打开 → PC 发文本 → 无通知（走原有 Snackbar 提示）。
3. 系统设置里关闭 App 通知 → 再发 → 无通知无崩溃。
4. Android 13+ 拒绝通知权限 → 同上静默跳过。

---

## A5 · 设置页"通知"开关

### 改动

**1. `data/settings/SettingsRepository.kt`**：增加持久化开关（默认开启）

```kotlin
private val _notificationsEnabled = MutableStateFlow(prefs.getBoolean(KEY_NOTIFICATIONS, true))
val notificationsEnabled: StateFlow<Boolean> = _notificationsEnabled.asStateFlow()

fun setNotificationsEnabled(enabled: Boolean) {
    prefs.edit().putBoolean(KEY_NOTIFICATIONS, enabled).apply()
    _notificationsEnabled.value = enabled
}

// companion object 增加：
private const val KEY_NOTIFICATIONS = "notifications_enabled"
```

**2. `ui/viewmodel/MainViewModel.kt`**：暴露给设置页

```kotlin
val notificationsEnabled: StateFlow<Boolean> = settingsRepository.notificationsEnabled

fun setNotificationsEnabled(enabled: Boolean) = settingsRepository.setNotificationsEnabled(enabled)
```

**3. `ui/screens/SettingsScreen.kt`**：在主题区块之后加一个"通知"区块（沿用 `SettingsBubble` + `SettingsOptionRow` 现有样式）：

```kotlin
Spacer(modifier = Modifier.height(28.dp))

Text(
    text = stringResource(R.string.settings_notifications_section),
    style = MaterialTheme.typography.titleMedium.copy(fontWeight = FontWeight.Bold),
    color = MaterialTheme.colorScheme.onSurface,
    modifier = Modifier.padding(bottom = 10.dp)
)
SettingsBubble {
    SettingsOptionRow(
        label = stringResource(R.string.settings_notifications_on),
        selected = notificationsEnabled,
        testTag = "notifications_on",
        onClick = {
            viewModel.setNotificationsEnabled(true)
            // 若系统级通知被关闭（areNotificationsEnabled=false 或权限被拒），
            // 在此 Toast/snackbar 引导：Settings.ACTION_APP_NOTIFICATION_SETTINGS
        }
    )
    HorizontalDivider(color = MaterialTheme.colorScheme.outline)
    SettingsOptionRow(
        label = stringResource(R.string.settings_notifications_off),
        selected = !notificationsEnabled,
        testTag = "notifications_off",
        onClick = { viewModel.setNotificationsEnabled(false) }
    )
}
```

> 顶部收集状态：`val notificationsEnabled by viewModel.notificationsEnabled.collectAsStateWithLifecycle()`。
> 若想更现代，也可以用 `Switch` 行样式，但沿用现有 `SettingsOptionRow` 与语言/主题两区块视觉最一致。

**4. `res/values/strings.xml`**（设置区块）追加：

```xml
<string name="settings_notifications_section">通知 · 通知</string>
<string name="settings_notifications_on">开启新消息通知</string>
<string name="settings_notifications_off">关闭新消息通知</string>
<string name="notif_channel_name">新消息</string>
<string name="notif_channel_desc">收到文本或文件时通知</string>
<string name="notif_message_title">来自 %1$s 的新消息</string>
<string name="notif_file_title">%1$s 发来了文件</string>
<string name="notif_open_system_settings">系统通知已关闭，请到系统设置中开启</string>
```

**5. `res/values-en/strings.xml`** 同步追加英文。

**6. 双重开关语义**（写进设置页交互）：
- App 内开关 = 应用层闸门（`notificationsEnabled`）
- 系统通知权限/系统开关 = 平台层闸门（`areNotificationsEnabled` + POST_NOTIFICATIONS）
- 只有两者都开才发通知（`canNotify` + `notificationsEnabled.value` 双重检查，见 A4 代码）。
- 引导文案：当用户把 App 内开关打开但系统关闭时，提示 `notif_open_system_settings` 并可跳系统设置。

### 验证

1. 设置页出现"通知"区块，与语言/主题样式一致。
2. 关闭 App 内开关 → 后台收消息无通知（系统权限开着）。
3. 重新打开 → 通知恢复（无需重启 App，StateFlow 即时生效）。
4. 杀进程重启 → 开关状态保持（SharedPreferences 持久化）。

---

## A6 · 测试矩阵（验收）

| # | 场景 | 步骤 | 通过标准 |
|---|------|------|---------|
| N1 | 选中不重连（A1） | 已连接设备反复点击 | 状态不闪断、PC 无重连日志 |
| N2 | 可发现性（A2） | 手机进添加设备页 → PC 扫描 | PC 能扫到并完成配对 |
| N3 | 权限拒绝降级（A2/A3） | 拒绝可发现弹窗/通知权限 | 无崩溃，蓝牙功能正常 |
| N4 | 后台消息通知（A4） | App 退后台 → PC 发文本/文件 | 状态栏通知，预览正确 |
| N5 | 前台不打扰（A4） | App 前台 → PC 发消息 | 无通知，仅 Snackbar |
| N6 | 设置开关（A5） | 关闭开关 → 收消息 | 无通知；重开即恢复 |
| N7 | 持久化（A5） | 开关状态改后杀进程重启 | 状态保持 |
| N8 | 回归 | 与 PC 端收发文本+文件（20MB） | 传输功能不受影响 |

**兼容性抽查**：Android 11（可发现性/位置权限路径）与 Android 13+（POST_NOTIFICATIONS 路径）各测一遍 N2~N6。

---

## 附：依赖与清单核对

- 无新增第三方依赖（`NotificationCompat` 来自已有的 `androidx.core`）。
- Manifest 净增一行：`POST_NOTIFICATIONS`。
- 新增文件 1 个：`notifications/MessageNotifier.kt`。
- 建议按 A1~A5 每步一个 git commit，便于独立回退。
