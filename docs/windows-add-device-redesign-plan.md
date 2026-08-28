# Windows 端「添加新设备」重做方案

> 状态：**待确认**（2026-08-28）
> 范围：`windows/FileTransferApp.WinUI` 的添加设备页（AddDeviceView）重做 + 设备图标分类协议升级（含 Android 端一处小改）
> 背景：手机→PC 连接问题已解决（配对记录退化 + legacy link key 被 Windows 策略拒绝，两端重配后恢复正常传输）。本方案只针对重配后暴露的三个问题：
> 1. 手机端保存的 PC 图标显示为手机图标；
> 2. PC 端扫描发现不了新设备；
> 3. PC 端扫描界面 UI 不正常（扫描图标异常）。

---

## 1. 现状与根因（基于两端代码逐行确认）

### 1.1 电脑图标失效：旧方案从来没有真正"改到"蓝牙名

旧方案的设计意图是"在电脑端的名称添加 Windows 用于被识别"。代码事实：

| 环节 | 代码事实 | 结论 |
|---|---|---|
| PC 端名称生成 | `TransferService.LocalDeviceName()`：机器名不含 `电脑`/`Windows` 时，**返回值**追加 `" Windows"` | 它只是**生成**一个字符串，全工程没有任何代码把这个名字写回系统蓝牙名。PC 实际广播的还是原始机器名（如 `LUXUNUS`） |
| 这个名字用在哪 | 仅用于 PC→手机方向 TXT 帧的 `sName` 字段（`SendTextAsync` 里 `"blueself-pc", LocalDeviceName()`） | 只影响消息层的"发送者显示名" |
| Android 端图标来源 | `addDevice(scanned)` 入库时 `deviceType = scanned.deviceType`，而 `scanned.deviceType` 来自**扫描时**的 `determineDeviceType(设备名, CoD)` | 图标取决于**扫描到的名字**，不是消息里的 sName |
| sName 的实际用途 | Android `handleFrame()` 收到 TXT 后，sName 只传给通知栏 `notifyMessage(senderName, …)` | **从不回写 DeviceEntity**，错型一旦保存就固化 |
| CoD 兜底 | `determineDeviceType` 在名字无关键词时查 `bluetoothClass.majorDeviceClass`（0x0100=PC） | 本机组合（Realtek 适配器 × 一加 11 Qualcomm 栈）上报的 CoD 不可靠，兜底失灵 |

**根因**：旧方案实际只是"PC 发出的消息里带 Windows 字样"，而 Android 入库的名字走"扫描/远端 socket 名"这条不受控路径。两个名字从不校准，且 PC 从未真正修改自己的广播名。删除配对重连后，手机按扫描名重新入库，关键词匹配不到 → 存成 PHONE → 图标错。

### 1.2 PC 端扫描发现不了新设备

`DeviceScanner.ScanAsync()` 的问题（按影响排序）：

1. **发现机制用错了 API 路径**：`BluetoothDevice.GetDeviceSelector()` + `DeviceInformation.FindAllAsync(selector, null, AssociationEndpoint)` 本质是**枚举系统已知的 AEP（Association Endpoint）缓存**，不会主动发起射频层 inquiry。Windows 对未配对设备的 AEP 缓存驱逐很激进：手机短暂可见后很快从枚举里消失，结果是"扫不到新设备"。
2. **过滤条件太激进**：`Discovery.IsLikelyPeerDevice(name, CoD)` 在名字无关键词且 CoD 不可靠时直接丢弃。一加 11 广播名（如"一加 11"）既不含关键词、CoD 又不可靠 → 即使扫到也被滤掉。
3. **逐台 `BluetoothDevice.FromIdAsync()` 串行拉对象**：对未配对设备可能触发后台连接尝试，慢且个别设备卡住拖垮整轮扫描。
4. **一次性 12s 超时后整体返回**：无流式上屏，扫描期间 UI 空白，体验像卡死。

### 1.3 扫描界面不正常

- **扫描图标是空的**：`AddDeviceView.xaml` 状态横幅左侧 36×36 圆里的 `TextBlock` **没有绑定任何 glyph 字符**（`Text` 缺失，`IsScanning` 只控制可见性）——这就是"扫描图标不正常"的直接原因。
- 刷新按钮文案误用 `{DynamicResource wsRefresh}`（"刷新设备列表"），语义错位；按钮本身没有扫描中的动态反馈。
- 空态与扫描中的互斥显示基本正确，但扫描中列表区完全空白，没有引导文案（第一次使用像坏了）。
- "已配对"徽标有数据（`IsPaired`），但未配对设备没有醒目的"可添加"视觉，行尾按钮文案不随配对状态变化。

## 2. 设计目标

1. **图标协议升级**：不再依赖"名字里必须带 Windows"。两端都能稳定区分 PC/手机/平板，且具备事后自愈能力。
2. **PC 端能发现可配对的新设备**：发起真实射频扫描（DeviceWatcher），流式上屏，可直接触发系统配对。
3. **扫描体验重做**：扫描动画、空态引导、未配对徽标、结果即时插入。
4. **两端逻辑对称**：Android 端保存 PC 时同样能被正确分类（同一套分类协议，双向一致）。

## 3. 方案总览

```
┌────────────────────────────┐            ┌────────────────────────────┐
│ Android（一加 11）          │            │ Windows（BlueSelf PC）      │
│                            │            │                            │
│ 扫描：系统发现 + 四信号分类  │            │ 扫描：DeviceWatcher 流式     │
│ 保存：deviceType 入库       │◀═══RFCOMM══▶│ 配对：DeviceInformation     │
│       ▲                    │            │       .PairAsync()          │
│       │ 收到 TXT 的 sName   │            │ 图标：DeviceKind.Classify   │
│       │ 时校准 deviceType   │            │       (name, CoD, service)  │
└────────────────────────────┘            └────────────────────────────┘
```

### 3.1 图标分类协议：从「名字猜测」升级为「四信号表决」

新增统一分类器 `DeviceKind.Classify(name, cod, hasBlueSelfService, senderNameHint)`，返回 `pc | tablet | phone | other`：

| 信号 | 权重 | 来源 | 说明 |
|---|---|---|---|
| ① 名字关键词 | 高 | 蓝牙名 | 保留现有词表，补充 `laptop`/`notebook`/`desktop`/`台式`/`笔电`/`笔记本`，**两端词表完全对齐** |
| ② CoD 主类 | 中 | Class of Device | 名字无关键词时的兜底；Realtek/Qualcomm 上报不可靠，不单独定论 |
| ③ 是否暴露 BlueSelf 服务 | 高 | 扫描时 SDP 缓存命中 / 连接成功事实 | **新增权威信号**：能连上的设备一定装了 BlueSelf，此信号独立于名字与 CoD |
| ④ 链路自报姓名（sName） | 高 | TXT 帧的 `sName` 字段 | 协议已有字段；连接后用于**事后校准**已保存设备的类型与显示名 |

表决逻辑：

```
pc     ← ①命中 PC 词，或 ④sName 命中 PC 词，或（②=Computer 且 ③有服务）
tablet ← ①命中平板词，或 ④sName 命中平板词
phone  ← ①命中手机词，或（②=Phone 且 ③有服务）
other  ← 其余
```

**实施要点**：

- Windows 端：新增 `Bluetooth/Core/DeviceKind.cs` 作为唯一分类实现；`MainViewModel.KindOfName()`、`Discovery.IsLikelyPeerDevice()` 的分类部分全部改为调用它。扫描过滤不再因为"名字不像"而丢弃设备（见 3.2）。
- Android 端：`determineDeviceType()` 词表与 Windows 对齐；并在 `handleFrame()` 的 `FT_TXT` 分支增加校准逻辑——收到 TXT 后，若 `packet.sName` 与已保存的 `DeviceEntity.name/deviceType` 不一致，按四信号重判并回写 Room。效果：**即使当初存错成手机图标，PC 发来第一条消息后图标自动纠正为电脑**。

**为什么不让 PC 程序化改系统蓝牙名**（评估过、否决）：
- 写蓝牙名要动 `BTHPORT` 注册表项，需要管理员权限并重启蓝牙栈；
- 手机端已保存的 `deviceType` 不会因对端改名而自动重判（Room 表无触发逻辑），改系统名对已保存设备无效；
- 消息层 sName 校准零权限、即时生效、双向闭环，且协议字段已存在，只需接上。

### 3.2 发现机制重做：DeviceWatcher 流式扫描

新增 `Bluetooth/Core/DeviceWatcherScanner.cs` 替换 `DeviceScanner.cs`：

```csharp
public sealed record ScannedDevice(
    ulong Address, string Name, bool IsPaired, bool HasBlueSelfService);

public sealed class DeviceWatcherScanner : IDisposable
{
    // AQS（实现时以实际属性调试为准）：
    //  - 限定经典蓝牙 AEP：System.Devices.Aep.ProtocolId := {e0cbf06c-cd8b-4647-bb8b-7f2ef1233d1f}
    //  - (CanPair = True OR IsPaired = True)   ← 放宽：已配对设备也要显示（用于重连）
    // 请求的附加属性：
    //  - System.ItemNameDisplay                （名称）
    //  - System.Devices.Aep.IsPaired           （配对状态）
    //  - System.Devices.Aep.AepId / ...(地址)   （解析 MAC）
    //  - System.Devices.AepBluetooth... Cod 相关属性键（实现时打印候选属性后固化）
    public void Start();                                  // CreateWatcher + 订阅 Added/Updated/Removed
    public event Action<ScannedDevice>? DeviceAdded;      // 事件线程上抛，VM 做 800ms debounce 合并后上屏
    public event Action<ScannedDevice>? DeviceUpdated;
    public event Action<ulong>? DeviceRemoved;
    public void Stop();
}
```

工作流程：

1. `Start()` 创建 `DeviceInformation.CreateWatcher(aqs, additionalProperties, AssociationEndpoint)`；
2. `Added/Updated` 事件里**只从事件参数 properties 读属性**（名称/地址/IsPaired/CoD），不调用 `BluetoothDevice.FromIdAsync`——避免串行拉对象拖慢扫描；
3. 事件线程收集 → VM 层 800ms debounce 合并去重 → UI 流式插入（不等扫描结束）；
4. 单轮扫描 15s 自动停止；扫描中按钮变"停止扫描"，可提前结束；
5. 地址解析：从 AepId（形如 `Bluetooth#Bluetoothxx:xx…-…`）或地址属性中提取 MAC → ulong（实现时以实际属性值格式为准，先打印再固化解析代码）。

**与现状对比**：

| 维度 | 现状（FindAllAsync） | 重做后（DeviceWatcher） |
|---|---|---|
| 是否触发真实射频扫描 | 否（翻系统 AEP 缓存） | 是（系统级 discovery） |
| 未配对设备可见性 | 短暂可见后消失（缓存驱逐） | 持续事件流，Removed 前一直在 |
| 结果上屏 | 12s 超时后一次性 | ~0.8s debounce 流式上屏 |
| 单设备开销 | 每台一次 `FromIdAsync` | 零额外对象创建，纯属性读取 |

**为什么换 32feet/InTheHand 等第三方库**：否决。现有栈是纯 WinRT API，第三方库对 RfcommHost/TransferService 无增益，徒增依赖；WinRT DeviceWatcher 本身足够。

**配对流程**：watcher 事件里的 `DeviceInformation` 保存在 VM 内存（不落盘），点"配对并添加"直接 `info.Pairing.PairAsync()`（默认保护级别）→ 系统弹配对确认框 → 两端确认走 SSP 新密钥（天然绕开本次已确认的 legacy link key 拒绝路径）→ 成功后走现有 `RfcommHost.ConnectAsync` 建链 → 加设备栏 → 切回工作区。现有 `ConnectAndAddAsync` 的"配对→连接→添加"骨架保留，只换前置的设备获取方式。

### 3.3 扫描 UI 重做

| 区域 | 现状 | 重做后 |
|---|---|---|
| 顶栏按钮 | 「刷新设备列表」（误用 wsRefresh） | 「扫描设备」⇄ 扫描中变「停止扫描」 |
| 状态横幅图标 | 空 TextBlock（无 glyph，显示异常） | 蓝牙 glyph（E706）+ 扫描中旋转动画（XAML Storyboard 由 IsScanning 触发） |
| 状态文案 | 静态"正在扫描/扫描完成" | "正在扫描…（已发现 N 台）"，计数实时更新 |
| 结果列表 | 12s 后一次性全量 | 流式插入；每行类型图标（新分类器）+ 未配对高亮徽标 |
| 行尾按钮 | 固定「连接并添加」 | 未配对 →「配对并添加」；已配对未连 →「连接」；已连接 →「已连接 ✓」（禁用态） |
| 空态 | 静态"未发现设备" | 扫描中：脉动蓝牙图标 + "请确认手机 App 在前台、蓝牙可发现"；0 结果：给出 3 条排查建议 |

具体修复点：
1. 状态横幅图标补 glyph + 动画（这是"扫描图标不正常"的直接修复）。
2. 新增资源键 `adScan` / `adStopScan` / `adPairAndAdd` / `adConnect` / `adConnected` / `adEmptyHint1~3`（Zh/En 同步）。
3. 扫描中列表区显示引导文案，空态与扫描中互斥。
4. `DeviceKindToGlyphConverter` 增加 `other` 类型的通用设备 glyph。

### 3.4 配对后自动刷新设备栏

保留现有 `ConnectAndAddAsync` 的"配对 → 连接 → 加入设备栏 → 设为发送目标 → 切回工作区"流程；配对成功后调用 `RefreshDevicesAsync()` 保证设备栏即时可见。唯一变化是设备来源从旧扫描结果换成 3.2 的 watcher + `DeviceInformation.PairAsync()`。

## 4. 改动清单（文件级）

| # | 文件 | 动作 | 内容 |
|---|---|---|---|
| 1 | `windows/.../Bluetooth/Core/DeviceKind.cs` | 新增 | 四信号分类器（词表 + CoD + 服务命中 + sName），两端词表对齐的唯一实现 |
| 2 | `windows/.../Bluetooth/Core/DeviceWatcherScanner.cs` | 新增 | DeviceWatcher 流式扫描（替代 DeviceScanner.cs，旧文件删除） |
| 3 | `windows/.../UI/Views/AddDeviceView.xaml` | 重写 | 扫描动画图标、流式列表、空态引导、按状态变化的行尾按钮 |
| 4 | `windows/.../ViewModels/MainViewModel.cs` | 修改 | ScannedDeviceItem 增加 HasBlueSelfService/CoD；StartScan 改流式回调 + debounce；ConnectAndAddAsync 改走 DeviceInformation.PairAsync；KindOfName 调用点替换为 DeviceKind |
| 5 | `windows/.../UI/Converters.cs` | 微调 | KindToGlyphConverter 支持 "other" |
| 6 | `windows/.../Resources/Strings/Zh.xaml`、`En.xaml` | 修改 | 新增 adScan/adStopScan/adPairAndAdd/adConnect/adConnected/adEmptyHint1~3；顶栏按钮不再用 wsRefresh |
| 7 | `app/.../bluetooth/BluetoothManager.kt` | 修改 | determineDeviceType 词表对齐 + handleFrame 收到 sName 后校准 DeviceEntity（约 15 行） |

> Android 侧只动 BluetoothManager.kt 一个文件，改动极小，可独立发版。

## 5. 不做 / 后续可选

- **不做** PC 程序化改系统蓝牙名（3.1 已论证否决）。
- **不做** 第三方蓝牙库引入（32feet 等）。
- **不做** BLE GATT 通道（现有协议是经典蓝牙 RFCOMM，保持不变）。
- **不做** Android 端 AddDeviceScreen 大改（其发现性请求 ACTION_REQUEST_DISCOVERABLE 逻辑已可用，仅在 Windows 空态文案里加对应引导）。
- 后续可选：设备栏 DeviceItem.Kind 与添加设备页统一走 DeviceKind 单一来源。
- 后续可选：给已配对设备提供"重命名备注"能力，从根上消除命名歧义。

## 6. 风险与验证

**风险与对策**：

| 风险 | 对策 |
|---|---|
| AQS 属性键在部分 Realtek 驱动下缺失（拿不到 CoD/地址） | 分类退化为名字关键词 + sName 校准双保险；地址解析先打印属性再固化 |
| CanPair 条件可能把"已配对但未连接"设备排除 | AQS 放宽为 CanPair OR IsPaired |
| DeviceWatcher 对 BLE 设备也会上报（非对端） | 保留 CoD/名称过滤，但只过滤明确的音频/外设类，不再"不像就丢" |
| PairAsync 系统弹窗被两端任一方拒绝 | 结果状态码落到日志区，UI 提示重试 |
| Android 端校准只对已保存设备生效 | 与现状一致，无回归；未保存设备维持现有行为 |

**验证清单**（实施后逐条执行）：

1. 手机端删除 PC 记录 → 手机端"添加设备"重新扫描 → PC 图标显示为电脑（广播名无 Windows 字样也能判对：CoD + 服务信号）。
2. PC 端添加设备页点「扫描设备」→ 未配对手机（App 前台、蓝牙可发现）在 5~10s 内流式出现在列表 → 点「配对并添加」→ 系统配对弹窗 → 成功后自动加入设备栏且图标为电脑。
3. 扫描动画、空态引导、按钮三态符合 3.3 描述。
4. 反向：PC 先配对手机 → 手机端添加设备扫到 PC → 保存后图标为电脑。
5. Android 端自愈：把已保存 PC 记录的 deviceType 改成 PHONE（Room db）→ PC 发来一条文本 → 图标自动变回 PC、显示名更新为 sName。
6. 回归：已配对设备重连、文本互发、文件互传不受影响（现有 RFCOMM/协议帧不动）。

## 7. 实施顺序（确认后执行）

1. Windows：DeviceKind 分类器 + KindOfName 调用点替换（独立可验证，先行）。
2. Windows：DeviceWatcherScanner + AddDeviceView 重做（主体工作）。
3. Windows：字符串资源 + 转换器微调。
4. Android：词表对齐 + sName 校准（改动最小，可独立发版）。
5. 双端联调（验证清单 1-6）。
