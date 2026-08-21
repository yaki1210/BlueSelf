using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace FileTransferApp.WinUI.Bluetooth.Core;

internal sealed record DiscoveredDevice(string Name, ulong Address);

/// <summary>Enumerates Bluetooth Classic devices (paired) and their addresses for the device column.</summary>
internal static class Discovery
{
    /// <summary>Lists currently paired Bluetooth devices. Returns an empty list when none/busy.</summary>
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
                result.Add(new DiscoveredDevice(string.IsNullOrWhiteSpace(name) ? "未知设备" : name, device.BluetoothAddress));
            }
        }
        catch (Exception)
        {
            // Bluetooth unavailable / permissions missing → empty list.
        }
        return result;
    }
}