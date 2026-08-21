using System.Windows;
using System.Windows.Input;
using FileTransferApp.WinUI.ViewModels;

namespace FileTransferApp.WinUI.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = MainViewModel.Instance;
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