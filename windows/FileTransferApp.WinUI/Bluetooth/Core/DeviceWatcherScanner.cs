using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace FileTransferApp.WinUI.Bluetooth.Core;

/// <summary>A live scan result streamed from the DeviceWatcher.</summary>
public sealed record ScannedDevice(
    ulong Address, string Name, bool IsPaired, BluetoothMajorClass MajorClass);

/// <summary>
/// 真实射频层扫描：DeviceWatcher(AssociationEndpoint) 触发系统 discovery，
/// Added/Updated 事件流式上抛（VM 层做 debounce 合并上屏）。
/// 只从事件参数属性读名称/地址/配对态/CoD，绝不逐台 FromIdAsync（避免串行拖慢扫描）。
/// </summary>
public sealed class DeviceWatcherScanner : IDisposable
{
    // 经典蓝牙 AEP 协议Id（真实值，经 WatchProbe 实测验证）
    private const string ClassicBluetoothProtocolId = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";

    private DeviceWatcher? _watcher;
    private readonly ConcurrentDictionary<string, ulong> _addressById = new();

    /// <summary>Watch 未配对(可发 inquiry 的缓存 AEP) + 已配对 的经典蓝牙设备。
    /// 与官方 GetDeviceSelector() 同源（含 IssueInquiry:=False 缓存条件），再叠加已配对条件。
    /// 注意：ProtocolId GUID 尾段与旧实现不同，旧 GUID 不存在导致枚举恒空。</summary>
    private static string Aqs =>
        $"(System.Devices.Aep.ProtocolId:={ClassicBluetoothProtocolId} AND (System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True OR System.Devices.Aep.Bluetooth.IssueInquiry:=System.StructuredQueryType.Boolean#False))";

    private static readonly string[] AdditionalProperties =
    {
        "System.ItemNameDisplay",
        "System.Devices.Aep.IsPaired",
        "System.Devices.Aep.IsConnected",
    };

    public event Action<ScannedDevice>? DeviceAdded;
    public event Action<ScannedDevice>? DeviceUpdated;
    public event Action<ulong>? DeviceRemoved;

    public void Start()
    {
        if (_watcher != null) return;

        var watcher = DeviceInformation.CreateWatcher(
            Aqs, AdditionalProperties, DeviceInformationKind.AssociationEndpoint);

        watcher.Added += OnAdded;
        watcher.Updated += OnUpdated;
        watcher.Removed += OnRemoved;
        _watcher = watcher;
        watcher.Start();
    }

    public void Stop()
    {
        var watcher = _watcher;
        if (watcher == null) return;
        _watcher = null;
        try
        {
            watcher.Added -= OnAdded;
            watcher.Updated -= OnUpdated;
            watcher.Removed -= OnRemoved;
            watcher.Stop();
        }
        catch { /* 已停止/枚举完成时 Stop 可能抛，忽略 */ }
        _addressById.Clear();
    }

    private void OnAdded(DeviceWatcher sender, DeviceInformation info)
    {
        var d = ToScanned(info);
        if (d == null) return;
        _addressById[info.Id] = d.Address;
        DeviceAdded?.Invoke(d);
    }

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (!_addressById.TryGetValue(update.Id, out var addr)) return;
        var d = FromProperties(update.Properties, addr, fallbackName: null);
        if (d == null) return;
        DeviceUpdated?.Invoke(d);
    }

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (_addressById.TryRemove(update.Id, out var addr))
            DeviceRemoved?.Invoke(addr);
    }

    private static ScannedDevice? ToScanned(DeviceInformation info)
    {
        var addr = ParseAddress(info);
        if (addr == 0) return null;
        var name = string.IsNullOrWhiteSpace(info.Name) ? "未知设备" : info.Name;
        var paired = info.Pairing?.IsPaired ?? false;
        // CoD 属性键在多数驱动下不可用（已实测），分类退化为名字信号 + 连接后服务信号。
        return new ScannedDevice(addr, name, paired, (BluetoothMajorClass)0);
    }

    private static ScannedDevice? FromProperties(IReadOnlyDictionary<string, object> props, ulong addr, string? fallbackName)
    {
        if (addr == 0) return null;
        string name = fallbackName ?? "未知设备";
        if (props.TryGetValue("System.ItemNameDisplay", out var n) && n is string s && !string.IsNullOrWhiteSpace(s))
            name = s;
        bool paired = false;
        if (props.TryGetValue("System.Devices.Aep.IsPaired", out var p) && p is bool b) paired = b;
        return new ScannedDevice(addr, name, paired, (BluetoothMajorClass)0);
    }

    private static ulong ParseAddress(DeviceInformation info)
    {
        // AepId 实测格式："Bluetooth#Bluetoothcc:47:40:03:17:cd-d4:ba:fa:9c:e4:46"
        // 末段（最后一个 '-' 之后）即对端 MAC。
        try
        {
            var id = info.Id;
            var idx = id.LastIndexOf('-');
            if (idx >= 0)
            {
                var tail = id[(idx + 1)..].Replace(":", "");
                if (tail.Length == 12 && ulong.TryParse(tail, System.Globalization.NumberStyles.HexNumber, null, out var mac) && mac != 0)
                    return mac;
            }
        }
        catch { }
        return 0;
    }

    public void Dispose() => Stop();
}
