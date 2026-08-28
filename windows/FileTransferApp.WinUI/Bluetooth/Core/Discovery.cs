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

    /// <summary>True if the device is a plausible BlueSelf peer. Only filters clear accessory classes
    /// (audio/peripheral/imaging); unknown names/classes are kept so real peers are never dropped —
    /// final peer confirmation happens at connection time (RFCOMM service UUID match).</summary>
    internal static bool IsLikelyPeerDevice(string name, BluetoothMajorClass majorClass)
    {
        // 明确的配件类型直接过滤；其余全部保留（含未知名字 + 未知 CoD）。
        switch (majorClass)
        {
            case BluetoothMajorClass.AudioVideo:
            case BluetoothMajorClass.Peripheral:
            case BluetoothMajorClass.Imaging:
                return false;
        }
        return true;
    }
}