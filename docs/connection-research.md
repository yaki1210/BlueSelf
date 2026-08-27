# BlueSelf 电脑端 ↔ 手机端 连接稳定性调研报告（v2）

> 调研日期：2026-08-27（v2：纳入双向失败实测反馈与评审意见）
> 范围：Android 端（`app/`）与 Windows 端（`windows/FileTransferApp.WinUI/`）的蓝牙连接链路
> 一句话定性：**传输层可靠、连接层脆弱**——连接建立后文件传输一切正常，问题全部集中在连接的建立、检测与恢复。
> 开发主线：**重构 PC 端"连接生命周期管理"**。不要继续优化传输，也不要把 Bluetooth 当成"一次连接成功就长期可靠"的静态资源，而要把它当成一个**随时可能失效、需要检测、恢复、重建的状态机**。

---

## 1. 问题现象

| # | 现象 | 频率 |
|---|------|------|
| S1 | 电脑端点击设备无法连接手机端 | 经常 |
| S2 | 关闭电脑蓝牙再打开后重连，**有时能成功，有时不行** | 偶尔有效 |
| S3 | 已连接的设备再次点击（选中为目标）会触发重新连接 | 必现（设计如此） |
| S4 | 连接失败导致无法发送文件；传输一旦开始则正常 | 经常 |
| S5 | 手机端互传（手机↔手机）很稳定 | —（对照组） |
| S6 | 故障是**双向同时**的：PC 连不上手机时，手机也连不上 PC | 伴随 S1 出现 |
| S7 | 测试时手机端 App 处于前台打开状态（不存在退后台被杀） | —（排除项） |

### 1.1 关键推理（v2 收紧表述）

- S5 + S7 排除"手机端 App / 协议 / 权限"作为主要嫌疑。
- S6 表明故障发生在两个方向共享的公共层，严谨结论为：

> **双向同时失败高度指向 Windows PC 侧共享连接基础设施；Windows Bluetooth Radio/驱动/协议栈是最高优先级嫌疑，同时需要排查 PC 端 RFCOMM 监听生命周期、连接状态机、SDP 缓存与连接竞态。**

- S2（关开 PC 蓝牙"有时恢复"）证明 PC 侧深度参与，但**不构成"Windows 栈是唯一根因"的证明**——如果日志最终显示是 `RfcommServiceProvider` 未重新 advertising，或 SDP 缓存异常，与本结论并不冲突。
- **最终归因依赖步进式诊断日志**（见 4.4），用于区分五种可能：Windows 蓝牙栈问题 / RFCOMM listener 没恢复 / SDP 查询异常 / socket 建连异常 / 应用自己的状态机错误。

---

## 2. 当前连接链路

### 2.1 总体架构

两端都是**双角色**（既是服务端也是客户端），服务 UUID 均为 `fa87c0d0-afac-11de-8a39-0800200c9a66`（两端一致，已核对）：

```
┌─────────────── Android (Kotlin) ───────────────┐      ┌────────────── Windows (C#/WinRT) ──────────────┐
│ BluetoothManager                                │      │ RfcommHost                                      │
│                                                 │      │                                                  │
│ 服务端: listenUsingRfcommWithServiceRecord      │◄─────│ 客户端: BluetoothDevice.FromBluetoothAddressAsync │
│        ("BluetoothTransfer", appUuid)           │      │   → GetRfcommServicesForIdAsync (Cached→Uncached)│
│        while { serverSocket.accept() }          │      │   → StreamSocket.ConnectAsync                   │
│                                                 │      │                                                  │
│ 客户端: createRfcommSocketToServiceRecord       │─────►│ 服务端: RfcommServiceProvider.CreateAsync        │
│        ×3尝试(secure-app/secure-spp/insecure)   │      │   + StreamSocketListener.BindServiceNameAsync    │
│                                                 │      │   + StartAdvertising                            │
└─────────────────────────────────────────────────┘      └──────────────────────────────────────────────────┘
```

连接建立后，两端各自进入读循环：
- Android：`FrameCodec.decode` → `handleFrame` 分发（[BluetoothManager.kt](../app/src/main/java/com/example/bluetooth/BluetoothManager.kt)）
- Windows：`ReadLoop` → `HandleFrameAsync`（[TransferService.cs](../windows/FileTransferApp.WinUI/Bluetooth/TransferService.cs)）

### 2.2 PC → 手机（电脑主动连接）

代码路径：[MainViewModel.cs](../windows/FileTransferApp.WinUI/ViewModels/MainViewModel.cs) `ConnectDeviceAsync` → [RfcommHost.cs](../windows/FileTransferApp.WinUI/Bluetooth/Core/RfcommHost.cs) `ConnectAsync`：

1. `BluetoothDevice.FromBluetoothAddressAsync(btAddress)` — 从地址取设备对象（走系统缓存）。
2. `GetRfcommServicesForIdAsync(Cached)` — 查 Windows 缓存的 SDP 记录；查不到再 `Uncached` 实时空中查询。
3. `StreamSocket.ConnectAsync(hostname, serviceName)` — 按查到的 RFCOMM 通道连接。
4. 整体最多尝试 **2 轮**，**全程无超时控制**。
5. 成功后 `_transfer.Attach(socket, addr)` → 旧 socket 被静默替换。

### 2.3 手机 → PC（手机主动连接）

- PC 端：`RfcommServiceProvider.CreateAsync` 发布服务 → `StartAdvertising` → `ConnectionReceived` → `OnIncomingConnected`。
- 手机端：`connectToDevice` → 先 `disconnect()` 断开现有连接 → `ensureBonded` → 依次尝试 `secure-appUuid → secure-spp → insecure-appUuid`，每种 15s 超时。
- 连入后 PC 解析对端 MAC 匹配设备栏，把该设备置 Online 并设为发送目标。

### 2.4 "添加设备"流程

- PC 端：`DeviceScanner.ScanAsync`（[DeviceScanner.cs](../windows/FileTransferApp.WinUI/Bluetooth/Core/DeviceScanner.cs)）枚举附近设备（12s 超时兜底）+ 已配对合并；点击 → `ConnectAndAddAsync`：未配对则 `PairAsync` → 连接 → 加入设备栏。
- 手机端：`startScan()` 经典蓝牙发现 + 已配对兜底；点击 → `addDevice` → 存 Room → `connectToDevice`。

### 2.5 断链与状态管理（现状摘要）

- PC：读循环退出 → `ClearIfCurrent` → `Detach(silent: true)`，**不触发 `Disconnected` 事件**（详见 P3）。
- Android：读循环 finally 把 `connectionState` 置 OFFLINE，UI 立即变灰。
- 两端均无心跳；PC 的 `IsConnected` 仅判断 `_socket != null`；PC 不监听蓝牙无线电状态变化。

---

## 3. 问题分析

### 3.0 嫌疑结构总览

```
            ┌─ Windows 蓝牙栈整体失效（P0，最高优先级嫌疑，待日志确认）
            │    无线电电源管理挂起 · 睡眠唤醒后栈损坏 · 驱动 wedge · ACL 连接残留
            │
双向同时失败 ┤
(S6)        ├─ P1 PC 监听不自愈（代码实锤）
(S7 排除     │    蓝牙开关后 IsListening 误报 → 入站方向永久断 → S2"有时不行"
手机端)     │
            ├─ P2 连接无超时（代码实锤）
            │    栈异常时 ConnectAsync 挂起几十秒 → S1"连接中"卡死
            │
            ├─ P3 PC 假在线（代码实锤）
            │    silent detach 不触发 Disconnected → S4 绿灯但发不出
            │
            ├─ P4 点击=重连（设计缺陷）→ S3，且失败时主动破坏可用连接
            │
            └─ P5 边界缺陷（MAC 解析/socket 覆盖/双向竞态）→ 偶发串扰
```

P0 与 P1/P2/P5 可能**共同参与**同一次故障（栈抖动是触发器，应用层缺陷决定它能否自愈、症状多重）。区分它们是 4.4 诊断日志的任务。

### P0【最高优先级嫌疑】Windows 蓝牙栈整体进入坏状态（待日志验证）

解释 S6（双向同时失败）的首要候选，典型触发场景：

1. **无线电电源管理**：Windows 默认允许系统/USB 关闭蓝牙适配器省电。无线电挂起再唤醒后栈可能未完全恢复，出站失败 + 入站广播失效。
2. **睡眠/唤醒后栈损坏**：Windows 蓝牙驱动（Intel/Realtek/CSR 等）在 S3/S4 唤醒后偶发 HCI 层 wedge，所有 RFCOMM/SDP 操作失败。
3. **ACL 连接残留**：一次传输断开后 PC 侧 ACL 链路半开（手机认为已断、PC 认为还占着），后续信道建立冲突——"传输正常、下次连接失败"的经典模式。
4. **快切换不彻底**：手动快速开关蓝牙时，部分驱动不会真正给控制器断电复位 → S2 的"有时不行"。

应用层现状对以上**零感知、零自愈**：不监听 `Radio.StateChanged`，任何失败后都用旧对象原样重试。用户手动关开蓝牙即对 P0 做栈复位——"有时成功"= 栈完整复位；"有时不行"= 快切换不彻底，或 P1 导致入站方向仍未恢复。

### P1【代码实锤】PC 端监听器在蓝牙开关后永久失效

[RfcommHost.cs](../windows/FileTransferApp.WinUI/Bluetooth/Core/RfcommHost.cs) + [MainViewModel.cs](../windows/FileTransferApp.WinUI/ViewModels/MainViewModel.cs)：

```csharp
public bool IsListening => _provider != null && _listener != null;   // 蓝牙关闭后仍为 true
public async Task EnsureListeningAsync()
{
    if (_host.IsListening) return;   // 永远命中，不会重启
    ...
}
```

蓝牙无线电关闭再打开后，`RfcommServiceProvider` 的广播实际已死，但对象还在，`IsListening` 误报 `true`，`EnsureListeningAsync` 短路返回。**S2 的另一半解释**：关开蓝牙后出站可能恢复，但入站（手机→PC）因监听未重建而永久失效——用户测"手机连电脑"仍失败，感觉"关蓝牙也没用"。启动日志里"稍后会自动重试"实际没有任何定时重试。

### P2【代码实锤】PC 端连接无超时

- `GetRfcommServicesForIdAsync(Uncached)` 和 `socket.ConnectAsync` **均无应用层超时**：栈异常时单次可挂 20s+，Cached/Uncached × 2 轮串行最坏 1~2 分钟，设备一直黄色"连接中"。
- 把"立刻失败"放大成"长时间挂起"，是 S1 的主要体感来源。

### P3【代码实锤】PC 端链路静默死亡：假在线

[TransferService.cs](../windows/FileTransferApp.WinUI/Bluetooth/TransferService.cs)：

```csharp
private async Task ReadLoop(StreamSocket socket, CancellationToken ct)
{
    ...
    finally { ClearIfCurrent(socket); }   // → Detach(silent: true)
}

private void Detach(bool silent)
{
    ...
    if (hadActive && !silent) Post(() => Disconnected?.Invoke());  // silent=true 永不触发
}
```

手机端断开后，PC 读循环退出、`_socket` 置空，但 `Disconnected` 事件不触发、设备栏不置灰——绿灯"在线"，点发送才被"尚未连接目标设备"拦下。**用户看到"手机在线"，点发送，却收到"当前未连接"，这种体验会让人误以为整个软件都不可靠**——P3 是必须优先修的体验问题。

### P4【设计缺陷】"点击 = 重连"

- PC：`ConnectDeviceCommand` 每次点击都 `ConnectDeviceAsync` → 新建 socket → `Attach` 内部先销毁旧连接。
- Android：`selectDevice` → `connectToDevice` → **第一行就 `disconnect()`**。

"选中发送目标"与"建立连接"两个语义被压进同一次点击：换目标就推倒重来一次连接；在 P0 窗口期重连失败会把原本可用的连接也干掉；每次抖动都被放大成"软件不稳定"。

### P5【低】边界缺陷

| 缺陷 | 位置 | 影响 |
|------|------|------|
| 手机连入但 MAC 解析失败 → `ConnectedPeerAddress=null` | `OnIncomingConnected` / `TryParseMacToUlong` | 发送被 `wrongTarget` 拦截："当前连接的不是所选设备" |
| `RefreshDevicesAsync` 按 `SelectedDevice` 恢复状态，把旧目标标 Online | `RefreshDevicesAsync` | 假在线（P3 的另一来源） |
| 手机端 accept 循环直接覆盖 `activeSocket` 且不 close | `startServerListener` | socket 泄漏；双向同时连接状态错乱 |
| 双端同时互连存在竞态，`Attach` 各自替换 | 两端 | 偶发连接串扰 |
| 手机未申请可发现性 | Android 端 | PC"添加设备"页扫不到未配对新手机 |

### 3.x 已排除项与待定论

| 假设 | 状态 | 依据 |
|------|------|------|
| 手机端 App 退后台被冻结/杀死 | 已排除（对本次主诉） | S7：前台打开仍双向失败 |
| 手机端协议/权限/代码缺陷 | 已排除 | S5：互传同一套管线稳定 |
| "Windows 蓝牙栈是唯一根因" | **表述过强，不予采用** | 应用层 P1/P2/P5 可能共同参与；最终归因待诊断日志 |
| Windows Radio/驱动/栈 | 最高优先级嫌疑 | S2 恢复方式指向 PC 侧；待日志确认 |

### 3.y 现象 ↔ 嫌疑映射

| 现象 | 主要嫌疑 | 备注 |
|------|---------|------|
| S1 电脑连不上手机 | P0（待日志确认）+ P2（放大为挂起） | |
| S2 关开蓝牙有时行有时不行 | 成功=栈完整复位；不行=P1（listener 未重建）或 P0-4 | 待日志区分 |
| S3 已连接设备点击又重连 | P4 | 方案 A 根治 |
| S4 传输正常但发不出 | P3（假在线）+ P5（MAC 边界） | |
| S6 双向同时失败 | P0 最高优先级嫌疑；P1/P2/P5 可能共同参与 | 依赖 4.4 日志定论 |

---

## 4. 改进方案

开发目标（一句话）：

> **不要把 Bluetooth 当成"一次连接成功就长期可靠"的静态资源，而要把它当成一个随时可能失效、需要检测、恢复、重建的状态机。**

目标连接模型：

```
设备存在
   │
   ├── 未连接 ──────► [连接]
   │                    │
   │                    ├─ 成功 ──► 已连接
   │                    │
   │                    └─ 失败 ──► 重试/恢复蓝牙（分级阶梯）
   │
   └── 已连接 ──────► [仅选择为发送目标]
                          │
                          └─ 不重新建立连接
```

### 4.1 方案 A：连接交互重写——"选中 ≠ 连接"（第一优先级）

设备行拆成三态，动作按钮与状态解耦：

```
设备名称        已连接
手机A           ● 在线        [设为目标]

设备名称        未连接
手机B           ○ 离线        [连接]

设备名称        连接异常
手机C           ! 失败        [重试]
```

- 点击**在线设备的主体区域**：只负责选中为发送目标（只改 `_selectedDevice`，不碰 socket）。
- 只有点击 **[连接] / [重试]** 才执行 RFCOMM 建链。
- 收益：即使 Windows 蓝牙栈偶发抖动，也不会因为用户只是换一个发送目标而把当前正常连接主动干掉。
- Android 端同步修改：`selectDevice` 不再先 `disconnect()`；目标相同且 ONLINE → 直接返回；离线才建链。
- 发送校验修正：`wrongTarget` 分支兼容 `ConnectedPeerAddress == null`；发送失败区分"链路已断（提示重连）"与"传输错误"。

### 4.2 方案 B：显式连接状态机——"`_socket != null` ≠ 已连接"

用五态状态机取代散落的多头真相（`DeviceItem.Status` / `IsListening` / `_socket != null` / `connectionState`）：

```
              [连接]/自动重连
Disconnected ───────────► Connecting ──成功──► Connected
     ▲                        │                   │
     │                    失败/超时           写失败/读停滞
     │                        │                   ▼
     │                        │              Degraded ──探测成功──► Connected
     │                        │                   │探测失败
     │                        ▼                   ▼
     │                   Reconnecting ◄───────────┘
     │                        │
     │           阶梯恢复成功 ──► Connected
     │                        │
     └──── 阶梯穷尽（含 Radio 复位后仍失败）◄────┘
```

- **Disconnected**：无链路（初始/断开后）。
- **Connecting**：建链进行中；**互斥**——同一时刻只允许一个 Connecting（消除 P5 竞态）。
- **Connected**：由**实际链路状态确认**（读循环存活 + 写成功），而不是 socket 对象是否存在。
- **Degraded**：写失败/读停滞等疑似异常 → 触发探测（轻量读写探测，暂不需要完整心跳协议）。
- **Reconnecting**：按分级恢复阶梯（4.5-E2）自动恢复中。
- 状态机是后续所有检测/恢复机制的**地基**，先于恢复策略落地。

### 4.3 方案 C：修复假在线（silent detach）

- 读循环退出 → **非静默** Detach → 触发 `Disconnected` → 设备栏实时变灰。
- `RefreshDevicesAsync` 不再把旧 `SelectedDevice` 标成 Online（P5 第二来源）。
- MAC 解析失败时给出明确日志与降级路径，而不是把 `ConnectedPeerAddress` 置 null 后被发送校验拦截。
- 心跳（PING/PONG）**暂缓**：它主要解决"已经断了但 UI 还显示在线"，而当前最痛的是"根本建立不了连接"。先把连接建立与恢复机制做好，稳定后再决定是否需要心跳（见实施顺序第 ⑨ 步）。

### 4.4 方案 D：连接超时 + 步进式诊断日志（可观测性先行）

**超时**：`GetRfcommServicesForIdAsync` / `ConnectAsync` 统一包 `AsTask().WaitAsync(TimeSpan.FromSeconds(8))`，快速失败，失败后短退避（1s/2s/4s）。

**诊断日志**：现在最需要知道的不是"连接失败了"，而是失败发生在哪一步。目标形态：

```text
[14:31:02] Radio = On
[14:31:02] Device = XX:XX
[14:31:02] BluetoothDevice acquired
[14:31:03] Cached SDP = 0 services
[14:31:03] Uncached SDP started
[14:31:11] Uncached SDP timeout
[14:31:11] Rebuilding listener
[14:31:12] Retry #1
[14:31:20] ConnectAsync timeout
[14:31:20] Recovery = Radio reset
[14:31:24] Radio = Off
[14:31:27] Radio = On
[14:31:29] Listener advertising = true
[14:31:31] Retry #2 succeeded
```

打点事件清单：`Radio = On/Off`（Radio.StateChanged）、`BluetoothDevice acquired/failed`、`Cached SDP = N`、`Uncached SDP started/timeout/N`、`ConnectAsync started/timeout/refused/ok`、`Listener rebuilt / advertising = true`、`Recovery = <level>`、`Retry #N`、最终结果。

有了它才能最终区分：

```text
Windows 蓝牙栈问题 vs RFCOMM listener 没恢复 vs SDP 查询异常
        vs socket 建连异常 vs 应用自己的状态机错误
```

**差分测试**（排障时区分应用层 vs 系统层）：用 Windows 自带"通过蓝牙发送文件"（fsquirt.exe）对同一手机发送——也失败 ⇒ 系统/驱动层（P0），去设备管理器禁用/启用蓝牙适配器或更新驱动；成功而本 App 失败 ⇒ 应用层路径，抓日志定位。

### 4.5 方案 E：Radio 状态监控 + 分级恢复

**E1. 无线电状态监控 + 监听自动重建（针对 P1）**
- 监听 `Windows.Devices.Radios.Radio.GetRadiosAsync()` 中蓝牙无线电的 `StateChanged`；
- 蓝牙 Off→On：主动销毁并重建 `RfcommServiceProvider` + `StreamSocketListener`（`StopAdvertising` → 重建 → `StartAdvertising`，WinRT 明确提供该生命周期 [1]），延迟 1~2s 等栈就绪；
- 把"启动监听失败自动重试"做成真正的定时器（如 30s 一次直到成功）。
- 效果：手动/系统引起的蓝牙开关后，**入站方向自动恢复**。

**E2. 连接失败分级恢复阶梯（把"失败 3 次就关蓝牙"改为逐级升格）**

```text
连接失败
   ↓
 1. 重新获取 BluetoothDevice
   ↓
 2. Cached SDP
   ↓
 3. Uncached SDP
   ↓
 4. 重建 StreamSocket
   ↓
 5. 短退避重试
   ↓
 6. 检查 Bluetooth Radio State
   ↓
 7. 重建 RfcommServiceProvider / Listener
   ↓
 8. 必要时请求 Radio Off
   ↓
 9. 等待 StateChanged = Off（确认真正生效）
   ↓
10. Radio On
   ↓
11. 等待 StateChanged = On（确认真正生效）
   ↓
12. 重新初始化整个蓝牙连接管理器
```

每一级失败才升到下一级；每一步动作与结果都打诊断日志（4.4）。

**E3. Radio Off/On 是恢复策略的最后一级，不是普通重试**

`Radio.SetStateAsync()` 不是无条件可用的硬复位接口 [2]：

- 受**用户权限、硬件和系统策略限制**，调用前需确认访问权限，返回值/结果需要检查；
- 状态转换是**异步**的——调用后必须**等待 `StateChanged` 事件或重新读取 `State`** 确认真正生效，不能固定 sleep 后假设完成（阶梯第 9/11 步即为此设计）；
- Radio 复位是全局性动作（影响电脑上所有蓝牙设备），触发时 UI 应给用户可见提示（"正在恢复蓝牙…"）。

### 4.6 方案 F：传输层迁移——现阶段明确不做

当前事实是"连接建立 → 文件传输正常"，说明现有 RFCOMM + 传输协议本身能工作。现在直接换 Wi-Fi/LAN，相当于**为了"门锁有问题"把整栋房子的运输系统重做**。D1（BLE 发现 + 局域网传输）留作远期备选，仅当 E 全套落地 + 日志确认驱动级不可恢复问题时再评估。

### 4.7 Android 前台服务（可选，非当前主线）

若希望锁屏/后台时 PC 仍能随时连入手机，可加 `connectedDevice` 类型前台服务常驻监听。当前主诉（前台打开仍双向失败）下非必需。

### 4.8 方案对比

| 方案 | 解决 | 成本 | 收益 | 风险 |
|------|------|------|------|------|
| A 选中≠连接 + 三态设备行 | S3、部分 S4 | 低 | 换目标不再破坏连接 | 无 |
| B 显式状态机 | 地基 | 中 | `Connected` 有真实语义；消除竞态 | 重构范围中等 |
| C 假在线修复 | S4 | 低 | 状态可信 | 无 |
| D 超时+诊断日志 | S1 挂起、归因能力 | 低 | 快速失败、可定位 | 无 |
| E1 Radio 监控+监听重建 | S2（不行的一半） | 低 | 蓝牙开关后自动恢复入站 | 低 |
| E2 分级恢复阶梯 | S1/S2 深层 | 中 | 逐级自愈，不再依赖手动 workaround | 阶梯需日志调参 |
| E3 Radio Off/On | 栈 wedged | 中 | 最后一级兜底 | SetStateAsync 权限/异步确认 [2] |
| F 传输层迁移 | — | 高 | 现阶段不做 | — |

---

## 5. 实施顺序

按评审确定的 ①~⑨ 执行；其中**诊断日志**按"正式修改前增加"的原则拆为两步：第 0 步先落最小版本（Radio 状态 + SDP/Connect 结果打点），后续每一步的验证都靠它，第 ⑧ 步再收口成完整版：

| 步骤 | 内容 | 对应方案 | 备注 |
|------|------|---------|------|
| 第 0 步 | 诊断日志（最小版本） | D | 先行落地，作为后续每步的验证手段 |
| ① | 选设备 ≠ 建立连接 | A | 第一优先级 |
| ② | 显式 Connected/Disconnected 状态机 | B | `Connected` 由实际链路确认 |
| ③ | 修复 silent detach / 假在线 | C | |
| ④ | 所有 SDP / Connect 增加超时 | D | |
| ⑤ | Radio.StateChanged + Listener 自动重建 | E1 | |
| ⑥ | 连接失败分级恢复 | E2 | |
| ⑦ | 程序化 Radio Off/On | E3 | 恢复策略最后一级 |
| ⑧ | 诊断日志完整版 | D | 覆盖分级恢复全链路打点 |
| ⑨ | 心跳 PING/PONG | 可选 | 稳定后再决定是否需要 |

验收方式：①~⑦ 每步完成后，用第 0 步的日志在真机复现"关开蓝牙 / 睡眠唤醒 / 正常使用"三个场景，确认对应打点符合预期。

---

## 6. 参考文献

- [1] Radio Class (Windows.Devices.Radios) — Microsoft Learn：
  https://learn.microsoft.com/en-us/uwp/api/windows.devices.radios.radio
- [2] Radio.SetStateAsync(RadioState) Method — Microsoft Learn：
  https://learn.microsoft.com/en-us/uwp/api/windows.devices.radios.radio.setstateasync

## 7. 附录：关键代码位置

| 主题 | 文件 |
|------|------|
| Android 扫描/配对/连接/读循环/服务监听 | [BluetoothManager.kt](../app/src/main/java/com/example/bluetooth/BluetoothManager.kt) |
| Android 业务状态机（selectDevice/addDevice） | [MainViewModel.kt](../app/src/main/java/com/example/ui/viewmodel/MainViewModel.kt) |
| Android 权限清单 | [AndroidManifest.xml](../app/src/main/Android/AndroidManifest.xml) |
| PC RFCOMM 服务端/客户端（IsListening 缺陷所在） | [RfcommHost.cs](../windows/FileTransferApp.WinUI/Bluetooth/Core/RfcommHost.cs) |
| PC 连接收发管线（silent detach 缺陷所在） | [TransferService.cs](../windows/FileTransferApp.WinUI/Bluetooth/TransferService.cs) |
| PC 设备栏/连接/添加设备逻辑 | [MainViewModel.cs](../windows/FileTransferApp.WinUI/ViewModels/MainViewModel.cs) |
| PC 扫描/已配对枚举 | [DeviceScanner.cs](../windows/FileTransferApp.WinUI/Bluetooth/Core/DeviceScanner.cs)、[Discovery.cs](../windows/FileTransferApp.WinUI/Bluetooth/Core/Discovery.cs) |
| 协议帧定义（两端对齐） | [MessageProtocol.kt](../app/src/main/java/com/example/bluetooth/MessageProtocol.kt)、[MessageProtocol.cs](../windows/FileTransferApp.WinUI/Bluetooth/Core/MessageProtocol.cs) |
