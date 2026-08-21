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

/// <summary>An inbound file notification (receiver shows it in the inbox).</summary>
public sealed record InboundFile(string Id, string Name, long Size, string SavePath);

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

    // Inbound file session (one active at a time)
    private ReceiveSession? _recv;

    public event Action<string>? TextReceived;
    public event Action<InboundFile>? FileReceived;
    public event Action<TransferProgress>? Progress; // both directions
    public event Action<string>? Info;
    public event Action<string>? LogError;
    public event Action? Disconnected;

    public TransferService(string saveDir)
    {
        _ui = SynchronizationContext.Current;
        SaveDir = saveDir;
    }

    public string SaveDir { get; }
    public bool IsConnected => _socket != null;

    // ---------- Connection ----------

    public void Attach(StreamSocket socket)
    {
        _socket = socket;
        _cts = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoop(socket, _cts.Token));
    }

    public void Detach()
    {
        try { _cts?.Cancel(); } catch { }
        try { _socket?.Dispose(); } catch { }
        try { _socket?.InputStream.Dispose(); } catch { }
        try { _socket?.OutputStream.Dispose(); } catch { }
        _socket = null;
        ClearReceiveSession();
        Post(() => Disconnected?.Invoke());
    }

    // ---------- Send ----------

    public async Task SendTextAsync(string content)
    {
        var buf = FileMetaJson.EncodeText(new Packet(
            MessageProtocol.ProtocolVersion, "TEXT", Guid.NewGuid().ToString(),
            "blueself-pc", Environment.MachineName, "remote", "remote", content,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await WriteFramesAsync(new[] { new Frame(MessageProtocol.TypeTxt, NextSeq(), buf) });
    }

    public async Task SendFileAsync(string path, CancellationToken ct = default)
    {
        var file = new FileInfo(path);
        var size = file.Length;
        if (size <= 0) return;

        var md5 = await ComputeMd5Async(path, ct);
        var fileId = Guid.NewGuid().ToString("N");
        var chunkSize = MessageProtocol.DefaultChunkSize;
        var totalChunks = (long)Math.Ceiling(size / (double)chunkSize);
        var start = DateTime.UtcNow;

        var startFrame = new Frame(MessageProtocol.TypeFileStart, NextSeq(),
            FileMetaJson.EncodeStart(fileId, fileId, file.Name, "application/octet-stream", size, md5, chunkSize, totalChunks));
        await WriteFramesAsync(new[] { startFrame });

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
                    if (sentBytes - lastProgress >= chunkSize * 4)
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
            return;
        }

        var endFrame = new Frame(MessageProtocol.TypeFileEnd, NextSeq(),
            FileMetaJson.EncodeEnd(fileId, totalChunks, md5));
        await WriteFramesAsync(new[] { endFrame });
        EmitProgress(TransferDir.SEND, size, size, start);
        Post(() => Info?.Invoke($"已发送 {file.Name}"));
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
            await writer.FlushAsync();
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
                HandleFrame(frame, socket);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Post(() => LogError?.Invoke($"连接中断: {ex.Message}"));
        }
        finally
        {
            Detach();
        }
    }

    private void HandleFrame(Frame frame, StreamSocket socket)
    {
        switch (frame.Type)
        {
            case MessageProtocol.TypeTxt:
                var pkt = FileMetaJson.DecodeText(frame.Payload, "remote", "远端设备");
                Post(() => TextReceived?.Invoke(pkt.Content));
                break;
            case MessageProtocol.TypeFileStart:
                HandleFileStart(frame);
                break;
            case MessageProtocol.TypeFileChunk:
                _ = HandleFileChunk(frame);
                break;
            case MessageProtocol.TypeFileEnd:
                _ = HandleFileEnd(frame);
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
                Post(() => FileReceived?.Invoke(new InboundFile(end.Id, session.Meta.Name, session.Meta.Size, finalPath)));
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