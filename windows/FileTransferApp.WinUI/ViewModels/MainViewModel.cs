using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace FileTransferApp.WinUI.ViewModels;

/// <summary>Device connection state shown by the status dot.</summary>
public enum DeviceStatus { Online, Connecting, Offline }

/// <summary>A paired device displayed in the device column.</summary>
public sealed class DeviceItem : ObservableObject
{
    public required string Name { get; init; }
    public required string SubLabel { get; init; }
    private DeviceStatus _status;
    public DeviceStatus Status { get => _status; set => Set(ref _status, value); }
}

/// <summary>A pending attachment chip in the composer.</summary>
public sealed class AttachmentItem : ObservableObject
{
    public required string Name { get; init; }
    public required string SizeText { get; init; }
    public required string Kind { get; init; } // pdf / image / file
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
/// Main UI state for the stage-1 shell (no real Bluetooth/file logic).
/// Provides a dispatcher-timer driven simulated transfer to preview the
/// real-time status column and per-second rate display.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();
    private int _progressSteps;
    private DateTime _transferStart;

    public MainViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTick();

        Devices.Add(new DeviceItem { Name = "Pixel 9a", SubLabel = "在线", Status = DeviceStatus.Online });
        Devices.Add(new DeviceItem { Name = "小米 14", SubLabel = "离线", Status = DeviceStatus.Offline });

        Inbox.Add(new InboxEntry
        {
            Device = "Pixel 9a", Time = "10:24", Preview = "照片已收到，请看下。",
            FileInfo = "1 个文件", IsUnread = true
        });
        Inbox.Add(new InboxEntry
        {
            Device = "Pixel 9a", Time = "09:58", Preview = "好的，收到。",
            FileInfo = "", IsUnread = false
        });

        Log("已启动 · 界面预览（占位）");
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
    public RelayCommandNoArg ShowInboxCommand => new(() => { CurrentView = AppView.Inbox; Log("打开收件箱（占位）"); });
    public RelayCommandNoArg ShowSettingsCommand => new(() => { CurrentView = AppView.Settings; Log("打开设置（占位）"); });

    // ---- Device column ----
    public ObservableCollection<DeviceItem> Devices { get; } = new();
    private DeviceItem? _selectedDevice;
    public DeviceItem? SelectedDevice { get => _selectedDevice; set => Set(ref _selectedDevice, value); }
    public string LocalName { get; } = "BlueSelf-PC";
    private bool _isListening = true;
    public bool IsListening { get => _isListening; set => Set(ref _isListening, value); }

    public RelayCommandNoArg AddPairingCommand => new(() => Log("添加配对（占位）"));

    // ---- Composer ----
    private string _text = string.Empty;
    public string Text { get => _text; set => Set(ref _text, value); }
    public ObservableCollection<AttachmentItem> Attachments { get; } = new();

    public RelayCommandNoArg PasteCommand => new(() => Log("粘贴（占位）"));
    public RelayCommandNoArg AttachCommand => new(() => { AddSampleAttachment(); Log("添加附件（占位）"); });
    public RelayCommandNoArg StartSendCommand => new(BeginTransfer);

    private void AddSampleAttachment()
    {
        if (Attachments.All(a => a.Name != "报告.pdf"))
        {
            Attachments.Add(new AttachmentItem { Name = "报告.pdf", SizeText = "1.2 MB", Kind = "pdf" });
        }
        if (Attachments.All(a => a.Name != "照片.png"))
        {
            Attachments.Add(new AttachmentItem { Name = "照片.png", SizeText = "3.4 MB", Kind = "image" });
        }
    }

    public RelayCommandNoArg AddLogCommand => new(() => Log("占位操作"));

    /// <summary>Removes an attachment by name from the composer.</summary>
    public void RemoveAttachment(AttachmentItem item)
    {
        Attachments.Remove(item);
        Log($"移除附件 {item.Name}");
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

    // ---- Settings (dynamic settings arrive in stage 2; static flags for preview) ----
    public string[] Languages { get; } = { "中文", "English" };
    private string _language = "中文";
    public string Language { get => _language; set => Set(ref _language, value); }
    public string[] Themes { get; } = { "跟随系统", "亮色", "暗色" };
    private string _theme = "跟随系统";
    public string Theme { get => _theme; set => Set(ref _theme, value); }
    public string SavePath => @"%TEMP%\BlueSelf\received (占位路径)";

    // ---- Transfer status (third column) ----
    private bool _isTransferring;
    public bool IsTransferring { get => _isTransferring; set => Set(ref _isTransferring, value); }
    private string _fileName = "报告.pdf";
    public string FileName { get => _fileName; set => Set(ref _fileName, value); }
    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    private string _percentText = "0%";
    public string PercentText { get => _percentText; set => Set(ref _percentText, value); }
    private string _instantRate = "0.0 KB/s";
    public string InstantRate { get => _instantRate; set => Set(ref _instantRate, value); }
    private string _avgMbps = "-";
    public string AvgMbps { get => _avgMbps; set => Set(ref _avgMbps, value); }

    // ---- Log ----
    public ObservableCollection<LogEntry> Logs { get; } = new();
    public bool IsLogExpanded { get; set; }

    public void Log(string message)
    {
        Logs.Add(new LogEntry { Time = DateTime.Now.ToString("HH:mm:ss"), Message = message });
    }

    private void BeginTransfer()
    {
        if (SelectedDevice == null)
        {
            Log("请先在设备栏选择目标设备（占位）");
            return;
        }
        IsTransferring = true;
        Progress = 0;
        _progressSteps = 0;
        _transferStart = DateTime.Now;
        PercentText = "0%";
        InstantRate = "0.0 KB/s";
        AvgMbps = "-";
        FileName = Attachments.FirstOrDefault()?.Name ?? "sample.bin";
        Log($"开始发送 {FileName} → {SelectedDevice.Name}（占位模拟）");
        _timer.Start();
    }

    private void OnTick()
    {
        _progressSteps++;
        var incr = _rng.Next(9, 20);
        Progress = Math.Min(100, Progress + incr);
        PercentText = $"{Progress:0}%";

        var kbPerSec = _rng.Next(95, 141);
        InstantRate = $"{kbPerSec:0.0} KB/s";
        AvgMbps = $"{kbPerSec * 8.0 / 1000.0:0.00} Mbps";

        if (Progress >= 100)
        {
            _timer.Stop();
            IsTransferring = false;
            Progress = 0;
            PercentText = "0%";
            InstantRate = "0.0 KB/s";
            AvgMbps = "-";
            Log($"传输完成 {FileName}（占位）");
        }
    }
}