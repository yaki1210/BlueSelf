using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;

namespace FileTransferApp.WinUI.Bluetooth.Core;

/// <summary>
/// Manages the RFCOMM socket from both roles:
///   Server:  RfcommServiceProvider + StreamSocketListener (accepts incoming Android connections).
///   Client:  BluetoothDevice.GetRfcommServicesForIdAsync → StreamSocket.ConnectAsync.
/// Produces a live <see cref="StreamSocket"/> that the transfer pipeline reads/writes.
/// </summary>
internal sealed class RfcommHost : IAsyncDisposable
{
    private RfcommServiceProvider? _provider;
    private StreamSocketListener? _listener;

    /// <summary>True once the BlueSelf service is being advertised.</summary>
    public bool IsListening => _provider != null && _listener != null;

    /// <summary>Raised (on the UI SynchronizationContext) when a remote device connects to us.</summary>
    public event Action<StreamSocket>? IncomingConnected;

    /// <summary>
    /// Publishes the BlueSelf RFCOMM service so Android can connect to this PC.
    /// </summary>
    public async Task StartListeningAsync()
    {
        if (_provider != null) return;

        var provider = await RfcommServiceProvider.CreateAsync(
            RfcommServiceId.FromUuid(MessageProtocol.AppServiceId));
        var listener = new StreamSocketListener();
        listener.ConnectionReceived += (_, args) =>
            IncomingConnected?.Invoke(args.Socket);

        try
        {
            await listener.BindServiceNameAsync(
                provider.ServiceId.AsString(),
                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);

            provider.StartAdvertising(listener);

            _provider = provider;
            _listener = listener;
        }
        catch
        {
            try { provider.StopAdvertising(); } catch { }
            try { listener.Dispose(); } catch { }
            throw;
        }
    }

    /// <summary>Connects to a remote device by its Bluetooth address (client role).</summary>
    public static async Task<StreamSocket> ConnectAsync(ulong btAddress)
    {
        Exception? lastError = null;
        // 整体尝试两次：第一次快失败（用缓存服务）；失败后整体重试一次（走最新 SDP）。
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var device = await BluetoothDevice.FromBluetoothAddressAsync(btAddress);
                if (device == null)
                    throw new InvalidOperationException("未找到该蓝牙设备");

                // 先用 Cached（快、无需对方即时广播），找不到再走 Uncached 实时查询。
                foreach (var mode in new[] { BluetoothCacheMode.Cached, BluetoothCacheMode.Uncached })
                {
                    var result = await device.GetRfcommServicesForIdAsync(
                        RfcommServiceId.FromUuid(MessageProtocol.AppServiceId), mode);
                    var serviceInfo = result.Services.FirstOrDefault();
                    if (serviceInfo != null)
                    {
                        var socket = new StreamSocket();
                        await socket.ConnectAsync(
                            serviceInfo.ConnectionHostName,
                            serviceInfo.ConnectionServiceName,
                            SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
                        return socket;
                    }
                }
                throw new InvalidOperationException($"{device.Name} 未开启 BlueSelf 服务（请确认手机端 App 已打开）");
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        throw lastError ?? new InvalidOperationException("Failed to connect to the Bluetooth device.");
    }

    public async ValueTask DisposeAsync()
    {
        try { _provider?.StopAdvertising(); } catch { }
        try { _listener?.Dispose(); } catch { }
        _provider = null;
        _listener = null;
        await Task.CompletedTask;
    }
}