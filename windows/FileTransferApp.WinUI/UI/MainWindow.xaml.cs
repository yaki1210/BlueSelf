using System.Windows;
using FileTransferApp.WinUI.ViewModels;

namespace FileTransferApp.WinUI.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = MainViewModel.Instance;
    }
}