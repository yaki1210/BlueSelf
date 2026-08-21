using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using FileTransferApp.WinUI.ViewModels;

namespace FileTransferApp.WinUI.UI;

/// <summary>Converts a DeviceStatus to a status-dot brush.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DeviceStatus s
            ? s switch
            {
                DeviceStatus.Online => new SolidColorBrush(Color.FromRgb(29, 201, 129)),
                DeviceStatus.Connecting => new SolidColorBrush(Color.FromRgb(239, 170, 23)),
                _ => new SolidColorBrush(Color.FromRgb(158, 158, 158)),
            }
            : new SolidColorBrush(Colors.Gray);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts an attachment kind to a glyph (Segoe MDL2 Assets).</summary>
public sealed class KindToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string kind
            ? kind switch
            {
                "pdf" => "\uE9A4",   // PDF glyph
                "image" => "\uEB9F", // Photo2
                _ => "\uE8A5",       // Document
            }
            : "\uE8A5";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts a boolean to Visibility (true → Visible).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>Converts a boolean to Visibility (true → Collapsed, i.e. inverted).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible is false;
}

/// <summary>Converts an empty/null string to Visible (used for a text watermark).</summary>
public sealed class EmptyTextToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}