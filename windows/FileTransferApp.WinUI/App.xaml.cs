using System.Windows;

namespace FileTransferApp.WinUI;

public partial class App : Application
{
    private const string ThemeMarker = "__ThemeMarker";
    private const string LangMarker = "__LangMarker";

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