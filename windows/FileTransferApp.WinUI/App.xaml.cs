using System.Windows;

namespace FileTransferApp.WinUI;

public partial class App : Application
{
    /// <summary>
    /// Switches the UI theme by swapping the theme resource dictionary (Light/Dark/System).
    /// </summary>
    public static void ApplyTheme(string theme)
    {
        const string marker = "/Themes/";
        var mds = Current.Resources.MergedDictionaries;
        // 跟随系统：根据系统浅色/深色偏好选择对应主题
        var effective = theme switch
        {
            "暗色" => "Dark",
            "跟随系统" => GetSystemDarkMode() ? "Dark" : "Light",
            _ => "Light"
        };
        var source = $"Resources/Themes/{effective}.xaml";
        var uri = new Uri(source, UriKind.Relative);
        SwapDictionary(mds, marker, uri);
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
    /// Switches the UI language by swapping the strings resource dictionary (中文/English).
    /// </summary>
    public static void ApplyLanguage(string language)
    {
        const string marker = "/Strings/";
        var mds = Current.Resources.MergedDictionaries;
        var source = language == "English" ? "Resources/Strings/En.xaml" : "Resources/Strings/Zh.xaml";
        var uri = new Uri(source, UriKind.Relative);
        SwapDictionary(mds, marker, uri);
    }

    private static void SwapDictionary(
        System.Collections.ObjectModel.Collection<ResourceDictionary> mds, string marker, Uri newSource)
    {
        var index = -1;
        for (var i = 0; i < mds.Count; i++)
        {
            var dir = mds[i];
            var src = dir.Source?.ToString() ?? string.Empty;
            if (src.Contains(marker))
            {
                index = i;
                mds.RemoveAt(i);
                break;
            }
        }
        var merged = new ResourceDictionary { Source = newSource };
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