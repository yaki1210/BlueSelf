using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FileTransferApp.WinUI.ViewModels;

namespace FileTransferApp.WinUI.UI.Views;

public partial class AddDeviceView : UserControl
{
    public AddDeviceView()
    {
        InitializeComponent();
        DataContext = MainViewModel.Instance;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        // 扫描中旋转动画跟随 IsScanning 启停。
        var vm = (MainViewModel)DataContext;
        ApplySpin(vm.IsScanning);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsScanning))
                Dispatcher.Invoke(() => ApplySpin(vm.IsScanning));
        };
    }

    private void ApplySpin(bool scanning)
    {
        var sb = (Storyboard?)FindResource("ScanSpin");
        if (sb == null) return;
        if (scanning) sb.Begin(this, isControllable: true);
        else
        {
            try { sb.Remove(this); } catch { }
            var glyph = scanGlyph;
            if (glyph?.RenderTransform is RotateTransform rt) rt.Angle = 0;
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // 离开添加设备页时若仍在扫描则停止，避免后台常驻射频扫描。
        if (e.NewValue is false && DataContext is MainViewModel vm && vm.IsScanning)
            vm.StopScanCommand?.Execute(null);
    }
}
