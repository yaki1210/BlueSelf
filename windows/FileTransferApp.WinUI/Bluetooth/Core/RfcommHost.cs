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
    private readonly SemaphoreSlim _listenLock = new(1, 1);

    /// <summary>True once the BlueSelf service is being advertised.</summary>
    public bool IsListening => _provider != null && _listener != null;

    /// <summary>Raised (on the UI SynchronizationContext) when a remote device connects to us.</summary>
    public event Action<StreamSocket>? IncomingConnected;

    /// <summary>
    /// Publishes the BlueSelf RFCOMM service so Android can connect to this PC.
    /// </summary>
    public async Task StartListeningAsync()
    {
        await _listenLock.WaitAsync();
        try
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
                ConnectionLog.Write("Listener advertising = true");
            }
            catch (Exception ex)
            {
                ConnectionLog.Write("Listener start failed", ex.Message);
                try { provider.StopAdvertising(); } catch { }
                try { listener.Dispose(); } catch { }
                throw;
            }
        }
        finally { _listenLock.Release(); }
    }

    /// <summary>
    /// 销毁并重建监听（蓝牙开关后调用；DisposeAsync 会把 IsListening 置假）。
    /// 与 StartListeningAsync 共用同一把锁，防止并发重建。
    /// </summary>
    public async Task RestartAsync()
    {
        await _listenLock.WaitAsync();
        try
        {
            ConnectionLog.Write("Listener restart requested");
            try { _provider?.StopAdvertising(); } catch { }
            try { _listener?.Dispose(); } catch { }
            _provider = null;
            _listener = null;

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
                ConnectionLog.Write("Listener advertising = true", "restart");
            }
            catch (Exception ex)
            {
                ConnectionLog.Write("Listener restart failed", ex.Message);
                try { provider.StopAdvertising(); } catch { }
                try { listener.Dispose(); } catch { }
                throw;
            }
        }
        finally { _listenLock.Release(); }
    }

    /// <summary>SDP 查询超时：蓝牙栈挂起时不至于无限等待。</summary>
    private static readonly TimeSpan SdpTimeout = TimeSpan.FromSeconds(8);
    /// <summary>建链超时。</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Connects to a remote device by its Bluetooth address (client role).</summary>
    public static async Task<StreamSocket> ConnectAsync(ulong btAddress, int maxAttempts = 2)
    {
        Exception? lastError = null;
        // 整体尝试 maxAttempts 次（默认 2）：第一次快失败（用缓存服务）；失败后短退避再走最新 SDP。
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var device = await BluetoothDevice.FromBluetoothAddressAsync(btAddress);
                if (device == null)
                    throw new InvalidOperationException("未找到该蓝牙设备");

                ConnectionLog.Write($"Device = 0x{btAddress:X12}", device.Name);

                // 先用 Cached（快、无需对方即时广播），找不到再走 Uncached 实时查询。均包超时。
                foreach (var mode in new[] { BluetoothCacheMode.Cached, BluetoothCacheMode.Uncached })
                {
                    var result = await device.GetRfcommServicesForIdAsync(
                        RfcommServiceId.FromUuid(MessageProtocol.AppServiceId), mode)
                        .AsTask().WaitAsync(SdpTimeout);
                    ConnectionLog.Write($"{mode} SDP = {result.Services.Count} services");
                    var serviceInfo = result.Services.FirstOrDefault();
                    if (serviceInfo != null)
                    {
                        var socket = new StreamSocket();
                        try
                        {
                            await socket.ConnectAsync(
                                serviceInfo.ConnectionHostName,
                                serviceInfo.ConnectionServiceName,
                                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication)
                                .AsTask().WaitAsync(ConnectTimeout);
                            ConnectionLog.Write("ConnectAsync ok");
                            return socket;
                        }
                        catch (Exception cex)
                        {
                            try { socket.Dispose(); } catch { }
                            ConnectionLog.Write($"{mode} connect failed", cex.Message);
                            throw; // 与原逻辑一致：跳出本次尝试，进入下一轮整体重试
                        }
                    }
                }
                throw new InvalidOperationException($"{device.Name} 未开启 BlueSelf 服务（请确认手机端 App 已打开）");
            }
            catch (Exception ex)
            {
                lastError = ex;
                ConnectionLog.Write($"Attempt {attempt + 1} failed", ex.Message);
                if (attempt < maxAttempts - 1) await Task.Delay(TimeSpan.FromSeconds(attempt + 1)); // 短退避 1s/2s
            }
        }

        throw lastError switch
        {
            TimeoutException => new TimeoutException($"连接超时（查询/建链均无响应），手机可能不可达或已超出范围"),
            _ => lastError ?? new InvalidOperationException("Failed to connect to the Bluetooth device.")
        };
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