using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;

namespace FileTransferApp.WinUI.Bluetooth.Core;

internal sealed record DiscoveredDevice(string Name, ulong Address);

/// <summary>Enumerates Bluetooth Classic devices (paired) and their addresses for the device column.</summary>
internal static class Discovery
{
    /// <summary>Lists all paired Bluetooth Classic devices (fast & reliable).
    /// Whether a device exposes the BlueSelf service is checked at connection time,
    /// not here, so devices don't disappear just because the peer app isn't running.</summary>
    public static async Task<IReadOnlyList<DiscoveredDevice>> GetPairedDevicesAsync()
    {
        var result = new List<DiscoveredDevice>();
        try
        {
            var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var infos = await DeviceInformation.FindAllAsync(selector, null, DeviceInformationKind.Device);
            foreach (var info in infos)
            {
                var device = await BluetoothDevice.FromIdAsync(info.Id);
                if (device == null) continue;

                var name = string.IsNullOrWhiteSpace(device.Name) ? info.Name : device.Name;
                if (string.IsNullOrWhiteSpace(name)) name = "未知设备";

                // 过滤耳机/键盘等非对端设备，只保留手机/电脑/平板类设备。
                if (!IsLikelyPeerDevice(name, device.ClassOfDevice.MajorClass)) continue;

                result.Add(new DiscoveredDevice(name, device.BluetoothAddress));
            }
        }
        catch (Exception)
        {
            // Bluetooth unavailable / permissions missing → empty list.
        }
        return result;
    }

    /// <summary>True if the device is likely a BlueSelf peer (phone/computer/tablet), not an accessory.
    /// Uses Bluetooth major class as the primary signal, with name-based fallback for devices
    /// that don't report a usable class.</summary>
    internal static bool IsLikelyPeerDevice(string name, BluetoothMajorClass majorClass)
    {
        // 明确的对端类型（手机/电脑）直接保留。
        switch (majorClass)
        {
            case BluetoothMajorClass.Phone:
            case BluetoothMajorClass.Computer:
                return true;
            // 明确的配件类型（音频/键盘鼠标等）直接过滤。
            case BluetoothMajorClass.AudioVideo:
            case BluetoothMajorClass.Peripheral:
            case BluetoothMajorClass.Imaging:
                return false;
        }

        // 其它/未知类型：退化为按名称判断，避免依赖不可靠的 MajorClass。
        var lower = name.ToLowerInvariant();
        return lower.Contains("phone") || lower.Contains("手机")
            || lower.Contains("pc") || lower.Contains("windows") || lower.Contains("电脑") || lower.Contains("mac")
            || lower.Contains("tab") || lower.Contains("pad") || lower.Contains("平板") || lower.Contains("ipad")
            || lower.Contains("pixel") || lower.Contains("galaxy") || lower.Contains("xiaomi");
    }
}