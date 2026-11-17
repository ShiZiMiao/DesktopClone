using System.Text.Json;

namespace DesktopClone.Services;

/// <summary>持久化到 %APPDATA%\DesktopClone\settings.json 的用户设置。</summary>
internal sealed class AppSettings
{
    public bool SmartSizing { get; set; } = true;
    public bool Topmost { get; set; }
    public bool BottomMost { get; set; }
    public bool GlobalHotkeysEnabled { get; set; } = true;
    public bool HotkeyUseShift { get; set; }
    public bool AudioMuted { get; set; }
    public bool ShortcutsToRemote { get; set; } = true;
    public bool WatchMode { get; set; }
    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 752;
    public bool WindowMaximized { get; set; }

    /// <summary>分身桌面分辨率;两者为 0 时使用本机主显示器的原始分辨率。</summary>
    public int DesktopWidth { get; set; }
    public int DesktopHeight { get; set; }
    public List<string> RecentPrograms { get; set; } = [];

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopClone",
            "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException
                                              or UnauthorizedAccessException)
        {
            // 配置损坏时回退默认值。
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception) when (exception is JsonException or IOException
                                              or UnauthorizedAccessException)
        {
            // 保存失败不影响运行。
        }
    }
}
