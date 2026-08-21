using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Channels;
using Windows.Networking.Sockets;
using FileTransferApp.WinUI.Bluetooth.Core;

namespace FileTransferApp.WinUI.Bluetooth;

/// <summary>Direction of a transfer (drives the status column header/icon).</summary>
public enum TransferDir { SEND, RECEIVE }

/// <summary>Progress snapshot pushed to the UI (values are simple; VM formats them).</summary>
public sealed record TransferProgress(TransferDir Dir, double Fraction, long BytesDone, long TotalBytes, long ElapsedMs);

/// <summary>An inbound file notification (receiver shows it in the inbox).
/// <paramref name="ParentMessageId"/> is the parent TEXT message id (from FILE_START.msgId),
/// used by the UI to merge a file into the message it belongs to.</summary>
public sealed record InboundFile(string Id, string Name, long Size, string SavePath, string ParentMessageId = "");

/// <summary>
/// Drives the frame pipeline over a live <see cref="StreamSocket"/>. It both:
///   - sends text / file (window-batched, time-throttled progress, MD5),
///   - reads and handles inbound frames (text + file receive with read/write decoupled to disk).
/// Events are marshalled to the UI SynchronizationContext captured at construction.
/// </summary>
public sealed class TransferService : IDisposable
{
    private readonly SynchronizationContext? _ui;
    private StreamSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private long _seq;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Bluetooth address of the currently connected peer (null when disconnected).</summary>
    public ulong? ConnectedPeerAddress { get; private set; }

    // Inbound file session (one active at a time)
    private ReceiveSession? _recv;

    /// <summary>Raised with (textMessageId, content) when a TEXT frame arrives.</summary>
    public event Action<string, string>? TextReceived;
    public event Action<InboundFile>? FileReceived;
    /// <summary>Raised with the incoming file name when an inbound FILE_START begins (for the status panel).</summary>
    public event Action<string>? ReceiveStarted;
    public event Action<TransferProgress>? Progress; // both directions
    public event Action<TransferDir>? TransferCompleted; // send/receive finished (UI hides status)
    public event Action<string>? Info;
    public event Action<string>? LogError;
    public event Action? Disconnected;

    public TransferService(string saveDir)
    {
        _ui = SynchronizationContext.Current;
        SaveDir = saveDir;
    }

    public string SaveDir { get; set; }
    public bool IsConnected => _socket != null;

    /// <summary>
    /// Returns a device name the peer can classify into the correct icon:
    /// if the machine name doesn't already contain "电脑"/"Windows", append " Windows"
    /// so Android shows the PC icon for this device.
    /// </summary>
    public static string LocalDeviceName()
    {
        var name = Environment.MachineName;
        if (string.IsNullOrWhiteSpace(name)) return "Windows";
        if (name.Contains("电脑", StringComparison.Ordinal) || name.Contains("Windows", StringComparison.OrdinalIgnoreCase))
            return name;
        return name + " Windows";
    }

    // ---------- Connection ----------

    public void Attach(StreamSocket socket) => Attach(socket, null);

    public void Attach(StreamSocket socket, ulong? peerAddress)
    {
        // 先安全清理旧连接，避免旧 ReadLoop 与新连接串扰。
        Detach(silent: true);

        _socket = socket;
        ConnectedPeerAddress = peerAddress;
        _cts = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoop(socket, _cts.Token));
    }

    /// <summary>断开当前连接（幂等）。仅当确实有活动 socket 时才触发 Disconnected 事件。</summary>
    public void Detach() => Detach(silent: false);

    private void Detach(bool silent)
    {
        var hadActive = _socket != null;
        var cts = _cts;
        _cts = null;
        try { cts?.Cancel(); } catch { }
        var socket = _socket;
        _socket = null;
        ConnectedPeerAddress = null;
        if (socket != null)
        {
            try { socket.Dispose(); } catch { }
            try { socket.InputStream.Dispose(); } catch { }
            try { socket.OutputStream.Dispose(); } catch { }
        }
        ClearReceiveSession();
        if (hadActive && !silent) Post(() => Disconnected?.Invoke());
    }

    /// <summary>移除一个已知的连接，仅当其仍是当前连接时才做全局清理（用于 ReadLoop 退出时）。</summary>
    private void ClearIfCurrent(StreamSocket socket)
    {
        if (!ReferenceEquals(_socket, socket)) return; // 已被新连接替换，不做任何清理
        Detach(silent: true);
    }

    // ---------- Send ----------

    public async Task SendTextAsync(string content, string? messageId = null)
    {
        var id = messageId ?? Guid.NewGuid().ToString();
        var buf = FileMetaJson.EncodeText(new Packet(
            MessageProtocol.ProtocolVersion, "TEXT", id,
            "blueself-pc", LocalDeviceName(), "remote", "remote", content,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await WriteFramesAsync(new[] { new Frame(MessageProtocol.TypeTxt, NextSeq(), buf) });
    }

    public async Task SendFileAsync(string path, string fileId, string? msgId = null, CancellationToken ct = default)
    {
        var file = new FileInfo(path);
        var size = file.Length;
        if (size <= 0) return;

        var md5 = await ComputeMd5Async(path, ct);
        var chunkSize = MessageProtocol.DefaultChunkSize;
        var totalChunks = (long)Math.Ceiling(size / (double)chunkSize);
        var start = DateTime.UtcNow;

        // FILE_START.msgId 必须等于父 TXT 消息的 id，Android 端才会把文件挂到该消息下。
        var parentMsgId = msgId ?? fileId;
        var startFrame = new Frame(MessageProtocol.TypeFileStart, NextSeq(),
            FileMetaJson.EncodeStart(fileId, parentMsgId, file.Name, "application/octet-stream", size, md5, chunkSize, totalChunks));
        await WriteFramesAsync(new[] { startFrame });
        EmitProgress(TransferDir.SEND, size, 0, start); // 0% 起点

        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, chunkSize, FileOptions.SequentialScan);
        var buffer = new byte[chunkSize];
        var sentBytes = 0L;
        long lastProgress = 0;

        try
        {
            while (true)
            {
                var window = new List<Frame>(MessageProtocol.SendWindow);
                while (window.Count < MessageProtocol.SendWindow)
                {
                    var read = await input.ReadAsync(buffer, ct);
                    if (read <= 0) break;
                    var payload = new byte[4 + read];
                    BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)(sentBytes / chunkSize));
                    Buffer.BlockCopy(buffer, 0, payload, 4, read);
                    window.Add(new Frame(MessageProtocol.TypeFileChunk, NextSeq(), payload));
                    sentBytes += read;
                    if (sentBytes - lastProgress >= chunkSize)
                    {
                        EmitProgress(TransferDir.SEND, size, sentBytes, start);
                        lastProgress = sentBytes;
                    }
                }
                if (window.Count == 0) break;
                await WriteFramesAsync(window);
            }
        }
        catch (Exception ex)
        {
            Post(() => LogError?.Invoke($"发送失败: {ex.Message}"));
            Post(() => TransferCompleted?.Invoke(TransferDir.SEND));
            throw; // 上抛，让调用方感知失败，避免"看起来发送成功实际没发出去"
        }

        var endFrame = new Frame(MessageProtocol.TypeFileEnd, NextSeq(),
            FileMetaJson.EncodeEnd(fileId, totalChunks, md5));
        await WriteFramesAsync(new[] { endFrame });
        EmitProgress(TransferDir.SEND, size, size, start);
        Post(() => Info?.Invoke($"已发送 {file.Name}"));
        Post(() => TransferCompleted?.Invoke(TransferDir.SEND));
    }

    private async Task WriteFramesAsync(IEnumerable<Frame> frames)
    {
        var socket = _socket;
        if (socket == null) throw new InvalidOperationException("未连接设备");
        var data = FrameCodec.EncodeBatch(frames.ToArray());
        var writer = new Windows.Storage.Streams.DataWriter(socket.OutputStream);
        writer.WriteBytes(data);
        await _writeLock.WaitAsync();
        try
        {
            await writer.StoreAsync();
            // 注意：不调用 FlushAsync()。对 StreamSocket 输出流，FlushAsync 会等待对端消费确认，
            // 蓝牙流控下可能长时间挂起导致发送 UI 卡"传输中"。数据已交给蓝牙栈发送，
            // 帧可靠性由协议层的 FILE_ACK / MD5 端到端校验保证（与 Android 端 flush 语义一致）。
        }
        finally
        {
            _writeLock.Release();
            writer.DetachStream();
            writer.Dispose();
        }
    }

    // ---------- Read loop (also ACK/Emit) ----------

    private async Task ReadLoop(StreamSocket socket, CancellationToken ct)
    {
        using var reader = new Windows.Storage.Streams.DataReader(socket.InputStream);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Frame frame;
                try
                {
                    frame = await FrameCodec.DecodeAsync(reader, ct);
                }
                catch (EndOfStreamException)
                {
                    break;
                }
                await HandleFrameAsync(frame, socket);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Post(() => LogError?.Invoke($"连接中断: {ex.Message}"));
        }
        finally
        {
            // 只清理本循环所属的连接；若已被新连接替换则不动，避免误杀新连接。
            ClearIfCurrent(socket);
        }
    }

    private async Task HandleFrameAsync(Frame frame, StreamSocket socket)
    {
        switch (frame.Type)
        {
            case MessageProtocol.TypeTxt:
                var pkt = FileMetaJson.DecodeText(frame.Payload, "remote", "远端设备");
                Post(() => TextReceived?.Invoke(pkt.Id, pkt.Content));
                break;
            case MessageProtocol.TypeFileStart:
                HandleFileStart(frame);
                break;
            case MessageProtocol.TypeFileChunk:
                // 严格按读取顺序写入磁盘（对齐 Android 的同步入队），避免乱序导致 MD5 失败
                await HandleFileChunk(frame);
                break;
            case MessageProtocol.TypeFileEnd:
                await HandleFileEnd(frame);
                break;
            case MessageProtocol.TypeFileAck:
                break; // acks are informational; progress is driven locally
            case MessageProtocol.TypeErr:
                FileMetaJson.DecodeError(frame.Payload, out var code, out var msg);
                Post(() => LogError?.Invoke($"对端错误[{code}]: {msg}"));
                break;
        }
    }

    // ---------- Receive (decoupled: read→channel→writer) ----------

    private void HandleFileStart(Frame frame)
    {
        ClearReceiveSession();
        var meta = FileMetaJson.DecodeStart(frame.Payload);
        var stagingDir = Path.Combine(SaveDir, "_staging");
        Directory.CreateDirectory(stagingDir);
        var stagingPath = Path.Combine(stagingDir, meta.Id);

        var channel = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait });
        var outStream = new FileStream(stagingPath, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous);
        var session = new ReceiveSession(meta, stagingPath, outStream, channel);
        session.WriterTask = Task.Run(() => DrainWriter(session));
        _recv = session;

        EmitProgress(TransferDir.RECEIVE, meta.Size, 0, DateTime.UtcNow);
        Post(() => Info?.Invoke($"开始接收 {meta.Name}（{meta.Size} 字节）"));
        Post(() => ReceiveStarted?.Invoke(meta.Name));
    }

    private async Task DrainWriter(ReceiveSession s)
    {
        try
        {
            await foreach (var bytes in s.Channel.Reader.ReadAllAsync())
            {
                await s.Out.WriteAsync(bytes);
            }
        }
        catch (Exception ex)
        {
            s.WriteError = ex.Message;
        }
    }

    private async Task HandleFileChunk(Frame frame)
    {
        var payload = frame.Payload;
        if (payload.Length < 4) return;
        var session = _recv;
        if (session == null) return;
        var bytes = new byte[payload.Length - 4];
        Buffer.BlockCopy(payload, 4, bytes, 0, bytes.Length);

        await session.Channel.Writer.WriteAsync(bytes);
        session.ReceivedBytes += bytes.Length;

        var now = DateTime.UtcNow;
        if (now - session.LastAckTime >= TimeSpan.FromMilliseconds(1000))
        {
            session.LastAckTime = now;
            _ = SendAckAsync(session.Meta.Id, session.ReceivedBytes, false, false);
            EmitProgress(TransferDir.RECEIVE, session.Meta.Size, session.ReceivedBytes, session.StartedAt);
        }
    }

    private async Task HandleFileEnd(Frame frame)
    {
        var end = FileMetaJson.DecodeEnd(frame.Payload);
        var session = _recv;
        if (session == null || session.Meta.Id != end.Id) return;
        _recv = null;

        session.Channel.Writer.TryComplete();
        if (session.WriterTask != null) await session.WriterTask;
        await session.Out.FlushAsync();
        await session.Out.DisposeAsync();

        var md5Ok = session.WriteError == null && await VerifyMd5Async(session.StagingPath, end.Md5);
        await SendAckAsync(end.Id, session.ReceivedBytes, true, md5Ok);

        if (md5Ok && File.Exists(session.StagingPath))
        {
            var finalPath = Path.Combine(SaveDir, SanitizeFileName(session.Meta.Name));
            try
            {
                File.Move(session.StagingPath, finalPath, overwrite: true);
                Post(() => FileReceived?.Invoke(new InboundFile(end.Id, session.Meta.Name, session.Meta.Size, finalPath, session.Meta.MsgId)));
                Post(() => Info?.Invoke($"文件保存位置: {finalPath}"));
            }
            catch (Exception ex)
            {
                Post(() => LogError?.Invoke($"保存文件失败: {ex.Message}"));
            }
        }
        else
        {
            try { if (File.Exists(session.StagingPath)) File.Delete(session.StagingPath); } catch { }
            Post(() => LogError?.Invoke($"接收 {session.Meta.Name} 校验失败"));
        }
        Post(() => TransferCompleted?.Invoke(TransferDir.RECEIVE));
    }

    private void ClearReceiveSession()
    {
        var s = _recv;
        if (s == null) return;
        _recv = null;
        try { s.Channel.Writer.TryComplete(); } catch { }
        try { s.Out.Dispose(); } catch { }
        try { if (File.Exists(s.StagingPath)) File.Delete(s.StagingPath); } catch { }
    }

    private async Task SendAckAsync(string fileId, long ackedChunks, bool ok, bool md5Match)
    {
        try
        {
            await WriteFramesAsync(new[] { new Frame(MessageProtocol.TypeFileAck, NextSeq(),
                FileMetaJson.EncodeAck(fileId, ackedChunks, ok, md5Match)) });
        }
        catch (Exception) { }
    }

    private void EmitProgress(TransferDir dir, long total, long done, DateTime start)
    {
        if (total <= 0) return;
        var frac = (double)done / total;
        var elapsed = (long)(DateTime.UtcNow - start).TotalMilliseconds;
        Post(() => Progress?.Invoke(new TransferProgress(dir, frac, done, total, elapsed)));
    }

    // ---------- Utilities ----------

    private long NextSeq() => ++_seq;

    private void Post(Action action)
    {
        if (_ui != null && _ui != SynchronizationContext.Current)
            _ui.Post(_ => action(), null);
        else
            action();
    }

    private static async Task<string> ComputeMd5Async(string path, CancellationToken ct = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024))
        {
            var buf = new byte[64 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buf, ct);
                if (read <= 0) break;
                hash.AppendData(buf, 0, read);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<bool> VerifyMd5Async(string path, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;
        try
        {
            var actual = await ComputeMd5Async(path);
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "received.bin" : clean;
    }

    public void Dispose()
    {
        Detach();
        _writeLock.Dispose();
    }

    /// <summary>State for one active inbound file receive.</summary>
    private sealed class ReceiveSession(FileStart meta, string stagingPath, FileStream outStream, Channel<byte[]> channel)
    {
        public FileStart Meta { get; } = meta;
        public string StagingPath { get; } = stagingPath;
        public FileStream Out { get; } = outStream;
        public Channel<byte[]> Channel { get; } = channel;
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public long ReceivedBytes;
        public DateTime LastAckTime = DateTime.UtcNow;
        public string? WriteError;
        public Task? WriterTask;
    }
}