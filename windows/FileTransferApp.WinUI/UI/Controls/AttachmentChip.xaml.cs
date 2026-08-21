using System.Windows;
using System.Windows.Controls;
using FileTransferApp.WinUI.ViewModels;

namespace FileTransferApp.WinUI.UI.Controls;

public partial class AttachmentChip : UserControl
{
    public AttachmentChip()
    {
        InitializeComponent();
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is AttachmentItem item)
        {
            MainViewModel.Instance.RemoveAttachment(item);
        }
    }
}