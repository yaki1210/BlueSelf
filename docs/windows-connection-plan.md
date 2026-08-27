# Windows 端连接生命周期重构 · 实施计划

> 依据：[connection-research.md](connection-research.md)（v2）实施顺序第 0 步 + ①~⑦
> 目标：**传输层可靠、连接层脆弱** → 把连接当成"随时可能失效、需要检测、恢复、重建的状态机"。
> 前提：项目 TFM 为 `net10.0-windows10.0.19041.0`，`Windows.Devices.Radios` 等 WinRT API 可直接使用，无需改 csproj。
> 执行方式：按 W0→W7 顺序逐步执行，**每完成一步做对应验证再进入下一步**。W8 为最终稳定性测试矩阵。

---

## 总览

| 步骤 | 内容 | 涉及文件（均为相对 `windows/FileTransferApp.WinUI/`） | 解决 |
|------|------|------|------|
| W0 | 步进式诊断日志 | 新建 `Bluetooth/Core/ConnectionLog.cs`；改 `RfcommHost.cs`、`TransferService.cs`、`MainViewModel.cs` | 可观测性（后续每步的验证手段） |
| W1 | 选中 ≠ 连接（三态设备行） | `ViewModels/MainViewModel.cs`、`UI/Controls/DeviceListItem.xaml`、`UI/Views/WorkspaceView.xaml`、`UI/Converters.cs` | S3 |
| W2 | 显式连接状态机 | `Bluetooth/TransferService.cs`、`ViewModels/MainViewModel.cs` | `_socket != null` ≠ 已连接 |
| W3 | 修复假在线（silent detach 等） | `Bluetooth/TransferService.cs`、`ViewModels/MainViewModel.cs` | S4 |
| W4 | SDP/Connect 超时 + 退避 | `Bluetooth/Core/RfcommHost.cs` | S1 挂起 |
| W5 | Radio 监控 + Listener 自动重建 | `Bluetooth/Core/RfcommHost.cs`、`ViewModels/MainViewModel.cs` | S2（入站不恢复） |
| W6 | 连接失败分级恢复 | `ViewModels/MainViewModel.cs`、`Bluetooth/Core/RfcommHost.cs` | S1/S2 深层 |
| W7 | 程序化 Radio Off/On（最后一级） | `ViewModels/MainViewModel.cs` | 栈 wedged 兜底 |
| W8 | 稳定性测试矩阵 | — | 验收 |

---

## W0 · 步进式诊断日志

### 目标

连接失败时能回答"失败发生在哪一步"：Radio 状态 / BluetoothDevice 获取 / Cached SDP / Uncached SDP / ConnectAsync / Listener 状态 / 恢复动作 / 重试结果。

### 改动

**1. 新建 `Bluetooth/Core/ConnectionLog.cs`**

```csharp
namespace FileTransferApp.WinUI.Bluetooth.Core;

/// <summary>
/// 连接诊断日志：静态事件总线，任何层都可打点，UI 层订阅后转投日志区。
/// 每条带时间戳，格式与调研报告 4.4 的目标形态一致。
/// </summary>
internal static class ConnectionLog
{
    public static event Action<string>? Entry;

    public static void Write(string step, string detail = "") =>
        Entry?.Invoke($"[{DateTime.Now:HH:mm:ss}] {step}{(detail.Length > 0 ? $": {detail}" : "")}");
}
```

**2. `ViewModels/MainViewModel.cs` 构造函数中订阅**（`WireTransferEvents()` 调用之后加一行）

```csharp
ConnectionLog.Entry += line => Post(() => Log(line));
```

### 打点插入位置（本步先落最小集）

| 文件 | 位置 | 打点 |
|------|------|------|
| `RfcommHost.cs` | `StartListeningAsync` 成功后 | `Write("Listener advertising = true")` |
| `RfcommHost.cs` | `StartListeningAsync` catch 内 | `Write("Listener start failed", ex.Message)` |
| `RfcommHost.cs` | `ConnectAsync` 拿到 device 后 | `Write($"Device = 0x{btAddress:X12}", device.Name)` |
| `RfcommHost.cs` | 每次 `GetRfcommServicesForIdAsync` 返回后 | `Write($"{mode} SDP = {result.Services.Count} services")` |
| `RfcommHost.cs` | `ConnectAsync` 成功 return 前 | `Write("ConnectAsync ok")` |
| `RfcommHost.cs` | catch 内 | `Write($"{mode} connect failed", ex.Message)` |
| `TransferService.cs` | `ReadLoop` finally | `Write("ReadLoop ended")` |
| `TransferService.cs` | `Attach` | `Write("Socket attached", peerAddress?.ToString("X12") ?? "unknown")` |

### 验证

`dotnet build -c Debug && dotnet run -c Debug`，点击设备栏任一设备，日志区应出现完整的步进序列（Device → Cached SDP = N → Uncached SDP = N → ConnectAsync ok/failed）。把手机蓝牙关掉再点一次，确认能看到失败发生在哪一步。

---

## W1 · 选中 ≠ 连接（三态设备行）

### 目标

点击**在线设备的主体区域只切换发送目标**（不碰 socket）；只有点击动作按钮才建链。消除"换个目标就把可用连接干掉"。

### 改动

**1. `ViewModels/MainViewModel.cs`：`DeviceStatus` 枚举增加 `Failed`**

```csharp
public enum DeviceStatus { Online, Connecting, Offline, Failed }
```

`DeviceItem.StatusText` 的 switch 加分支：`DeviceStatus.Failed => "statFailed"`（见第 5 点资源）。

**2. `MainViewModel` 新增"仅设为目标"命令**（放在 `ConnectDeviceCommand` 旁边）

```csharp
/// <summary>点击设备主体：仅切换发送目标，绝不触碰 socket。</summary>
public RelayCommand SetTargetCommand => new(p =>
{
    if (p is DeviceItem d) SelectAsTarget(d);
});

private void SelectAsTarget(DeviceItem d)
{
    _selectedDevice = d;
    _selectedAddress = d.Address;
    _peerName = d.Name;
    ShowTargetHint = false;
    OnPropertyChanged(nameof(SelectedDevice));
    OnPropertyChanged(nameof(HasSelectedDevice));
    OnPropertyChanged(nameof(IsTargetRowVisible));
    ConnectionLog.Write("Target set", $"{d.Name} (no reconnect)");
}
```

**3. `MainViewModel.DeviceItem` 增加动作按钮文案属性**

```csharp
/// <summary>右侧动作按钮文案：在线=设为目标，连接中=…，失败=重试，离线=连接。</summary>
public string ActionText => _status switch
{
    DeviceStatus.Online => "设为目标",
    DeviceStatus.Connecting => "…",
    DeviceStatus.Failed => "重试",
    _ => "连接"
};
```

（在 `Status` 的 setter 里追加 `OnPropertyChanged(nameof(ActionText));`。）

**4. `MainViewModel.ConnectAsync`（建链路径）失败时置 `Failed` 而非 `Offline`**

```csharp
catch (Exception ex)
{
    device.Status = DeviceStatus.Failed;   // 原 Offline
    Log($"连接 {device.Name} 失败: {ex.Message}");
}
```

`OnDisconnected` / `OnIncomingConnected` 中的置灰逻辑不变（全部回 Offline）。

**5. `UI/Converters.cs`：`StatusToBrushConverter` 增加 `Failed` 分支**（红色，参考现有 Online/Offline 的取色方式，加 `DeviceStatus.Failed => FailedBrush` 之类的资源；若不想加新资源，先复用 Offline 灰色 + `ActionText` 区分也可接受，建议加红色）。

**6. `UI/Views/WorkspaceView.xaml`：设备行模板改为"主体 + 动作按钮"双命令结构**

把现有 DataTemplate 中包住 `DeviceListItem` 的单个 Button 替换为：

```xml
<DataTemplate>
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*" />
            <ColumnDefinition Width="Auto" />
        </Grid.ColumnDefinitions>
        <!-- 主体：点击 = 设为目标（在线设备）；点击 = 连接（离线/失败设备） -->
        <Button Grid.Column="0" Background="Transparent" BorderThickness="0" Padding="0"
                HorizontalContentAlignment="Stretch" VerticalContentAlignment="Center" Cursor="Hand"
                Command="{Binding DataContext.RowClickCommand, RelativeSource={RelativeSource AncestorType={x:Type ItemsControl}}}"
                CommandParameter="{Binding}">
            <controls:DeviceListItem />
        </Button>
        <!-- 动作按钮：显式建链入口 -->
        <Button Grid.Column="1" Style="{StaticResource PillButtonOutline}" Margin="4,0,0,0"
                Padding="10,3" FontSize="11" Cursor="Hand"
                Command="{Binding DataContext.ConnectDeviceCommand, RelativeSource={RelativeSource AncestorType={x:Type ItemsControl}}}"
                CommandParameter="{Binding}"
                Content="{Binding ActionText}"
                Visibility="{Binding Status, Converter={StaticResource ConnectingToCollapsedConverter}, FallbackValue=Visible}" />
    </Grid>
</DataTemplate>
```

> `ConnectingToCollapsedConverter`：新增一个简单转换器（`DeviceStatus.Connecting → Collapsed`，其余 `Visible`），避免"连接中"时按钮可点。也可以不加转换器、在命令里判断状态，二选一。

**7. `MainViewModel` 新增 `RowClickCommand`（主体点击的分发逻辑）**

```csharp
/// <summary>设备行主体点击：在线 → 仅设为目标；离线/失败/连接中 → 不动作（建链只走显式按钮）。</summary>
public RelayCommand RowClickCommand => new(p =>
{
    if (p is not DeviceItem d) return;
    if (d.Status == DeviceStatus.Online) SelectAsTarget(d);
});
```

**8. `MainViewModel.ConnectDeviceAsync` 开头增加"已连接则只切目标"的短路**

```csharp
private async Task ConnectDeviceAsync(DeviceItem device)
{
    // 已连接该设备：仅设为目标，不重建链路（方案 A 核心）。
    if (_transfer.IsConnected &&
        _transfer.ConnectedPeerAddress is ulong addr && addr == device.Address)
    {
        SelectAsTarget(device);
        ConnectionLog.Write("Already connected", "set target only");
        return;
    }
    // ……原有建链逻辑不变
}
```

### 验证

1. 连接成功后，点击设备行主体：日志出现 `Target set ... (no reconnect)`，**没有**新的 SDP/Connect 打点 → 目标切换不重连。
2. 点击动作按钮 [连接]：走完整建链流程。
3. 断开手机后（手机关蓝牙），PC 点击设备失败：状态显示"重试"红色（Failed），按钮文案变为"重试"。

---

## W2 · 显式连接状态机

### 目标

`Connected` 由实际链路确认（读循环存活），取代 `_socket != null`；为 W3/W5/W6 提供统一状态源。

### 改动（`Bluetooth/TransferService.cs`）

**1. 新增枚举与事件**

```csharp
/// <summary>连接状态机的显式状态（取代“socket 对象是否存在”的隐式判断）。</summary>
public enum ConnState { Disconnected, Connecting, Connected, Reconnecting }

// TransferService 内：
public ConnState State { get; private set; } = ConnState.Disconnected;
public event Action<ConnState>? StateChanged;

private void SetState(ConnState s)
{
    if (State == s) return;
    State = s;
    Post(() => StateChanged?.Invoke(s));
}
```

**2. 状态迁移点**

| 位置 | 迁移 |
|------|------|
| `Attach` 开头（`Detach(silent:true)` 之后） | `SetState(ConnState.Connecting)` |
| `ReadLoop` 第一帧成功 decode 之后（或进入 try 后立即） | `SetState(ConnState.Connected)` |
| `ReadLoop` finally（`ClearIfCurrent` 前） | `SetState(ConnState.Disconnected)` |
| `WriteFramesAsync` catch / 上抛前 | 记录 `LastWriteError`，状态留给 W3/W6 处理 |

**3. `MainViewModel` 订阅并驱动设备栏**

```csharp
_transfer.StateChanged += s => Post(() =>
{
    foreach (var d in Devices)
        d.Status = s switch
        {
            ConnState.Connected when d.Address == _transfer.ConnectedPeerAddress => DeviceStatus.Online,
            ConnState.Connecting or ConnState.Reconnecting when d.Address == _selectedAddress => DeviceStatus.Connecting,
            _ => DeviceStatus.Offline
        };
});
```

> 现有 `OnDisconnected` 保留（做日志与 `_selectedAddress = 0`），设备栏置灰改由 `StateChanged` 统一驱动，避免双头更新。

### 验证

手机断开后（走远/关蓝牙）：日志出现 `ReadLoop ended` → `State = Disconnected` → 设备栏几秒内变灰（读循环 EOF 立即感知）。此前是永远绿灯。

---

## W3 · 修复假在线（silent detach / 状态恢复 / MAC 边界）

### 改动

**1. `TransferService.ClearIfCurrent`：读循环自然退出 → 非静默**

```csharp
private void ClearIfCurrent(StreamSocket socket)
{
    if (!ReferenceEquals(_socket, socket)) return; // 已被新连接替换，不动
    Detach(silent: false);   // 原为 true：读循环退出=链路死亡，必须通知 UI
}
```

> `Attach` 内部调用的 `Detach(silent: true)`（主动替换旧连接）保持不变，二者语义不同。

**2. `MainViewModel.RefreshDevicesAsync`：状态恢复以真实连接为准**

把 `var targetAddr = SelectedDevice?.Address ?? _selectedAddress;` 及其后的 `d.Address == targetAddr ? Online : Offline` 改为：

```csharp
var connectedAddr = _transfer.ConnectedPeerAddress;   // null = 未连接
// …
Status = connectedAddr is ulong ca && d.Address == ca ? DeviceStatus.Online : DeviceStatus.Offline
```

选中项恢复逻辑保留（`match` 那段不动）。

**3. `MainViewModel.StartTransfer`：`wrongTarget` 校验兼容 MAC 未解析的入站连接**

```csharp
var notConnected = !_transfer.IsConnected;
var connectedAddr = _transfer.ConnectedPeerAddress;
// 仅在能确定对端地址且确实不匹配时才判 wrongTarget；地址未知（入站连接 MAC 解析失败）时放行。
var wrongTarget = !notSelected && !notConnected &&
                  connectedAddr is ulong addr && addr != SelectedDevice!.Address;
```

### 验证

1. 连接成功后手机关蓝牙：设备栏 1~2s 内变灰（原来不变）。
2. 变灰后点发送：提示"尚未连接目标设备"（符合真实状态）。
3. 手机重新连入 PC（PC 重开监听后）：设备栏自动变绿且能直接发送。

---

## W4 · SDP / Connect 超时 + 短退避

### 改动（`Bluetooth/Core/RfcommHost.cs` 的 `ConnectAsync`）

**1. 全流程超时（8s SDP / 10s Connect）**

```csharp
private static readonly TimeSpan SdpTimeout = TimeSpan.FromSeconds(8);
private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

// SDP 查询（Cached 与 Uncached 都包）：
var result = await device.GetRfcommServicesForIdAsync(
    RfcommServiceId.FromUuid(MessageProtocol.AppServiceId), mode)
    .AsTask().WaitAsync(SdpTimeout);

// 建链：
var socket = new StreamSocket();
await socket.ConnectAsync(
    serviceInfo.ConnectionHostName,
    serviceInfo.ConnectionServiceName,
    SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication)
    .AsTask().WaitAsync(ConnectTimeout);
```

**2. 每轮失败后短退避（1s / 2s）**

在 `catch (Exception ex)` 块末尾加：

```csharp
lastError = ex;
ConnectionLog.Write($"Attempt {attempt + 1} failed", ex.Message);
if (attempt < 1) await Task.Delay(TimeSpan.FromSeconds(attempt + 1));
```

**3. 超时异常的文案**：`TimeoutException` 单独给出"查询/连接超时"提示（`lastError` 处理时判断类型），便于用户区分"手机不可达"与"服务未开启"。

### 验证

手机关蓝牙（或走远）后点击 [连接]：最坏 ~36s 内必然返回失败（8+10)×2 + 退避），不再出现"连接中"永久挂起；日志能看到 `Attempt 1 failed: timeout` 之类的明确原因。

---

## W5 · Radio 状态监控 + Listener 自动重建

### 改动

**1. `RfcommHost.cs`：新增 `RestartAsync`（销毁并重建 provider/listener）**

```csharp
private readonly SemaphoreSlim _listenLock = new(1, 1);

/// <summary>销毁并重建监听（蓝牙开关后调用；DisposeAsync 会把 IsListening 置假）。</summary>
public async Task RestartAsync()
{
    await _listenLock.WaitAsync();
    try
    {
        await DisposeAsync();
        await StartListeningAsync();
    }
    finally { _listenLock.Release(); }
}
```

（`StartListeningAsync` 开头也套同一把锁，防止并发重建。）

**2. `MainViewModel`：蓝牙无线电状态监控**

新增字段与方法（`InitAsync` 末尾调用 `InitRadioWatcherAsync()`）：

```csharp
private Windows.Devices.Radios.Radio? _btRadio;

private async Task InitRadioWatcherAsync()
{
    try
    {
        var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
        _btRadio = radios.FirstOrDefault(r => r.Kind == Windows.Devices.Radios.RadioKind.Bluetooth);
        if (_btRadio == null) { ConnectionLog.Write("Radio watcher", "no bluetooth radio found"); return; }

        _btRadio.StateChanged += (radio, _) =>
        {
            ConnectionLog.Write("Radio", radio.State.ToString());
            if (radio.State == Windows.Devices.Radios.RadioState.On)
                _ = RecoverAfterRadioOnAsync();
        };
        ConnectionLog.Write("Radio initial", _btRadio.State.ToString());
    }
    catch (Exception ex) { ConnectionLog.Write("Radio watcher failed", ex.Message); }
}

/// <summary>蓝牙 Off→On 后：延迟等栈就绪 → 重建监听 → 刷新设备栏。</summary>
private async Task RecoverAfterRadioOnAsync()
{
    await Task.Delay(1500);                       // 等无线电完全就绪
    try
    {
        await _host.RestartAsync();
        Post(() => Log("蓝牙已重新开启，监听已重建"));
    }
    catch (Exception ex)
    {
        Post(() => Log($"监听重建失败: {ex.Message}"));
    }
    await RefreshDevicesAsync();
}
```

**3. 启动监听失败 → 真正的定时重试**

`InitAsync` 里改为持有重试定时器（失败 30s 后重试，成功即停）：

```csharp
private System.Windows.Threading.DispatcherTimer? _listenRetryTimer;

// EnsureListeningAsync 的 catch 分支里：
_listenRetryTimer ??= new System.Windows.Threading.DispatcherTimer
{
    Interval = TimeSpan.FromSeconds(30)
};
_listenRetryTimer.Tick += async (_, _) =>
{
    if (_host.IsListening) { _listenRetryTimer.Stop(); return; }
    await EnsureListeningAsync();
};
_listenRetryTimer.Start();
```

（`EnsureListeningAsync` 成功的分支里 `Stop` 该定时器。）

### 验证（对应 S2 核心场景）

1. 连接正常后，**关闭 PC 蓝牙**：日志 `Radio = Off`。
2. **重新打开 PC 蓝牙**：日志依次出现 `Radio = On` → `Listener advertising = true`（重建成功）。
3. 手机端随即发起连接 → **能连上**（修复前：手动开关蓝牙后手机永远连不进 PC）。
4. 启动 App 前先关蓝牙再开：若监听失败，30s 内日志出现自动重试并最终成功。

---

## W6 · 连接失败分级恢复

### 目标

把"点一次失败就结束"改为分级升格重试；Radio 复位前的每一级都是无损/低干扰动作。

### 改动

**1. `RfcommHost.ConnectAsync` 增加可选重试级别参数**（签名变化，供恢复编排调用）

```csharp
public static async Task<StreamSocket> ConnectAsync(ulong btAddress, int maxAttempts = 2)
```

（循环 `for (var attempt = 0; attempt < maxAttempts; attempt++)`，W4 的退避已内置。）

**2. `MainViewModel.ConnectAsync`（建链方法）改为分级升格**

```csharp
private async Task ConnectAsync(DeviceItem device)
{
    device.Status = DeviceStatus.Connecting;
    Log($"正在连接 {device.Name}…");
    try
    {
        var socket = await RfcommHost.ConnectAsync(device.Address);
        OnConnected(device, socket);                    // 抽出：Attach + 状态 + 日志
        return;
    }
    catch (Exception ex)
    {
        ConnectionLog.Write("Connect L1 failed", ex.Message);
    }

    // L2：重建监听后重试（修复 P1 类故障）
    Log("常规连接失败，重建监听后重试…");
    device.Status = DeviceStatus.Connecting;
    try
    {
        await _host.RestartAsync();
        var socket = await RfcommHost.ConnectAsync(device.Address);
        OnConnected(device, socket);
        return;
    }
    catch (Exception ex)
    {
        ConnectionLog.Write("Connect L2 failed", ex.Message);
    }

    // L3：检查无线电状态；异常则进入 W7 的 Radio 复位（最后一级）
    var radioState = _btRadio?.State.ToString() ?? "unknown";
    ConnectionLog.Write("Radio check", radioState);
    if (_btRadio is { State: Windows.Devices.Radios.RadioState.On })
    {
        Log($"连接 {device.Name} 失败（蓝牙无线电正常，疑似对端未开服务或不可达）");
        device.Status = DeviceStatus.Failed;
        return;
    }

    Log("蓝牙无线电异常，尝试自动恢复…");
    device.Status = DeviceStatus.Connecting;
    try
    {
        await ResetBluetoothRadioAsync();               // W7 实现
        await Task.Delay(1500);
        await _host.RestartAsync();
        var socket = await RfcommHost.ConnectAsync(device.Address);
        OnConnected(device, socket);
    }
    catch (Exception ex)
    {
        ConnectionLog.Write("Connect L3 failed", ex.Message);
        device.Status = DeviceStatus.Failed;
        Log($"连接 {device.Name} 失败: {ex.Message}（可尝试在系统设置中重开蓝牙）");
    }
}

private void OnConnected(DeviceItem device, StreamSocket socket)
{
    _selectedAddress = device.Address;
    _peerName = device.Name;
    _transfer.Attach(socket, device.Address);
    foreach (var d in Devices) if (d.Address != device.Address) d.Status = DeviceStatus.Offline;
    device.Status = DeviceStatus.Online;
    Log($"已连接 {device.Name}");
}
```

### 验证

1. 正常路径行为不变（L1 成功）。
2. 模拟 L2 场景：手动关闭再快速打开 PC 蓝牙（监听死亡态），点 [连接]：日志应出现 `L1 failed` → `Listener advertising = true`（重建）→ 连接成功。
3. 全部失败时：设备行进入红色 `Failed`（重试）态，日志有完整分级轨迹。

---

## W7 · 程序化 Radio Off/On（恢复策略最后一级）

> 注意（依据 Microsoft Learn 文档）：`Radio.SetStateAsync` 受用户权限、硬件与系统策略限制；状态转换是**异步**的，必须等待 `StateChanged` 或重新读取 `State` 确认生效，不能固定 sleep 假设完成。

### 改动（`MainViewModel` 新增）

```csharp
/// <summary>
/// 程序化复位蓝牙无线电（恢复阶梯最后一级）。
/// SetStateAsync 受权限/系统策略限制，返回值必须检查；
/// 状态转换是异步的——每一步都等待 StateChanged 确认，而非固定延时。
/// </summary>
private async Task ResetBluetoothRadioAsync()
{
    if (_btRadio == null) throw new InvalidOperationException("未找到蓝牙无线电");

    ConnectionLog.Write("Recovery", "Radio reset requested");

    var offConfirmed = WaitForRadioState(Windows.Devices.Radios.RadioState.Off);
    var setOff = await _btRadio.SetStateAsync(Windows.Devices.Radios.RadioState.Off);
    if (setOff != Windows.Devices.Radios.RadioAccessStatus.Allowed)
        throw new InvalidOperationException($"关闭蓝牙无线电被拒绝（{setOff}），请在系统中手动重开蓝牙");
    await offConfirmed;                                   // 等 StateChanged = Off

    var onConfirmed = WaitForRadioState(Windows.Devices.Radios.RadioState.On);
    await _btRadio.SetStateAsync(Windows.Devices.Radios.RadioState.On);
    await onConfirmed;                                    // 等 StateChanged = On

    ConnectionLog.Write("Recovery", "Radio reset done");
}

/// <summary>注册一次性 StateChanged 监听，等待无线电到达目标状态（8s 超时兜底）。</summary>
private Task WaitForRadioState(Windows.Devices.Radios.RadioState target)
{
    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    Windows.Devices.Radios.Radio radio = _btRadio!;
    Windows.Foundation.TypedEventHandler<Windows.Devices.Radios.Radio, object> handler = (s, _) =>
    {
        if (s.State == target) tcs.TrySetResult(true);
    };
    radio.StateChanged += handler;
    // 若已是目标状态，立即完成
    if (radio.State == target) tcs.TrySetResult(true);
    return tcs.Task.WaitAsync(TimeSpan.FromSeconds(8)).ContinueWith(_ =>
    {
        radio.StateChanged -= handler;                    // 无论成败都解绑
    });
}
```

> 补充说明：`StateChanged` 回调在 W5 已被 `InitRadioWatcherAsync` 订阅，Radio On 事件会自动触发 `RecoverAfterRadioOnAsync`（重建监听），与本处逻辑天然配合——复位完成后监听自动恢复，无需额外调用。

### 验证

1. 制造栈 wedged 场景（连续快速开关蓝牙数次后连接失败）：点 [连接] → 日志应出现 `Recovery: Radio reset requested` → `Radio = Off` → `Radio = On` → `Listener advertising = true` → 连接成功。
2. 权限被拒时：日志出现"被拒绝"提示，引导用户手动操作（不崩溃、不死循环）。
3. **重要**：确认 `SetStateAsync` 拒绝后不会重复触发（W6 只调用一次 Radio 复位，失败即止，避免复位风暴）。

---

## W8 · 稳定性测试矩阵（验收）

> 每个场景执行 5 轮，记录：连接成功率、假在线出现次数、最长挂起时间、日志轨迹是否完整。
> 测试环境：手机端 App 保持前台打开、屏幕常亮（排除手机端变量）。

| # | 场景 | 操作步骤 | 通过标准 |
|---|------|---------|---------|
| T1 | 正常连接收发 | PC 点击 [连接] → 发文本+文件 → 手机回发 | 全部成功；日志有完整 L1 轨迹 |
| T2 | 蓝牙开关恢复（S2 核心） | 关 PC 蓝牙 → 等 3s → 开 → 手机连 PC | 日志 `Radio=Off→On→Listener advertising=true`；手机能连上 |
| T3 | 睡眠唤醒 | PC 睡眠 1 分钟 → 唤醒 → 点击 [连接] | 连接成功；若失败，日志显示分级恢复轨迹 |
| T4 | 对端断开感知（S4） | 连接成功后手机关蓝牙 | PC 设备栏 ≤5s 变灰（ReadLoop ended → Disconnected）；再点发送提示未连接 |
| T5 | 选中不重连（S3） | 已连接 A → 点击 A 行主体 10 次 | 日志无任何 SDP/Connect 打点；连接不中断 |
| T6 | 快速开关循环 | 连续 5 轮"关蓝牙→3s→开蓝牙" | 每轮日志都有 Listener 重建；第 5 轮后手机仍能连入 |
| T7 | 栈 wedged 自愈 | 快速开关蓝牙 3 次（<2s 间隔）后点 [连接] | 触发 L2/L3 恢复并最终连接成功（或明确提示手动操作） |
| T8 | 双向并发 | PC 点 [连接] 的同时手机端也发起连接 | 一端成功即可（另一端走 ClearIfCurrent 让位）；无崩溃、无状态错乱 |
| T9 | 传输中断恢复 | 传大文件中断开手机蓝牙 | 错误提示明确；重新连接后续传正常（新传输） |
| T10 | 日志完备性抽查 | 任取一次失败 | 能从日志指出失败发生在哪一步（Radio/SDP/Connect/Listener 之一） |

**回归确认**：T1 发送 20MB 文件，MD5 校验通过、进度/速率显示正常（确认重构未破坏传输层）。

---

## 附：风险与回退

- 每步改动独立成篇，任一步出问题可单独回退该步（建议按步提交 git commit）。
- W5/W7 涉及 WinRT Radio API 在 WPF 中的行为，若 `GetRadiosAsync` 返回空或 `StateChanged` 不触发（个别驱动），日志会明确记录（`Radio watcher failed` / `no bluetooth radio found`），功能退化为手动重试，不影响其他步骤。
- W7 若 `SetStateAsync` 始终被拒绝，保留"提示用户手动开关蓝牙"的降级路径即可（这正是用户现有 workaround）。
