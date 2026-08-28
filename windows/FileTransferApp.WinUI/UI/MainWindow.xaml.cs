using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using FileTransferApp.WinUI.ViewModels;

namespace FileTransferApp.WinUI.UI;

public partial class MainWindow : Window
{
    // Win11 DWM 圆角（Win10 无此 API，调用失败静默保持直角）。
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_DEFAULT = 0;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWCP_ROUND = 2;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int sizeOfValue);

    public MainWindow()
    {
        InitializeComponent();
        DataContext = MainViewModel.Instance;
        StateChanged += OnStateChangedForCorner;
        // 窗口级文件拖拽：拖到窗口任意位置都能加附件。
        // 用隧道事件 + handledEventsToo：编辑器 TextBox 的原生拖放类处理器会把冒泡事件标为已处理，
        // 导致发送区（TextBox 上方）拖放失效；隧道在事件到达 TextBox 前截获，文件拖放全窗口生效，
        // 非 FileDrop（如拖文本进编辑器）不标记 Handled，保留 TextBox 原生行为。
        AllowDrop = true;
        AddHandler(UIElement.PreviewDragOverEvent, new DragEventHandler(OnWindowDragOver), handledEventsToo: true);
        AddHandler(UIElement.PreviewDropEvent, new DragEventHandler(OnWindowDrop), handledEventsToo: true);
    }

    /// <summary>Window handle is guaranteed here; enable rounded corners + no OS border on Win11.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyWindowChromeRounding();
    }

    /// <summary>Enables rounded corners and removes the OS border on Win11; silently no-ops on Win10.</summary>
    private void ApplyWindowChromeRounding()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            var none = unchecked((int)DWMWA_COLOR_NONE);
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
        }
        catch
        {
            // Win10 / dwmapi 不可用：保持直角，不影响功能。
        }
    }

    /// <summary>Rounded corners while normal; square while maximized (avoids clipped corners).</summary>
    private void OnStateChangedForCorner(object sender, EventArgs e)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var pref = WindowState == WindowState.Maximized ? DWMWCP_DONOTROUND : DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { }
    }

    /// <summary>窗口级拖拽反馈：有文件时显示复制效果（隧道阶段截获，TextBox 抢不走）。</summary>
    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        // 非 FileDrop（拖文本等）不标记，交给内部控件原生处理。
    }

    /// <summary>窗口级拖放：文件/文件夹加入待发附件（文件夹取内一层文件）。</summary>
    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (System.IO.Directory.Exists(path))
            {
                try { files.AddRange(System.IO.Directory.EnumerateFiles(path)); }
                catch { /* 无权限的文件夹跳过 */ }
            }
            else if (System.IO.File.Exists(path))
            {
                files.Add(path);
            }
        }
        if (files.Count > 0)
        {
            MainViewModel.Instance.AddAttachmentFiles(files);
        }
        else
        {
            MainViewModel.Instance.Log("拖入的内容没有可添加的文件");
        }
        e.Handled = true;
    }

    // 窗口重新获得焦点时刷新设备列表（如从系统蓝牙设置配对后返回）。
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        MainViewModel.Instance.OnWindowActivated();
    }

    // 标题栏拖拽移动窗口
    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnMinimizeButtonClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeButtonClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            MaximizeButton.Content = "\uE922";
            MaximizeButton.ToolTip = "最大化";
        }
        else
        {
            WindowState = WindowState.Maximized;
            MaximizeButton.Content = "\uE923";
            MaximizeButton.ToolTip = "向下还原";
        }
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}