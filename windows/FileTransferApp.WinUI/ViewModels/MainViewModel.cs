using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using FileTransferApp.WinUI.Bluetooth;
using FileTransferApp.WinUI.Bluetooth.Core;

namespace FileTransferApp.WinUI.ViewModels;

/// <summary>Device connection state shown by the status dot.</summary>
public enum DeviceStatus { Online, Connecting, Offline }

/// <summary>A Bluetooth device displayed in the device column.</summary>
public sealed class DeviceItem : ObservableObject
{
    public required string Name { get; init; }
    public required string SubLabel { get; init; }
    public ulong Address { get; init; }
    private DeviceStatus _status;
    public DeviceStatus Status { get => _status; set => Set(ref _status, value); }
}

/// <summary>A pending attachment chip in the composer.</summary>
public sealed class AttachmentItem : ObservableObject
{
    public required string Name { get; init; }
    public required string SizeText { get; init; }
    public required string Kind { get; init; } // pdf / image / file
    public required string PathText { get; init; }
}

/// <summary>A log line shown in the collapsible log area.</summary>
public sealed class LogEntry : ObservableObject
{
    public required string Time { get; init; }
    public required string Message { get; init; }
}

/// <summary>An entry in the inbox list.</summary>
public sealed class InboxEntry : ObservableObject
{
    public required string Device { get; init; }
    public required string Time { get; init; }
    public required string Preview { get; init; }
    public required string FileInfo { get; init; } // "" or "2 个文件"
    public required bool IsUnread { get; init; }
}

/// <summary>
/// Main UI state, wired to the real Bluetooth transfer pipeline.
/// Listening is always on; picking a device connects to it; sending uses the
/// live socket; inbound text/files surface in the inbox; progress feeds the
/// third status column.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new SynchronizationContext();
    private readonly RfcommHost _host = new();
    private readonly TransferService _transfer;
    private long _lastBytes;
    private DateTime _lastSample = DateTime.MinValue;

    public MainViewModel()
    {
        _transfer = new TransferService(SavePath);
        WireTransferEvents();
        LocalName = Environment.MachineName;

        Inbox.Add(new InboxEntry
        {
            Device = "系统", Time = "-", Preview = "等待接收消息。", FileInfo = "", IsUnread = false
        });

        Log("BlueSelf 已启动，正在开启蓝牙监听…");
        _ = InitAsync();
    }

    private static MainViewModel? _instance;
    public static MainViewModel Instance => _instance ??= new MainViewModel();

    // ---- App views ----
    public enum AppView { Workspace, Inbox, Settings }

    private AppView _currentView = AppView.Workspace;
    public AppView CurrentView
    {
        get => _currentView;
        set { if (Set(ref _currentView, value)) NotifyViewFlags(); }
    }
    private void NotifyViewFlags()
    {
        OnPropertyChanged(nameof(IsWorkspace));
        OnPropertyChanged(nameof(IsInbox));
        OnPropertyChanged(nameof(IsSettings));
    }
    public bool IsWorkspace => _currentView == AppView.Workspace;
    public bool IsInbox => _currentView == AppView.Inbox;
    public bool IsSettings => _currentView == AppView.Settings;

    public RelayCommandNoArg ShowWorkspaceCommand => new(() => CurrentView = AppView.Workspace);
    public RelayCommandNoArg ShowInboxCommand => new(() => CurrentView = AppView.Inbox);
    public RelayCommandNoArg ShowSettingsCommand => new(() => CurrentView = AppView.Settings);

    // ---- Bluetooth init / device column ----
    private async Task InitAsync()
    {
        try
        {
            var saveDir = SavePath;
            Directory.CreateDirectory(Path.Combine(saveDir, "_staging"));
            await _host.StartListeningAsync();
            _host.IncomingConnected += socket => { _transfer.Attach(socket); Post(() => { IsListening = true; Log("有设备接入，连接已建立"); }); };
            Post(() => Log("蓝牙监听已开启"));
        }
        catch (Exception ex)
        {
            Post(() => Log($"开启监听失败: {ex.Message}"));
        }
        await RefreshDevicesAsync();
    }

    private async Task RefreshDevicesAsync()
    {
        var devices = await Discovery.GetPairedDevicesAsync();
        Post(() =>
        {
            Devices.Clear();
            foreach (var d in devices)
            {
                Devices.Add(new DeviceItem
                {
                    Name = d.Name,
                    SubLabel = "已配对",
                    Address = d.Address,
                    Status = d.Address == _selectedAddress ? DeviceStatus.Online : DeviceStatus.Offline
                });
            }
            Log($"已发现 {Devices.Count} 台已配对设备");
        });
    }

    private ulong _selectedAddress;
    public ObservableCollection<DeviceItem> Devices { get; } = new();

    private DeviceItem? _selectedDevice;
    public DeviceItem? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (Set(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(HasSelectedDevice));
                ShowTargetHint = false;
                if (_selectedDevice != null) _ = ConnectAsync(_selectedDevice);
            }
        }
    }
    public bool HasSelectedDevice => SelectedDevice != null;

    private async Task ConnectAsync(DeviceItem device)
    {
        device.Status = DeviceStatus.Connecting;
        Log($"正在连接 {device.Name}…");
        try
        {
            var socket = await RfcommHost.ConnectAsync(device.Address);
            _selectedAddress = device.Address;
            _transfer.Attach(socket);
            device.Status = DeviceStatus.Online;
            Log($"已连接 {device.Name}");
        }
        catch (Exception ex)
        {
            device.Status = DeviceStatus.Offline;
            Log($"连接 {device.Name} 失败: {ex.Message}（如未配对，请先在系统蓝牙设置中配对）");
        }
    }

    public string LocalName { get; private set; }
    private bool _isListening;
    public bool IsListening { get => _isListening; set => Set(ref _isListening, value); }

    // 未选择设备时点了发送，才显示目标提示
    private bool _showTargetHint;
    public bool ShowTargetHint { get => _showTargetHint; set => Set(ref _showTargetHint, value); }

    public RelayCommandNoArg AddPairingCommand => new(() =>
    {
        Log("请在 Windows 设置 → 蓝牙与其他设备中完成配对");
        _ = Process.Start("ms-settings:bluetooth");
    });

    // ---- Composer ----
    private string _text = string.Empty;
    public string Text { get => _text; set => Set(ref _text, value); }
    public ObservableCollection<AttachmentItem> Attachments { get; } = new();

    public RelayCommandNoArg PasteCommand => new(() =>
    {
        var text = System.Windows.Clipboard.GetText();
        if (!string.IsNullOrWhiteSpace(text))
        {
            Text = string.IsNullOrWhiteSpace(Text) ? text : Text + text;
            Log("已粘贴文本");
        }
    });

    public RelayCommandNoArg AttachCommand => new(SendAttach);

    private void SendAttach()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "选择要发送的文件" };
        if (dlg.ShowDialog() != true) return;
        foreach (var file in dlg.FileNames)
        {
            var fi = new FileInfo(file);
            if (Attachments.All(a => a.PathText != fi.FullName))
            {
                Attachments.Add(new AttachmentItem
                {
                    Name = fi.Name,
                    SizeText = FormatSize(fi.Length),
                    Kind = KindOf(fi.Name),
                    PathText = fi.FullName
                });
            }
        }
        Log($"已添加 {dlg.FileNames.Length} 个附件");
    }

    public RelayCommandNoArg StartSendCommand => new(StartTransfer);

    private async void StartTransfer()
    {
        var device = SelectedDevice;
        if (device == null)
        {
            ShowTargetHint = true; // 未选设备时点了发送 → 显示提示
            Log("请先在设备栏选择目标设备");
            return;
        }
        if (!_transfer.IsConnected)
        {
            Log("尚未建立连接，请稍候或重新选择目标设备");
            return;
        }
        ShowTargetHint = false;
        var hasText = !string.IsNullOrWhiteSpace(Text);
        var hasFiles = Attachments.Count > 0;
        if (!hasText && !hasFiles)
        {
            Log("没有可发送的内容");
            return;
        }
        IsTransferring = true;
        var sent = false;
        try
        {
            if (hasText)
            {
                await _transfer.SendTextAsync(Text);
                AddOutgoing(device.Name, Text, Attachments.Count);
                sent = true;
                Text = string.Empty;
            }
            foreach (var att in Attachments.ToList())
            {
                FileName = att.Name;
                if (new FileInfo(att.PathText).Length > 20L * 1024 * 1024)
                    Log("提示：此文件过大，蓝牙传输需较长时间");
                await _transfer.SendFileAsync(att.PathText);
                sent = true;
            }
            if (sent) Attachments.Clear();
        }
        catch (Exception ex)
        {
            Log($"发送失败: {ex.Message}");
        }
        finally
        {
            ResetTransferUi();
        }
    }

    /// <summary>Removes an attachment by reference from the composer.</summary>
    public void RemoveAttachment(AttachmentItem item)
    {
        Attachments.Remove(item);
    }

    private void AddOutgoing(string device, string preview, int fileCount)
    {
        Inbox.Insert(0, new InboxEntry
        {
            Device = device,
            Time = DateTime.Now.ToString("HH:mm"),
            Preview = preview,
            FileInfo = fileCount > 0 ? $"{fileCount} 个文件" : "",
            IsUnread = false
        });
    }

    // ---- Inbox ----
    public ObservableCollection<InboxEntry> Inbox { get; } = new();
    private InboxEntry? _selectedInbox;
    public InboxEntry? SelectedInbox
    {
        get => _selectedInbox;
        set { if (Set(ref _selectedInbox, value)) OnPropertyChanged(nameof(DetailDevice)); }
    }
    public string DetailDevice => SelectedInbox?.Device ?? "";
    public string DetailTime => SelectedInbox?.Time ?? "";
    public string DetailPreview => SelectedInbox?.Preview ?? "";

    // ---- Settings ----
    public string[] Languages { get; } = { "中文", "English" };
    private string _language = "中文";
    public string Language
    {
        get => _language;
        set { if (Set(ref _language, value)) App.ApplyLanguage(value); }
    }
    public string[] Themes { get; } = { "跟随系统", "亮色", "暗色" };
    private string _theme = "跟随系统";
    public string Theme
    {
        get => _theme;
        set { if (Set(ref _theme, value)) App.ApplyTheme(value); }
    }
    public string SavePath => SaveDir;

    private static readonly string SaveDir = ResolveSaveDir();

    private static string ResolveSaveDir()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueSelf", "received"),
            Path.Combine(Path.GetTempPath(), "BlueSelf", "received")
        };
        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                Directory.CreateDirectory(Path.Combine(dir, "_staging"));
                return dir;
            }
            catch { /* try next */ }
        }
        return Path.Combine(Path.GetTempPath(), "BlueSelf", "received");
    }

    // ---- Transfer status (third column) ----
    private bool _isTransferring;
    public bool IsTransferring { get => _isTransferring; set => Set(ref _isTransferring, value); }
    private string _fileName = "";
    public string FileName { get => _fileName; set => Set(ref _fileName, value); }
    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    private string _percentText = "0%";
    public string PercentText { get => _percentText; set => Set(ref _percentText, value); }
    private string _instantRate = "0.0 KB/s";
    public string InstantRate { get => _instantRate; set => Set(ref _instantRate, value); }
    private string _avgMbps = "-";
    public string AvgMbps { get => _avgMbps; set => Set(ref _avgMbps, value); }

    private void ResetTransferUi()
    {
        IsTransferring = false;
        Progress = 0;
        PercentText = "0%";
        InstantRate = "0.0 KB/s";
        AvgMbps = "-";
        _lastBytes = 0;
        _lastSample = DateTime.MinValue;
    }

    // ---- Log ----
    public ObservableCollection<LogEntry> Logs { get; } = new();

    public void Log(string message)
    {
        Logs.Add(new LogEntry { Time = DateTime.Now.ToString("HH:mm:ss"), Message = message });
    }

    // ---- Transfer event wiring ----
    private void WireTransferEvents()
    {
        _transfer.TextReceived += content =>
            Post(() => AddInbound("接收文本", content));

        _transfer.FileReceived += file =>
            Post(() => AddInbound("接收文件", $"收到文件 {file.Name}（{FormatSize(file.Size)}） → {file.SavePath}", needFileInfo: true));

        _transfer.Progress += p => Post(() => ApplyProgress(p));

        _transfer.Info += msg => Post(() => Log(msg));
        _transfer.LogError += msg => Post(() => Log(msg));
        _transfer.Disconnected += () => Post(() => Log("连接已断开"));
    }

    private void AddInbound(string device, string preview, bool needFileInfo = false, string? deviceName = null)
    {
        Inbox.Insert(0, new InboxEntry
        {
            Device = deviceName ?? device,
            Time = DateTime.Now.ToString("HH:mm"),
            Preview = preview,
            FileInfo = needFileInfo ? "1 个文件" : "",
            IsUnread = true
        });
    }

    private void ApplyProgress(TransferProgress p)
    {
        if (!IsTransferring)
        {
            IsTransferring = true;
            _lastBytes = 0;
            _lastSample = DateTime.UtcNow;
        }
        Progress = p.Fraction * 100.0;
        PercentText = $"{(int)Math.Round(p.Fraction * 100)}%";

        var now = DateTime.UtcNow;
        if (_lastSample == DateTime.MinValue) { _lastSample = now; _lastBytes = p.BytesDone; }
        if (p.BytesDone >= _lastBytes && (now - _lastSample).TotalSeconds >= 0.8)
        {
            var elapsed = (now - _lastSample).TotalSeconds;
            var bytesDelta = p.BytesDone - _lastBytes;
            InstantRate = FormatBytes((long)(bytesDelta / elapsed)) + "/s";
            _lastSample = now;
            _lastBytes = p.BytesDone;
        }
        if (p.ElapsedMs > 0)
        {
            AvgMbps = $"{(p.BytesDone * 8.0 / 1_000_000.0) / (p.ElapsedMs / 1000.0):0.00} Mbps";
        }
    }

    private void Post(Action action)
    {
        if (_ui != null && _ui != SynchronizationContext.Current)
            _ui.Post(_ => action(), null);
        else
            action();
    }

    // ---- Formatting helpers ----
    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB"
    };

    private static string FormatSize(long bytes) => FormatBytes(bytes);

    private static string KindOf(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "pdf",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => "image",
            ".txt" or ".md" => "text",
            _ => "file"
        };
    }
}