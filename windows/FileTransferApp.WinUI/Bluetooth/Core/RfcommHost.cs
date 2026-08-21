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

        await listener.BindServiceNameAsync(
            provider.ServiceId.AsString(),
            SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);

        provider.StartAdvertising(listener);

        _provider = provider;
        _listener = listener;
    }

    /// <summary>Connects to a remote device by its Bluetooth address (client role).</summary>
    public static async Task<StreamSocket> ConnectAsync(ulong btAddress)
    {
        var device = await BluetoothDevice.FromBluetoothAddressAsync(btAddress);
        if (device == null) throw new InvalidOperationException("No such Bluetooth device.");

        var result = await device.GetRfcommServicesForIdAsync(
            RfcommServiceId.FromUuid(MessageProtocol.AppServiceId),
            BluetoothCacheMode.Uncached);

        var serviceInfo = result.Services.FirstOrDefault()
            ?? throw new InvalidOperationException($"{device.Name} does not expose the BlueSelf service.");

        var socket = new StreamSocket();
        await socket.ConnectAsync(
            serviceInfo.ConnectionHostName,
            serviceInfo.ConnectionServiceName,
            SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
        return socket;
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