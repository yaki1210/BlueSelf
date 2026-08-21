using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using FileTransferApp.WinUI.ViewModels;

namespace FileTransferApp.WinUI.UI;

/// <summary>Converts a device kind ("pc"/"tablet"/"phone", derived from the peer name)
/// to a Segoe MDL2 glyph, mirroring Android's per-device sender icon.</summary>
public sealed class DeviceKindToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var kind = value?.ToString() ?? "";
        return kind switch
        {
            "pc" => "\uE968",     // Computer
            "tablet" => "\uE970", // Tablet
            _ => "\uE8EA"         // CellPhone
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts a count to Visibility (count &gt; 0 → Visible).</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts the children count of an IEnumerable (e.g. an ObservableCollection) to Visibility.</summary>
public sealed class HasItemsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var any = value is System.Collections.ICollection c && c.Count > 0;
        return any ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

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

/// <summary>Converts null to Collapsed and non-null to Visible.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts null to Visible and non-null to Collapsed.</summary>
public sealed class InverseNullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value == null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts kind string to a subtle background brush for chips.</summary>
public sealed class KindToBgBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string kind
            ? kind switch
            {
                "pdf" => new SolidColorBrush(Color.FromArgb(30, 239, 68, 68)),    // Red tint
                "image" => new SolidColorBrush(Color.FromArgb(30, 16, 185, 129)), // Green tint
                _ => new SolidColorBrush(Color.FromArgb(30, 75, 63, 227)),        // Purple tint
            }
            : new SolidColorBrush(Color.FromArgb(20, 0, 0, 0));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts kind string to an accent foreground brush.</summary>
public sealed class KindToFgBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string kind
            ? kind switch
            {
                "pdf" => new SolidColorBrush(Color.FromRgb(220, 38, 38)),
                "image" => new SolidColorBrush(Color.FromRgb(5, 150, 105)),
                _ => new SolidColorBrush(Color.FromRgb(75, 63, 227)),
            }
            : new SolidColorBrush(Color.FromRgb(100, 100, 100));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}