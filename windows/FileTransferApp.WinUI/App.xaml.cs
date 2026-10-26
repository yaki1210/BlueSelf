using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FileTransferApp.WinUI;

public partial class App : Application
{
    private const string ThemeMarker = "__ThemeMarker";
    private const string LangMarker = "__LangMarker";
    /// <summary>One mouse-wheel notch scrolls this many pixels. Smaller than the system default of about 48.</summary>
    private const double WheelPixelsPerNotch = 24;

    protected override void OnStartup(StartupEventArgs e)
    {
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnSmoothMouseWheel));
        base.OnStartup(e);
    }

    /// <summary>Scroll the nearest viewer by a short pixel step so the wheel does not jump a whole row.</summary>
    private static void OnSmoothMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer viewer) return;
        if (e.OriginalSource is not DependencyObject source) return;

        var horizontal = Keyboard.Modifiers == ModifierKeys.Shift;
        if (FindScrollable(source, horizontal) != viewer) return;

        var step = e.Delta / 120.0 * WheelPixelsPerNotch;
        var offset = horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset;
        var limit = horizontal ? viewer.ScrollableWidth : viewer.ScrollableHeight;
        var next = Math.Clamp(offset - step, 0, limit);
        if (Math.Abs(next - offset) < 0.5) return;

        if (horizontal) viewer.ScrollToHorizontalOffset(next);
        else viewer.ScrollToVerticalOffset(next);
        e.Handled = true;
    }

    /// <summary>Nearest ancestor that can still scroll on this axis. Skips text-box viewers that have nothing to scroll.</summary>
    private static ScrollViewer? FindScrollable(DependencyObject source, bool horizontal)
    {
        for (var node = source; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not ScrollViewer viewer) continue;
            if (horizontal ? viewer.ScrollableWidth > 0 : viewer.ScrollableHeight > 0)
                return viewer;
        }
        return null;
    }

    /// <summary>
    /// Switches the UI theme by replacing the theme resource dictionary
    /// (Light / Dark / follow system).
    /// </summary>
    public static void ApplyTheme(string theme)
    {
        var effective = theme switch
        {
            "暗色" => "Dark",
            "跟随系统" => GetSystemDarkMode() ? "Dark" : "Light",
            _ => "Light"
        };
        SwapDictionary(
            markerKey: ThemeMarker,
            source: $"Resources/Themes/{effective}.xaml");
    }

    /// <summary>Detects whether the OS is in dark mode via the registry.</summary>
    private static bool GetSystemDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Switches the UI language by replacing the strings resource dictionary
    /// (中文 / English).
    /// </summary>
    public static void ApplyLanguage(string language)
    {
        SwapDictionary(
            markerKey: LangMarker,
            source: language == "English" ? "Resources/Strings/En.xaml" : "Resources/Strings/Zh.xaml");
    }

    /// <summary>
    /// Locates the merged dictionary that carries <paramref name="markerKey"/>
    /// (a sentinel any of our theme/string dictionaries defines) and replaces it with
    /// a freshly loaded dictionary, so DynamicResource consumers update live.
    /// </summary>
    private static void SwapDictionary(string markerKey, string source)
    {
        var mds = Current.Resources.MergedDictionaries;

        var index = -1;
        for (var i = 0; i < mds.Count; i++)
        {
            if (mds[i].Contains(markerKey))
            {
                index = i;
                mds.RemoveAt(i);
                break;
            }
        }

        var merged = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };
        if (index >= 0)
        {
            mds.Insert(index, merged);
        }
        else
        {
            mds.Add(merged);
        }
    }
}