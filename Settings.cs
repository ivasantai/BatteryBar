using System.Text.Json;
using Microsoft.Win32;

namespace BatteryBar;

internal sealed class Settings
{
    /// <summary>Расстояние (в физических пикселях) от правого края панели задач до правого края виджета.
    /// null — ставить автоматически слева от области уведомлений.</summary>
    public int? RightOffset { get; set; }

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BatteryBar", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }

    // ---------- Автозапуск (HKCU\...\Run) ----------

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "BatteryBar";

    public static bool AutostartEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunName) is string s && s.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(RunName, false);
        }
    }

    /// <summary>Светлая ли тема у панели задач (SystemUsesLightTheme).</summary>
    public static bool TaskbarIsLight
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
    }
}
