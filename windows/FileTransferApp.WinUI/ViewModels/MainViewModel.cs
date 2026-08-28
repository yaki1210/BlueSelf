using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using FileTransferApp.WinUI.Bluetooth;
using FileTransferApp.WinUI.Bluetooth.Core;

namespace FileTransferApp.WinUI.ViewModels;

/// <summary>Device connection state shown by the status dot.</summary>
public enum DeviceStatus { Online, Connecting, Offline, Failed }

/// <summary>A Bluetooth peer shown in the device column.</summary>
public sealed class DeviceItem : ObservableObject
{
    public required string Name { get; init; }
    public ulong Address { get; init; }
    /// <summary>Device icon kind (PC / tablet / phone) derived from the name, like Android.</summary>
    public required string Kind { get; init; }

    private DeviceStatus _status;
    public DeviceStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ActionText));
            }
        }
    }
    /// <summary>Localized status text for the secondary label.</summary>
    public string StatusText
    {
        get
        {
            var key = _status switch
            {
                DeviceStatus.Online => "statOnline",
                DeviceStatus.Connecting => "statConnecting",
                DeviceStatus.Failed => "statFailed",
                _ => "statOffline"
            };
            return MainViewModel.Loc(key, "离线");
        }
    }

    /// <summary>Localized action-button text: online=set target, connecting=…, failed=retry, offline=connect.</summary>
    public string ActionText => _status switch
    {
        DeviceStatus.Online => MainViewModel.Loc("actSetTarget", "设为目标"),
        DeviceStatus.Connecting => "…",
        DeviceStatus.Failed => MainViewModel.Loc("actRetry", "重试"),
        _ => MainViewModel.Loc("actConnect", "连接")
    };

    /// <summary>Re-queries the localized status/action text (called when the UI language changes).</summary>
    public void NotifyStatusText()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ActionText));
    }
}

/// <summary>A nearby device shown on the "add new device" page.</summary>
public sealed class ScannedDeviceItem : ObservableObject
{
    private string _name;
    private bool _isPaired;

    public ulong Address { get; init; }
    public BluetoothMajorClass MajorClass { get; private set; }

    public ScannedDeviceItem() { _name = ""; }

    public ScannedDeviceItem(Bluetooth.Core.ScannedDevice d)
    {
        _name = d.Name;
        _isPaired = d.IsPaired;
        Address = d.Address;
        MajorClass = d.MajorClass;
    }

    public string Name { get => _name; private set => Set(ref _name, value); }
    public bool IsPaired { get => _isPaired; private set => Set(ref _isPaired, value); }

    private string _linkState = ""; // ""=未连接 | connected=已连接
    /// <summary>连接态（扫描页点击连接成功后置 connected，行尾按钮变禁用）。</summary>
    public string LinkState
    {
        get => _linkState;
        set { if (Set(ref _linkState, value)) OnPropertyChanged(nameof(ActionText)); }
    }

    /// <summary>是否暴露 BlueSelf 服务（连接成功事实，最高权重信号）。</summary>
    public bool HasBlueSelfService => LinkState == "connected";

    /// <summary>四信号分类（当前仅名字 + CoD 两信号可用；连接成功后服务信号权重生效）。</summary>
    public string Kind => Bluetooth.Core.DeviceKind.Classify(Name, MajorClass, HasBlueSelfService);

    /// <summary>A short address suffix for display.</summary>
    public string AddressText => (Address & 0xFFFFFFFF).ToString("X4");

    /// <summary>行尾按钮文案：未配对 → 配对并添加；已配对未连 → 连接；已连接 → 禁用态文案。</summary>
    public string ActionText => LinkState == "connected"
        ? MainViewModel.Loc("adConnected", "已连接")
        : IsPaired
            ? MainViewModel.Loc("adConnect", "连接")
            : MainViewModel.Loc("adPairAndAdd", "配对并添加");

    /// <summary>流式更新：watcher 的 Updated 事件合并后刷新已有行。</summary>
    public void RefreshFrom(Bluetooth.Core.ScannedDevice d)
    {
        Name = d.Name;
        IsPaired = d.IsPaired;
        MajorClass = d.MajorClass;
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(AddressText));
        OnPropertyChanged(nameof(ActionText));
    }

    /// <summary>连接/配对成功后由 VM 调用（PairAsync 结果回写）。</summary>
    public void MarkPaired()
    {
        IsPaired = true;
        OnPropertyChanged(nameof(ActionText));
    }
}

/// <summary>A pending attachment chip in the composer.</summary>
public sealed class AttachmentItem : ObservableObject
{
    public required string Name { get; init; }
    public required string SizeText { get; init; }
    public required string Kind { get; init; } // pdf / image / file
    public required string PathText { get; init; }
    public long Size { get; init; }
}

/// <summary>A log line shown in the collapsible log area.</summary>
public sealed class LogEntry : ObservableObject
{
    public required string Time { get; init; }
    public required string Message { get; init; }
}

/// <summary>An attachment attached to an inbox message (received saved file, or sent original path).</summary>
public sealed class InboxAttachment : ObservableObject
{
    public required string Name { get; init; }
    public required string SizeText { get; init; }
    public required string Kind { get; init; } // pdf / image / file
    /// <summary>On-disk file: for received attachments the saved path; for sent attachments the original path.</summary>
    public required string Path { get; init; }
    /// <summary>True for received (saved) attachments whose file may be deleted with the message.</summary>
    public bool IsIncomingSaved { get; init; }
}

/// <summary>An entry in the inbox list.</summary>
public sealed class InboxEntry : ObservableObject
{
    public DeviceItem? Peer { get; init; }
    public required string Device { get; init; }
    public required string Time { get; init; }
    public required string Content { get; set; }
    public bool IsOutgoing { get; init; }

    public ObservableCollection<InboxAttachment> Attachments { get; } = new();

    private bool _isUnread;
    public bool IsUnread { get => _isUnread; set => Set(ref _isUnread, value); }

    /// <summary>Peer device icon kind (pc/tablet/phone) for the list avatar.</summary>
    public string DeviceKind => MainViewModel.KindOfName(Device);

    /// <summary>Preview line for the list (content first line or first attachment name).</summary>
    public string Preview
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Content)) return Content.Replace("\r", " ").Replace("\n", " ").Trim();
            return Attachments.Count > 0 ? Attachments[0].Name : "";
        }
    }
    /// <summary>Attachment badge text for the list.</summary>
    public string FileInfo => Attachments.Count > 0 ? $"{Attachments.Count} 个文件" : "";
    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>Raises notifications so the list text/badge update after attachments change.</summary>
    public void NotifyAttachments()
    {
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(FileInfo));
        OnPropertyChanged(nameof(HasAttachments));
    }
}

/// <summary>
/// Main UI state, wired to the real Bluetooth transfer pipeline.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new SynchronizationContext();
    private readonly RfcommHost _host = new();
    private readonly TransferService _transfer;
    private string _peerName = "远端设备";
    private long _lastBytes;
    private DateTime _lastSample = DateTime.MinValue;

    public MainViewModel()
    {
        // 加载持久化设置（语言/主题/保存目录），覆盖内存默认值。
        var settings = AppSettingsStore.Load();
        _language = ValidateChoice(Languages, settings.Language, "中文");
        _theme = ValidateChoice(Themes, settings.Theme, "跟随系统");
        if (!string.IsNullOrWhiteSpace(settings.SavePath))
        {
            try
            {
                Directory.CreateDirectory(settings.SavePath);
                Directory.CreateDirectory(Path.Combine(settings.SavePath, "_staging"));
                _savePath = settings.SavePath;
            }
            catch { /* ignore invalid saved path */ }
        }

        _transfer = new TransferService(SavePath);
        WireTransferEvents();
        // W0：连接诊断日志 → 日志区（带时间戳的步进打点）。
        ConnectionLog.Entry += line => Post(() => Log(line));
        LocalName = TransferService.LocalDeviceName();

        Log("BlueSelf 已启动，正在开启蓝牙监听…");
        SaveSettings(); // 先落盘，确保设置即时持久化
        App.ApplyLanguage(_language);
        App.ApplyTheme(_theme);
        _ = InitAsync();
    }

    private static string ValidateChoice(IEnumerable<string> allowed, string value, string fallback)
        => allowed.Contains(value) ? value : fallback;

    private static MainViewModel? _instance;
    public static MainViewModel Instance => _instance ??= new MainViewModel();

    /// <summary>Looks up a UI string from the current language resource dictionary.</summary>
    internal static string Loc(string key, string fallback)
        => System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;

    // ---- App views ----
    public enum AppView { Workspace, Inbox, Settings, AddDevice }

    private AppView _currentView = AppView.Workspace;
    public AppView CurrentView
    {
        get => _currentView;
        set { if (Set(ref _currentView, value)) NotifyViewFlags(); }
    }
    private void NotifyViewFlags()
    {
        OnPropertyChanged(nameof(IsWorkspace));
        OnPropertyChanged(nameof(IsInbox));
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsAddDevice));
    }
    public bool IsWorkspace => _currentView == AppView.Workspace;
    public bool IsInbox => _currentView == AppView.Inbox;
    public bool IsSettings => _currentView == AppView.Settings;
    public bool IsAddDevice => _currentView == AppView.AddDevice;

    public RelayCommandNoArg ShowWorkspaceCommand => new(() => CurrentView = AppView.Workspace);
    public RelayCommandNoArg ShowInboxCommand => new(() => CurrentView = AppView.Inbox);
    public RelayCommandNoArg ShowSettingsCommand => new(() => CurrentView = AppView.Settings);

    /// <summary>Opens the add-device page and starts scanning.</summary>
    public RelayCommandNoArg ShowAddDeviceCommand => new(() =>
    {
        CurrentView = AppView.AddDevice;
        _ = EnsureListeningAsync();
        StartScan();
    });

    // ---- Bluetooth init / device column ----
    private async Task InitAsync()
    {
        try
        {
            var saveDir = SavePath;
            Directory.CreateDirectory(Path.Combine(saveDir, "_staging"));
        }
        catch { }

        // 先订阅再监听：避免订阅之前就有设备连入而丢失连接。
        _host.IncomingConnected += OnIncomingConnected;

        await EnsureListeningAsync();
        await RefreshDevicesAsync();
        await InitRadioWatcherAsync(); // W5：监控蓝牙无线电开关，Off→On 后自动重建监听
    }

    // ---- W5: 蓝牙无线电状态监控 ----
    private Windows.Devices.Radios.Radio? _btRadio;

    /// <summary>注册蓝牙无线电状态监听；Off→On 后延迟等栈就绪，重建监听并刷新设备栏。</summary>
    private async Task InitRadioWatcherAsync()
    {
        try
        {
            var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
            _btRadio = radios.FirstOrDefault(r => r.Kind == Windows.Devices.Radios.RadioKind.Bluetooth);
            if (_btRadio == null)
            {
                ConnectionLog.Write("Radio watcher", "no bluetooth radio found");
                return;
            }

            _btRadio.StateChanged += (radio, _) =>
            {
                ConnectionLog.Write("Radio", radio.State.ToString());
                if (radio.State == Windows.Devices.Radios.RadioState.On)
                    _ = RecoverAfterRadioOnAsync();
            };
            ConnectionLog.Write("Radio initial", _btRadio.State.ToString());
        }
        catch (Exception ex)
        {
            ConnectionLog.Write("Radio watcher failed", ex.Message);
        }
    }

    /// <summary>蓝牙 Off→On 后：延迟等栈就绪 → 重建监听 → 刷新设备栏。</summary>
    private async Task RecoverAfterRadioOnAsync()
    {
        await Task.Delay(1500); // 等无线电完全就绪（栈初始化需要时间）
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

    /// <summary>Handles a phone connecting into this PC, syncing the device column and current target.</summary>
    private void OnIncomingConnected(StreamSocket socket)
    {
        var addr = TryParseMacToUlong(socket.Information.RemoteHostName?.CanonicalName);
        _transfer.Attach(socket, addr);

        Post(() =>
        {
            _selectedAddress = addr ?? 0;
            _peerName = "远端设备";
            DeviceItem? match = null;

            foreach (var d in Devices)
            {
                if (addr.HasValue && d.Address == addr.Value)
                {
                    d.Status = DeviceStatus.Online;
                    match = d;
                }
                else
                {
                    d.Status = DeviceStatus.Offline; // 其余设备全部下线，避免"选择A却发到B"
                }
            }

            if (match != null)
            {
                _peerName = match.Name;
                _selectedDevice = match; // 直接赋值字段 + 通知，绕过 setter 避免触发重连
                OnPropertyChanged(nameof(SelectedDevice));
                OnPropertyChanged(nameof(HasSelectedDevice));
                OnPropertyChanged(nameof(IsTargetRowVisible));
                OnPropertyChanged(nameof(PlaceholderText));
                Log($"{match.Name} 已接入");
            }
            else
            {
                IsListening = true;
                Log(addr.HasValue ? "远端设备已接入" : "有设备接入（未识别地址），连接已建立");
            }
        });
    }

    /// <summary>Parses a Bluetooth MAC ("AA:BB:CC:DD:EE:FF") into a ulong address, or null.</summary>
    private static ulong? TryParseMacToUlong(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var hex = mac.Replace(":", "").Replace("-", "").Trim();
        if (hex.Length != 12) return null;
        return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : null;
    }

    /// <summary>Ensures the inbound RFCOMM listener is running (self-heals if Bluetooth came up late).</summary>
    public async Task EnsureListeningAsync()
    {
        if (_host.IsListening) { _listenRetryTimer?.Stop(); return; }
        try
        {
            await _host.StartListeningAsync();
            _listenRetryTimer?.Stop(); // 成功即停表
            Post(() => { IsListening = true; Log("蓝牙监听已开启"); });
        }
        catch (Exception ex)
        {
            StartListenRetryTimer();
            Post(() => Log($"开启监听失败: {ex.Message}（稍后会自动重试）"));
        }
    }

    private System.Windows.Threading.DispatcherTimer? _listenRetryTimer;

    /// <summary>启动监听失败后的定时重试（30s 间隔；成功后停止）。可能在任意线程被调用，统一投回 UI 线程。</summary>
    private void StartListenRetryTimer()
    {
        Post(() =>
        {
            if (_listenRetryTimer == null)
            {
                _listenRetryTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                _listenRetryTimer.Tick += async (_, _) =>
                {
                    if (_host.IsListening) { _listenRetryTimer.Stop(); return; }
                    ConnectionLog.Write("Listen retry", "timer tick");
                    await EnsureListeningAsync();
                };
            }
            if (!_listenRetryTimer.IsEnabled) _listenRetryTimer.Start();
        });
    }

    private bool _isRefreshing;
    public async Task RefreshDevicesAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            await EnsureListeningAsync();
            var devices = await Discovery.GetPairedDevicesAsync();
            var targetAddr = SelectedDevice?.Address ?? _selectedAddress;
            // W3：状态恢复以真实连接为准，而非“选中即在线”的假在线。
            var connectedAddr = _transfer.ConnectedPeerAddress;
            Post(() =>
            {
                Devices.Clear();
                foreach (var d in devices)
                {
                    Devices.Add(new DeviceItem
                    {
                        Name = d.Name,
                        Address = d.Address,
                        Kind = KindOfName(d.Name),
                        Status = connectedAddr is ulong ca && d.Address == ca ? DeviceStatus.Online : DeviceStatus.Offline
                    });
                }

                // 恢复选中项：直接赋值字段 + 通知，绕过 setter 以免触发自动重连。
                var match = Devices.FirstOrDefault(x => x.Address == targetAddr);
                if (match != null)
                {
                    _selectedDevice = match;
                    OnPropertyChanged(nameof(SelectedDevice));
                    OnPropertyChanged(nameof(HasSelectedDevice));
                    OnPropertyChanged(nameof(IsTargetRowVisible));
                    OnPropertyChanged(nameof(PlaceholderText));
                }
                Log($"已发现 {Devices.Count} 台 BlueSelf 设备");
            });
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    public RelayCommandNoArg RefreshDevicesCommand => new(() => _ = RefreshDevicesAsync());

    /// <summary>Called when the main window regains focus (e.g. after pairing in Settings).</summary>
    public void OnWindowActivated()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastActivated).TotalSeconds < 3) return; // 防抖
        _lastActivated = now;
        _ = RefreshDevicesAsync();
    }
    private DateTime _lastActivated = DateTime.MinValue;

    private ulong _selectedAddress;
    public ObservableCollection<DeviceItem> Devices { get; } = new();

    private DeviceItem? _selectedDevice;
    public DeviceItem? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (Set(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(HasSelectedDevice));
                OnPropertyChanged(nameof(IsTargetRowVisible));
                OnPropertyChanged(nameof(PlaceholderText));
                ShowTargetHint = false;
            }
        }
    }
    public bool HasSelectedDevice => SelectedDevice != null;
    /// <summary>True when the target-device row should be shown (a device is selected and no hint is visible).</summary>
    public bool IsTargetRowVisible => SelectedDevice != null && !ShowTargetHint;

    /// <summary>Editor watermark: guides to the device column when no target, normal hint once selected.</summary>
    public string PlaceholderText => SelectedDevice == null
        ? Loc("wsTargetHint", "请在左侧选择要发送的目标设备")
        : Loc("wsPlaceholder", "在此输入要即时发送的文本内容，或点击下方添加附件……");

    /// <summary>Clicking a device: select it as target and (re)connect. Fires on every click,
    /// so the same device can be clicked again to retry regardless of the last result.</summary>
    public RelayCommand ConnectDeviceCommand => new(p =>
    {
        if (p is DeviceItem d) _ = ConnectDeviceAsync(d);
    });

    /// <summary>点击设备主体：仅切换发送目标，绝不触碰 socket（W1 核心）。</summary>
    public RelayCommand SetTargetCommand => new(p =>
    {
        if (p is DeviceItem d) SelectAsTarget(d);
    });

    /// <summary>设备行点击（单连接模型）：在线 → 仅设为目标；离线/失败 → 连接/重试；连接中 → 忽略。
    /// 状态由圆点颜色表达，不再有独立动作按钮。</summary>
    public RelayCommand RowClickCommand => new(p =>
    {
        if (p is not DeviceItem d) return;
        switch (d.Status)
        {
            case DeviceStatus.Online:
                SelectAsTarget(d);
                break;
            case DeviceStatus.Offline:
            case DeviceStatus.Failed:
                _ = ConnectDeviceAsync(d);
                break;
            // Connecting：忽略点击，等待结果
        }
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
        OnPropertyChanged(nameof(PlaceholderText));
        ConnectionLog.Write("Target set", $"{d.Name} (no reconnect)");
    }

    private async Task ConnectDeviceAsync(DeviceItem device)
    {
        // 已连接该设备：仅设为目标，不重建链路。
        if (_transfer.IsConnected &&
            _transfer.ConnectedPeerAddress is ulong addr && addr == device.Address)
        {
            SelectAsTarget(device);
            ConnectionLog.Write("Already connected", "set target only");
            return;
        }

        _selectedDevice = device;
        _selectedAddress = device.Address;
        _peerName = device.Name;
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(HasSelectedDevice));
        OnPropertyChanged(nameof(IsTargetRowVisible));
        OnPropertyChanged(nameof(PlaceholderText));
        await ConnectAsync(device);
    }

    /// <summary>
    /// 建链分级升格：L1 直接连接 → L2 重建监听后重试 → L3 检查无线电并复位后重试。
    /// </summary>
    private async Task ConnectAsync(DeviceItem device)
    {
        device.Status = DeviceStatus.Connecting;
        Log($"正在连接 {device.Name}…");

        // ---- L1：常规路径（内置 SDP/Connect 超时 + 短退避）----
        try
        {
            var socket = await RfcommHost.ConnectAsync(device.Address);
            OnConnected(device, socket);
            return;
        }
        catch (Exception ex)
        {
            ConnectionLog.Write("Connect L1 failed", ex.Message);
        }

        // ---- L2：重建监听后重试（修复监听死亡态）----
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

        // ---- L3：检查无线电状态；异常则进入 Radio 复位（最后一级）----
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
            await ResetBluetoothRadioAsync(); // W7：程序化 Off/On
            await Task.Delay(1500);           // 等栈就绪
            await _host.RestartAsync();       // 重建监听（Radio=On 事件也会触发，锁保证幂等）
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

    /// <summary>建链成功后的统一收尾：Attach + 目标切换 + 设备栏状态。</summary>
    private void OnConnected(DeviceItem device, StreamSocket socket)
    {
        _selectedAddress = device.Address;
        _peerName = device.Name;
        _transfer.Attach(socket, device.Address);
        foreach (var d in Devices) if (d.Address != device.Address) d.Status = DeviceStatus.Offline;
        device.Status = DeviceStatus.Online;
        Log($"已连接 {device.Name}");
    }

    public string LocalName { get; private set; }
    private bool _isListening;
    public bool IsListening { get => _isListening; set => Set(ref _isListening, value); }

    private bool _showTargetHint;
    public bool ShowTargetHint
    {
        get => _showTargetHint;
        set
        {
            if (Set(ref _showTargetHint, value)) OnPropertyChanged(nameof(IsTargetRowVisible));
        }
    }

    /// <summary>Opens the add-device page（保留别名，供旧的按钮绑定使用）。</summary>
    public RelayCommandNoArg AddPairingCommand => ShowAddDeviceCommand;

    // ---- Add-device page: scan & connect ----
    public ObservableCollection<ScannedDeviceItem> ScannedDevices { get; } = new();

    private bool _isScanning;
    public bool IsScanning { get => _isScanning; set => Set(ref _isScanning, value); }

    /// <summary>已发现设备数（扫描中状态文案实时显示）。</summary>
    public int FoundCount => ScannedDevices.Count;

    /// <summary>列表是否有结果（控制空态区显隐）。</summary>
    public bool HasResults => ScannedDevices.Count > 0;

    /// <summary>停止扫描命令（离开页面时由视图调用）。</summary>
    public RelayCommandNoArg StopScanCommand => new(() => StopScan(completed: false));

    /// <summary>顶栏按钮文案：扫描中 → 停止扫描；否则 → 扫描设备。</summary>
    public string ScanButtonText => IsScanning ? Loc("adStopScan", "停止扫描") : Loc("adScan", "扫描设备");

    public RelayCommandNoArg StartScanCommand => new(ToggleScan);

    private readonly Bluetooth.Core.DeviceWatcherScanner _watcherScanner = new();
    private System.Timers.Timer? _debounceTimer;
    private readonly HashSet<ulong> _pendingAdds = new();
    private readonly HashSet<ulong> _pendingUpdates = new();
    private readonly HashSet<ulong> _pendingRemoves = new();
    private const int ScanDurationMs = 15_000;
    private System.Timers.Timer? _autoStopTimer;

    private void ToggleScan()
    {
        if (IsScanning) { StopScan(completed: false); return; }
        StartScan();
    }

    private void StartScan()
    {
        if (IsScanning) return;
        ScannedDevices.Clear();
        OnPropertyChanged(nameof(FoundCount));
        OnPropertyChanged(nameof(HasResults));
        IsScanning = true;
        OnPropertyChanged(nameof(ScanButtonText));
        Log("正在扫描附近设备…");

        _pendingAdds.Clear(); _pendingUpdates.Clear(); _pendingRemoves.Clear();
        _watcherScanner.DeviceAdded += OnWatcherDevice;
        _watcherScanner.DeviceUpdated += OnWatcherDevice;
        _watcherScanner.DeviceRemoved += OnWatcherRemoved;
        try { _watcherScanner.Start(); }
        catch (Exception ex)
        {
            Log($"扫描启动失败: {ex.Message}");
            StopScan(completed: false);
            return;
        }

        _autoStopTimer = new System.Timers.Timer(ScanDurationMs) { AutoReset = false };
        _autoStopTimer.Elapsed += (_, _) => Post(() => StopScan(completed: true));
        _autoStopTimer.Start();
    }

    private void StopScan(bool completed)
    {
        if (!IsScanning) return;
        _autoStopTimer?.Stop();
        _autoStopTimer?.Dispose();
        _autoStopTimer = null;
        _watcherScanner.DeviceAdded -= OnWatcherDevice;
        _watcherScanner.DeviceUpdated -= OnWatcherDevice;
        _watcherScanner.DeviceRemoved -= OnWatcherRemoved;
        _watcherScanner.Stop();
        FlushPending();
        IsScanning = false;
        OnPropertyChanged(nameof(ScanButtonText));
        Log(completed ? $"扫描完成，发现 {ScannedDevices.Count} 台设备" : "扫描已停止");
    }

    private void OnWatcherDevice(Bluetooth.Core.ScannedDevice d)
    {
        if (d.Address == 0) return;
        // 只过滤明确的非对端类型（音频/外设/成像），名字未知的对端候选一律保留。
        if (d.MajorClass is BluetoothMajorClass.AudioVideo
            or BluetoothMajorClass.Peripheral
            or BluetoothMajorClass.Imaging) return;
        _pendingAdds.Add(d.Address);
        _knownScanned[d.Address] = d;
        ScheduleDebounce();
    }

    private readonly Dictionary<ulong, Bluetooth.Core.ScannedDevice> _knownScanned = new();

    private void OnWatcherRemoved(ulong addr)
    {
        _pendingAdds.Remove(addr);
        _pendingUpdates.Remove(addr);
        if (_knownScanned.ContainsKey(addr)) _pendingRemoves.Add(addr);
        ScheduleDebounce();
    }

    private void ScheduleDebounce()
    {
        lock (_debounceGate)
        {
            if (_debounceTimer == null)
            {
                _debounceTimer = new System.Timers.Timer(800) { AutoReset = false };
                _debounceTimer.Elapsed += (_, _) => Post(FlushPending);
            }
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }
    }

    private readonly object _debounceGate = new();

    /// <summary>把 pending 合并去重后一次性应用到 UI 集合（必须已在 UI 线程调用）。</summary>
    private void FlushPending()
    {
        lock (_debounceGate)
        {
            _debounceTimer?.Stop();
            _debounceTimer = null;
        }

        foreach (var addr in _pendingRemoves)
        {
            var existing = ScannedDevices.FirstOrDefault(x => x.Address == addr);
            if (existing != null) ScannedDevices.Remove(existing);
            _knownScanned.Remove(addr);
        }
        _pendingRemoves.Clear();

        foreach (var addr in _pendingUpdates)
        {
            if (!_knownScanned.TryGetValue(addr, out var d)) continue;
            var existing = ScannedDevices.FirstOrDefault(x => x.Address == addr);
            if (existing != null) existing.RefreshFrom(d);
        }
        _pendingUpdates.Clear();

        foreach (var addr in _pendingAdds)
        {
            if (!_knownScanned.TryGetValue(addr, out var d)) continue;
            var existing = ScannedDevices.FirstOrDefault(x => x.Address == addr);
            if (existing == null)
            {
                ScannedDevices.Add(new ScannedDeviceItem(d));
                OnPropertyChanged(nameof(FoundCount));
            }
            else existing.RefreshFrom(d);
        }
        _pendingAdds.Clear();
        OnPropertyChanged(nameof(FoundCount));
        OnPropertyChanged(nameof(HasResults));
    }

    private bool _isConnecting;
    public RelayCommand ConnectAndAddCommand => new(p => _ = ConnectAndAddAsync(p as ScannedDeviceItem));

    /// <summary>Pairs (if needed) then connects to a nearby device and adds it to the device column.</summary>
    private async Task ConnectAndAddAsync(ScannedDeviceItem? item)
    {
        if (item == null || _isConnecting) return;
        _isConnecting = true;
        try
        {
            await EnsureListeningAsync();

            var device = await BluetoothDevice.FromBluetoothAddressAsync(item.Address);            if (device == null) { Log("未找到该蓝牙设备，请重试扫描"); return; }

            if (!device.DeviceInformation.Pairing.IsPaired)
            {
                Log($"正在与 {item.Name} 配对，请在两端确认…");
                try
                {
                    // 系统配对弹窗走 SSP 新密钥，绕开 legacy link key 被拒的路径。
                    var pairing = await device.DeviceInformation.Pairing.PairAsync();
                    if (pairing != null &&
                        pairing.Status != DevicePairingResultStatus.Paired &&
                        pairing.Status != DevicePairingResultStatus.AlreadyPaired)
                    {
                        Log($"与 {item.Name} 配对失败: {pairing.Status}");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log($"与 {item.Name} 配对失败: {ex.Message}");
                    return;
                }
            }

            Log($"正在连接 {item.Name}…");
            try
            {
                var socket = await RfcommHost.ConnectAsync(item.Address);
                _selectedAddress = item.Address;
                _peerName = item.Name;
                _transfer.Attach(socket, item.Address);

                // 其余设备全部下线。
                foreach (var d in Devices) if (d.Address != item.Address) d.Status = DeviceStatus.Offline;

                // 加入设备栏并标记为在线；设为发送目标；同步扫描页行状态。
                var existing = Devices.FirstOrDefault(x => x.Address == item.Address);
                if (existing != null)
                {
                    existing.Status = DeviceStatus.Online;
                    _selectedDevice = existing;
                }
                else
                {
                    var kind = Bluetooth.Core.DeviceKind.Classify(item.Name, item.MajorClass, hasBlueSelfService: true);
                    var added = new DeviceItem { Name = item.Name, Address = item.Address, Kind = kind, Status = DeviceStatus.Online };
                    Devices.Add(added);
                    _selectedDevice = added;
                }
                OnPropertyChanged(nameof(SelectedDevice));
                OnPropertyChanged(nameof(HasSelectedDevice));
                OnPropertyChanged(nameof(IsTargetRowVisible));
                OnPropertyChanged(nameof(PlaceholderText));
                item.LinkState = "connected";
                item.MarkPaired();

                Log($"已连接并添加 {item.Name}");
                CurrentView = AppView.Workspace;
            }
            catch (Exception ex)
            {
                Log($"连接 {item.Name} 失败: {ex.Message}（请确认手机端 App 已打开）");
            }
        }
        finally
        {
            _isConnecting = false;
        }
    }

    // ---- Composer ----
    private string _text = string.Empty;
    public string Text
    {
        get => _text;
        set { if (Set(ref _text, value)) OnPropertyChanged(nameof(PendingSizeText)); }
    }
    public ObservableCollection<AttachmentItem> Attachments { get; } = new();

    /// <summary>附件区显隐（集合增删不会触发实例绑定重求值，需显式通知）。</summary>
    public bool HasPendingAttachments => Attachments.Count > 0;

    private void NotifyAttachmentsChanged()
    {
        OnPropertyChanged(nameof(HasPendingAttachments));
    }

    /// <summary>Pending content info shown above the send button (text bytes + attachment bytes).</summary>
    public string PendingSizeText
    {
        get
        {
            var textBytes = System.Text.Encoding.UTF8.GetByteCount(_text);
            var fileBytes = Attachments.Sum(a => a.Size);
            return $"{FormatBytes(textBytes + fileBytes)} · {_text.Length} {Loc("unitChar", "字符")}";
        }
    }

    public RelayCommandNoArg PasteCommand => new(() =>
    {
        var text = System.Windows.Clipboard.GetText();
        if (!string.IsNullOrWhiteSpace(text))
        {
            Text = string.IsNullOrWhiteSpace(Text) ? text : Text + text;
            Log("已粘贴文本");
        }
    });

    public RelayCommandNoArg AttachCommand => new(SendAttach);

    private void SendAttach()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "选择要发送的文件" };
        if (dlg.ShowDialog() != true) return;
        AddAttachmentFiles(dlg.FileNames);
    }

    /// <summary>把一组文件路径加入待发附件（去重），供文件对话框与拖拽共用。</summary>
    public void AddAttachmentFiles(System.Collections.Generic.IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) continue;
            var fi = new FileInfo(path);
            if (Attachments.All(a => a.PathText != fi.FullName))
            {
                Attachments.Add(new AttachmentItem
                {
                    Name = fi.Name,
                    SizeText = FormatBytes(fi.Length),
                    Kind = KindOfExt(fi.Name),
                    PathText = fi.FullName,
                    Size = fi.Length
                });
                added++;
            }
        }
        if (added > 0)
        {
            OnPropertyChanged(nameof(PendingSizeText));
            NotifyAttachmentsChanged();
            Log($"已添加 {added} 个附件");
        }
    }

    /// <summary>Removes an attachment by reference from the composer.</summary>
    public void RemoveAttachment(AttachmentItem item)
    {
        Attachments.Remove(item);
        OnPropertyChanged(nameof(PendingSizeText));
        NotifyAttachmentsChanged();
    }

    public RelayCommandNoArg StartSendCommand => new(StartTransfer);

    private async void StartTransfer()
    {
        // 三合一校验：未选目标 / 未建立连接 / 所选设备与实际连接不一致 → 统一弹出目标提示。
        var notSelected = SelectedDevice == null;
        var notConnected = !_transfer.IsConnected;
        // W3：仅在能确定对端地址且确实不匹配时才判 wrongTarget；地址未知（入站连接 MAC 解析失败）时放行。
        var wrongTarget = !notSelected && !notConnected &&
                          _transfer.ConnectedPeerAddress is ulong addr && addr != SelectedDevice!.Address;
        if (notSelected || notConnected || wrongTarget)
        {
            ShowTargetHint = true;
            Log(notSelected ? "请先在设备栏选择要发送的目标设备"
                : notConnected ? "尚未连接目标设备，请先在设备栏点击设备建立连接"
                : "当前连接的不是所选设备，请重新在设备栏选择");
            return;
        }
        ShowTargetHint = false;
        var hasText = !string.IsNullOrWhiteSpace(Text);
        var hasFiles = Attachments.Count > 0;
        if (!hasText && !hasFiles)
        {
            Log("没有可发送的内容");
            return;
        }
        IsTransferring = true;
        var sentContent = Text;
        var messageId = Guid.NewGuid().ToString("N");
        ConnectionLog.Write("Send requested",
            $"target={SelectedDevice?.Name}, state={_transfer.State}, files={Attachments.Count}");
        try
        {
            // 与 Android 对齐：总是先发一条 TXT（即使空文本）在对端创建父消息，
            // 文件用同一 messageId 作为 FILE_START.msgId 挂到该消息下。
            await _transfer.SendTextAsync(sentContent, messageId);
            ConnectionLog.Write("TXT sent", $"{sentContent.Length} chars");
            foreach (var att in Attachments.ToList())
            {
                FileName = att.Name;
                if (att.Size > 20L * 1024 * 1024)
                    Log("提示：此文件过大，蓝牙传输需较长时间");
                await _transfer.SendFileAsync(att.PathText, Guid.NewGuid().ToString("N"), messageId);
                ConnectionLog.Write("File sent", att.Name);
            }
            Text = string.Empty;
            // 发件侧始终生成一条记录（文本/附件）
            AddOutgoing(sentContent);
            Attachments.Clear();
            OnPropertyChanged(nameof(PendingSizeText));
            NotifyAttachmentsChanged();
        }
        catch (Exception ex)
        {
            ConnectionLog.Write("Send failed", $"{ex.GetType().Name}: {ex.Message}");
            Log($"发送失败: {ex.Message}");
        }
        finally
        {
            ResetTransferUi();
        }
    }

    // ---- Inbox ----
    public ObservableCollection<InboxEntry> Inbox { get; } = new();
    /// <summary>Tracks inbound entries by parent message id so a message's text and files merge into one row.</summary>
    private readonly Dictionary<string, InboxEntry> _inboundByKey = new();

    public int UnreadCount
    {
        get
        {
            lock (Inbox) { return Inbox.Count(e => e.IsUnread); }
        }
    }

    private void InboxChanged() => OnPropertyChanged(nameof(UnreadCount));

    private InboxEntry? _selectedInbox;
    public InboxEntry? SelectedInbox
    {
        get => _selectedInbox;
        set
        {
            if (Set(ref _selectedInbox, value))
            {
                NotifyDetail();
                if (value != null && value.IsUnread)
                {
                    value.IsUnread = false;
                    InboxChanged();
                }
            }
        }
    }
    private void NotifyDetail()
    {
        OnPropertyChanged(nameof(DetailDevice));
        OnPropertyChanged(nameof(DetailTime));
        OnPropertyChanged(nameof(DetailContent));
        OnPropertyChanged(nameof(DetailHasContent));
        OnPropertyChanged(nameof(DetailIsOutgoing));
        OnPropertyChanged(nameof(DetailAttachments));
        OnPropertyChanged(nameof(DetailHasSavedAttachments));
        OnPropertyChanged(nameof(DetailGlyph));
    }
    public string DetailDevice => SelectedInbox?.Device ?? "";
    public string DetailTime => SelectedInbox?.Time ?? "";
    public string DetailContent => SelectedInbox?.Content ?? "";
    public bool DetailHasContent => !string.IsNullOrWhiteSpace(SelectedInbox?.Content);
    public bool DetailIsOutgoing => SelectedInbox?.IsOutgoing == true;
    public ObservableCollection<InboxAttachment> DetailAttachments => SelectedInbox?.Attachments ?? new();
    public bool DetailHasSavedAttachments => SelectedInbox?.Attachments.Any(a => a.IsIncomingSaved) == true;
    public string DetailGlyph => KindOfName(SelectedInbox?.Device ?? "");

    /// <summary>Create an outgoing record after a send (always, text and/or files).</summary>
    private void AddOutgoing(string? sentContent)
    {
        var entry = new InboxEntry
        {
            Peer = SelectedDevice,
            Device = _peerName,
            Time = DateTime.Now.ToString("HH:mm"),
            Content = sentContent ?? "",
            IsOutgoing = true,
            IsUnread = false
        };
        foreach (var att in Attachments.ToList())
        {
            entry.Attachments.Add(new InboxAttachment
            {
                Name = att.Name,
                SizeText = att.SizeText,
                Kind = att.Kind,
                Path = att.PathText,
                IsIncomingSaved = false
            });
        }
        entry.NotifyAttachments();
        Inbox.Insert(0, entry);
        InboxChanged();
    }

    private void HandleInboundText(string msgId, string content)
    {
        InboxEntry entry;
        if (!string.IsNullOrWhiteSpace(msgId) && _inboundByKey.TryGetValue(msgId, out var existing))
        {
            // 同一消息的附件已先到，仅补写文本内容。
            entry = existing;
            entry.Content = content;
            entry.NotifyAttachments();
        }
        else
        {
            entry = AddInbound(_peerName, content);
            if (!string.IsNullOrWhiteSpace(msgId)) _inboundByKey[msgId] = entry;
        }
    }

    private void HandleInboundFile(InboundFile file)
    {
        // 附件挂到对应父文本消息下；父消息未到时先建一条仅附件的占位，文本到达后再合并。
        InboxEntry entry;
        var key = file.ParentMessageId;
        if (!string.IsNullOrWhiteSpace(key) && _inboundByKey.TryGetValue(key, out var existing))
        {
            entry = existing;
        }
        else
        {
            entry = AddInbound(_peerName, string.Empty);
            if (!string.IsNullOrWhiteSpace(key)) _inboundByKey[key] = entry;
        }

        entry.Attachments.Add(new InboxAttachment
        {
            Name = file.Name,
            SizeText = FormatBytes(file.Size),
            Kind = KindOfExt(file.Name),
            Path = file.SavePath,
            IsIncomingSaved = true
        });
        entry.NotifyAttachments();
    }

    /// <summary>Creates an inbound entry (inserted at top of the inbox).</summary>
    private InboxEntry AddInbound(string device, string content, bool isUnread = true)
    {
        var entry = new InboxEntry
        {
            Device = device,
            Time = DateTime.Now.ToString("HH:mm"),
            Content = content,
            IsOutgoing = false,
            IsUnread = isUnread
        };
        Inbox.Insert(0, entry);
        InboxChanged();
        return entry;
    }

    public RelayCommandNoArg MarkAllReadCommand => new(() =>
    {
        foreach (var e in Inbox) e.IsUnread = false;
        InboxChanged();
        Log("已全部标记为已读");
    });

    public RelayCommandNoArg ClearInboxCommand => new(() =>
    {
        if (Inbox.Count == 0) return;
        if (MessageBox.Show("确定要清空全部消息记录吗？", "清空收件箱", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        Inbox.Clear();
        SelectedInbox = null;
        InboxChanged();
        Log("已清空收件箱");
    });

    public RelayCommand DeleteMessageCommand => new(_ => DeleteMessage(_ as InboxEntry));

    private void DeleteMessage(InboxEntry? entry)
    {
        if (entry == null) return;
        var hasSaved = entry.Attachments.Any(a => a.IsIncomingSaved);
        var deleteFiles = false;
        if (hasSaved)
        {
            var r = MessageBox.Show("该消息包含已保存的附件，是否同时删除已保存的附件文件？\n\n选“是”＝删记录并删除附件文件；选“否”＝仅删除消息记录。",
                "删除消息", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            deleteFiles = r == MessageBoxResult.Yes;
        }
        else
        {
            if (MessageBox.Show("确定删除这条消息吗？", "删除消息", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
        }

        if (deleteFiles)
        {
            foreach (var att in entry.Attachments.Where(a => a.IsIncomingSaved && a.Path != null))
            {
                try { if (File.Exists(att.Path)) File.Delete(att.Path); } catch { }
            }
        }
        Inbox.Remove(entry);
        if (SelectedInbox == entry) SelectedInbox = null;
        InboxChanged();
        Log("已删除消息");
    }

    /// <summary>Opens the attachment in Explorer (selects the file if it exists).</summary>
    public RelayCommand OpenAttachmentCommand => new(p =>
    {
        if (p is not InboxAttachment att) return;
        try
        {
            if (!string.IsNullOrEmpty(att.Path) && File.Exists(att.Path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{att.Path}\"") { UseShellExecute = true });
            }
        }
        catch { }
    });

    // ---- Settings ----
    public string[] Languages { get; } = { "中文", "English" };
    private string _language = "中文";
    public string Language
    {
        get => _language;
        set
        {
            if (Set(ref _language, value))
            {
                App.ApplyLanguage(value);
                SaveSettings();
                // 刷新依赖语言字符串的 UI（设备状态、待发大小、占位引导）。
                foreach (var d in Devices) d.NotifyStatusText();
                OnPropertyChanged(nameof(PendingSizeText));
                OnPropertyChanged(nameof(PlaceholderText));
            }
        }
    }
    public string[] Themes { get; } = { "跟随系统", "亮色", "暗色" };
    private string _theme = "跟随系统";
    public string Theme
    {
        get => _theme;
        set { if (Set(ref _theme, value)) { App.ApplyTheme(value); SaveSettings(); } }
    }
    private void SaveSettings() => AppSettingsStore.Save(_language, _theme, _savePath);
    private string _savePath = ResolveSaveDir();
    public string SavePath
    {
        get => _savePath;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (Set(ref _savePath, value))
            {
                try
                {
                    Directory.CreateDirectory(value);
                    Directory.CreateDirectory(Path.Combine(value, "_staging"));
                }
                catch { }
                _transfer.SaveDir = value;
                SaveSettings();
            }
        }
    }

    /// <summary>Opens a folder picker to change the received-files save directory.</summary>
    public RelayCommandNoArg ChangeSavePathCommand => new(ChangeSavePath);

    private void ChangeSavePath()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择接收文件保存目录" };
            if (dlg.ShowDialog() != true) return;
            var folder = dlg.FolderName;
            if (string.IsNullOrWhiteSpace(folder)) return;
            SavePath = folder;
            Log($"接收文件保存目录已更改为: {folder}");
        }
        catch (Exception ex)
        {
            Log($"更改保存目录失败: {ex.Message}");
        }
    }

    private static string ResolveSaveDir()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueSelf", "received"),
            Path.Combine(Path.GetTempPath(), "BlueSelf", "received")
        };
        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                Directory.CreateDirectory(Path.Combine(dir, "_staging"));
                return dir;
            }
            catch { /* try next */ }
        }
        return Path.Combine(Path.GetTempPath(), "BlueSelf", "received");
    }

    // ---- Transfer status (third column) ----
    private bool _isTransferring;
    public bool IsTransferring { get => _isTransferring; set => Set(ref _isTransferring, value); }
    private string _fileName = "";
    public string FileName { get => _fileName; set => Set(ref _fileName, value); }
    private TransferDir _currentDir = TransferDir.SEND;
    /// <summary>Header badge text: "接收中" for receives, else "传输中".</summary>
    public string TransferDirectionText => _currentDir == TransferDir.RECEIVE ? "接收中" : "传输中";
    /// <summary>Small action label: "正在接收" for receives, else "正在传输".</summary>
    public string TransferActionText => _currentDir == TransferDir.RECEIVE ? "正在接收" : "正在传输";
    private void NotifyTransferDirection()
    {
        OnPropertyChanged(nameof(TransferDirectionText));
        OnPropertyChanged(nameof(TransferActionText));
    }
    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    private string _percentText = "0%";
    public string PercentText { get => _percentText; set => Set(ref _percentText, value); }
    private string _instantRate = "0.0 KB/s";
    public string InstantRate { get => _instantRate; set => Set(ref _instantRate, value); }
    private string _avgMbps = "-";
    public string AvgMbps { get => _avgMbps; set => Set(ref _avgMbps, value); }

    private void ResetTransferUi()
    {
        IsTransferring = false;
        FileName = "";
        _currentDir = TransferDir.SEND;
        NotifyTransferDirection();
        Progress = 0;
        PercentText = "0%";
        InstantRate = "0.0 KB/s";
        AvgMbps = "-";
        _lastBytes = 0;
        _lastSample = DateTime.MinValue;
    }

    // ---- Log ----
    public ObservableCollection<LogEntry> Logs { get; } = new();

    public void Log(string message)
    {
        Logs.Add(new LogEntry { Time = DateTime.Now.ToString("HH:mm:ss"), Message = message });
    }

    // ---- Transfer event wiring ----
    private void WireTransferEvents()
    {
        _transfer.TextReceived += (id, content) =>
            Post(() => HandleInboundText(id, content));

        _transfer.FileReceived += file =>
            Post(() => HandleInboundFile(file));

        _transfer.Progress += p => Post(() => ApplyProgress(p));

        // 接收开始：状态面板显示本次接收的文件名（配合方向"接收中"）。
        _transfer.ReceiveStarted += name => Post(() => FileName = name);

        // 双方向（发送/接收）结束均收起传输 UI，避免任一方向挂起导致面板卡"传输中"。
        _transfer.TransferCompleted += dir => Post(() => ResetTransferUi());
        _transfer.Info += msg => Post(() => Log(msg));
        _transfer.LogError += msg => Post(() => Log(msg));
        _transfer.Disconnected += OnDisconnected;

        // W2：设备栏置灰/变绿改由连接状态机统一驱动，避免双头更新。
        _transfer.StateChanged += s => Post(() =>
        {
            var connectedAddr = _transfer.ConnectedPeerAddress;
            foreach (var d in Devices)
            {
                d.Status = s switch
                {
                    ConnState.Connected when connectedAddr is ulong ca && d.Address == ca => DeviceStatus.Online,
                    ConnState.Connecting or ConnState.Reconnecting when d.Address == _selectedAddress => DeviceStatus.Connecting,
                    _ => DeviceStatus.Offline
                };
            }
        });
    }

    /// <summary>On disconnect, reset the selected address so selecting the same device again
    /// actually triggers a reconnection; 设备栏置灰由 StateChanged 统一驱动。</summary>
    private void OnDisconnected()
    {
        Post(() =>
        {
            Log("连接已断开");
            _selectedAddress = 0;
        });
    }

    private void ApplyProgress(TransferProgress p)
    {
        if (_currentDir != p.Dir)
        {
            _currentDir = p.Dir;
            NotifyTransferDirection();
        }
        if (!IsTransferring)
        {
            IsTransferring = true;
            _lastBytes = 0;
            _lastSample = DateTime.UtcNow;
        }
        Progress = p.Fraction * 100.0;
        PercentText = $"{(int)Math.Round(p.Fraction * 100)}%";

        var now = DateTime.UtcNow;
        if (_lastSample == DateTime.MinValue) { _lastSample = now; _lastBytes = p.BytesDone; }
        if (p.BytesDone >= _lastBytes && (now - _lastSample).TotalSeconds >= 0.8)
        {
            var elapsed = (now - _lastSample).TotalSeconds;
            var bytesDelta = p.BytesDone - _lastBytes;
            InstantRate = FormatBytes((long)(bytesDelta / elapsed)) + "/s";
            _lastSample = now;
            _lastBytes = p.BytesDone;
        }
        if (p.ElapsedMs > 0)
        {
            AvgMbps = $"{(p.BytesDone * 8.0 / 1_000_000.0) / (p.ElapsedMs / 1000.0):0.00} Mbps";
        }
    }

    private void Post(Action action)
    {
        if (_ui != null && _ui != SynchronizationContext.Current)
            _ui.Post(_ => action(), null);
        else
            action();
    }

    // ---- W7: 程序化 Radio 复位（恢复阶梯最后一级）----

    /// <summary>
    /// 程序化复位蓝牙无线电（恢复阶梯最后一级）。
    /// SetStateAsync 受权限/系统策略限制，返回值必须检查；状态转换是异步的——
    /// 每一步都等待 StateChanged 确认，而非固定延时。仅调用一次，失败即止，避免复位风暴。
    /// </summary>
    private async Task ResetBluetoothRadioAsync()
    {
        if (_btRadio == null) throw new InvalidOperationException("未找到蓝牙无线电");

        ConnectionLog.Write("Recovery", "Radio reset requested");

        var offConfirmed = WaitForRadioState(Windows.Devices.Radios.RadioState.Off);
        var setOff = await _btRadio.SetStateAsync(Windows.Devices.Radios.RadioState.Off);
        if (setOff != Windows.Devices.Radios.RadioAccessStatus.Allowed)
            throw new InvalidOperationException($"关闭蓝牙无线电被拒绝（{setOff}），请在系统中手动重开蓝牙");
        await offConfirmed; // 等 StateChanged = Off

        var onConfirmed = WaitForRadioState(Windows.Devices.Radios.RadioState.On);
        var setOn = await _btRadio.SetStateAsync(Windows.Devices.Radios.RadioState.On);
        if (setOn != Windows.Devices.Radios.RadioAccessStatus.Allowed)
            throw new InvalidOperationException($"打开蓝牙无线电被拒绝（{setOn}），请在系统中手动重开蓝牙");
        await onConfirmed; // 等 StateChanged = On

        ConnectionLog.Write("Recovery", "Radio reset done");
    }

    /// <summary>注册一次性 StateChanged 监听，等待无线电到达目标状态（8s 超时兑底，不抛出）。</summary>
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
            radio.StateChanged -= handler; // 无论成败都解绑
        });
    }

    // ---- Formatting / classification helpers ----
    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB"
    };

    /// <summary>Device icon kind: delegates to DeviceKind four-signal classifier (name-keyword tier).</summary>
    public static string KindOfName(string name)
        => Bluetooth.Core.DeviceKind.Classify(name, (BluetoothMajorClass)0, hasBlueSelfService: false);

    private static string KindOfExt(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "pdf",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => "image",
            ".txt" or ".md" => "text",
            _ => "file"
        };
    }
}