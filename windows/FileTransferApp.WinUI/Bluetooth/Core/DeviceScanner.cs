using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace FileTransferApp.WinUI.Bluetooth.Core;

/// <summary>A nearby Bluetooth device surfaced by the add-device page.</summary>
internal sealed record ScannedDevice(ulong Address, string Name, bool IsPaired);

/// <summary>
/// Scans for nearby Bluetooth Classic devices (including unpaired ones) and merges
/// them with the already-paired device list. This backs the "add new device" page so a
/// phone can be paired + connected from inside the app instead of jumping to Settings.
/// </summary>
internal static class DeviceScanner
{
    /// <summary>
    /// Scans for nearby devices for up to <paramref name="timeoutSeconds"/> seconds.
    /// Always merges in all paired devices. Never throws; returns whatever was found.
    /// </summary>
    public static async Task<IReadOnlyList<ScannedDevice>> ScanAsync(int timeoutSeconds = 12)
    {
        var byAddress = new Dictionary<ulong, ScannedDevice>();

        // 已配对设备始终可见（兜底）。
        foreach (var paired in await Discovery.GetPairedDevicesAsync())
        {
            byAddress[paired.Address] = new ScannedDevice(paired.Address, paired.Name, IsPaired: true);
        }

        try
        {
            var selector = BluetoothDevice.GetDeviceSelector();
            var infos = await DeviceInformation.FindAllAsync(
                selector, null, DeviceInformationKind.AssociationEndpoint).AsTask().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));

            foreach (var info in infos)
            {
                try
                {
                    var device = await BluetoothDevice.FromIdAsync(info.Id);
                    if (device == null) continue;

                    var address = device.BluetoothAddress;
                    if (address == 0) continue;

                    var name = string.IsNullOrWhiteSpace(device.Name) ? info.Name : device.Name;
                    if (string.IsNullOrWhiteSpace(name)) name = "未知设备";

                    // 过滤耳机/键盘等非对端设备。
                    if (!Discovery.IsLikelyPeerDevice(name, device.ClassOfDevice.MajorClass)) continue;

                    var isPaired = device.DeviceInformation.Pairing.IsPaired;

                    // 未配对设备插入；已配对设备保留配对标记。
                    byAddress[address] = new ScannedDevice(address, name, isPaired);
                }
                catch
                {
                    // 单个设备失败不阻塞整体扫描。
                }
            }
        }
        catch
        {
            // 扫描失败/超时：以已配对设备为兜底结果返回。
        }

        // 名字为空的项目（个别驱动只返回地址）sanitize。
        return byAddress.Values.OrderByDescending(d => d.IsPaired)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}