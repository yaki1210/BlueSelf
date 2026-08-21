using System.IO;
using System.Text.Json;

namespace FileTransferApp.WinUI.ViewModels;

/// <summary>
/// Persists app settings (language / theme / received-files save path) to a JSON file
/// under %LOCALAPPDATA%\BlueSelf\settings.json so they survive app restarts.
/// </summary>
internal static class AppSettingsStore
{
    /// <summary>Primary settings dir; falls back to Temp when LocalAppData is not writable.</summary>
    private static string SettingsDir
    {
        get
        {
            var primary = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueSelf");
            try
            {
                Directory.CreateDirectory(primary);
                return primary;
            }
            catch
            {
                return Path.Combine(Path.GetTempPath(), "BlueSelf");
            }
        }
    }

    private static string SettingsFile => Path.Combine(SettingsDir, "settings.json");

    /// <summary>Reads persisted settings; returns defaults when missing or malformed.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return new AppSettings();
            var json = File.ReadAllText(SettingsFile);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return new AppSettings(
                Language: Get(r, "language", "中文"),
                Theme: Get(r, "theme", "跟随系统"),
                SavePath: Get(r, "savePath", ""));
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(string language, string theme, string savePath)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(new { language, theme, savePath });
            File.WriteAllText(SettingsFile, json);
        }
        catch
        {
            // Settings persistence is best-effort; ignore failures.
        }
    }

    private static string Get(JsonElement e, string key, string fallback)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
}

/// <summary>In-memory snapshot of persisted app settings.</summary>
internal sealed record AppSettings(string Language, string Theme, string SavePath)
{
    public AppSettings() : this("中文", "跟随系统", "") { }
}